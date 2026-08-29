using Wisper.Api.Domain;

namespace Wisper.Api.Persistence.Leases;

/// <summary>
/// In-memory <see cref="ILeaseUsageRepository"/> double for unit tests (Grunt has no Postgres).
/// Semantics mirror the SQL side: <see cref="AppendAsync"/> is idempotent on
/// <c>(lease_id, period_start)</c> -- a second append for the same key returns the first-written row
/// unchanged instead of inserting a duplicate (mirroring <c>ON CONFLICT DO NOTHING</c>).
/// </summary>
public sealed class InMemoryLeaseUsageRepository : InMemoryRepositoryBase<Guid, LeaseUsage>, ILeaseUsageRepository
{
    protected override Guid KeyOf(LeaseUsage entity) => entity.Id;

    public Task<LeaseUsage> AppendAsync(LeaseUsage usage, CancellationToken ct = default)
    {
        if (FindBy(u => u.LeaseId == usage.LeaseId && u.PeriodStart == usage.PeriodStart) is { } existing)
        {
            return Task.FromResult(existing);
        }

        var stored = usage.Id == Guid.Empty ? usage with { Id = Guid.NewGuid() } : usage;
        Insert(stored);
        return Task.FromResult(stored);
    }

    public Task<LeaseUsage?> GetByLeaseAndPeriodAsync(
        Guid leaseId, DateTimeOffset periodStart, CancellationToken ct = default) =>
        Task.FromResult(FindBy(u => u.LeaseId == leaseId && u.PeriodStart == periodStart));

    public Task<IReadOnlyList<LeaseUsage>> ListByLeaseAsync(Guid leaseId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<LeaseUsage>>(
            Where(u => u.LeaseId == leaseId).OrderBy(u => u.PeriodStart).ToList());
}
