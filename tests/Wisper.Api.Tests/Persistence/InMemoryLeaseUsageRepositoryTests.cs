using System.Threading.Tasks;
using Wisper.Api.Domain;
using Wisper.Api.Persistence.Leases;
using Xunit;

namespace Wisper.Api.Tests.Persistence;

/// <summary>
/// Contract tests for <see cref="ILeaseUsageRepository"/> against the in-memory double (Grunt has no
/// Postgres). Covers append + round-trip of a flushed metering interval, per-lease ordering, and the
/// idempotency on <c>(lease_id, period_start)</c> that lets a retried flush be a no-op
/// (docs/DATA_MODEL.md §6, §14).
/// </summary>
public class InMemoryLeaseUsageRepositoryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 12, 0, 0, 0, TimeSpan.Zero);

    private static LeaseUsage NewUsage(Guid lease, DateTimeOffset periodStart, long amount = 120) => new()
    {
        LeaseId = lease,
        PeriodStart = periodStart,
        PeriodEnd = periodStart.AddSeconds(60),
        BillableSeconds = 60,
        AmountCents = amount,
        PlatformFeeCents = amount * 15 / 100,
        HostPayoutCents = amount - amount * 15 / 100,
        ChargeTxnId = Guid.NewGuid(),
    };

    [Fact]
    public async Task Append_assigns_id_and_round_trips()
    {
        var repo = new InMemoryLeaseUsageRepository();
        var lease = Guid.NewGuid();

        var appended = await repo.AppendAsync(NewUsage(lease, T0));

        Assert.NotEqual(Guid.Empty, appended.Id);
        Assert.Equal(60, appended.BillableSeconds);
        Assert.Equal(appended.AmountCents, appended.PlatformFeeCents + appended.HostPayoutCents);
        Assert.Equal(appended, await repo.GetByLeaseAndPeriodAsync(lease, T0));
    }

    [Fact]
    public async Task Append_is_idempotent_on_lease_and_period_start()
    {
        var repo = new InMemoryLeaseUsageRepository();
        var lease = Guid.NewGuid();

        var first = await repo.AppendAsync(NewUsage(lease, T0, amount: 120));
        // A retried flush for the same interval — even with different figures — is a no-op.
        var retry = await repo.AppendAsync(NewUsage(lease, T0, amount: 999) with { Id = Guid.NewGuid() });

        Assert.Equal(first.Id, retry.Id);
        Assert.Equal(120, retry.AmountCents);
        Assert.Single(await repo.ListByLeaseAsync(lease));
    }

    [Fact]
    public async Task ListByLease_scopes_and_orders_by_period_start()
    {
        var repo = new InMemoryLeaseUsageRepository();
        var lease = Guid.NewGuid();
        await repo.AppendAsync(NewUsage(lease, T0.AddMinutes(2)));
        await repo.AppendAsync(NewUsage(lease, T0));
        await repo.AppendAsync(NewUsage(lease, T0.AddMinutes(1)));
        await repo.AppendAsync(NewUsage(Guid.NewGuid(), T0));

        var rows = await repo.ListByLeaseAsync(lease);

        Assert.Equal(
            new[] { T0, T0.AddMinutes(1), T0.AddMinutes(2) },
            rows.Select(r => r.PeriodStart).ToArray());
    }

    [Fact]
    public async Task Missing_lookups_return_null_or_empty()
    {
        var repo = new InMemoryLeaseUsageRepository();
        Assert.Null(await repo.GetByLeaseAndPeriodAsync(Guid.NewGuid(), T0));
        Assert.Empty(await repo.ListByLeaseAsync(Guid.NewGuid()));
    }
}
