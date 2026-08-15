using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wisper.Api.Domain;
using Wisper.Api.Hosts;
using Wisper.Api.Leases;
using Wisper.Api.Ledger;
using Wisper.Api.Metering;
using Wisper.Api.Payouts;
using Wisper.Api.Persistence.HostImages;
using Wisper.Api.Persistence.Hosts;
using Wisper.Api.Persistence.Leases;
using Wisper.Api.Persistence.Payouts;
using Wisper.Api.Persistence.Policy;
using Wisper.Api.Persistence.Users;
using Wisper.Api.Policy;
using Wisper.Api.Tests.TestSupport;
using Wisper.Api.Tunnel;
using Wisper.Api.Tunnel.Backplane;
using Xunit;
using Host = Wisper.Api.Domain.Host;

namespace Wisper.Api.Tests.Leases;

/// <summary>
/// Task #46 regression suite (billing-integrity, live-bug report): the price stamped on the lease row at
/// create time (<see cref="Lease.PriceCentsPerMin"/>) must be the ONLY rate ever applied to that lease —
/// for the up-front hold, every metering tick, the running-cost display (<c>cost_cents_so_far</c>),
/// revive re-holds, end-of-lease settlement, and host payout/earnings accrual. A host that reprices the
/// underlying <c>host_images</c> row mid-lease must NOT be able to change what an already-open lease is
/// charged (otherwise a host raising the price from 1¢/min to 10000¢/min drains the consumer's wallet).
/// <para>
/// This wires the REAL <see cref="LeaseService"/>, <see cref="MeteringService"/>, <see cref="WalletLeaseGate"/>,
/// <see cref="LeaseReconciliationService"/> and <see cref="HostService"/> over the in-memory ledger + repos
/// so the whole composition is proven end-to-end: an actual host PATCH on the image row cannot influence
/// a live lease's ledger movement, wallet debit, cost_cents_so_far, or host earnings accrual — even across
/// a suspend / grace-expiry / post-grace revive.
/// </para>
/// </summary>
public class LeasePriceSnapshotTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 14, 0, 0, 0, TimeSpan.Zero);

    // A 60¢/min lease is exactly 1¢/second so the arithmetic reads cleanly (charged cents == billable seconds).
    private const long SnapshotPricePerMin = 60;

    // The malicious mid-lease reprice: 10000¢/min ($100/min) — the exact attack shape the bug report calls out.
    private const long AttackPricePerMin = 10_000;

    private const int FeeBps = 1500; // 15% platform fee → readable 51/9 split on a 60¢ charge

    private sealed class Fixture
    {
        public InMemoryLeaseRepository Leases { get; } = new();
        public InMemoryLeaseUsageRepository Usage { get; } = new();
        public InMemoryHostImageRepository Images { get; } = new();
        public InMemoryHostRepository Hosts { get; } = new();
        public InMemoryUserRepository Users { get; } = new();
        public InMemoryLedgerStore LedgerStore { get; } = new();
        public InMemoryPlatformPolicyRepository Policies { get; } = new();
        public FakeTunnelRelay Relay { get; } = new();
        public FakeHostCapabilitySource Capabilities { get; } = new();
        public FakeHostRegistry Registry { get; } = new();
        public InMemoryHostPresenceStore Presence { get; } = new();
        public InMemoryHostDegradedStore Degraded { get; } = new();
        public FakeAgentTunnelCloser TunnelCloser { get; } = new();
        public FakeUserRoleGranter RoleGranter { get; } = new();
        public FakeTimeProvider Clock { get; } = new(T0);
        public TunnelOptions TunnelOpts { get; } = new() { ManagerWebSocketUrl = "wss://wisper.test/agent" };

        public LedgerService Ledger { get; }
        public PlatformPolicyService Policy { get; }
        public FraudGuardService Fraud { get; }
        public WalletLeaseGate WalletGate { get; }
        public MeteringService Meter { get; }
        public LeaseReconciliationService Reconciler { get; }
        public PayoutService Payouts { get; }
        public HostService HostService { get; }
        public LeaseService LeaseService { get; }

        public Guid ConsumerId { get; } = Guid.NewGuid();
        public Guid HostOwnerId { get; } = Guid.NewGuid();
        public Host Host { get; private set; } = null!;
        public HostImage Image { get; private set; } = null!;

        public Fixture()
        {
            Ledger = new LedgerService(LedgerStore);
            Policy = new PlatformPolicyService(Policies, Clock);
            Fraud = new FraudGuardService(
                Ledger, Leases, Policy, Clock, NullLogger<FraudGuardService>.Instance);
            WalletGate = new WalletLeaseGate(
                Ledger, Leases, Policy, Fraud, NullLogger<WalletLeaseGate>.Instance);
            Meter = new MeteringService(
                Leases, Usage, Hosts, Ledger, Policy, Clock, NullLogger<MeteringService>.Instance);
            Reconciler = new LeaseReconciliationService(
                Leases, Meter, WalletGate, Clock, NullLogger<LeaseReconciliationService>.Instance);
            Payouts = new PayoutService(
                Ledger, new InMemoryPayoutRepository(), Users, new FakeStripeConnectGateway(),
                Options.Create(new PayoutOptions()), Clock, NullLogger<PayoutService>.Instance);
            HostService = new HostService(
                Hosts, Images, Leases, Users, Registry, Presence, Capabilities, TunnelCloser, Payouts,
                RoleGranter, Options.Create(TunnelOpts), Clock, NullLogger<HostService>.Instance);
            LeaseService = new LeaseService(
                Leases, Hosts, Images, Relay, Capabilities, WalletGate,
                Meter, Policy, Degraded, Clock);
        }

        /// <summary>
        /// Seeds a host owned by <see cref="HostOwnerId"/>, a priced image in its allow-list, a generous live
        /// capability, the platform policy for the fee split, and a wallet top-up big enough for the biggest
        /// hold the attack could require — so we can prove the attack does NOT drain even a well-funded wallet.
        /// </summary>
        public async Task SeedAsync()
        {
            await Policy.PublishAsync(new PlatformPolicy { FeeBps = FeeBps, EffectiveFrom = T0.AddSeconds(-1) });

            // Host owner is Connect-enabled so the PATCH on a priced image passes the Connect gate.
            await Users.CreateAsync(new User
            {
                Id = HostOwnerId,
                CognitoSub = $"sub-{HostOwnerId}",
                Email = $"{HostOwnerId}@hosts.test",
                ConnectStatus = ConnectStatus.Enabled,
                CreatedAt = T0,
                UpdatedAt = T0,
            });

            Host = await Hosts.CreateAsync(new Host
            {
                Id = Guid.NewGuid(),
                OwnerUserId = HostOwnerId,
                Name = "priced-host",
                Label = "us",
                Status = HostStatus.Online,
                AgentTokenHash = "hash",
                CreatedAt = T0,
                UpdatedAt = T0,
            });

            Image = await Images.CreateAsync(new HostImage
            {
                HostId = Host.Id,
                ImageRef = "reg/wisp-base:latest",
                PriceCentsPerMin = SnapshotPricePerMin,
                Networks = new[] { NetworkMode.None, NetworkMode.Open },
                MaxTtlSeconds = 14400,
                MaxCpus = 4,
                MaxMemoryMb = 8192,
                MaxPids = 1024,
                Enabled = true,
                CreatedAt = T0,
                UpdatedAt = T0,
            });

            // The live capability the PATCH revalidates against — the image ref must be in the allow-list.
            Capabilities.Set(Host.Id, new HostCapabilitySnapshot(
                Images: new[] { "reg/wisp-base:latest" },
                Networks: new[] { NetworkMode.None, NetworkMode.Open },
                MaxTtlSeconds: 14400,
                MaxCpus: 4,
                MaxMemoryMb: 8192,
                MaxPids: 1024));

            // Fund the consumer wallet WAY above what the honest lease could ever cost. If the attack worked,
            // the wallet would drain into host earnings; a snapshot-locked lease leaves it almost untouched.
            await FundWalletAsync(amountCents: 1_000_000_000); // $10,000,000
        }

        public async Task FundWalletAsync(long amountCents)
        {
            var wallet = await Ledger.GetOrCreateAccountAsync(LedgerAccountKind.UserWallet, ConsumerId);
            var cash = await Ledger.GetOrCreateAccountAsync(LedgerAccountKind.PlatformCash, null);
            var fees = await Ledger.GetOrCreateAccountAsync(LedgerAccountKind.StripeFees, null);
            await Ledger.PostAsync(LedgerFlows.Topup(
                wallet.Id, cash.Id, fees.Id, grossAmountCents: amountCents, stripeFeeCents: 0,
                idempotencyKey: $"topup:{ConsumerId:D}:{amountCents}"));
        }

        public CreateLeaseRequest Request(int ttlSeconds = 3600) => new(
            HostId: Host.Id.ToString(),
            HostImageId: Image.Id.ToString(),
            Network: "open",
            Resources: null,
            TtlSeconds: ttlSeconds,
            Userdata: null);

        /// <summary>Mutates the image's price via the real host PATCH endpoint — the exact attack surface.</summary>
        public Task RepriceImageAsync(long newPriceCentsPerMin) =>
            HostService.PatchImageAsync(
                HostOwnerId, Host.Id, Image.Id,
                new PatchImageRequest(
                    PriceCentsPerMin: newPriceCentsPerMin,
                    Networks: null, MaxTtlSeconds: null, MaxCpus: null, MaxMemoryMb: null, MaxPids: null,
                    Enabled: null));

        public Task<Lease?> ReloadAsync(Guid leaseId) => Leases.GetByIdAsync(leaseId);

        public async Task<long> WalletCentsAsync()
        {
            var wallet = await Ledger.GetOrCreateAccountAsync(LedgerAccountKind.UserWallet, ConsumerId);
            return await Ledger.GetBalanceAsync(wallet.Id);
        }

        public async Task<long> HoldsCentsAsync()
        {
            var holds = await Ledger.GetOrCreateAccountAsync(LedgerAccountKind.LeaseHolds, null);
            return await Ledger.GetBalanceAsync(holds.Id);
        }

        public async Task<long> HostEarningsCentsAsync()
        {
            var earnings = await Ledger.GetOrCreateAccountAsync(
                LedgerAccountKind.HostEarnings, HostOwnerId);
            return await Ledger.GetBalanceAsync(earnings.Id);
        }

        public async Task<long> PlatformRevenueCentsAsync()
        {
            var revenue = await Ledger.GetOrCreateAccountAsync(LedgerAccountKind.PlatformRevenue, null);
            return await Ledger.GetBalanceAsync(revenue.Id);
        }

        /// <summary>The exact wire figure the consumer sees in the read view.</summary>
        public async Task<long> CostCentsSoFarViewAsync(Guid leaseId)
        {
            var view = await LeaseService.GetAsync(ConsumerId, leaseId);
            return view?.CostCentsSoFar ?? 0;
        }
    }

    /// <summary>
    /// The core attack: create a priced lease, host raises the image price ×10000 mid-lease, meter and end the
    /// lease. Every posted <c>lease_charge</c>, the <c>cost_cents_so_far</c> display, the total wallet debit,
    /// and the host earnings/platform revenue accrual MUST reflect only the create-time snapshot price — the
    /// mid-lease reprice must have zero effect on this lease's ledger movement.
    /// </summary>
    [Fact]
    public async Task Mid_lease_reprice_never_changes_charges_wallet_debit_or_host_earnings_for_an_open_lease()
    {
        var fx = new Fixture();
        await fx.SeedAsync();
        var walletBefore = await fx.WalletCentsAsync();

        // 1) Create at the snapshot price. Hold = ⌈180/60⌉ · 60 = 180¢.
        var created = await fx.LeaseService.CreateAsync(fx.ConsumerId, fx.Request(ttlSeconds: 180));
        var leaseId = created.Lease.Id;
        Assert.Equal(SnapshotPricePerMin, created.Lease.PriceCentsPerMin);
        Assert.Equal(180, created.HoldCents);

        // 2) One metering tick at snapshot price: charge = 60¢ (fee 9 + host 51).
        fx.Clock.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(1, await fx.Meter.RunTickAsync());
        Assert.Equal(60, await fx.CostCentsSoFarViewAsync(leaseId));

        // 3) THE ATTACK: host reprices the image row from 60¢/min to 10000¢/min mid-lease. The image row's
        // price_cents_per_min is now the attack price; the LEASE row's snapshot must stay 60.
        await fx.RepriceImageAsync(AttackPricePerMin);
        var repricedImage = await fx.Images.GetByIdAsync(fx.Image.Id);
        Assert.Equal(AttackPricePerMin, repricedImage!.PriceCentsPerMin);
        Assert.Equal(SnapshotPricePerMin, (await fx.ReloadAsync(leaseId))!.PriceCentsPerMin);

        // 4) Meter another 60s tick AFTER the reprice: it MUST still charge only 60¢ (snapshot price), NOT
        // 10000¢. This is the whole load-bearing assertion of the bug — a live tick after a mid-lease reprice
        // charges the snapshot, not the current image price.
        fx.Clock.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(1, await fx.Meter.RunTickAsync());
        Assert.Equal(120, await fx.CostCentsSoFarViewAsync(leaseId)); // 2 minutes @ snapshot = 120¢, not 10 060¢

        // 5) Release the lease mid-way through the third minute (30s tail). The on-end final flush at task #34
        // must also use the snapshot: 30s at 60¢/min = 30¢, NOT 30s at 10000¢/min = 5000¢.
        fx.Clock.Advance(TimeSpan.FromSeconds(30));
        var releasedView = await fx.LeaseService.ReleaseAsync(fx.ConsumerId, leaseId);

        Assert.NotNull(releasedView);
        Assert.Equal("ended", releasedView!.Status);
        Assert.Equal(SnapshotPricePerMin, releasedView.PriceCentsPerMin); // view still shows snapshot
        Assert.Equal(150, releasedView.BillableSeconds);
        Assert.Equal(150, releasedView.CostCentsSoFar); // 2m30s @ snapshot = 150¢

        // 6) Ledger invariants: every money-moving account reflects the snapshot only.
        // - Wallet was debited exactly 150¢ (the honest cost), not 25 000¢+ (the attack).
        var walletAfter = await fx.WalletCentsAsync();
        Assert.Equal(150, walletBefore - walletAfter);

        // - Host earnings and platform revenue split the 150¢ per the 15% fee (host 128, fee 22 by SplitFee).
        //   Sum must equal the honest total, never the attack total.
        var earnings = await fx.HostEarningsCentsAsync();
        var revenue = await fx.PlatformRevenueCentsAsync();
        Assert.Equal(150, earnings + revenue);
        Assert.True(revenue > 0 && earnings > 0);

        // - lease_holds is fully unwound (hold 180¢ − charged 150¢ = 30¢ remainder returned to the wallet).
        Assert.Equal(0, await fx.HoldsCentsAsync());

        // - The double-entry ledger is still balanced and no account was driven negative — a leaked live-price
        // charge would either overdraw lease_holds or drop the wallet through zero long before the release.
        foreach (var recon in await fx.Ledger.ReconcileAsync())
        {
            Assert.True(recon.IsBalanced);
            Assert.True(recon.MaintainedBalanceCents >= 0);
        }
    }

    /// <summary>
    /// The revive-path variant (docs/TUNNEL.md §8): create a lease, drop the host and end it as
    /// <c>host_disconnect</c>, host reprices while the lease is ended, then host reconnects post-grace and
    /// the reconciler revives the lease. The revival re-hold, subsequent metering ticks, and the eventual
    /// release must all still use the original snapshot price — the mid-outage reprice must not sneak in
    /// through the ended→active revival path either.
    /// </summary>
    [Fact]
    public async Task Revive_after_reprice_re_holds_and_charges_at_the_snapshot_not_the_live_price()
    {
        var fx = new Fixture();
        await fx.SeedAsync();

        var created = await fx.LeaseService.CreateAsync(fx.ConsumerId, fx.Request(ttlSeconds: 600));
        var leaseId = created.Lease.Id;
        // Sanity: create hold @ snapshot = ⌈600/60⌉·60 = 600¢.
        Assert.Equal(600, created.HoldCents);

        // One healthy minute, then last-healthy freezes at T0+60.
        fx.Clock.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(1, await fx.Meter.RunTickAsync());

        // Suspend and grace-expire the lease (ends as host_disconnect at last-healthy T0+60).
        await fx.Reconciler.SuspendHostLeasesAsync(fx.Host.Id, T0.AddSeconds(60));
        await fx.Reconciler.EndSuspendedHostLeasesAsync(fx.Host.Id, T0.AddSeconds(60));
        var ended = await fx.ReloadAsync(leaseId);
        Assert.Equal(LeaseStatus.Ended, ended!.Status);
        Assert.Equal(LeaseEndReason.HostDisconnect, ended.EndReason);
        Assert.Equal(60, ended.BillableSeconds);
        // The pre-drop hold was released at grace expiry: lease_holds is empty for this lease.
        Assert.Equal(0, await fx.HoldsCentsAsync());
        // 60¢ charged so far.
        Assert.Equal(60, await fx.HostEarningsCentsAsync() + await fx.PlatformRevenueCentsAsync());

        // THE ATTACK VARIANT: while the lease is ended (host is disconnected past grace), the host mutates
        // the image price. When the container comes back and the reconciler revives the lease, the fresh
        // hold + subsequent charges MUST still use the snapshot, never the reprice.
        await fx.RepriceImageAsync(AttackPricePerMin);
        Assert.Equal(AttackPricePerMin, (await fx.Images.GetByIdAsync(fx.Image.Id))!.PriceCentsPerMin);

        // Reconnect 5 minutes after grace: reconciler revives (container never died on the host).
        fx.Clock.Advance(TimeSpan.FromMinutes(5));
        var reconnectAt = fx.Clock.GetUtcNow();
        var outcome = await fx.Reconciler.RevivePostGraceAsync(fx.Host.Id, new[] { leaseId }, reconnectAt);
        Assert.Equal(new[] { leaseId }, outcome.Revived);

        var revived = await fx.ReloadAsync(leaseId);
        Assert.Equal(LeaseStatus.Active, revived!.Status);
        // The snapshot survives revive — the reprice must NOT touch the lease row's price.
        Assert.Equal(SnapshotPricePerMin, revived.PriceCentsPerMin);

        // The revive re-hold covers the REMAINING billable time at the SNAPSHOT price, not the live price:
        // remaining = ttl − billable = 540s → ⌈540/60⌉ · 60 = 540¢. Under the bug this would be ⌈540/60⌉ ·
        // 10 000 = 90 000¢ (150× larger).
        Assert.Equal(540, await fx.HoldsCentsAsync());

        // A metering tick after revive charges only the snapshot (60¢ per healthy minute).
        fx.Clock.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(1, await fx.Meter.RunTickAsync());
        Assert.Equal(120, await fx.CostCentsSoFarViewAsync(leaseId)); // 2 minutes total @ snapshot = 120¢

        // Release: total metered runtime is 120s of billable, so the ledger has moved 120¢ from the wallet
        // to earnings + revenue — not 60 100¢. The revive-generation hold_release returns the 480¢ remainder
        // (revive hold 540¢ − 60¢ charged post-revive), so lease_holds is again zero.
        await fx.LeaseService.ReleaseAsync(fx.ConsumerId, leaseId);
        Assert.Equal(0, await fx.HoldsCentsAsync());
        Assert.Equal(120, await fx.HostEarningsCentsAsync() + await fx.PlatformRevenueCentsAsync());

        // Ledger stays balanced with no account below zero.
        foreach (var recon in await fx.Ledger.ReconcileAsync())
        {
            Assert.True(recon.IsBalanced);
            Assert.True(recon.MaintainedBalanceCents >= 0);
        }
    }

    /// <summary>
    /// Belt-and-braces guard against the "wrong constant reintroduced" refactor: even if someone drills
    /// <see cref="MeteringService.ChargeCentsFor"/> to consult the current image row, the API-shape guarantee
    /// stays that a fresh <see cref="Lease.PriceCentsPerMin"/> read after a reprice still yields the snapshot.
    /// This pins the leases-repository update discipline: <see cref="ILeaseRepository.UpdateAsync"/> and
    /// <see cref="ILeaseRepository.TransitionStateAsync"/> must not touch <c>price_cents_per_min</c> — a
    /// reprice on the host image + any transition on the lease must leave the lease price snapshot exactly
    /// as stored on create.
    /// </summary>
    [Fact]
    public async Task Lease_row_price_is_immutable_across_repository_updates_and_state_transitions()
    {
        var fx = new Fixture();
        await fx.SeedAsync();
        var created = await fx.LeaseService.CreateAsync(fx.ConsumerId, fx.Request(ttlSeconds: 3600));
        var leaseId = created.Lease.Id;

        // Reprice the image row.
        await fx.RepriceImageAsync(AttackPricePerMin);

        // Force through every write path a running lease might take: an UpdateAsync with a tampered price,
        // and a TransitionStateAsync. Neither must mutate the price column on the leases row.
        var loaded = await fx.ReloadAsync(leaseId);
        // Try to sneak an attacker price through UpdateAsync — the repo contract must ignore it.
        await fx.Leases.UpdateAsync(loaded! with { PriceCentsPerMin = AttackPricePerMin });
        Assert.Equal(SnapshotPricePerMin, (await fx.ReloadAsync(leaseId))!.PriceCentsPerMin);

        await fx.Leases.TransitionStateAsync(
            leaseId, LeaseStatus.Active, lastMeteredAt: T0.AddSeconds(30), billableSeconds: 30);
        Assert.Equal(SnapshotPricePerMin, (await fx.ReloadAsync(leaseId))!.PriceCentsPerMin);
    }
}
