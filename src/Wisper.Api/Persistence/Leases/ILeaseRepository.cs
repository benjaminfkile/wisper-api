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

    /// <summary>
    /// Counts a host's live leases — <c>status IN ('active','suspended')</c> (partial index
    /// <c>leases_host_active_idx</c>). This is the host's non-terminal contract count the per-host admission
    /// fast-fail gates against (task #571): a create yields <c>active</c> directly and <c>suspended</c> is the
    /// only other live state, so this set is exactly the host's in-flight contracts (terminal ended/failed
    /// excluded). Cheaper than materializing the rows for a count.
    /// </summary>
    Task<int> CountActiveByHostAsync(Guid hostId, CancellationToken ct = default);

    /// <summary>
    /// Every lease with <c>status = 'active'</c> — the metering engine's working set (docs/DATA_MODEL.md
    /// §14). On each tick the meter accrues over this set, and on restart it reloads the set from the DB
    /// and resumes each lease from its persisted <see cref="Lease.LastMeteredAt"/> watermark. Suspended
    /// leases are excluded: a suspended gap never bills (docs/TUNNEL.md §8).
    /// </summary>
    Task<IReadOnlyList<Lease>> ListActiveAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns <c>true</c> when any lease (any status, active or historical) references
    /// <paramref name="hostImageId"/>. Used to decide soft- vs hard-delete when a host omits an image
    /// from its replace-list: rows with lease history must be soft-disabled to preserve FK integrity.
    /// </summary>
    Task<bool> HasLeaseForImageAsync(Guid hostImageId, CancellationToken ct = default);

    /// <summary>
    /// Every lease with <c>status = 'suspended'</c> whose <see cref="Lease.SuspendedAt"/> is at or before
    /// <paramref name="suspendedOnOrBefore"/> — the driving query of the durable grace sweep (task #55).
    /// Backed by the partial index <c>leases_suspended_at_idx</c>; ordered oldest-first so a batch-limited
    /// sweep tackles the oldest strays first.
    /// </summary>
    Task<IReadOnlyList<Lease>> ListSuspendedOlderThanAsync(
        DateTimeOffset suspendedOnOrBefore, CancellationToken ct = default);

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
    /// <paramref name="lastMeteredAt"/>, <paramref name="billableSeconds"/>, <paramref name="endedAt"/>,
    /// <paramref name="suspendedAt"/>); a null argument leaves that column unchanged. Returns the stored
    /// row, or <c>null</c> if no such lease OR the optional <paramref name="expectedCurrentStatus"/>
    /// CAS guard did not match — used by the durable grace sweep (task #55) so two concurrent instances
    /// converge on exactly one <c>suspended → ended</c> transition per lease. <c>suspended_at</c> is
    /// auto-cleared on any transition to a non-<c>suspended</c> status (a resumed/ended row never carries
    /// a stale suspension timestamp). This is what <c>pending → provisioning → active ⇄ suspended → ended/failed</c>
    /// transitions and each metering tick use (docs/DATA_MODEL.md §5, docs/TUNNEL.md §8).
    /// </summary>
    Task<Lease?> TransitionStateAsync(
        Guid id,
        LeaseStatus status,
        LeaseEndReason? endReason = null,
        DateTimeOffset? startedAt = null,
        DateTimeOffset? lastMeteredAt = null,
        long? billableSeconds = null,
        DateTimeOffset? endedAt = null,
        DateTimeOffset? suspendedAt = null,
        LeaseStatus? expectedCurrentStatus = null,
        CancellationToken ct = default);
}
