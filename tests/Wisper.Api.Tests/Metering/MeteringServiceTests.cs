using Microsoft.Extensions.Logging.Abstractions;
using Wisper.Api.Domain;
using Wisper.Api.Ledger;
using Wisper.Api.Leases;
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

    // A price of 60¢/min is exactly 1¢/second, so a healthy interval's charge equals its seconds -- the
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

        // The charge is a lease_charge -- no Stripe/topup/payout involvement (docs/PAYMENTS.md §2).
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

        // On end, flush the remaining sub-tick interval (30s) -- the on-end flush of the final partial minute.
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

        // Two minutes total -- the first minute was not re-billed (resume from last_metered_at, §14).
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

    [Fact]
    public async Task Zero_price_lease_meters_ledger_safely_writing_no_charge_or_usage_row()
    {
        // A free image (price 0): the meter still accrues healthy seconds so the timeline is correct, but the
        // charge is 0¢, so no lease_charge txn and no lease_usage row are written -- a 0=0 ledger txn would be
        // vacuous and is skipped (docs/PAYMENTS.md §4, docs/DATA_MODEL.md §8). No hold was ever placed.
        var fx = await ReadyFixtureAsync();
        var lease = await fx.SeedActiveLeaseAsync(price: 0);
        // Deliberately do NOT fund a hold: a free lease needs no wallet money at all.

        fx.Clock.Advance(TimeSpan.FromSeconds(90));
        var flushed = await fx.NewService().RunTickAsync();

        Assert.Equal(0, flushed); // no billable flush -- nothing to charge

        // The watermark still advances (billable_seconds accrue) so a later paid image can't rely on drift.
        var stored = await fx.Leases.GetByIdAsync(lease.Id);
        Assert.Equal(90, stored!.BillableSeconds);
        Assert.Equal(T0.AddSeconds(90), stored.LastMeteredAt);

        // No money moved anywhere: no usage row, and every touched account is flat at zero.
        Assert.Empty(await fx.Usage.ListByLeaseAsync(lease.Id));
        Assert.Equal(0, await fx.BalanceAsync(LedgerAccountKind.LeaseHolds));
        Assert.Equal(0, await fx.BalanceAsync(LedgerAccountKind.HostEarnings, fx.HostOwnerId));
        Assert.Equal(0, await fx.BalanceAsync(LedgerAccountKind.PlatformRevenue));
        Assert.Equal(0, await fx.BalanceAsync(LedgerAccountKind.UserWallet, fx.ConsumerId));

        // The ledger is internally consistent (no corruption / drift) after a zero-price tick.
        foreach (var account in await fx.Ledger.ReconcileAsync())
        {
            Assert.True(account.IsBalanced);
            Assert.True(account.MaintainedBalanceCents >= 0); // no account driven negative
        }
    }

    [Fact]
    public async Task Zero_price_on_end_flush_returns_null_and_moves_no_money()
    {
        var fx = await ReadyFixtureAsync();
        var lease = await fx.SeedActiveLeaseAsync(price: 0);
        fx.Clock.Advance(TimeSpan.FromSeconds(45));

        // The on-end flush of the final (sub-tick) interval is a no-op for a free lease.
        var end = await fx.NewService().FlushLeaseByIdAsync(lease.Id, fx.Clock.GetUtcNow());

        Assert.Null(end);
        Assert.Equal(45, (await fx.Leases.GetByIdAsync(lease.Id))!.BillableSeconds); // seconds still accrued
        Assert.Empty(await fx.Usage.ListByLeaseAsync(lease.Id));
        Assert.Equal(0, await fx.BalanceAsync(LedgerAccountKind.LeaseHolds));
    }

    [Fact]
    public async Task FinalizeLease_caps_the_flush_watermark_at_started_at_plus_ttl()
    {
        // Task #34: the on-end finalization must cap at ttl even when `now` is past it -- the lease was
        // not entitled to run past its TTL, so a release / TTL-expiry / container-lost finalize called
        // some seconds after TTL must still only bill up to started_at + ttl_seconds.
        var fx = await ReadyFixtureAsync();
        var lease = await fx.SeedActiveLeaseAsync(ttlSeconds: 180);
        await fx.FundHoldAsync(lease.Id, holdCents: 180);

        fx.Clock.Advance(TimeSpan.FromSeconds(220)); // 40s past TTL
        var end = await fx.NewService().FinalizeLeaseAsync(lease.Id, fx.Clock.GetUtcNow());

        Assert.NotNull(end);
        Assert.Equal(180, end!.Usage.BillableSeconds);
        Assert.Equal(T0.AddSeconds(180), end.Usage.PeriodEnd);
        var stored = await fx.Leases.GetByIdAsync(lease.Id);
        Assert.Equal(180, stored!.BillableSeconds);
    }

    [Fact]
    public async Task Ticks_never_advance_billable_time_or_charge_past_started_at_plus_ttl()
    {
        // Task #54 (billing integrity): if a host keeps reporting a lease past its TTL -- wisp's reaper
        // kills at TTL but the agent only notices on a 30s poll, and today's agent can report stale lease
        // lists -- the tick MUST apply the same TTL cap the finalize path applies (started_at + ttl_seconds).
        // Without that cap the tick would keep charging every 60s past TTL, burn the entire hold, then
        // throw InsufficientFunds on lease_holds and the tick would error forever while the lease stayed
        // active; the later TTL-capped finalize would land behind the runaway watermark and short-circuit
        // to null, leaving the overcharge un-corrected.
        var fx = await ReadyFixtureAsync();
        var lease = await fx.SeedActiveLeaseAsync(ttlSeconds: 120); // 2min → hold = ⌈120/60⌉·60 = 120¢
        await fx.FundHoldAsync(lease.Id, holdCents: 120);
        var meter = fx.NewService();

        // Ten ticks of 60s each: two are within TTL (bill 60¢ each), the remaining eight are entirely past
        // TTL and must be structurally no-ops (no charge, no ledger post, no watermark advance).
        for (var i = 0; i < 10; i++)
        {
            fx.Clock.Advance(TimeSpan.FromSeconds(60));
            await meter.RunTickAsync(); // must never throw InsufficientFunds
        }

        var stored = await fx.Leases.GetByIdAsync(lease.Id);

        // The watermark never advanced past the TTL cap …
        Assert.Equal(T0.AddSeconds(120), stored!.LastMeteredAt);
        Assert.Equal(120, stored.BillableSeconds);

        // … and total posted lease_charge never exceeded the up-front hold.
        var totalCharged = (await fx.Usage.ListByLeaseAsync(lease.Id)).Sum(u => u.AmountCents);
        Assert.Equal(120, totalCharged);
        Assert.True(totalCharged <= 120, $"total charged {totalCharged}¢ must not exceed the 120¢ hold");
        Assert.Equal(0, await fx.BalanceAsync(LedgerAccountKind.LeaseHolds)); // hold fully but exactly drawn

        // A subsequent finalize past TTL must be a no-op -- the tick already billed up to TTL, so
        // elapsedSeconds is 0 and no double-charge lands.
        var end = await meter.FinalizeLeaseAsync(lease.Id, fx.Clock.GetUtcNow());
        Assert.Null(end);
        Assert.Equal(120, (await fx.Leases.GetByIdAsync(lease.Id))!.BillableSeconds);
    }

    [Theory]
    [InlineData(0, 0, 0)]        // sub-second / zero
    [InlineData(60, 60, 60)]    // one minute @ 1¢/s
    [InlineData(90, 60, 90)]    // 90s @ 1¢/s
    [InlineData(59, 60, 59)]    // ⌊59·60/60⌋
    [InlineData(30, 30, 15)]    // 30s @ 0.5¢/s → ⌊15⌋
    public void ChargeCentsFor_floors_seconds_at_the_per_minute_price(long seconds, long price, long expected) =>
        Assert.Equal(expected, MeteringService.ChargeCentsFor(seconds, price));

    [Fact]
    public async Task Lease_view_running_cost_equals_the_sum_of_posted_lease_charge_at_every_tick()
    {
        // Reproduces the wisper-api-dev finding (task #35, 2026-08-12): at billable_seconds=137 on a
        // 60¢/min lease the ledger charged 60+60+17=137¢ but a whole-minute display would show 120¢.
        // The read-side CostCentsSoFar must share the meter's per-second formula so the two never diverge.
        var fx = await ReadyFixtureAsync();
        var lease = await fx.SeedActiveLeaseAsync();
        await fx.FundHoldAsync(lease.Id, holdCents: 3600);
        var meter = fx.NewService();

        // Tick 1 -- 60s billed, 60¢ posted; display and ledger agree at whole-minute boundary.
        fx.Clock.Advance(TimeSpan.FromSeconds(60));
        await meter.RunTickAsync();
        await AssertViewMatchesLedgerAsync(fx, lease.Id, expectedSeconds: 60, expectedCharge: 60);

        // Tick 2 -- another 60s billed; whole-minute rounding still happens to agree at 120s.
        fx.Clock.Advance(TimeSpan.FromSeconds(60));
        await meter.RunTickAsync();
        await AssertViewMatchesLedgerAsync(fx, lease.Id, expectedSeconds: 120, expectedCharge: 120);

        // Final flush of the partial 17s -- this is where whole-minute display used to lie:
        // ⌊137/60⌋·60 = 120¢ shown, but the ledger has actually charged 137¢.
        fx.Clock.Advance(TimeSpan.FromSeconds(17));
        await meter.FlushLeaseByIdAsync(lease.Id, fx.Clock.GetUtcNow());
        await AssertViewMatchesLedgerAsync(fx, lease.Id, expectedSeconds: 137, expectedCharge: 137);
    }

    private static async Task AssertViewMatchesLedgerAsync(
        Fixture fx, Guid leaseId, long expectedSeconds, long expectedCharge)
    {
        var stored = await fx.Leases.GetByIdAsync(leaseId);
        Assert.Equal(expectedSeconds, stored!.BillableSeconds);

        var chargedByLedger = (await fx.Usage.ListByLeaseAsync(leaseId)).Sum(u => u.AmountCents);
        Assert.Equal(expectedCharge, chargedByLedger);

        var view = LeaseView.From(stored);
        Assert.Equal(chargedByLedger, view.CostCentsSoFar);
        Assert.Equal(MeteringService.ChargeCentsFor(stored.BillableSeconds, stored.PriceCentsPerMin),
            view.CostCentsSoFar);
    }

    [Fact]
    public async Task RunTick_isolates_a_failing_lease_and_still_flushes_the_next_one()
    {
        // Task #202: a single lease that throws during flush must NOT abort the whole tick; the loop
        // must continue with the next lease. Before the fix, an unhandled throw in FlushLeaseAsync
        // (an unknown host row, a transient ledger error, etc.) leaked out of RunTickAsync and left
        // every LATER lease unflushed for the tick interval. Money safety is unchanged: the failing
        // lease's watermark stays put (the ledger post never ran) and its next tick retries the same
        // interval, so no charge is lost.
        var fx = await ReadyFixtureAsync();

        // Seed the throwing lease FIRST (older CreatedAt is ordered first by ListActiveAsync). It
        // references a host id that is NOT in the hosts repository, so FlushLeaseAsync's
        // `_hosts.GetByIdAsync ?? throw` fires: a realistic proxy for "one lease's dependencies
        // are momentarily broken."
        var missingHostId = Guid.NewGuid();
        var failingLease = await fx.Leases.CreateAsync(new Lease
        {
            Id = Guid.NewGuid(),
            ConsumerUserId = fx.ConsumerId,
            HostId = missingHostId,
            HostImageId = Guid.NewGuid(),
            ImageRef = "reg/wisp-base:latest",
            Network = NetworkMode.Open,
            TtlSeconds = 3600,
            PriceCentsPerMin = PricePerMin,
            Currency = "usd",
            Status = LeaseStatus.Active,
            CreatedAt = T0.AddSeconds(-1),
            StartedAt = T0,
            LastMeteredAt = T0,
            BillableSeconds = 0,
        });
        await fx.FundHoldAsync(failingLease.Id, holdCents: 3600);

        var goodLease = await fx.SeedActiveLeaseAsync();
        await fx.FundHoldAsync(goodLease.Id, holdCents: 3600);

        fx.Clock.Advance(TimeSpan.FromSeconds(60));

        // The tick must NOT throw: the failing lease is logged and the loop proceeds. The good
        // lease flushes normally.
        var flushed = await fx.NewService().RunTickAsync();
        Assert.Equal(1, flushed);

        // Failing lease: watermark unchanged (money-safety preserved), no usage row.
        var stillBroken = await fx.Leases.GetByIdAsync(failingLease.Id);
        Assert.Equal(0, stillBroken!.BillableSeconds);
        Assert.Equal(T0, stillBroken.LastMeteredAt);
        Assert.Empty(await fx.Usage.ListByLeaseAsync(failingLease.Id));

        // Good lease: flushed for the healthy minute.
        var stored = await fx.Leases.GetByIdAsync(goodLease.Id);
        Assert.Equal(60, stored!.BillableSeconds);
        Assert.Equal(T0.AddSeconds(60), stored.LastMeteredAt);
        var usage = Assert.Single(await fx.Usage.ListByLeaseAsync(goodLease.Id));
        Assert.Equal(60, usage.AmountCents);
    }

    [Fact]
    public async Task Finalize_falls_back_to_latest_policy_when_no_row_is_active_yet()
    {
        // Task #202: if the active-policy lookup returns null but a policy row exists (e.g. only a
        // future-dated version, or an operator error) the finalize path must NOT throw. It charges
        // against the newest version regardless of effective_from and logs the stale-fallback event.
        // Without this fallback the finalize driver aborts, the lease stays active with its hold
        // parked, and the consumer's DELETE cannot end it.
        var fx = new Fixture();
        await fx.SeedHostAsync();

        // Publish a policy whose effective_from is far in the future: GetActiveAsync at T0 returns
        // null, but ListVersionsAsync still returns the row (newest first).
        var futurePolicy = await fx.Policy.PublishAsync(new PlatformPolicy
        {
            FeeBps = 2000, // 20%: chosen to be distinct from other tests' 15%
            EffectiveFrom = T0.AddYears(10),
        });

        var lease = await fx.SeedActiveLeaseAsync();
        await fx.FundHoldAsync(lease.Id, holdCents: 3600);

        fx.Clock.Advance(TimeSpan.FromSeconds(60));
        var result = await fx.NewService().FinalizeLeaseAsync(lease.Id, fx.Clock.GetUtcNow());

        // Finalize completed rather than throwing, and it split the charge against the fallback
        // version (20% fee ⇒ 12¢ platform + 48¢ host on a 60¢ charge).
        Assert.NotNull(result);
        Assert.Equal(60, result!.AmountCents);
        Assert.Equal(12, result.Usage.PlatformFeeCents);
        Assert.Equal(48, result.Usage.HostPayoutCents);

        // The lease is ready to be ended by the driver: watermark advanced, hold drawn down by the
        // charged amount, and the ReleaseHoldAsync path can now return the remainder cleanly.
        var stored = await fx.Leases.GetByIdAsync(lease.Id);
        Assert.Equal(60, stored!.BillableSeconds);
        Assert.Equal(T0.AddSeconds(60), stored.LastMeteredAt);
        Assert.Equal(3600 - 60, await fx.BalanceAsync(LedgerAccountKind.LeaseHolds));
        Assert.Equal(48, await fx.BalanceAsync(LedgerAccountKind.HostEarnings, fx.HostOwnerId));
        Assert.Equal(12, await fx.BalanceAsync(LedgerAccountKind.PlatformRevenue));

        // Sanity: the fallback did use the row we published, not some phantom default.
        Assert.Equal(2000, futurePolicy.FeeBps);
    }

    [Fact]
    public async Task Finalize_when_no_policy_row_exists_at_all_returns_null_and_moves_no_money()
    {
        // Task #202: the truly-no-policy branch (impossible after migration 0017 but kept as a
        // guard). FinalizeLeaseAsync must NOT throw. It logs the billing-integrity alert, skips
        // the lease_charge, and returns null so the finalize driver's transition-to-ended and
        // ReleaseHoldAsync still run. Because no charge was posted, the release returns the full
        // hold to the wallet: no unbilled runtime is charged to the consumer and no lease is
        // stranded with a parked hold.
        var fx = new Fixture();
        await fx.SeedHostAsync();
        // Deliberately do NOT publish any policy: Policies is empty.

        var lease = await fx.SeedActiveLeaseAsync();
        await fx.FundHoldAsync(lease.Id, holdCents: 3600);

        fx.Clock.Advance(TimeSpan.FromSeconds(60));
        var result = await fx.NewService().FinalizeLeaseAsync(lease.Id, fx.Clock.GetUtcNow());

        // No charge could be split, so the flush returns null (rather than throwing).
        Assert.Null(result);

        // Watermark did not advance (nothing was posted); no usage row was written; the hold is
        // still fully earmarked in lease_holds (the driver's own ReleaseHoldAsync will return the
        // full 3600¢ to the wallet once the lease transitions to ended).
        var stored = await fx.Leases.GetByIdAsync(lease.Id);
        Assert.Equal(0, stored!.BillableSeconds);
        Assert.Equal(T0, stored.LastMeteredAt);
        Assert.Empty(await fx.Usage.ListByLeaseAsync(lease.Id));
        Assert.Equal(3600, await fx.BalanceAsync(LedgerAccountKind.LeaseHolds));
        Assert.Equal(0, await fx.BalanceAsync(LedgerAccountKind.HostEarnings, fx.HostOwnerId));
        Assert.Equal(0, await fx.BalanceAsync(LedgerAccountKind.PlatformRevenue));
    }
}
