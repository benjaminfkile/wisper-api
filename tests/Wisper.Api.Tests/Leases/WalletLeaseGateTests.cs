using Microsoft.Extensions.Logging.Abstractions;
using Wisper.Api.Domain;
using Wisper.Api.Infrastructure;
using Wisper.Api.Leases;
using Wisper.Api.Ledger;
using Wisper.Api.Persistence.Leases;
using Wisper.Api.Persistence.Policy;
using Wisper.Api.Policy;
using Wisper.Api.Tests.TestSupport;
using Xunit;

namespace Wisper.Api.Tests.Leases;

/// <summary>
/// Unit tests for <see cref="WalletLeaseGate"/> — the real wallet-hold lifecycle (docs/PAYMENTS.md §4,
/// docs/DATA_MODEL.md §8) — against the in-memory ledger (Grunt has no Postgres): the balance hard gate,
/// the per-user concurrency cap, the <c>lease_hold</c> earmark, and the <c>hold_release</c> remainder at
/// lease end. The invariants under test: a hold gates on balance; charges debit the hold; release returns
/// exactly the unused remainder; and because the hold covers the whole max lease, it can never be
/// exhausted mid-run.
/// </summary>
public class WalletLeaseGateTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 12, 0, 0, 0, TimeSpan.Zero);

    private sealed class Fixture
    {
        public InMemoryLeaseRepository Leases { get; } = new();
        public InMemoryLedgerStore LedgerStore { get; } = new();
        public InMemoryPlatformPolicyRepository Policies { get; } = new();
        public FakeTimeProvider Clock { get; } = new(T0);

        public LedgerService Ledger { get; }
        public PlatformPolicyService Policy { get; }
        public WalletLeaseGate Gate { get; }

        public Guid ConsumerId { get; } = Guid.NewGuid();
        public Guid HostOwnerId { get; } = Guid.NewGuid();
        public Guid HostId { get; } = Guid.NewGuid();

        public Fixture()
        {
            Ledger = new LedgerService(LedgerStore);
            Policy = new PlatformPolicyService(Policies, Clock);
            var fraud = new FraudGuardService(
                Ledger, Leases, Policy, Clock, NullLogger<FraudGuardService>.Instance);
            Gate = new WalletLeaseGate(Ledger, Leases, Policy, fraud, NullLogger<WalletLeaseGate>.Instance);
        }

        public Task PublishCapAsync(int cap) =>
            Policy.PublishAsync(new PlatformPolicy { FeeBps = 1500, MaxConcurrentLeasesPerUser = cap, EffectiveFrom = T0 });

        public Task PublishSpendCapAsync(long capCents) =>
            Policy.PublishAsync(new PlatformPolicy { FeeBps = 1500, MaxSpendCentsPerDay = capCents, EffectiveFrom = T0 });

        public async Task FundWalletAsync(long cents)
        {
            var wallet = await Ledger.GetOrCreateAccountAsync(LedgerAccountKind.UserWallet, ConsumerId);
            var cash = await Ledger.GetOrCreateAccountAsync(LedgerAccountKind.PlatformCash, null);
            var fees = await Ledger.GetOrCreateAccountAsync(LedgerAccountKind.StripeFees, null);
            await Ledger.PostAsync(LedgerFlows.Topup(
                wallet.Id, cash.Id, fees.Id, grossAmountCents: cents, stripeFeeCents: 0,
                idempotencyKey: $"topup:{Guid.NewGuid():N}"));
        }

        public async Task<Lease> SeedLeaseAsync(
            LeaseStatus status = LeaseStatus.Active,
            int ttlSeconds = 3600,
            long price = 5,
            long billableSeconds = 0)
        {
            return await Leases.CreateAsync(new Lease
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
                BillableSeconds = billableSeconds,
            });
        }

        /// <summary>Bills <paramref name="amountCents"/> out of the hold (host earnings, no fee) — the meter's effect.</summary>
        public async Task ChargeAsync(Guid leaseId, long amountCents)
        {
            var holds = await Ledger.GetOrCreateAccountAsync(LedgerAccountKind.LeaseHolds, null);
            var earnings = await Ledger.GetOrCreateAccountAsync(LedgerAccountKind.HostEarnings, HostOwnerId);
            var revenue = await Ledger.GetOrCreateAccountAsync(LedgerAccountKind.PlatformRevenue, null);
            await Ledger.PostAsync(LedgerFlows.LeaseCharge(
                holds.Id, earnings.Id, revenue.Id, leaseId, amountCents, platformFeeCents: 0,
                idempotencyKey: $"charge:{leaseId:N}:{Guid.NewGuid():N}"));
        }

        public Task<long> WalletCentsAsync() => Ledger.GetWalletBalanceCentsAsync(ConsumerId);

        public async Task<long> HoldsCentsAsync()
        {
            var holds = await Ledger.GetOrCreateAccountAsync(LedgerAccountKind.LeaseHolds, null);
            return holds.BalanceCents;
        }
    }

    [Fact]
    public async Task Authorize_allows_when_the_wallet_covers_the_hold()
    {
        var fx = new Fixture();
        await fx.FundWalletAsync(300);

        var decision = await fx.Gate.AuthorizeHoldAsync(fx.ConsumerId, holdCents: 300, "usd");

        Assert.True(decision.Allowed);
    }

    [Fact]
    public async Task Authorize_denies_and_reports_the_shortfall_when_the_wallet_is_short()
    {
        var fx = new Fixture();
        await fx.FundWalletAsync(299);

        var decision = await fx.Gate.AuthorizeHoldAsync(fx.ConsumerId, holdCents: 300, "usd");

        Assert.False(decision.Allowed);
        Assert.Equal(300, decision.RequiredCents);
        Assert.Equal(299, decision.AvailableCents); // the exact figures the 402 envelope reports
    }

    [Fact]
    public async Task Authorize_allows_a_free_image_with_a_zero_hold_and_an_empty_wallet()
    {
        var fx = new Fixture();

        var decision = await fx.Gate.AuthorizeHoldAsync(fx.ConsumerId, holdCents: 0, "usd");

        Assert.True(decision.Allowed); // nothing to hold, so nothing to gate on
    }

    [Fact]
    public async Task Authorize_throws_at_capacity_when_the_concurrency_cap_is_reached()
    {
        var fx = new Fixture();
        await fx.PublishCapAsync(cap: 2);
        await fx.FundWalletAsync(10_000);
        await fx.SeedLeaseAsync(LeaseStatus.Active);
        await fx.SeedLeaseAsync(LeaseStatus.Suspended); // suspended still holds a slot

        var ex = await Assert.ThrowsAsync<ApiException>(() =>
            fx.Gate.AuthorizeHoldAsync(fx.ConsumerId, holdCents: 300, "usd"));

        Assert.Equal(ApiErrorCode.AtCapacity, ex.Code);
    }

    [Fact]
    public async Task Authorize_allows_when_below_the_cap_and_ignores_ended_leases()
    {
        var fx = new Fixture();
        await fx.PublishCapAsync(cap: 2);
        await fx.FundWalletAsync(1000);
        await fx.SeedLeaseAsync(LeaseStatus.Active);
        await fx.SeedLeaseAsync(LeaseStatus.Ended); // terminal — does not count against the cap

        var decision = await fx.Gate.AuthorizeHoldAsync(fx.ConsumerId, holdCents: 300, "usd");

        Assert.True(decision.Allowed);
    }

    [Fact]
    public async Task Authorize_throws_limit_exceeded_when_the_daily_spend_cap_is_reached()
    {
        var fx = new Fixture();
        await fx.PublishSpendCapAsync(capCents: 10_000);
        await fx.FundWalletAsync(1_000_000);
        // A 60s @ 8000¢/min lease commits a hold of 8000 today.
        await fx.SeedLeaseAsync(ttlSeconds: 60, price: 8000);

        // Another lease committing 3000 would push the day's committed spend to 11000 > 10000.
        var ex = await Assert.ThrowsAsync<ApiException>(() =>
            fx.Gate.AuthorizeHoldAsync(fx.ConsumerId, holdCents: 3000, "usd"));

        Assert.Equal(ApiErrorCode.LimitExceeded, ex.Code);
    }

    [Fact]
    public async Task Authorize_allows_under_the_daily_spend_cap()
    {
        var fx = new Fixture();
        await fx.PublishSpendCapAsync(capCents: 10_000);
        await fx.FundWalletAsync(1_000_000);
        await fx.SeedLeaseAsync(ttlSeconds: 60, price: 4000); // 4000 committed today

        var decision = await fx.Gate.AuthorizeHoldAsync(fx.ConsumerId, holdCents: 3000, "usd"); // 7000 ≤ 10000

        Assert.True(decision.Allowed);
    }

    [Fact]
    public async Task PlaceHold_earmarks_the_hold_from_the_wallet_into_lease_holds()
    {
        var fx = new Fixture();
        await fx.FundWalletAsync(300);
        var lease = await fx.SeedLeaseAsync(price: 5, ttlSeconds: 3600);

        var holdTxnId = await fx.Gate.PlaceHoldAsync(fx.ConsumerId, lease.Id, holdCents: 300, "usd");

        Assert.NotNull(holdTxnId);
        Assert.Equal(0, await fx.WalletCentsAsync());  // debited out of the wallet
        Assert.Equal(300, await fx.HoldsCentsAsync()); // earmarked into lease_holds
    }

    [Fact]
    public async Task PlaceHold_is_a_noop_for_a_free_image()
    {
        var fx = new Fixture();
        var lease = await fx.SeedLeaseAsync(price: 0);

        var holdTxnId = await fx.Gate.PlaceHoldAsync(fx.ConsumerId, lease.Id, holdCents: 0, "usd");

        Assert.Null(holdTxnId);
        Assert.Equal(0, await fx.HoldsCentsAsync());
    }

    [Fact]
    public async Task PlaceHold_is_idempotent_per_lease_and_cannot_double_hold()
    {
        var fx = new Fixture();
        await fx.FundWalletAsync(300);
        var lease = await fx.SeedLeaseAsync(price: 5, ttlSeconds: 3600);

        var first = await fx.Gate.PlaceHoldAsync(fx.ConsumerId, lease.Id, holdCents: 300, "usd");
        var second = await fx.Gate.PlaceHoldAsync(fx.ConsumerId, lease.Id, holdCents: 300, "usd");

        Assert.Equal(first, second);                   // the replay returns the same txn
        Assert.Equal(0, await fx.WalletCentsAsync());  // and moved no new money
        Assert.Equal(300, await fx.HoldsCentsAsync());
    }

    [Fact]
    public async Task Charges_debit_the_hold_and_release_returns_exactly_the_remainder()
    {
        var fx = new Fixture();
        await fx.FundWalletAsync(300);
        // ttl 3600s @ 5¢/min → hold 300¢. Meter bills 120 healthy seconds → ⌊120·5/60⌋ = 10¢.
        var lease = await fx.SeedLeaseAsync(price: 5, ttlSeconds: 3600, billableSeconds: 120);
        await fx.Gate.PlaceHoldAsync(fx.ConsumerId, lease.Id, holdCents: 300, "usd");
        await fx.ChargeAsync(lease.Id, amountCents: 10);

        // The charge debited the hold; the wallet is still fully held.
        Assert.Equal(290, await fx.HoldsCentsAsync());
        Assert.Equal(0, await fx.WalletCentsAsync());

        await fx.Gate.ReleaseHoldAsync(lease.Id);

        // Release returns hold − charged = 300 − 10 = 290 to the wallet; the lease's hold is fully unwound.
        Assert.Equal(290, await fx.WalletCentsAsync());
        Assert.Equal(0, await fx.HoldsCentsAsync());
    }

    [Fact]
    public async Task Release_returns_the_full_hold_when_nothing_was_metered()
    {
        var fx = new Fixture();
        await fx.FundWalletAsync(300);
        var lease = await fx.SeedLeaseAsync(price: 5, ttlSeconds: 3600, billableSeconds: 0);
        await fx.Gate.PlaceHoldAsync(fx.ConsumerId, lease.Id, holdCents: 300, "usd");

        await fx.Gate.ReleaseHoldAsync(lease.Id);

        Assert.Equal(300, await fx.WalletCentsAsync()); // untouched money comes straight back
        Assert.Equal(0, await fx.HoldsCentsAsync());
    }

    [Fact]
    public async Task The_hold_cannot_be_exhausted_mid_lease()
    {
        var fx = new Fixture();
        await fx.FundWalletAsync(300);
        // The hold covers the WHOLE max lease: ttl 3600s @ 5¢/min = 300¢ = ⌊3600·5/60⌋.
        var lease = await fx.SeedLeaseAsync(price: 5, ttlSeconds: 3600, billableSeconds: 3600);
        await fx.Gate.PlaceHoldAsync(fx.ConsumerId, lease.Id, holdCents: 300, "usd");

        // Billing every second of the max lease debits exactly the whole hold — never more.
        await fx.ChargeAsync(lease.Id, amountCents: 300);
        Assert.Equal(0, await fx.HoldsCentsAsync());

        // Any attempt to bill beyond the hold is rejected by the non-negative guard (§7d) — the hold can
        // never go negative, so "insufficient funds mid-lease" cannot happen in this model.
        var overdraw = await Assert.ThrowsAsync<LedgerException>(() => fx.ChargeAsync(lease.Id, amountCents: 1));
        Assert.Equal(LedgerViolation.HoldOverdrawn, overdraw.Reason);

        // A fully-metered lease releases nothing (remainder 0), leaving the hold cleanly at zero.
        await fx.Gate.ReleaseHoldAsync(lease.Id);
        Assert.Equal(0, await fx.HoldsCentsAsync());
        Assert.Equal(0, await fx.WalletCentsAsync());
    }

    [Fact]
    public async Task Release_is_idempotent_across_the_several_lease_end_drivers()
    {
        var fx = new Fixture();
        await fx.FundWalletAsync(300);
        var lease = await fx.SeedLeaseAsync(price: 5, ttlSeconds: 3600, billableSeconds: 120);
        await fx.Gate.PlaceHoldAsync(fx.ConsumerId, lease.Id, holdCents: 300, "usd");
        await fx.ChargeAsync(lease.Id, amountCents: 10);

        await fx.Gate.ReleaseHoldAsync(lease.Id);
        await fx.Gate.ReleaseHoldAsync(lease.Id); // consumer DELETE + reconciler converge on one release

        Assert.Equal(290, await fx.WalletCentsAsync()); // credited exactly once
        Assert.Equal(0, await fx.HoldsCentsAsync());
    }

    [Fact]
    public async Task Release_is_a_noop_for_a_free_lease()
    {
        var fx = new Fixture();
        var lease = await fx.SeedLeaseAsync(price: 0, billableSeconds: 600);

        await fx.Gate.ReleaseHoldAsync(lease.Id); // no hold was ever placed

        Assert.Equal(0, await fx.WalletCentsAsync());
        Assert.Equal(0, await fx.HoldsCentsAsync());
    }
}
