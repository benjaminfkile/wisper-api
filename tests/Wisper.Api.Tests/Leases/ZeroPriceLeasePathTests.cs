using Microsoft.Extensions.Logging.Abstractions;
using Wisper.Api.Domain;
using Wisper.Api.Leases;
using Wisper.Api.Ledger;
using Wisper.Api.Metering;
using Wisper.Api.Persistence.Hosts;
using Wisper.Api.Persistence.Leases;
using Wisper.Api.Persistence.Policy;
using Wisper.Api.Policy;
using Wisper.Api.Tests.TestSupport;
using Xunit;

namespace Wisper.Api.Tests.Leases;

/// <summary>
/// End-to-end pin for the zero-priced-image path (task #386, docs/PAYMENTS.md §4): a self-hosted operator
/// prices their own image at <c>0</c>, so the wallet gate must pass a <b>0-cent hold with an empty wallet</b>,
/// metering must be <b>ledger-safe at 0 cents</b>, and lease-end release of a 0 hold must be a <b>no-op that
/// does not throw</b>. This wires the <em>real</em> <see cref="WalletLeaseGate"/> and <see cref="MeteringService"/>
/// over the same in-memory ledger (Grunt has no Postgres) so the composition — not just each piece — is proven:
/// a free lease moves the ledger zero times and drives no balance negative.
/// </summary>
public class ZeroPriceLeasePathTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 12, 0, 0, 0, TimeSpan.Zero);

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
        public WalletLeaseGate Gate { get; }

        public Guid ConsumerId { get; } = Guid.NewGuid();
        public Guid HostId { get; } = Guid.NewGuid();

        public Fixture()
        {
            Ledger = new LedgerService(LedgerStore);
            Policy = new PlatformPolicyService(Policies, Clock);
            var fraud = new FraudGuardService(
                Ledger, Leases, Policy, Clock, NullLogger<FraudGuardService>.Instance);
            Gate = new WalletLeaseGate(Ledger, Leases, Policy, fraud, NullLogger<WalletLeaseGate>.Instance);
        }

        public MeteringService Meter() =>
            new(Leases, Usage, Hosts, Ledger, Policy, Clock, NullLogger<MeteringService>.Instance);

        /// <summary>Seeds an active free (price 0) lease that just went ready at <see cref="T0"/>.</summary>
        public Task<Lease> SeedFreeLeaseAsync(int ttlSeconds = 3600) =>
            Leases.CreateAsync(new Lease
            {
                Id = Guid.NewGuid(),
                ConsumerUserId = ConsumerId,
                HostId = HostId,
                HostImageId = Guid.NewGuid(),
                ImageRef = "reg/wisp-base:latest",
                Network = NetworkMode.Open,
                TtlSeconds = ttlSeconds,
                PriceCentsPerMin = 0, // free image
                Currency = "usd",
                Status = LeaseStatus.Active,
                CreatedAt = T0,
                StartedAt = T0,
                LastMeteredAt = T0,
                BillableSeconds = 0,
            });

        public Task<long> BalanceAsync(LedgerAccountKind kind, Guid? owner = null) => BalanceCoreAsync(kind, owner);

        private async Task<long> BalanceCoreAsync(LedgerAccountKind kind, Guid? owner)
        {
            var account = await Ledger.GetOrCreateAccountAsync(kind, owner);
            return await Ledger.GetBalanceAsync(account.Id);
        }
    }

    [Fact]
    public async Task Free_lease_runs_the_whole_hold_meter_release_loop_moving_the_ledger_zero_times()
    {
        var fx = new Fixture();
        // ttl 3600s @ 0¢/min → estimated hold = ⌈3600/60⌉ · 0 = 0.
        var holdCents = LeaseHoldPricing.EstimateHoldCents(3600, priceCentsPerMin: 0);
        Assert.Equal(0, holdCents);

        // 1. Authorize with an EMPTY wallet: a zero hold always allows — nothing to gate on.
        var decision = await fx.Gate.AuthorizeHoldAsync(fx.ConsumerId, holdCents, "usd");
        Assert.True(decision.Allowed);

        // 2. Place the (zero) hold once the lease exists: a no-op that earmarks nothing.
        var lease = await fx.SeedFreeLeaseAsync(ttlSeconds: 3600);
        var holdTxn = await fx.Gate.PlaceHoldAsync(fx.ConsumerId, lease.Id, holdCents, "usd");
        Assert.Null(holdTxn);
        Assert.Equal(0, await fx.BalanceAsync(LedgerAccountKind.LeaseHolds));

        // 3. Meter a tick: seconds accrue but no lease_charge/usage row is written (0¢ is skipped).
        fx.Clock.Advance(TimeSpan.FromSeconds(120));
        Assert.Equal(0, await fx.Meter().RunTickAsync());
        var metered = await fx.Leases.GetByIdAsync(lease.Id);
        Assert.Equal(120, metered!.BillableSeconds);
        Assert.Empty(await fx.Usage.ListByLeaseAsync(lease.Id));

        // 4. End the lease: releasing a 0 hold is a no-op that does not throw.
        await fx.Gate.ReleaseHoldAsync(lease.Id);

        // The whole loop moved the ledger zero times — every account is flat at zero and internally consistent.
        Assert.Equal(0, await fx.BalanceAsync(LedgerAccountKind.UserWallet, fx.ConsumerId));
        Assert.Equal(0, await fx.BalanceAsync(LedgerAccountKind.LeaseHolds));
        foreach (var account in await fx.Ledger.ReconcileAsync())
        {
            Assert.True(account.IsBalanced);
            Assert.True(account.MaintainedBalanceCents >= 0); // no balance driven negative
        }
    }

    [Fact]
    public async Task Free_lease_authorize_passes_even_when_the_wallet_account_has_never_been_funded()
    {
        var fx = new Fixture();

        // No topup, no wallet account created — the gate must not require one for a zero hold.
        var decision = await fx.Gate.AuthorizeHoldAsync(fx.ConsumerId, holdCents: 0, "usd");

        Assert.True(decision.Allowed);
        Assert.Equal(0, decision.RequiredCents);
    }
}
