using Wisper.Api.Domain;

namespace Wisper.Api.Persistence.ApiKeys;

/// <summary>
/// Data access for <see cref="ApiKey"/> rows (docs/DATA_MODEL.md §3). Covers minting a key, the hashed
/// <b>active-only</b> lookup the auth gate uses to resolve a presented key to its owner, the owner-scoped
/// listing (mint/list/revoke UX), revocation, and a best-effort last-used stamp. A Dapper + explicit-SQL
/// implementation backs Postgres; an in-memory double backs the unit suite (Grunt has no Postgres).
/// </summary>
public interface IApiKeyRepository : IRepository
{
    /// <summary>Inserts a new key and returns the stored row (with any DB-generated id).</summary>
    Task<ApiKey> CreateAsync(ApiKey apiKey, CancellationToken ct = default);

    /// <summary>
    /// Gets the <b>active</b> key whose stored hash equals <paramref name="tokenHash"/>, or <c>null</c>.
    /// Revoked keys (<c>revoked_at IS NOT NULL</c>) never resolve, so a revoked key fails closed.
    /// </summary>
    Task<ApiKey?> GetByTokenHashAsync(string tokenHash, CancellationToken ct = default);

    /// <summary>All keys owned by <paramref name="userId"/>, newest first (includes revoked keys).</summary>
    Task<IReadOnlyList<ApiKey>> ListByUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Revokes the key identified by <paramref name="id"/> by stamping <paramref name="revokedAt"/>, and
    /// returns the stored row. Returns <c>null</c> if no such <b>active</b> key exists (already revoked or
    /// missing), so revocation is idempotent.
    /// </summary>
    Task<ApiKey?> RevokeAsync(Guid id, DateTimeOffset revokedAt, CancellationToken ct = default);

    /// <summary>
    /// Best-effort stamp of <paramref name="usedAt"/> onto the key's <c>last_used_at</c>. This is a
    /// fire-and-forget touch on the authenticated request path: it must never fail or slow the request,
    /// so a failure (or a missing key) is swallowed rather than surfaced.
    /// </summary>
    Task TouchLastUsedAsync(Guid id, DateTimeOffset usedAt, CancellationToken ct = default);
}
