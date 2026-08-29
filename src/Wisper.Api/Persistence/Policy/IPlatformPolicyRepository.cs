using Wisper.Api.Domain;

namespace Wisper.Api.Persistence.Policy;

/// <summary>
/// Data access for <see cref="PlatformPolicy"/> versions (docs/DATA_MODEL.md §11). Rows are append-only:
/// <see cref="AppendAsync"/> inserts a new version and <see cref="GetActiveAsync"/> reads the active one
/// (the newest <c>effective_from</c> at or before a given instant), never an in-place edit. A Dapper +
/// explicit-SQL implementation backs Postgres; an in-memory double backs the unit suite (Grunt has no
/// Postgres).
/// </summary>
public interface IPlatformPolicyRepository : IRepository
{
    /// <summary>Appends a new policy version (assigning an id when unset) and returns the stored row.</summary>
    Task<PlatformPolicy> AppendAsync(PlatformPolicy policy, CancellationToken ct = default);

    /// <summary>
    /// The active policy as of <paramref name="asOf"/> -- the row with the greatest
    /// <see cref="PlatformPolicy.EffectiveFrom"/> that is at or before it -- or <c>null</c> when no version
    /// has taken effect yet.
    /// </summary>
    Task<PlatformPolicy?> GetActiveAsync(DateTimeOffset asOf, CancellationToken ct = default);

    /// <summary>Every policy version, newest <c>effective_from</c> first.</summary>
    Task<IReadOnlyList<PlatformPolicy>> ListAsync(CancellationToken ct = default);
}
