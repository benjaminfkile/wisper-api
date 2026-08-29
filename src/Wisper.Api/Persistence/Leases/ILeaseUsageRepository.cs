using Wisper.Api.Domain;

namespace Wisper.Api.Persistence.Leases;

/// <summary>
/// Data access for <see cref="LeaseUsage"/> rows -- the flushed metering intervals (docs/DATA_MODEL.md
/// §6). The meter appends one row per tick and on lease end; <see cref="AppendAsync"/> is idempotent on
/// <c>(lease_id, period_start)</c> so a retried flush can't double-charge (§14). A Dapper +
/// explicit-SQL implementation backs Postgres; an in-memory double backs the unit suite.
/// </summary>
public interface ILeaseUsageRepository : IRepository
{
    /// <summary>
    /// Appends a usage interval, idempotent on <c>(lease_id, period_start)</c>: if a row for that key
    /// already exists it is returned unchanged (first-writer-wins) and nothing new is inserted, so a
    /// retried flush is a no-op. Returns the persisted row.
    /// </summary>
    Task<LeaseUsage> AppendAsync(LeaseUsage usage, CancellationToken ct = default);

    /// <summary>Gets the usage row for <c>(lease_id, period_start)</c>, or <c>null</c> if none.</summary>
    Task<LeaseUsage?> GetByLeaseAndPeriodAsync(
        Guid leaseId, DateTimeOffset periodStart, CancellationToken ct = default);

    /// <summary>The lease's usage intervals, oldest first.</summary>
    Task<IReadOnlyList<LeaseUsage>> ListByLeaseAsync(Guid leaseId, CancellationToken ct = default);
}
