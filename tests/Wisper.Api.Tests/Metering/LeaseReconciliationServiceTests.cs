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
using Host = Wisper.Api.Domain.Host;

namespace Wisper.Api.Tests.Metering;

/// <summary>
/// Unit tests for <see cref="LeaseReconciliationService"/> with in-memory doubles (Grunt has no Postgres):
/// the docs/TUNNEL.md §8 disconnect policy — pause billing at last-healthy on suspend, resume the same
/// lease id on reconnect (billing restarts, gap never billed), end vanished leases (container_lost) and
/// grace-expired leases (host_disconnect) finalized at last-healthy, and the reconnect set-diff.
/// </summary>
public class LeaseReconciliationServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 12, 0, 0, 0, TimeSpan.Zero);

    // 60¢/min = exactly 1¢/second, so a healthy interval's charge equals its seconds — readable arithmetic.
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
        public MeteringService Meter { get; }
        public LeaseReconciliationService Reconciler { get; }

        public Guid ConsumerId { get; } = Guid.NewGuid();
        public Guid HostOwnerId { get; } = Guid.NewGuid();
        public Guid HostId { get; private set; }

        public Fixture()
        {
            Ledger = new LedgerService(LedgerStore);
            Policy = new PlatformPolicyService(Policies, Clock);
            Meter = new MeteringService(
                Leases, Usage, Hosts, Ledger, Policy, Clock, NullLogger<MeteringService>.Instance);
            var walletGate = new WalletLeaseGate(
                Ledger, Leases, Policy, NullLogger<WalletLeaseGate>.Instance);
            Reconciler = new LeaseReconciliationService(
                Leases, Meter, walletGate, Clock, NullLogger<LeaseReconciliationService>.Instance);
        }

        public async Task SeedAsync()
        {
            await Policy.PublishAsync(new PlatformPolicy { FeeBps = FeeBps, EffectiveFrom = T0 });
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

        public async Task<Lease> SeedActiveLeaseAsync(long price = PricePerMin, long holdCents = 3600)
        {
            var lease = await Leases.CreateAsync(new Lease
            {
                Id = Guid.NewGuid(),
                ConsumerUserId = ConsumerId,
                HostId = HostId,
                HostImageId = Guid.NewGuid(),
                ImageRef = "reg/wisp-base:latest",
                Network = NetworkMode.Open,
                TtlSeconds = 3600,
                PriceCentsPerMin = price,
                Currency = "usd",
                Status = LeaseStatus.Active,
                CreatedAt = T0,
                StartedAt = T0,
                LastMeteredAt = T0,
                BillableSeconds = 0,
            });
            await FundHoldAsync(lease.Id, holdCents);
            return lease;
        }

        private async Task FundHoldAsync(Guid leaseId, long holdCents)
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

        public Task<Lease?> ReloadAsync(Guid leaseId) => Leases.GetByIdAsync(leaseId);
    }

    private static async Task<Fixture> ReadyAsync()
    {
        var fx = new Fixture();
        await fx.SeedAsync();
        return fx;
    }

    [Fact]
    public async Task Suspend_pauses_billing_at_last_healthy_and_leaves_the_gap_unbilled()
    {
        var fx = await ReadyAsync();
        var lease = await fx.SeedActiveLeaseAsync();

        // The tunnel goes silent at T0+90 (last-healthy) but the drop is not detected until T0+150.
        fx.Clock.Advance(TimeSpan.FromSeconds(150));
        var outcome = await fx.Reconciler.SuspendHostLeasesAsync(fx.HostId, T0.AddSeconds(90));

        Assert.Equal(1, outcome.NewlySuspended);
        Assert.Equal(1, outcome.TotalSuspended);

        var stored = await fx.ReloadAsync(lease.Id);
        Assert.Equal(LeaseStatus.Suspended, stored!.Status);
        // Billed exactly to last-healthy (90s), not to the detection instant (150s): the blind window
        // is structurally un-billable.
        Assert.Equal(90, stored.BillableSeconds);
        Assert.Equal(T0.AddSeconds(90), stored.LastMeteredAt);

        var usage = Assert.Single(await fx.Usage.ListByLeaseAsync(lease.Id));
        Assert.Equal(90, usage.BillableSeconds);
        Assert.Equal(90, usage.AmountCents);
    }

    [Fact]
    public async Task A_suspended_lease_never_accrues_however_long_the_outage_lasts()
    {
        var fx = await ReadyAsync();
        var lease = await fx.SeedActiveLeaseAsync();

        fx.Clock.Advance(TimeSpan.FromSeconds(60));
        await fx.Reconciler.SuspendHostLeasesAsync(fx.HostId, T0.AddSeconds(60));

        // A metering tick five minutes into the outage bills nothing — a suspended lease is not in the
        // active working set (docs/TUNNEL.md §8: metering accrues only while active).
        fx.Clock.Advance(TimeSpan.FromMinutes(5));
        var flushed = await fx.Meter.RunTickAsync();

        Assert.Equal(0, flushed);
        var stored = await fx.ReloadAsync(lease.Id);
        Assert.Equal(60, stored!.BillableSeconds);
        Assert.Single(await fx.Usage.ListByLeaseAsync(lease.Id));
    }

    [Fact]
    public async Task Suspend_is_idempotent()
    {
        var fx = await ReadyAsync();
        await fx.SeedActiveLeaseAsync();

        fx.Clock.Advance(TimeSpan.FromSeconds(60));
        await fx.Reconciler.SuspendHostLeasesAsync(fx.HostId, T0.AddSeconds(60));
        var second = await fx.Reconciler.SuspendHostLeasesAsync(fx.HostId, T0.AddSeconds(60));

        Assert.Equal(0, second.NewlySuspended); // nothing new to suspend
        Assert.Equal(1, second.TotalSuspended);  // still one suspended to resolve
    }

    [Fact]
    public async Task Reconnect_resumes_a_present_lease_with_the_same_id_and_restarts_billing()
    {
        var fx = await ReadyAsync();
        var lease = await fx.SeedActiveLeaseAsync();

        // One healthy minute, then the tunnel drops at T0+60 and stays down for five minutes.
        fx.Clock.Advance(TimeSpan.FromSeconds(60));
        await fx.Reconciler.SuspendHostLeasesAsync(fx.HostId, T0.AddSeconds(60));
        fx.Clock.Advance(TimeSpan.FromMinutes(5)); // now T0+360

        var outcome = await fx.Reconciler.ReconcileHostAsync(
            fx.HostId, new[] { lease.Id }, lastHealthyAt: T0.AddSeconds(60));

        Assert.Equal(new[] { lease.Id }, outcome.Resumed);
        Assert.Empty(outcome.ContainerLost);

        var resumed = await fx.ReloadAsync(lease.Id);
        Assert.Equal(lease.Id, resumed!.Id); // same lease id
        Assert.Equal(LeaseStatus.Active, resumed.Status);
        Assert.Equal(60, resumed.BillableSeconds);              // the 5-minute gap was not billed
        Assert.Equal(T0.AddSeconds(360), resumed.LastMeteredAt); // meter restarted at the reconnect instant

        // A tick one minute after resume bills exactly that minute — never the outage gap.
        fx.Clock.Advance(TimeSpan.FromSeconds(60)); // now T0+420
        await fx.Meter.RunTickAsync();
        var afterTick = await fx.ReloadAsync(lease.Id);
        Assert.Equal(120, afterTick!.BillableSeconds); // 60 pre-drop + 60 post-resume
    }

    [Fact]
    public async Task Reconnect_ends_a_vanished_lease_as_container_lost_at_last_healthy()
    {
        var fx = await ReadyAsync();
        var lease = await fx.SeedActiveLeaseAsync();

        fx.Clock.Advance(TimeSpan.FromSeconds(60));
        await fx.Reconciler.SuspendHostLeasesAsync(fx.HostId, T0.AddSeconds(60));
        fx.Clock.Advance(TimeSpan.FromMinutes(2));

        // The host reconnects but no longer runs the container (crash/restart) — it is not reported live.
        var outcome = await fx.Reconciler.ReconcileHostAsync(
            fx.HostId, Array.Empty<Guid>(), lastHealthyAt: T0.AddSeconds(60));

        Assert.Empty(outcome.Resumed);
        Assert.Equal(new[] { lease.Id }, outcome.ContainerLost);

        var ended = await fx.ReloadAsync(lease.Id);
        Assert.Equal(LeaseStatus.Ended, ended!.Status);
        Assert.Equal(LeaseEndReason.ContainerLost, ended.EndReason);
        Assert.Equal(T0.AddSeconds(60), ended.EndedAt); // finalized at last-healthy
        Assert.Equal(60, ended.BillableSeconds);
    }

    [Fact]
    public async Task Reconnect_set_diff_resumes_present_and_ends_absent_together()
    {
        var fx = await ReadyAsync();
        var present = await fx.SeedActiveLeaseAsync();
        var vanished = await fx.SeedActiveLeaseAsync();

        fx.Clock.Advance(TimeSpan.FromSeconds(60));
        var suspended = await fx.Reconciler.SuspendHostLeasesAsync(fx.HostId, T0.AddSeconds(60));
        Assert.Equal(2, suspended.NewlySuspended);

        fx.Clock.Advance(TimeSpan.FromSeconds(30));
        var outcome = await fx.Reconciler.ReconcileHostAsync(
            fx.HostId, new[] { present.Id }, lastHealthyAt: T0.AddSeconds(60));

        Assert.Equal(new[] { present.Id }, outcome.Resumed);
        Assert.Equal(new[] { vanished.Id }, outcome.ContainerLost);

        Assert.Equal(LeaseStatus.Active, (await fx.ReloadAsync(present.Id))!.Status);
        var lost = await fx.ReloadAsync(vanished.Id);
        Assert.Equal(LeaseStatus.Ended, lost!.Status);
        Assert.Equal(LeaseEndReason.ContainerLost, lost.EndReason);
    }

    [Fact]
    public async Task Grace_expiry_ends_suspended_leases_as_host_disconnect_at_last_healthy()
    {
        var fx = await ReadyAsync();
        var lease = await fx.SeedActiveLeaseAsync();

        fx.Clock.Advance(TimeSpan.FromSeconds(60));
        await fx.Reconciler.SuspendHostLeasesAsync(fx.HostId, T0.AddSeconds(60));
        fx.Clock.Advance(TimeSpan.FromSeconds(90)); // grace elapses with no reconnect

        var ended = await fx.Reconciler.EndSuspendedHostLeasesAsync(fx.HostId, T0.AddSeconds(60));

        Assert.Equal(1, ended);
        var stored = await fx.ReloadAsync(lease.Id);
        Assert.Equal(LeaseStatus.Ended, stored!.Status);
        Assert.Equal(LeaseEndReason.HostDisconnect, stored.EndReason);
        Assert.Equal(T0.AddSeconds(60), stored.EndedAt); // finalized at last-healthy
        Assert.Equal(60, stored.BillableSeconds);         // never billed past last-healthy
    }

    [Fact]
    public async Task Reconcile_and_end_are_no_ops_when_nothing_is_suspended()
    {
        var fx = await ReadyAsync();
        await fx.SeedActiveLeaseAsync(); // active, not suspended

        var reconcile = await fx.Reconciler.ReconcileHostAsync(
            fx.HostId, Array.Empty<Guid>(), lastHealthyAt: T0);
        var ended = await fx.Reconciler.EndSuspendedHostLeasesAsync(fx.HostId, T0);

        Assert.Empty(reconcile.Resumed);
        Assert.Empty(reconcile.ContainerLost);
        Assert.Equal(0, ended);
    }
}
