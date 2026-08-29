using System.Security.Cryptography;
using System.Text;
using Wisper.Api.Hosts;
using Wisper.Api.Persistence.Hosts;

namespace Wisper.Api.Tunnel;

/// <summary>
/// DB-backed <see cref="IHostTokenValidator"/> (docs/TUNNEL.md §3, §13): resolves a presented agent token
/// to its host id by a <b>hashed, constant-time</b> lookup against the <c>hosts</c> table. The presented
/// token is SHA-256 hashed (<see cref="HostAgentToken.Hash"/>) and the digest -- never the raw token -- is
/// looked up against the stored <c>agent_token_hash</c>. The stored hash is then re-compared with
/// <see cref="CryptographicOperations.FixedTimeEquals"/> so the resolution does not leak via timing, and
/// because the lookup key is a preimage-resistant digest the token itself is never recoverable from the
/// row. Rotation simply writes a new hash, so a revoked token stops resolving (its tunnel is closed 4402).
/// <para>
/// The lookup runs against whichever <see cref="IHostRepository"/> backs the persistence layer -- the
/// Postgres table in production, or the in-memory store on a DB-less dev boot (docs/DATA_MODEL.md §1) --
/// so an agent token issued by <c>POST /v1/hosts</c> resolves in either mode. A token the store does not
/// recognize falls through to the config-backed allow-list (<see cref="ConfigHostTokenValidator"/>), the
/// operator bootstrap escape hatch. That fallback is itself env-gated: it fails closed in any non-Development
/// environment regardless of the configured <c>Tunnel:HostTokens</c>, so a deployed environment always uses
/// the DB as the sole source of truth and an unknown token fails closed even if a secret ships static tokens.
/// </para>
/// </summary>
public sealed class DbHostTokenValidator : IHostTokenValidator
{
    private readonly IHostRepository _hosts;
    private readonly ConfigHostTokenValidator _fallback;

    public DbHostTokenValidator(IHostRepository hosts, ConfigHostTokenValidator fallback)
    {
        _hosts = hosts;
        _fallback = fallback;
    }

    public async Task<HostTokenValidationResult> ValidateAsync(string? bearerToken, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(bearerToken))
        {
            return HostTokenValidationResult.Failure();
        }

        // Hashed lookup against the host store (Postgres or the in-memory dev store -- both are queryable
        // without a live DB connection, docs/DATA_MODEL.md §1).
        var hash = HostAgentToken.Hash(bearerToken);
        var host = await _hosts.GetByAgentTokenHashAsync(hash, ct);
        if (host is not null && ConstantTimeEquals(host.AgentTokenHash, hash))
        {
            return HostTokenValidationResult.Success(host.Id.ToString());
        }

        // Not resolved from the store -- try the config allow-list (dev/bootstrap; empty and thus
        // fail-closed in production).
        return await _fallback.ValidateAsync(bearerToken, ct);
    }

    /// <summary>Constant-time equality over the two hex digests, so match latency never leaks the token.</summary>
    private static bool ConstantTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
