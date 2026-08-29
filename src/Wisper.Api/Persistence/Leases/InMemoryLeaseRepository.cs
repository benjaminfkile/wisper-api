using Wisper.Api.Domain;

namespace Wisper.Api.Persistence.Leases;

/// <summary>
/// In-memory <see cref="ILeaseRepository"/> double for unit tests (Grunt has no Postgres). Semantics
/// mirror the SQL side: <see cref="CreateAsync"/> assigns an id when unset, list queries return the
/// documented ordering/subset, <see cref="UpdateAsync"/> touches only the mutable columns, and
/// <see cref="TransitionStateAsync"/> advances the state plus any supplied timeline fields (a null
/// argument leaves that column unchanged, matching the SQL <c>COALESCE</c>).
/// </summary>
public sealed class InMemoryLeaseRepository : InMemoryRepositoryBase<Guid, Lease>, ILeaseRepository
{
    protected override Guid KeyOf(Lease entity) => entity.Id;

    public Task<Lease?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult(Find(id));

    public Task<IReadOnlyList<Lease>> ListByConsumerAsync(Guid consumerUserId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Lease>>(
            Where(l => l.ConsumerUserId == consumerUserId).OrderByDescending(l => l.CreatedAt).ToList());

    public Task<IReadOnlyList<Lease>> ListActiveByHostAsync(Guid hostId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Lease>>(
            Where(l => l.HostId == hostId &&
                       l.Status is LeaseStatus.Active or LeaseStatus.Suspended)
                .OrderByDescending(l => l.CreatedAt).ToList());

    public Task<int> CountActiveByHostAsync(Guid hostId, CancellationToken ct = default) =>
        Task.FromResult(Where(l => l.HostId == hostId &&
            l.Status is LeaseStatus.Active or LeaseStatus.Suspended).Count());

    public Task<IReadOnlyList<Lease>> ListActiveAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Lease>>(
            Where(l => l.Status == LeaseStatus.Active).OrderBy(l => l.CreatedAt).ToList());

    public Task<bool> HasLeaseForImageAsync(Guid hostImageId, CancellationToken ct = default) =>
        Task.FromResult(FindBy(l => l.HostImageId == hostImageId) is not null);

    public Task<IReadOnlyList<Lease>> ListSuspendedOlderThanAsync(
        DateTimeOffset suspendedOnOrBefore, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Lease>>(
            Where(l => l.Status == LeaseStatus.Suspended &&
                       l.SuspendedAt is { } s && s <= suspendedOnOrBefore)
                .OrderBy(l => l.SuspendedAt!.Value).ToList());

    public Task<IReadOnlyList<Lease>> ListNonTerminalAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Lease>>(
            Where(l => l.Status is LeaseStatus.Active or LeaseStatus.Suspended)
                .OrderBy(l => l.CreatedAt).ToList());

    public Task<Lease> CreateAsync(Lease lease, CancellationToken ct = default)
    {
        var stored = lease.Id == Guid.Empty ? lease with { Id = Guid.NewGuid() } : lease;
        Insert(stored);
        return Task.FromResult(stored);
    }

    public Task<Lease> UpdateAsync(Lease lease, CancellationToken ct = default)
    {
        if (Find(lease.Id) is not { } existing)
        {
            throw new InvalidOperationException($"Lease '{lease.Id}' does not exist.");
        }

        // Only the mutable columns change; the snapshots, identity and created_at stay as stored.
        var updated = existing with
        {
            Status = lease.Status,
            EndReason = lease.EndReason,
            WispContractId = lease.WispContractId,
            HoldTxnId = lease.HoldTxnId,
            StartedAt = lease.StartedAt,
            LastMeteredAt = lease.LastMeteredAt,
            BillableSeconds = lease.BillableSeconds,
            EndedAt = lease.EndedAt,
            SuspendedAt = lease.SuspendedAt,
        };
        Upsert(updated);
        return Task.FromResult(updated);
    }

    public Task<Lease?> TransitionStateAsync(
        Guid id,
        LeaseStatus status,
        LeaseEndReason? endReason = null,
        DateTimeOffset? startedAt = null,
        DateTimeOffset? lastMeteredAt = null,
        long? billableSeconds = null,
        DateTimeOffset? endedAt = null,
        DateTimeOffset? suspendedAt = null,
        LeaseStatus? expectedCurrentStatus = null,
        CancellationToken ct = default)
    {
        if (Find(id) is not { } lease)
        {
            return Task.FromResult<Lease?>(null);
        }

        // Conditional-update guard mirrors the SQL side (task #55): the sweep uses this so two concurrent
        // instances cannot both drive the same suspended → ended transition. A miss returns null (no row).
        if (expectedCurrentStatus is { } expected && lease.Status != expected)
        {
            return Task.FromResult<Lease?>(null);
        }

        // suspended_at is only meaningful while status = 'suspended' (task #55): a transition to any other
        // status auto-clears it; a transition into suspended sets it (or keeps the existing value when the
        // caller passes null -- idempotent re-suspend keeps the original moment).
        var nextSuspendedAt = status == LeaseStatus.Suspended
            ? suspendedAt ?? lease.SuspendedAt
            : (DateTimeOffset?)null;

        var updated = lease with
        {
            Status = status,
            EndReason = endReason ?? lease.EndReason,
            StartedAt = startedAt ?? lease.StartedAt,
            LastMeteredAt = lastMeteredAt ?? lease.LastMeteredAt,
            BillableSeconds = billableSeconds ?? lease.BillableSeconds,
            EndedAt = endedAt ?? lease.EndedAt,
            SuspendedAt = nextSuspendedAt,
        };
        Upsert(updated);
        return Task.FromResult<Lease?>(updated);
    }
}
