using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Wisper.Api.ApiKeys;
using Wisper.Api.Domain;
using Wisper.Api.Persistence.ApiKeys;
using Wisper.Api.Persistence.Users;

namespace Wisper.Api.Auth;

/// <summary>
/// DB-backed <see cref="IApiKeyAuthenticator"/> (docs/API.md §2, docs/DATA_MODEL.md §3): resolves a
/// presented <c>wck_…</c> key to the owning user's principal by a <b>hashed, constant-time</b> lookup
/// against the <c>api_keys</c> table. The presented key is SHA-256 hashed (<see cref="ApiKeyToken.Hash"/>)
/// and the digest — never the raw key — is looked up against the stored <c>token_hash</c> (active rows
/// only, so a revoked key never resolves). The stored hash is re-compared with
/// <see cref="CryptographicOperations.FixedTimeEquals"/> so resolution does not leak via timing, and
/// because the lookup key is a preimage-resistant digest the key itself is never recoverable from the row.
/// <para>
/// A resolved key's <b>owner must exist and be active</b> — a suspended or missing owner fails closed
/// (mirroring how host suspension gates the tunnel), and never falls through to the config allow-list.
/// The principal carries the owner's subject and email and roles = the <b>key's stored scopes</b> (never
/// Cognito groups), then a best-effort <c>last_used_at</c> stamp is written.
/// </para>
/// <para>
/// The lookup runs against whichever <see cref="IApiKeyRepository"/> backs the persistence layer — the
/// Postgres table in production, or the in-memory store on a DB-less dev boot (docs/DATA_MODEL.md §1). A
/// key the store does not recognize falls through to the config-backed allow-list
/// (<see cref="ConfigApiKeyAuthenticator"/>), the operator bootstrap escape hatch — exactly the layering
/// <see cref="Tunnel.DbHostTokenValidator"/> uses for host tokens. In production the config allow-list is
/// empty, so the store is the sole source of truth and an unknown key fails closed.
/// </para>
/// </summary>
public sealed class DbApiKeyAuthenticator : IApiKeyAuthenticator
{
    private readonly IApiKeyRepository _keys;
    private readonly IUserRepository _users;
    private readonly TimeProvider _time;
    private readonly ConfigApiKeyAuthenticator _fallback;

    public DbApiKeyAuthenticator(
        IApiKeyRepository keys,
        IUserRepository users,
        TimeProvider time,
        ConfigApiKeyAuthenticator fallback)
    {
        _keys = keys;
        _users = users;
        _time = time;
        _fallback = fallback;
    }

    public async Task<ClaimsPrincipal?> AuthenticateAsync(string? bearerToken, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(bearerToken))
        {
            return null;
        }

        // Hashed lookup against the api_keys store (Postgres or the in-memory dev store — both are
        // queryable without a live DB connection, docs/DATA_MODEL.md §1).
        var hash = ApiKeyToken.Hash(bearerToken);
        var key = await _keys.GetByTokenHashAsync(hash, ct);
        if (key is not null && ConstantTimeEquals(key.TokenHash, hash))
        {
            // Known, active key: the owner must exist and be active, else fail closed (a suspended or
            // deleted owner gates the key exactly as a host suspension gates the tunnel). A recognized
            // key never falls through to the config allow-list.
            var user = await _users.GetByIdAsync(key.UserId, ct);
            if (user is null || user.Status != UserStatus.Active)
            {
                return null;
            }

            // Best-effort last-used stamp on the authenticated path; the repo swallows any failure.
            await _keys.TouchLastUsedAsync(key.Id, _time.GetUtcNow(), ct);

            return WisperPrincipal.CreateForApiKey(user.CognitoSub, user.Email, key.Scopes);
        }

        // Not resolved from the store — try the config allow-list (dev/bootstrap; empty and thus
        // fail-closed in production).
        return await _fallback.AuthenticateAsync(bearerToken, ct);
    }

    /// <summary>Constant-time equality over the two hex digests, so match latency never leaks the key.</summary>
    private static bool ConstantTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
