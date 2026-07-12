using System.Security.Cryptography;
using System.Text;
using Wisper.Api.Hosts;
using Wisper.Api.Persistence;
using Wisper.Api.Persistence.Hosts;

namespace Wisper.Api.Tunnel;

/// <summary>
/// DB-backed <see cref="IHostTokenValidator"/> (docs/TUNNEL.md §3, §13): resolves a presented agent token
/// to its host id by a <b>hashed, constant-time</b> lookup against the <c>hosts</c> table. The presented
/// token is SHA-256 hashed (<see cref="HostAgentToken.Hash"/>) and the digest — never the raw token — is
/// looked up against the stored <c>agent_token_hash</c>. The stored hash is then re-compared with
/// <see cref="CryptographicOperations.FixedTimeEquals"/> so the resolution does not leak via timing, and
/// because the lookup key is a preimage-resistant digest the token itself is never recoverable from the
/// row. Rotation simply writes a new hash, so a revoked token stops resolving (its tunnel is closed 4402).
/// <para>
/// When no database is configured (the Grunt/dev tunnel-only boot) the DB path is skipped and validation
/// falls through to the config-backed allow-list (<see cref="ConfigHostTokenValidator"/>), which also
/// serves as an operator bootstrap escape hatch. In production the config allow-list is empty, so the DB
/// is the sole source of truth and an unknown token fails closed.
/// </para>
/// </summary>
public sealed class DbHostTokenValidator : IHostTokenValidator
{
    private readonly IHostRepository _hosts;
    private readonly Db _db;
    private readonly ConfigHostTokenValidator _fallback;

    public DbHostTokenValidator(IHostRepository hosts, Db db, ConfigHostTokenValidator fallback)
    {
        _hosts = hosts;
        _db = db;
        _fallback = fallback;
    }

    public async Task<HostTokenValidationResult> ValidateAsync(string? bearerToken, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(bearerToken))
        {
            return HostTokenValidationResult.Failure();
        }

        // Hashed lookup against the hosts table. Guarded on IsConfigured so a DB-less boot degrades to the
        // config fallback instead of throwing (docs/DATA_MODEL.md §1 — the tunnel can boot with no DB).
        if (_db.IsConfigured)
        {
            var hash = HostAgentToken.Hash(bearerToken);
            var host = await _hosts.GetByAgentTokenHashAsync(hash, ct);
            if (host is not null && ConstantTimeEquals(host.AgentTokenHash, hash))
            {
                return HostTokenValidationResult.Success(host.Id.ToString());
            }
        }

        // Not resolved from the DB — try the config allow-list (dev/bootstrap; empty and thus fail-closed
        // in production).
        return await _fallback.ValidateAsync(bearerToken, ct);
    }

    /// <summary>Constant-time equality over the two hex digests, so match latency never leaks the token.</summary>
    private static bool ConstantTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
