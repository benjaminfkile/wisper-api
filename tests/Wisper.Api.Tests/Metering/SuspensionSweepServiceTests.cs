using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wisper.Api.Domain;
using Wisper.Api.Leases;
using Wisper.Api.Ledger;
using Wisper.Api.Metering;
using Wisper.Api.Persistence;
using Wisper.Api.Persistence.Hosts;
using Wisper.Api.Persistence.Leases;
using Wisper.Api.Persistence.Policy;
using Wisper.Api.Policy;
using Wisper.Api.Tests.TestSupport;
using Wisper.Api.Tunnel;
using Xunit;
using Host = Wisper.Api.Domain.Host;

namespace Wisper.Api.Tests.Metering;

/// <summary>
/// Unit tests for the durable grace backstop (task #55) — <see cref="LeaseReconciliationService.SweepStaleSuspendedLeasesAsync"/>
/// and <see cref="SuspensionSweepService.RunOnceAsync"/>. The scenario is the highest-severity availability
/// bug the task fixes: a wisper-api restart while a host is in grace strands its leases in <c>suspended</c>
/// forever (wallet hold never released, host + consumer concurrency slots consumed forever). The sweep
/// discovers stale suspended rows via the durable <c>suspended_at</c> stamp and ends them as
/// <c>host_disconnect</c> on the same finalize path the in-memory grace timer uses.
/// </summary>
public class SuspensionSweepServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 12, 0, 0, 0, TimeSpan.Zero);

    // 60¢/min = exactly 1¢/second — readable arithmetic.
    private const long PricePerMin = 60;
    private const int FeeBps = 1500;
    private const int GraceSeconds = 90;

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
        public WalletLeaseGate WalletGate { get; }
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
            var fraud = new FraudGuardService(
                Ledger, Leases, Policy, Clock, NullLogger<FraudGuardService>.Instance);
            WalletGate = new WalletLeaseGate(
                Ledger, Leases, Policy, fraud, NullLogger<WalletLeaseGate>.Instance);
            Reconciler = new LeaseReconciliationService(
                Leases, Meter, WalletGate, Clock, NullLogger<LeaseReconciliationService>.Instance);
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

        public async Task<Lease> SeedActiveLeaseAsync(long holdCents = 3600, int ttlSeconds = 3600)
        {
            var lease = await Leases.CreateAsync(new Lease
            {
                Id = Guid.NewGuid(),
                ConsumerUserId = ConsumerId,
                HostId = HostId,
                HostImageId = Guid.NewGuid(),
                ImageRef = "reg/wisp-base:latest",
                Network = NetworkMode.Open,
                TtlSeconds = ttlSeconds,
                PriceCentsPerMin = PricePerMin,
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

        public async Task<long> HoldsCentsForAsync(Guid leaseId)
        {
            var holds = await Ledger.GetOrCreateAccountAsync(LedgerAccountKind.LeaseHolds, null);
            var entries = await Ledger.ListEntriesForAccountAsync(holds.Id);
            long sum = 0;
            foreach (var entry in entries.Where(e => e.LeaseId == leaseId))
            {
                sum += entry.CreditCents - entry.DebitCents;
            }
            return sum;
        }
    }

    private static async Task<Fixture> ReadyAsync()
    {
        var fx = new Fixture();
        await fx.SeedAsync();
        return fx;
    }

    // ---- AC180: durable grace — a fresh coordinator sweeps and ends a stranded suspended lease ----

    [Fact]
    public async Task Sweep_ends_a_suspended_lease_whose_in_memory_grace_was_lost_across_a_restart()
    {
        // The bug: a wisper-api restart while a host is in grace strands its leases in `suspended`
        // forever (wallet hold never released, host + consumer concurrency slots consumed forever). The
        // sweep + durable suspended_at stamp fix it.
        var fx = await ReadyAsync();
        var lease = await fx.SeedActiveLeaseAsync();

        // 60 healthy seconds, then the tunnel drops → suspend. suspended_at is stamped durably (wall-clock).
        fx.Clock.Advance(TimeSpan.FromSeconds(60));
        var lastHealthy = T0.AddSeconds(60);
        await fx.Reconciler.SuspendHostLeasesAsync(fx.HostId, lastHealthy);
        var suspended = await fx.ReloadAsync(lease.Id);
        Assert.Equal(LeaseStatus.Suspended, suspended!.Status);
        Assert.Equal(fx.Clock.GetUtcNow(), suspended.SuspendedAt);

        // Simulate a wisper-api restart with the host inside grace: the coordinator's in-memory _grace
        // dictionary is empty (we never armed it here). Advance past grace + safety.
        fx.Clock.Advance(TimeSpan.FromSeconds(GraceSeconds + 30 + 1));

        // Sweep with an empty "hosts under in-process grace" set (the fresh coordinator has none).
        var ended = await fx.Reconciler.SweepStaleSuspendedLeasesAsync(
            graceWithSafetyMargin: TimeSpan.FromSeconds(GraceSeconds + 30),
            hostsUnderInProcessGrace: new HashSet<Guid>());
        Assert.Equal(1, ended);

        var reaped = await fx.ReloadAsync(lease.Id);
        Assert.Equal(LeaseStatus.Ended, reaped!.Status);
        Assert.Equal(LeaseEndReason.HostDisconnect, reaped.EndReason);
        Assert.NotNull(reaped.EndedAt);
        Assert.Null(reaped.SuspendedAt); // cleared on transition off suspended

        // Wallet hold is released (the whole point of the fix — otherwise the hold sits forever).
        Assert.Equal(0, await fx.HoldsCentsForAsync(lease.Id));

        // Consumer + host concurrency slots are freed (they counted suspended as live).
        Assert.Equal(0, await fx.Leases.CountActiveByHostAsync(fx.HostId));
    }

    [Fact]
    public async Task Sweep_via_hosted_service_run_once_ends_stranded_lease_within_one_interval()
    {
        // AC180 end-to-end: the hosted service's public RunOnceAsync (the same code path the timer drives
        // each tick) reaps a stale suspended lease using its wired-in TunnelDisconnectCoordinator (which
        // has no armed grace timers — the "fresh coordinator" case).
        var fx = await ReadyAsync();
        var lease = await fx.SeedActiveLeaseAsync();

        fx.Clock.Advance(TimeSpan.FromSeconds(60));
        await fx.Reconciler.SuspendHostLeasesAsync(fx.HostId, T0.AddSeconds(60));

        // Fresh coordinator — mirrors the state after a wisper-api restart.
        var coordinator = new TunnelDisconnectCoordinator(
            fx.Reconciler,
            new StaticOptionsMonitor<TunnelOptions>(new TunnelOptions { GraceSeconds = GraceSeconds }),
            fx.Clock,
            NullLogger<TunnelDisconnectCoordinator>.Instance);
        var sweep = new SuspensionSweepService(
            fx.Reconciler,
            coordinator,
            new StaticOptionsMonitor<TunnelOptions>(new TunnelOptions { GraceSeconds = GraceSeconds }),
            Options.Create(new MeteringOptions()),
            Db.Unconfigured,
            fx.Clock,
            NullLogger<SuspensionSweepService>.Instance);

        // Just past grace+safety: the sweep must reap it on this pass.
        fx.Clock.Advance(TimeSpan.FromSeconds(GraceSeconds + 30 + 1));
        var ended = await sweep.RunOnceAsync();

        Assert.Equal(1, ended);
        var reaped = await fx.ReloadAsync(lease.Id);
        Assert.Equal(LeaseStatus.Ended, reaped!.Status);
        Assert.Equal(LeaseEndReason.HostDisconnect, reaped.EndReason);
    }

    // ---- AC184: within-grace leases must NOT be ended early ----

    [Fact]
    public async Task Sweep_does_not_end_a_lease_still_within_the_grace_window()
    {
        var fx = await ReadyAsync();
        var lease = await fx.SeedActiveLeaseAsync();

        fx.Clock.Advance(TimeSpan.FromSeconds(60));
        await fx.Reconciler.SuspendHostLeasesAsync(fx.HostId, T0.AddSeconds(60));

        // Advance only 30s — well inside the grace + safety window.
        fx.Clock.Advance(TimeSpan.FromSeconds(30));

        var ended = await fx.Reconciler.SweepStaleSuspendedLeasesAsync(
            graceWithSafetyMargin: TimeSpan.FromSeconds(GraceSeconds + 30),
            hostsUnderInProcessGrace: new HashSet<Guid>());
        Assert.Equal(0, ended);

        // Lease is still suspended, waiting for either a reconnect or the timer/sweep.
        var stored = await fx.ReloadAsync(lease.Id);
        Assert.Equal(LeaseStatus.Suspended, stored!.Status);
        Assert.NotNull(stored.SuspendedAt);
    }

    [Fact]
    public async Task Sweep_skips_hosts_whose_in_memory_grace_timer_is_currently_armed()
    {
        // The fast path is the in-memory grace timer (task #55): while it is armed on THIS instance, the
        // sweep must NOT race it — the sweep is the durable backstop, not a parallel decision maker.
        var fx = await ReadyAsync();
        var lease = await fx.SeedActiveLeaseAsync();

        fx.Clock.Advance(TimeSpan.FromSeconds(60));
        await fx.Reconciler.SuspendHostLeasesAsync(fx.HostId, T0.AddSeconds(60));
        fx.Clock.Advance(TimeSpan.FromSeconds(GraceSeconds + 30 + 1));

        var ended = await fx.Reconciler.SweepStaleSuspendedLeasesAsync(
            graceWithSafetyMargin: TimeSpan.FromSeconds(GraceSeconds + 30),
            hostsUnderInProcessGrace: new HashSet<Guid> { fx.HostId });

        Assert.Equal(0, ended);
        Assert.Equal(LeaseStatus.Suspended, (await fx.ReloadAsync(lease.Id))!.Status);
    }

    // ---- AC182: idempotency — twice, or two instances, converge on exactly one end + one release ----

    [Fact]
    public async Task Sweep_is_idempotent_running_twice_produces_exactly_one_end_and_one_release()
    {
        var fx = await ReadyAsync();
        var lease = await fx.SeedActiveLeaseAsync();

        fx.Clock.Advance(TimeSpan.FromSeconds(60));
        await fx.Reconciler.SuspendHostLeasesAsync(fx.HostId, T0.AddSeconds(60));
        fx.Clock.Advance(TimeSpan.FromSeconds(GraceSeconds + 30 + 1));

        var first = await fx.Reconciler.SweepStaleSuspendedLeasesAsync(
            graceWithSafetyMargin: TimeSpan.FromSeconds(GraceSeconds + 30),
            hostsUnderInProcessGrace: new HashSet<Guid>());
        var second = await fx.Reconciler.SweepStaleSuspendedLeasesAsync(
            graceWithSafetyMargin: TimeSpan.FromSeconds(GraceSeconds + 30),
            hostsUnderInProcessGrace: new HashSet<Guid>());

        // Exactly one end transition (CAS guard on suspended → ended); second pass finds nothing to sweep.
        Assert.Equal(1, first);
        Assert.Equal(0, second);

        var reaped = await fx.ReloadAsync(lease.Id);
        Assert.Equal(LeaseStatus.Ended, reaped!.Status);
        Assert.Equal(LeaseEndReason.HostDisconnect, reaped.EndReason);

        // Hold released exactly once — the ledger's key-per-hold-generation dedupes anyway, but this
        // captures the end-state: zero net earmark after the sweep converges.
        Assert.Equal(0, await fx.HoldsCentsForAsync(lease.Id));
    }

    [Fact]
    public async Task Sweep_two_concurrent_instances_produce_exactly_one_end_transition()
    {
        // AC182: the concurrent-instances race. Two "instances" of the reconciler racing on the same DB
        // (shared repo/ledger state) must converge — the CAS guard makes one lose the update.
        var fx = await ReadyAsync();
        var lease = await fx.SeedActiveLeaseAsync();

        fx.Clock.Advance(TimeSpan.FromSeconds(60));
        await fx.Reconciler.SuspendHostLeasesAsync(fx.HostId, T0.AddSeconds(60));
        fx.Clock.Advance(TimeSpan.FromSeconds(GraceSeconds + 30 + 1));

        // Second reconciler over the SAME repositories (models a second wisper-api instance sharing the DB).
        var reconcilerB = new LeaseReconciliationService(
            fx.Leases, fx.Meter, fx.WalletGate, fx.Clock, NullLogger<LeaseReconciliationService>.Instance);

        var results = await Task.WhenAll(
            fx.Reconciler.SweepStaleSuspendedLeasesAsync(
                TimeSpan.FromSeconds(GraceSeconds + 30), new HashSet<Guid>()),
            reconcilerB.SweepStaleSuspendedLeasesAsync(
                TimeSpan.FromSeconds(GraceSeconds + 30), new HashSet<Guid>()));

        Assert.Equal(1, results.Sum()); // exactly one instance transitioned the lease

        var reaped = await fx.ReloadAsync(lease.Id);
        Assert.Equal(LeaseStatus.Ended, reaped!.Status);
        Assert.Equal(0, await fx.HoldsCentsForAsync(lease.Id));
    }

    // ---- AC183: migration semantics — suspend sets, resume/revive clears ----

    [Fact]
    public async Task Suspend_stamps_suspended_at_and_resume_clears_it()
    {
        var fx = await ReadyAsync();
        var lease = await fx.SeedActiveLeaseAsync();

        Assert.Null(lease.SuspendedAt);

        fx.Clock.Advance(TimeSpan.FromSeconds(60));
        var suspendMoment = fx.Clock.GetUtcNow();
        await fx.Reconciler.SuspendHostLeasesAsync(fx.HostId, T0.AddSeconds(60));

        var suspended = await fx.ReloadAsync(lease.Id);
        Assert.Equal(LeaseStatus.Suspended, suspended!.Status);
        Assert.Equal(suspendMoment, suspended.SuspendedAt);

        // Reconnect within grace → resume clears suspended_at (via TransitionStateAsync's CASE).
        fx.Clock.Advance(TimeSpan.FromSeconds(30));
        await fx.Reconciler.ReconcileHostAsync(
            fx.HostId, new[] { lease.Id }, lastHealthyAt: T0.AddSeconds(60));

        var resumed = await fx.ReloadAsync(lease.Id);
        Assert.Equal(LeaseStatus.Active, resumed!.Status);
        Assert.Null(resumed.SuspendedAt);
    }

    [Fact]
    public async Task Revive_after_grace_expiry_clears_suspended_at()
    {
        // Post-grace revive path: the lease was suspended then ended (host_disconnect), and a later
        // reconnect revives it. suspended_at must not carry through the revival.
        var fx = await ReadyAsync();
        var lease = await fx.SeedActiveLeaseAsync();

        fx.Clock.Advance(TimeSpan.FromSeconds(60));
        await fx.Reconciler.SuspendHostLeasesAsync(fx.HostId, T0.AddSeconds(60));
        Assert.NotNull((await fx.ReloadAsync(lease.Id))!.SuspendedAt);

        // Grace expires — end as host_disconnect. suspended_at should also clear on this transition.
        await fx.Reconciler.EndSuspendedHostLeasesAsync(fx.HostId, T0.AddSeconds(60));
        var ended = await fx.ReloadAsync(lease.Id);
        Assert.Equal(LeaseStatus.Ended, ended!.Status);
        Assert.Null(ended.SuspendedAt); // cleared on suspended → ended (task #55)

        // Reconnect after grace: revive re-clears (defensively) even though it was already null.
        fx.Clock.Advance(TimeSpan.FromMinutes(5));
        await fx.Reconciler.RevivePostGraceAsync(fx.HostId, new[] { lease.Id }, fx.Clock.GetUtcNow());
        var revived = await fx.ReloadAsync(lease.Id);
        Assert.Equal(LeaseStatus.Active, revived!.Status);
        Assert.Null(revived.SuspendedAt);
    }

    // ---- AC181: reconnect after restart — heartbeat resolves the suspended set ----

    [Fact]
    public async Task Reconnect_after_restart_resumes_reported_and_ends_unreported_suspended()
    {
        // AC181: after a wisper-api restart the coordinator's in-memory grace is gone; the first heartbeat
        // must resume host-reported suspended leases (container still running) and end unreported ones as
        // container_lost. Two leases in one host — one reported live, one gone.
        var fx = await ReadyAsync();
        var alive = await fx.SeedActiveLeaseAsync();
        var gone = await fx.SeedActiveLeaseAsync();

        fx.Clock.Advance(TimeSpan.FromSeconds(60));
        await fx.Reconciler.SuspendHostLeasesAsync(fx.HostId, T0.AddSeconds(60));
        Assert.Equal(LeaseStatus.Suspended, (await fx.ReloadAsync(alive.Id))!.Status);
        Assert.Equal(LeaseStatus.Suspended, (await fx.ReloadAsync(gone.Id))!.Status);

        // Restart (no in-memory grace). Agent reconnects and heartbeats — reports only `alive`.
        fx.Clock.Advance(TimeSpan.FromSeconds(20));
        var heartbeatAt = fx.Clock.GetUtcNow();
        var outcome = await fx.Reconciler.ReconcileHeartbeatAsync(fx.HostId, new[] { alive.Id });

        Assert.Equal(new[] { alive.Id }, outcome.Revived);
        Assert.Equal(new[] { gone.Id }, outcome.ContainerLost);

        var aliveAfter = await fx.ReloadAsync(alive.Id);
        Assert.Equal(LeaseStatus.Active, aliveAfter!.Status);
        Assert.Null(aliveAfter.SuspendedAt);
        Assert.Equal(heartbeatAt, aliveAfter.LastMeteredAt); // gap not billed
        Assert.Equal(60, aliveAfter.BillableSeconds);

        var goneAfter = await fx.ReloadAsync(gone.Id);
        Assert.Equal(LeaseStatus.Ended, goneAfter!.Status);
        Assert.Equal(LeaseEndReason.ContainerLost, goneAfter.EndReason);
        Assert.Equal(60, goneAfter.BillableSeconds);
        Assert.Equal(0, await fx.HoldsCentsForAsync(gone.Id));
    }
}
