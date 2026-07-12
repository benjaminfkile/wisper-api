using Dapper;
using Wisper.Api.Domain;

namespace Wisper.Api.Persistence.Leases;

/// <summary>
/// Dapper + explicit-SQL <see cref="ILeaseUsageRepository"/> over Postgres (docs/DATA_MODEL.md §6).
/// <see cref="AppendAsync"/> uses <c>INSERT … ON CONFLICT (lease_id, period_start) DO NOTHING</c> so a
/// retried flush is a no-op; on conflict it reads back the existing row. The <c>charge_txn_id</c> FK to
/// <c>ledger_transactions</c> is added in P2.4 (circular dependency). Not exercised by the unit suite
/// (Grunt has no Postgres); the in-memory double is.
/// </summary>
public sealed class LeaseUsageRepository : RepositoryBase, ILeaseUsageRepository
{
    private const string Columns =
        "id, lease_id, period_start, period_end, billable_seconds, amount_cents, platform_fee_cents, " +
        "host_payout_cents, charge_txn_id, created_at";

    public LeaseUsageRepository(Db db) : base(db)
    {
    }

    public async Task<LeaseUsage> AppendAsync(LeaseUsage usage, CancellationToken ct = default)
    {
        const string sql = $"""
            INSERT INTO lease_usage (id, lease_id, period_start, period_end, billable_seconds, amount_cents,
                                     platform_fee_cents, host_payout_cents, charge_txn_id, created_at)
            VALUES (COALESCE(@Id, gen_random_uuid()), @LeaseId, @PeriodStart, @PeriodEnd, @BillableSeconds,
                    @AmountCents, @PlatformFeeCents, @HostPayoutCents, @ChargeTxnId, COALESCE(@CreatedAt, now()))
            ON CONFLICT (lease_id, period_start) DO NOTHING
            RETURNING {Columns}
            """;

        await using var conn = await OpenConnectionAsync(ct);
        var inserted = await conn.QuerySingleOrDefaultAsync<LeaseUsage>(
            new CommandDefinition(sql, ToParameters(usage), cancellationToken: ct));
        // On conflict the insert returns nothing; the existing row is the idempotent result.
        return inserted
               ?? await GetByLeaseAndPeriodAsync(usage.LeaseId, usage.PeriodStart, ct)
               ?? throw new InvalidOperationException(
                   $"lease_usage row for ({usage.LeaseId}, {usage.PeriodStart:o}) vanished after conflict.");
    }

    public async Task<LeaseUsage?> GetByLeaseAndPeriodAsync(
        Guid leaseId, DateTimeOffset periodStart, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<LeaseUsage>(new CommandDefinition(
            $"SELECT {Columns} FROM lease_usage WHERE lease_id = @leaseId AND period_start = @periodStart",
            new { leaseId, periodStart }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<LeaseUsage>> ListByLeaseAsync(Guid leaseId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<LeaseUsage>(new CommandDefinition(
            $"SELECT {Columns} FROM lease_usage WHERE lease_id = @leaseId ORDER BY period_start",
            new { leaseId }, cancellationToken: ct));
        return rows.ToList();
    }

    /// <summary>Projects a <see cref="LeaseUsage"/> to SQL parameters.</summary>
    private static object ToParameters(LeaseUsage usage) => new
    {
        Id = usage.Id == Guid.Empty ? (Guid?)null : usage.Id,
        usage.LeaseId,
        usage.PeriodStart,
        usage.PeriodEnd,
        usage.BillableSeconds,
        usage.AmountCents,
        usage.PlatformFeeCents,
        usage.HostPayoutCents,
        usage.ChargeTxnId,
        CreatedAt = usage.CreatedAt == default ? (DateTimeOffset?)null : usage.CreatedAt,
    };
}
