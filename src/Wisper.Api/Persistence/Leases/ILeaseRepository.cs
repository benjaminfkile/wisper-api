using Wisper.Api.Domain;

namespace Wisper.Api.Persistence.Leases;

/// <summary>
/// Data access for <see cref="Lease"/> rows (docs/DATA_MODEL.md §5). Covers the consumer lease surface
/// (create/get/list, P4.2), the host-scoped active set the metering engine reloads on restart
/// (docs/DATA_MODEL.md §14), a full <see cref="UpdateAsync"/> of the mutable columns, and the narrow
/// <see cref="TransitionStateAsync"/> the state machine and meter drive. A Dapper + explicit-SQL
/// implementation backs Postgres; an in-memory double backs the unit suite (Grunt has no Postgres).
/// </summary>
public interface ILeaseRepository : IRepository
{
    /// <summary>Gets a lease by id, or <c>null</c> if none.</summary>
    Task<Lease?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>The consumer's leases, newest first (index <c>leases_consumer_idx</c>).</summary>
    Task<IReadOnlyList<Lease>> ListByConsumerAsync(Guid consumerUserId, CancellationToken ct = default);

    /// <summary>
    /// A host's live leases — <c>status IN ('active','suspended')</c> (partial index
    /// <c>leases_host_active_idx</c>); the set the meter reloads to resume on restart/reconnect.
    /// </summary>
    Task<IReadOnlyList<Lease>> ListActiveByHostAsync(Guid hostId, CancellationToken ct = default);

    /// <summary>Inserts a new lease and returns the stored row (with any DB-generated id).</summary>
    Task<Lease> CreateAsync(Lease lease, CancellationToken ct = default);

    /// <summary>
    /// Updates the mutable columns (status, end reason, wisp contract, hold txn, and the metering
    /// timeline) of the lease identified by <see cref="Lease.Id"/> and returns the stored row. The
    /// immutable snapshots and identity columns are not touched. Throws when the lease does not exist.
    /// </summary>
    Task<Lease> UpdateAsync(Lease lease, CancellationToken ct = default);

    /// <summary>
    /// The narrow state-machine write: sets <paramref name="status"/> and, when non-null, advances the
    /// timeline fields (<paramref name="endReason"/>, <paramref name="startedAt"/>,
    /// <paramref name="lastMeteredAt"/>, <paramref name="billableSeconds"/>, <paramref name="endedAt"/>);
    /// a null argument leaves that column unchanged. Returns the stored row, or <c>null</c> if no such
    /// lease. This is what <c>pending → provisioning → active ⇄ suspended → ended/failed</c> transitions
    /// and each metering tick use (docs/DATA_MODEL.md §5, docs/TUNNEL.md §8).
    /// </summary>
    Task<Lease?> TransitionStateAsync(
        Guid id,
        LeaseStatus status,
        LeaseEndReason? endReason = null,
        DateTimeOffset? startedAt = null,
        DateTimeOffset? lastMeteredAt = null,
        long? billableSeconds = null,
        DateTimeOffset? endedAt = null,
        CancellationToken ct = default);
}
