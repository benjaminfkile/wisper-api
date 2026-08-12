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
            var fraud = new FraudGuardService(
                Ledger, Leases, Policy, Clock, NullLogger<FraudGuardService>.Instance);
            var walletGate = new WalletLeaseGate(
                Ledger, Leases, Policy, fraud, NullLogger<WalletLeaseGate>.Instance);
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

    // ---- Post-grace revival (docs/TUNNEL.md §8) ----

    [Fact]
    public async Task RevivePostGrace_revives_a_lease_ended_as_host_disconnect()
    {
        var fx = await ReadyAsync();
        var lease = await fx.SeedActiveLeaseAsync();

        // Drop + grace expiry: lease ends as host_disconnect.
        fx.Clock.Advance(TimeSpan.FromSeconds(60));
        await fx.Reconciler.SuspendHostLeasesAsync(fx.HostId, T0.AddSeconds(60));
        await fx.Reconciler.EndSuspendedHostLeasesAsync(fx.HostId, T0.AddSeconds(60));
        var beforeRevive = await fx.ReloadAsync(lease.Id);
        Assert.Equal(LeaseStatus.Ended, beforeRevive!.Status);
        Assert.Equal(LeaseEndReason.HostDisconnect, beforeRevive.EndReason);

        // Host reconnects after grace (container still running): revive the lease.
        fx.Clock.Advance(TimeSpan.FromMinutes(5)); // T0+360, well past grace
        var reconnectAt = fx.Clock.GetUtcNow();
        var outcome = await fx.Reconciler.RevivePostGraceAsync(fx.HostId, new[] { lease.Id }, reconnectAt);

        Assert.Equal(new[] { lease.Id }, outcome.Revived);
        Assert.Empty(outcome.Orphaned);

        var revived = await fx.ReloadAsync(lease.Id);
        Assert.Equal(lease.Id, revived!.Id);              // same lease id preserved
        Assert.Equal(LeaseStatus.Active, revived.Status);  // back to active
        Assert.Null(revived.EndReason);                    // end_reason cleared
        Assert.Null(revived.EndedAt);                      // ended_at cleared
        Assert.Equal(reconnectAt, revived.LastMeteredAt);  // meter restarts at reconnect
        Assert.Equal(60, revived.BillableSeconds);          // pre-drop usage preserved
    }

    [Fact]
    public async Task RevivePostGrace_meter_watermark_is_at_reconnect_not_at_last_healthy()
    {
        // The offline gap (last-healthy → reconnect) must never be billed: the meter watermark is
        // set to the reconnect instant so the next tick only accrues from that point forward.
        var fx = await ReadyAsync();
        var lease = await fx.SeedActiveLeaseAsync();

        fx.Clock.Advance(TimeSpan.FromSeconds(60));
        await fx.Reconciler.SuspendHostLeasesAsync(fx.HostId, T0.AddSeconds(60));
        await fx.Reconciler.EndSuspendedHostLeasesAsync(fx.HostId, T0.AddSeconds(60));

        // Reconnect 5 minutes after grace expiry (container was still running).
        fx.Clock.Advance(TimeSpan.FromMinutes(5));
        var reconnectAt = fx.Clock.GetUtcNow(); // T0+360
        await fx.Reconciler.RevivePostGraceAsync(fx.HostId, new[] { lease.Id }, reconnectAt);

        var revived = await fx.ReloadAsync(lease.Id);
        // Meter restarts at reconnect, not at last-healthy: the 5-minute offline gap is unbillable.
        Assert.Equal(reconnectAt, revived!.LastMeteredAt);
        Assert.Equal(60, revived.BillableSeconds); // pre-drop usage preserved, gap not accrued
    }

    [Fact]
    public async Task RevivePostGrace_skips_a_lease_ended_for_other_reasons()
    {
        var fx = await ReadyAsync();
        var lease = await fx.SeedActiveLeaseAsync();

        // End the lease as released (consumer cancelled), not host_disconnect.
        await fx.Leases.TransitionStateAsync(
            lease.Id, LeaseStatus.Ended, endReason: LeaseEndReason.Released, endedAt: T0);

        var outcome = await fx.Reconciler.RevivePostGraceAsync(fx.HostId, new[] { lease.Id }, T0);

        Assert.Empty(outcome.Revived);
        Assert.Equal(new[] { lease.Id }, outcome.Orphaned);

        var stored = await fx.ReloadAsync(lease.Id);
        Assert.Equal(LeaseStatus.Ended, stored!.Status); // unchanged
        Assert.Equal(LeaseEndReason.Released, stored.EndReason);
    }

    [Fact]
    public async Task RevivePostGrace_skips_an_already_active_lease()
    {
        var fx = await ReadyAsync();
        var lease = await fx.SeedActiveLeaseAsync(); // already active

        var outcome = await fx.Reconciler.RevivePostGraceAsync(fx.HostId, new[] { lease.Id }, T0);

        Assert.Empty(outcome.Revived);
        Assert.Empty(outcome.Orphaned); // already counted, not orphaned
        Assert.Equal(LeaseStatus.Active, (await fx.ReloadAsync(lease.Id))!.Status);
    }

    [Fact]
    public async Task RevivePostGrace_reports_unknown_lease_ids_as_orphaned()
    {
        var fx = await ReadyAsync();
        var unknownId = Guid.NewGuid();

        var outcome = await fx.Reconciler.RevivePostGraceAsync(fx.HostId, new[] { unknownId }, T0);

        Assert.Empty(outcome.Revived);
        Assert.Equal(new[] { unknownId }, outcome.Orphaned);
    }

    [Fact]
    public async Task RevivePostGrace_is_a_no_op_when_no_contracts_reported()
    {
        var fx = await ReadyAsync();
        var lease = await fx.SeedActiveLeaseAsync();
        await fx.Reconciler.SuspendHostLeasesAsync(fx.HostId, T0);
        await fx.Reconciler.EndSuspendedHostLeasesAsync(fx.HostId, T0);

        var outcome = await fx.Reconciler.RevivePostGraceAsync(fx.HostId, Array.Empty<Guid>(), T0);

        Assert.Empty(outcome.Revived);
        Assert.Empty(outcome.Orphaned);

        // Lease remains ended — no containers were reported, nothing to revive.
        Assert.Equal(LeaseStatus.Ended, (await fx.ReloadAsync(lease.Id))!.Status);
    }

    // ---- Continuous heartbeat reconciliation (task #22) ----

    [Fact]
    public async Task ReconcileHeartbeat_ends_active_unreported_lease_as_container_lost()
    {
        // AC78: silent container death — the manager has an active lease the host no longer reports.
        // The heartbeat reconciliation must end it as container_lost without a tunnel disconnect.
        var fx = await ReadyAsync();
        var lease = await fx.SeedActiveLeaseAsync();

        fx.Clock.Advance(TimeSpan.FromSeconds(60));
        var outcome = await fx.Reconciler.ReconcileHeartbeatAsync(fx.HostId, Array.Empty<Guid>());

        Assert.Equal(new[] { lease.Id }, outcome.ContainerLost);
        Assert.Empty(outcome.Revived);
        Assert.Empty(outcome.Orphaned);

        var stored = await fx.ReloadAsync(lease.Id);
        Assert.Equal(LeaseStatus.Ended, stored!.Status);
        Assert.Equal(LeaseEndReason.ContainerLost, stored.EndReason);
        // Finalized at the heartbeat instant (not at a prior last-healthy): billing runs to now.
        Assert.Equal(fx.Clock.GetUtcNow(), stored.EndedAt);
    }

    [Fact]
    public async Task ReconcileHeartbeat_billing_is_flushed_before_container_lost_end()
    {
        // Ending a silently-dead active lease must flush the accrued billing first so the charged total
        // reflects the full healthy interval up to the heartbeat instant.
        var fx = await ReadyAsync();
        var lease = await fx.SeedActiveLeaseAsync();

        fx.Clock.Advance(TimeSpan.FromSeconds(60));
        await fx.Reconciler.ReconcileHeartbeatAsync(fx.HostId, Array.Empty<Guid>());

        var stored = await fx.ReloadAsync(lease.Id);
        Assert.Equal(LeaseStatus.Ended, stored!.Status);
        Assert.Equal(60, stored.BillableSeconds); // 60s billed up to the heartbeat instant
    }

    [Fact]
    public async Task ReconcileHeartbeat_steady_state_matching_set_produces_zero_writes()
    {
        // AC80: when reported set == manager active set, the method must return empty and touch no rows.
        var fx = await ReadyAsync();
        var lease = await fx.SeedActiveLeaseAsync();

        var before = await fx.ReloadAsync(lease.Id);

        // Heartbeat reports exactly the active set — nothing to heal.
        var outcome = await fx.Reconciler.ReconcileHeartbeatAsync(fx.HostId, new[] { lease.Id });

        Assert.False(outcome.HasChanges);
        Assert.Empty(outcome.ContainerLost);
        Assert.Empty(outcome.Revived);
        Assert.Empty(outcome.Orphaned);
        Assert.Same(HeartbeatReconcileOutcome.Empty, outcome); // singleton fast-path

        // Record must be byte-for-byte identical — no write occurred.
        var after = await fx.ReloadAsync(lease.Id);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task ReconcileHeartbeat_empty_host_with_empty_report_is_zero_writes()
    {
        // Edge: no active leases and no reported leases → both sets empty → zero writes.
        var fx = await ReadyAsync();

        var outcome = await fx.Reconciler.ReconcileHeartbeatAsync(fx.HostId, Array.Empty<Guid>());

        Assert.Same(HeartbeatReconcileOutcome.Empty, outcome);
        Assert.False(outcome.HasChanges);
    }

    [Fact]
    public async Task ReconcileHeartbeat_revives_host_disconnect_ended_lease()
    {
        // AC79: a live contract with no active manager lease — ended as host_disconnect — must be revived.
        var fx = await ReadyAsync();
        var lease = await fx.SeedActiveLeaseAsync();

        // Simulate the lease being ended as host_disconnect (e.g. missed frame / manager restart).
        fx.Clock.Advance(TimeSpan.FromSeconds(60));
        await fx.Reconciler.SuspendHostLeasesAsync(fx.HostId, T0.AddSeconds(60));
        await fx.Reconciler.EndSuspendedHostLeasesAsync(fx.HostId, T0.AddSeconds(60));
        Assert.Equal(LeaseStatus.Ended, (await fx.ReloadAsync(lease.Id))!.Status);

        // A heartbeat that reports the lease as still live must revive it.
        fx.Clock.Advance(TimeSpan.FromSeconds(15)); // T0+75
        var heartbeatAt = fx.Clock.GetUtcNow();
        var outcome = await fx.Reconciler.ReconcileHeartbeatAsync(fx.HostId, new[] { lease.Id });

        Assert.Equal(new[] { lease.Id }, outcome.Revived);
        Assert.Empty(outcome.ContainerLost);
        Assert.Empty(outcome.Orphaned);

        var revived = await fx.ReloadAsync(lease.Id);
        Assert.Equal(LeaseStatus.Active, revived!.Status);
        Assert.Null(revived.EndReason);
        Assert.Null(revived.EndedAt);
        Assert.Equal(heartbeatAt, revived.LastMeteredAt); // billing restarts at heartbeat time
        Assert.Equal(60, revived.BillableSeconds);         // pre-disconnect usage preserved
    }

    [Fact]
    public async Task ReconcileHeartbeat_orphans_reported_lease_ended_for_other_reason()
    {
        // A reported lease that was ended for a non-host_disconnect reason cannot be revived — orphan it.
        var fx = await ReadyAsync();
        var lease = await fx.SeedActiveLeaseAsync();
        await fx.Leases.TransitionStateAsync(
            lease.Id, LeaseStatus.Ended, endReason: LeaseEndReason.Released, endedAt: T0);

        var outcome = await fx.Reconciler.ReconcileHeartbeatAsync(fx.HostId, new[] { lease.Id });

        Assert.Empty(outcome.ContainerLost);
        Assert.Empty(outcome.Revived);
        Assert.Equal(new[] { lease.Id }, outcome.Orphaned);

        // Lease stays ended with original reason — we must not touch purposefully-ended leases.
        var stored = await fx.ReloadAsync(lease.Id);
        Assert.Equal(LeaseStatus.Ended, stored!.Status);
        Assert.Equal(LeaseEndReason.Released, stored.EndReason);
    }

    [Fact]
    public async Task ReconcileHeartbeat_orphans_unknown_reported_lease_id()
    {
        var fx = await ReadyAsync();
        var unknownId = Guid.NewGuid();

        var outcome = await fx.Reconciler.ReconcileHeartbeatAsync(fx.HostId, new[] { unknownId });

        Assert.Empty(outcome.ContainerLost);
        Assert.Empty(outcome.Revived);
        Assert.Equal(new[] { unknownId }, outcome.Orphaned);
    }

    [Fact]
    public async Task ReconcileHeartbeat_set_diff_ends_absent_and_revives_present_together()
    {
        // Mixed drift: one active lease silently died, one ended/host_disconnect needs revival.
        var fx = await ReadyAsync();
        var dying = await fx.SeedActiveLeaseAsync();
        var revivable = await fx.SeedActiveLeaseAsync();

        // End revivable as host_disconnect to simulate missed teardown.
        await fx.Leases.TransitionStateAsync(
            revivable.Id, LeaseStatus.Ended, endReason: LeaseEndReason.HostDisconnect, endedAt: T0);

        fx.Clock.Advance(TimeSpan.FromSeconds(15));

        // Heartbeat: reports revivable (still running) but NOT dying (silently dead).
        var outcome = await fx.Reconciler.ReconcileHeartbeatAsync(
            fx.HostId, new[] { revivable.Id });

        Assert.Equal(new[] { dying.Id }, outcome.ContainerLost);
        Assert.Equal(new[] { revivable.Id }, outcome.Revived);
        Assert.Empty(outcome.Orphaned);

        Assert.Equal(LeaseStatus.Ended, (await fx.ReloadAsync(dying.Id))!.Status);
        Assert.Equal(LeaseEndReason.ContainerLost, (await fx.ReloadAsync(dying.Id))!.EndReason);
        Assert.Equal(LeaseStatus.Active, (await fx.ReloadAsync(revivable.Id))!.Status);
    }

    [Fact]
    public async Task ReconcileHeartbeat_is_idempotent_for_container_lost()
    {
        // AC81: repeated heartbeats that omit a silently-dead lease converge — second call is a no-op.
        var fx = await ReadyAsync();
        var lease = await fx.SeedActiveLeaseAsync();

        fx.Clock.Advance(TimeSpan.FromSeconds(60));
        var first = await fx.Reconciler.ReconcileHeartbeatAsync(fx.HostId, Array.Empty<Guid>());
        Assert.Equal(new[] { lease.Id }, first.ContainerLost);

        // Second heartbeat: lease is already ended — active set is empty, reported set is empty.
        var second = await fx.Reconciler.ReconcileHeartbeatAsync(fx.HostId, Array.Empty<Guid>());
        Assert.Same(HeartbeatReconcileOutcome.Empty, second); // fast-path: both sets empty
        Assert.False(second.HasChanges);

        // Lease is still Ended — idempotent.
        Assert.Equal(LeaseStatus.Ended, (await fx.ReloadAsync(lease.Id))!.Status);
    }

    [Fact]
    public async Task ReconcileHeartbeat_is_idempotent_for_revival()
    {
        // AC81: repeated heartbeats that report a revivable lease converge — second call is a no-op.
        var fx = await ReadyAsync();
        var lease = await fx.SeedActiveLeaseAsync();
        await fx.Reconciler.SuspendHostLeasesAsync(fx.HostId, T0);
        await fx.Reconciler.EndSuspendedHostLeasesAsync(fx.HostId, T0);

        fx.Clock.Advance(TimeSpan.FromSeconds(15));
        var first = await fx.Reconciler.ReconcileHeartbeatAsync(fx.HostId, new[] { lease.Id });
        Assert.Equal(new[] { lease.Id }, first.Revived);

        // Second heartbeat: lease is now active, reported set matches active set.
        var second = await fx.Reconciler.ReconcileHeartbeatAsync(fx.HostId, new[] { lease.Id });
        Assert.Same(HeartbeatReconcileOutcome.Empty, second); // steady-state fast-path
        Assert.False(second.HasChanges);

        Assert.Equal(LeaseStatus.Active, (await fx.ReloadAsync(lease.Id))!.Status);
    }

    [Fact]
    public async Task ReconcileHeartbeat_suspended_leases_are_not_touched()
    {
        // Suspended leases are under grace-window management and must not be affected by heartbeat
        // reconciliation (ending or reviving them here would race with the grace path).
        var fx = await ReadyAsync();
        var lease = await fx.SeedActiveLeaseAsync();

        // Suspend the lease (simulates a mid-grace state).
        await fx.Leases.TransitionStateAsync(lease.Id, LeaseStatus.Suspended);

        // Heartbeat reports no leases — but the suspended lease must not be ended here.
        var outcome = await fx.Reconciler.ReconcileHeartbeatAsync(fx.HostId, Array.Empty<Guid>());

        // Both the active set (empty after filtering suspended) and the reported set (empty) are equal
        // → fast-path, zero writes.
        Assert.Same(HeartbeatReconcileOutcome.Empty, outcome);
        Assert.Equal(LeaseStatus.Suspended, (await fx.ReloadAsync(lease.Id))!.Status);
    }
}
