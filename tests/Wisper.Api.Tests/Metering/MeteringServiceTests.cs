using Microsoft.Extensions.Logging.Abstractions;
using Wisper.Api.Domain;
using Wisper.Api.Ledger;
using Wisper.Api.Metering;
using Wisper.Api.Persistence.Hosts;
using Wisper.Api.Persistence.Leases;
using Wisper.Api.Persistence.Policy;
using Wisper.Api.Policy;
using Wisper.Api.Tests.TestSupport;
using Xunit;
using Host = Wisper.Api.Domain.Host;

namespace Wisper.Api.Tests.Metering;

/// <summary>
/// Unit tests for <see cref="MeteringService"/> with in-memory lease/usage/ledger doubles (Grunt has no
/// Postgres): accrual over healthy intervals from <c>lease.ready</c>, the 60s tick + on-end flush, the
/// <c>lease_charge</c> fee split into host earnings + platform revenue, idempotency on
/// <c>(lease_id, period_start)</c>, and restart-resume from <c>last_metered_at</c> (docs/DATA_MODEL.md §6,
/// §8, §14, docs/PAYMENTS.md §4).
/// </summary>
public class MeteringServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 12, 0, 0, 0, TimeSpan.Zero);

    // A price of 60¢/min is exactly 1¢/second, so a healthy interval's charge equals its seconds — the
    // arithmetic stays readable while still exercising the ⌊seconds·price/60⌋ cumulative rule.
    private const long PricePerMin = 60;
    private const int FeeBps = 1500; // 15%

    private sealed class Fixture
    {
        public InMemoryLeaseRepository Leases { get; } = new();
        public InMemoryLeaseUsageRepository Usage { get; } = new();
        public InMemoryHostRepository Hosts { get; } = new();
        public InMemoryLedgerStore LedgerStore { get; } = new();
        public InMemoryPlatformPolicyRepository Policies { get; } = new();
        public FakeTimeProvider Clock { get; } = new(T0);

        public LedgerService Ledger { get; }
        public PlatformPolicyService Policy { get; }

        public Guid ConsumerId { get; } = Guid.NewGuid();
        public Guid HostOwnerId { get; } = Guid.NewGuid();
        public Guid HostId { get; private set; }

        public Fixture()
        {
            Ledger = new LedgerService(LedgerStore);
            Policy = new PlatformPolicyService(Policies, Clock);
        }

        public MeteringService NewService() =>
            new(Leases, Usage, Hosts, Ledger, Policy, Clock, NullLogger<MeteringService>.Instance);

        public async Task SeedPolicyAsync(int feeBps = FeeBps) =>
            await Policy.PublishAsync(new PlatformPolicy { FeeBps = feeBps, EffectiveFrom = T0 });

        public async Task SeedHostAsync()
        {
            var host = await Hosts.CreateAsync(new Host
            {
                Id = Guid.NewGuid(),
                OwnerUserId = HostOwnerId,
                Status = HostStatus.Online,
                AgentTokenHash = "hash",
                CreatedAt = T0,
                UpdatedAt = T0,
            });
            HostId = host.Id;
        }

        /// <summary>Seeds an active lease that has just gone <c>ready</c> at <see cref="T0"/>.</summary>
        public async Task<Lease> SeedActiveLeaseAsync(
            LeaseStatus status = LeaseStatus.Active, long price = PricePerMin, int ttlSeconds = 3600) =>
            await Leases.CreateAsync(new Lease
            {
                Id = Guid.NewGuid(),
                ConsumerUserId = ConsumerId,
                HostId = HostId,
                HostImageId = Guid.NewGuid(),
                ImageRef = "reg/wisp-base:latest",
                Network = NetworkMode.Open,
                TtlSeconds = ttlSeconds,
                PriceCentsPerMin = price,
                Currency = "usd",
                Status = status,
                CreatedAt = T0,
                StartedAt = T0,
                LastMeteredAt = T0,
                BillableSeconds = 0,
            });

        /// <summary>Funds the wallet and places the up-front lease hold so lease_holds can be charged.</summary>
        public async Task FundHoldAsync(Guid leaseId, long holdCents)
        {
            var wallet = await Ledger.GetOrCreateAccountAsync(LedgerAccountKind.UserWallet, ConsumerId);
            var cash = await Ledger.GetOrCreateAccountAsync(LedgerAccountKind.PlatformCash, null);
            var fees = await Ledger.GetOrCreateAccountAsync(LedgerAccountKind.StripeFees, null);
            var holds = await Ledger.GetOrCreateAccountAsync(LedgerAccountKind.LeaseHolds, null);

            await Ledger.PostAsync(LedgerFlows.Topup(
                wallet.Id, cash.Id, fees.Id, grossAmountCents: holdCents, stripeFeeCents: 0,
                idempotencyKey: $"topup:{leaseId}"));
            await Ledger.PostAsync(LedgerFlows.LeaseHold(
                wallet.Id, holds.Id, leaseId, holdCents, idempotencyKey: $"hold:{leaseId}"));
        }

        public Task<long> BalanceAsync(LedgerAccountKind kind, Guid? owner = null) =>
            BalanceCoreAsync(kind, owner);

        private async Task<long> BalanceCoreAsync(LedgerAccountKind kind, Guid? owner)
        {
            var account = await Ledger.GetOrCreateAccountAsync(kind, owner);
            return await Ledger.GetBalanceAsync(account.Id);
        }
    }

    private static async Task<Fixture> ReadyFixtureAsync(int feeBps = FeeBps)
    {
        var fx = new Fixture();
        await fx.SeedPolicyAsync(feeBps);
        await fx.SeedHostAsync();
        return fx;
    }

    [Fact]
    public async Task Tick_accrues_a_minute_and_posts_a_split_lease_charge()
    {
        var fx = await ReadyFixtureAsync();
        var lease = await fx.SeedActiveLeaseAsync();
        await fx.FundHoldAsync(lease.Id, holdCents: 3600);

        fx.Clock.Advance(TimeSpan.FromSeconds(60));
        var flushed = await fx.NewService().RunTickAsync();

        Assert.Equal(1, flushed);

        // The lease watermark advanced by exactly the healthy minute.
        var stored = await fx.Leases.GetByIdAsync(lease.Id);
        Assert.Equal(60, stored!.BillableSeconds);
        Assert.Equal(T0.AddSeconds(60), stored.LastMeteredAt);

        // One usage row with the fee split: amount 60¢ → fee 9¢ (15%) + host 51¢.
        var usage = Assert.Single(await fx.Usage.ListByLeaseAsync(lease.Id));
        Assert.Equal(T0, usage.PeriodStart);
        Assert.Equal(T0.AddSeconds(60), usage.PeriodEnd);
        Assert.Equal(60, usage.BillableSeconds);
        Assert.Equal(60, usage.AmountCents);
        Assert.Equal(9, usage.PlatformFeeCents);
        Assert.Equal(51, usage.HostPayoutCents);
        Assert.Equal(usage.AmountCents, usage.PlatformFeeCents + usage.HostPayoutCents);

        // The ledger moved the charge out of the hold and into host earnings + platform revenue.
        Assert.Equal(3600 - 60, await fx.BalanceAsync(LedgerAccountKind.LeaseHolds));
        Assert.Equal(51, await fx.BalanceAsync(LedgerAccountKind.HostEarnings, fx.HostOwnerId));
        Assert.Equal(9, await fx.BalanceAsync(LedgerAccountKind.PlatformRevenue));
    }

    [Fact]
    public async Task Charge_txn_ties_to_the_usage_row_and_is_internal_ledger_only()
    {
        var fx = await ReadyFixtureAsync();
        var lease = await fx.SeedActiveLeaseAsync();
        await fx.FundHoldAsync(lease.Id, holdCents: 3600);

        fx.Clock.Advance(TimeSpan.FromSeconds(60));
        var result = await fx.NewService().FlushLeaseByIdAsync(lease.Id, fx.Clock.GetUtcNow());

        Assert.NotNull(result);
        Assert.False(result!.WasDeduplicated);

        var usage = Assert.Single(await fx.Usage.ListByLeaseAsync(lease.Id));
        Assert.Equal(usage.ChargeTxnId, result.ChargeTxnId);

        // The charge is a lease_charge — no Stripe/topup/payout involvement (docs/PAYMENTS.md §2).
        var txn = await fx.LedgerStore.FindTransactionByIdempotencyKeyAsync(
            MeteringService.ChargeIdempotencyKey(lease.Id, T0));
        Assert.NotNull(txn);
        Assert.Equal(LedgerTxnKind.LeaseCharge, txn!.Kind);
        Assert.Equal(lease.Id, txn.LeaseId);
    }

    [Fact]
    public async Task Successive_ticks_accrue_cumulatively_then_an_on_end_flush_bills_the_partial_minute()
    {
        var fx = await ReadyFixtureAsync();
        var lease = await fx.SeedActiveLeaseAsync();
        await fx.FundHoldAsync(lease.Id, holdCents: 3600);
        var meter = fx.NewService();

        for (var i = 0; i < 3; i++)
        {
            fx.Clock.Advance(TimeSpan.FromSeconds(60));
            await meter.RunTickAsync();
        }

        var afterTicks = await fx.Leases.GetByIdAsync(lease.Id);
        Assert.Equal(180, afterTicks!.BillableSeconds);
        Assert.Equal(3, (await fx.Usage.ListByLeaseAsync(lease.Id)).Count);

        // On end, flush the remaining sub-tick interval (30s) — the on-end flush of the final partial minute.
        fx.Clock.Advance(TimeSpan.FromSeconds(30));
        var end = await meter.FlushLeaseByIdAsync(lease.Id, fx.Clock.GetUtcNow());

        Assert.NotNull(end);
        Assert.Equal(30, end!.Usage.BillableSeconds);
        Assert.Equal(30, end.AmountCents); // 30s @ 1¢/s

        var stored = await fx.Leases.GetByIdAsync(lease.Id);
        Assert.Equal(210, stored!.BillableSeconds);
        Assert.Equal(T0.AddSeconds(210), stored.LastMeteredAt);

        // Total charged 210¢ = ⌊210·60/60⌋; the hold is drawn down by that and never over-drawn.
        Assert.Equal(3600 - 210, await fx.BalanceAsync(LedgerAccountKind.LeaseHolds));
        Assert.Equal(210, await fx.BalanceAsync(LedgerAccountKind.HostEarnings, fx.HostOwnerId)
            + await fx.BalanceAsync(LedgerAccountKind.PlatformRevenue));
    }

    [Fact]
    public async Task Flush_is_idempotent_on_lease_id_and_period_start()
    {
        var fx = await ReadyFixtureAsync();
        var lease = await fx.SeedActiveLeaseAsync();
        await fx.FundHoldAsync(lease.Id, holdCents: 3600);
        var meter = fx.NewService();

        fx.Clock.Advance(TimeSpan.FromSeconds(60));
        var first = await meter.FlushLeaseAsync(lease, fx.Clock.GetUtcNow());
        Assert.NotNull(first);
        Assert.False(first!.WasDeduplicated);

        // Simulate a crash AFTER the charge but BEFORE the watermark persisted: replay the same flush from
        // the original (un-advanced) lease at the same instant. It must move no new money.
        var replay = await meter.FlushLeaseAsync(lease, fx.Clock.GetUtcNow());

        Assert.NotNull(replay);
        Assert.True(replay!.WasDeduplicated);
        Assert.Equal(first.ChargeTxnId, replay.ChargeTxnId);

        // Exactly one usage row and one charge's worth of money moved.
        Assert.Single(await fx.Usage.ListByLeaseAsync(lease.Id));
        Assert.Equal(3600 - 60, await fx.BalanceAsync(LedgerAccountKind.LeaseHolds));
        Assert.Equal(51, await fx.BalanceAsync(LedgerAccountKind.HostEarnings, fx.HostOwnerId));
        Assert.Equal(9, await fx.BalanceAsync(LedgerAccountKind.PlatformRevenue));
    }

    [Fact]
    public async Task Restart_resumes_from_last_metered_at_and_does_not_re_bill()
    {
        var fx = await ReadyFixtureAsync();
        var lease = await fx.SeedActiveLeaseAsync();
        await fx.FundHoldAsync(lease.Id, holdCents: 3600);

        // First minute billed by the pre-restart meter.
        fx.Clock.Advance(TimeSpan.FromSeconds(60));
        await fx.NewService().RunTickAsync();

        // "Restart": a brand-new MeteringService over the SAME repositories (the watermark is durable).
        var afterRestart = fx.NewService();
        fx.Clock.Advance(TimeSpan.FromSeconds(60));
        await afterRestart.RunTickAsync();

        // Two minutes total — the first minute was not re-billed (resume from last_metered_at, §14).
        var stored = await fx.Leases.GetByIdAsync(lease.Id);
        Assert.Equal(120, stored!.BillableSeconds);
        Assert.Equal(T0.AddSeconds(120), stored.LastMeteredAt);

        var rows = await fx.Usage.ListByLeaseAsync(lease.Id);
        Assert.Equal(2, rows.Count);
        Assert.Equal(T0, rows[0].PeriodStart);
        Assert.Equal(T0.AddSeconds(60), rows[1].PeriodStart);

        Assert.Equal(3600 - 120, await fx.BalanceAsync(LedgerAccountKind.LeaseHolds));
        Assert.Equal(102, await fx.BalanceAsync(LedgerAccountKind.HostEarnings, fx.HostOwnerId));
        Assert.Equal(18, await fx.BalanceAsync(LedgerAccountKind.PlatformRevenue));
    }

    [Fact]
    public async Task Suspended_leases_are_not_metered()
    {
        var fx = await ReadyFixtureAsync();
        var lease = await fx.SeedActiveLeaseAsync(status: LeaseStatus.Suspended);
        await fx.FundHoldAsync(lease.Id, holdCents: 3600);

        fx.Clock.Advance(TimeSpan.FromMinutes(5));
        var flushed = await fx.NewService().RunTickAsync();

        Assert.Equal(0, flushed);
        Assert.Empty(await fx.Usage.ListByLeaseAsync(lease.Id));
        Assert.Equal(3600, await fx.BalanceAsync(LedgerAccountKind.LeaseHolds)); // untouched
    }

    [Fact]
    public async Task Sub_cent_interval_defers_until_it_is_worth_a_cent()
    {
        // 30¢/min = 0.5¢/s: a 1-second tick rounds to 0¢ and must NOT advance the watermark (no lost value).
        var fx = await ReadyFixtureAsync();
        var lease = await fx.SeedActiveLeaseAsync(price: 30);
        await fx.FundHoldAsync(lease.Id, holdCents: 1800);
        var meter = fx.NewService();

        fx.Clock.Advance(TimeSpan.FromSeconds(1));
        var afterOneSecond = (await fx.Leases.GetByIdAsync(lease.Id))!;
        Assert.Null(await meter.FlushLeaseAsync(afterOneSecond, fx.Clock.GetUtcNow()));
        Assert.Empty(await fx.Usage.ListByLeaseAsync(lease.Id));
        Assert.Equal(T0, (await fx.Leases.GetByIdAsync(lease.Id))!.LastMeteredAt); // not advanced

        // After a full minute the accumulated seconds are worth 30¢ and flush.
        fx.Clock.Advance(TimeSpan.FromSeconds(59));
        var afterMinute = (await fx.Leases.GetByIdAsync(lease.Id))!;
        var result = await meter.FlushLeaseAsync(afterMinute, fx.Clock.GetUtcNow());
        Assert.NotNull(result);
        Assert.Equal(30, result!.AmountCents);
        Assert.Equal(60, result.Usage.BillableSeconds);
    }

    [Theory]
    [InlineData(0, 0, 0)]        // sub-second / zero
    [InlineData(60, 60, 60)]    // one minute @ 1¢/s
    [InlineData(90, 60, 90)]    // 90s @ 1¢/s
    [InlineData(59, 60, 59)]    // ⌊59·60/60⌋
    [InlineData(30, 30, 15)]    // 30s @ 0.5¢/s → ⌊15⌋
    public void ChargeCentsFor_floors_seconds_at_the_per_minute_price(long seconds, long price, long expected) =>
        Assert.Equal(expected, MeteringService.ChargeCentsFor(seconds, price));
}
