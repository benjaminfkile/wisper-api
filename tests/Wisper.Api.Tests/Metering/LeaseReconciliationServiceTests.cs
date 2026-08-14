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

        public async Task<Lease> SeedActiveLeaseAsync(
            long price = PricePerMin, long holdCents = 3600, int ttlSeconds = 3600)
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
            if (holdCents <= 0)
            {
                // Free image: no hold posted at create (docs/PAYMENTS.md §4). Nothing to fund.
                return;
            }

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

        /// <summary>Consumer wallet balance in cents (topped-up cash minus outstanding holds).</summary>
        public Task<long> WalletCentsAsync() => Ledger.GetWalletBalanceCentsAsync(ConsumerId);

        /// <summary>Total earmarked-in-lease_holds balance across every lease id.</summary>
        public async Task<long> HoldsCentsAsync()
        {
            var holds = await Ledger.GetOrCreateAccountAsync(LedgerAccountKind.LeaseHolds, null);
            return holds.BalanceCents;
        }

        /// <summary>Signed net amount posted against <c>lease_holds</c> for a single lease id.</summary>
        public async Task<long> HoldsCentsForAsync(Guid leaseId)
        {
            var holds = await Ledger.GetOrCreateAccountAsync(LedgerAccountKind.LeaseHolds, null);
            var entries = await Ledger.ListEntriesForAccountAsync(holds.Id);
            long sum = 0;
            foreach (var entry in entries.Where(e => e.LeaseId == leaseId))
            {
                // lease_holds is credit-normal: credits grow the earmark, debits shrink it.
                sum += entry.CreditCents - entry.DebitCents;
            }
            return sum;
        }

        /// <summary>Count of <c>lease_hold</c> ledger transactions ever posted for a lease id.</summary>
        public async Task<int> HoldTxnCountForAsync(Guid leaseId)
        {
            var holds = await Ledger.GetOrCreateAccountAsync(LedgerAccountKind.LeaseHolds, null);
            var txns = await Ledger.ListAccountTransactionsAsync(holds.Id);
            return txns.Count(t =>
                t.Transaction.Kind == LedgerTxnKind.LeaseHold && t.Transaction.LeaseId == leaseId);
        }

        /// <summary>Tops the wallet up with cash so revive tests have funds beyond the initial hold.</summary>
        public async Task TopUpAsync(long cents, string tag)
        {
            var wallet = await Ledger.GetOrCreateAccountAsync(LedgerAccountKind.UserWallet, ConsumerId);
            var cash = await Ledger.GetOrCreateAccountAsync(LedgerAccountKind.PlatformCash, null);
            var fees = await Ledger.GetOrCreateAccountAsync(LedgerAccountKind.StripeFees, null);
            await Ledger.PostAsync(LedgerFlows.Topup(
                wallet.Id, cash.Id, fees.Id, grossAmountCents: cents, stripeFeeCents: 0,
                idempotencyKey: $"topup:{tag}"));
        }

        /// <summary>
        /// Drains the wallet by moving <paramref name="cents"/> back to platform_cash — the revive
        /// deny-path tests need the wallet empty at revive time to trigger insufficient funds. Adjustment
        /// keeps the ledger balanced and honors the non-negative wallet guard.
        /// </summary>
        public async Task DrainWalletAsync(long cents, string tag)
        {
            var wallet = await Ledger.GetOrCreateAccountAsync(LedgerAccountKind.UserWallet, ConsumerId);
            var cash = await Ledger.GetOrCreateAccountAsync(LedgerAccountKind.PlatformCash, null);
            await Ledger.PostAsync(LedgerFlows.Adjustment(
                debitAccountId: wallet.Id, creditAccountId: cash.Id, amountCents: cents,
                idempotencyKey: $"drain:{tag}"));
        }
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
    public async Task ReconcileHeartbeat_container_lost_flush_caps_at_lease_ttl()
    {
        // Task #34: the TTL-expiry / container-lost path (host reaped the container at TTL, we only
        // notice at the next heartbeat) must not bill past the lease TTL. Before the fix, the flush
        // used `now` (heartbeat instant) and over-billed the post-TTL tail.
        var fx = await ReadyAsync();
        var lease = await fx.SeedActiveLeaseAsync(ttlSeconds: 180, holdCents: 180);

        // Heartbeat arrives at 210s — 30s past the 180s TTL. The container is gone (not in the reported
        // set), so container-lost fires; billing must cap at 180s (started_at + ttl), not 210s.
        fx.Clock.Advance(TimeSpan.FromSeconds(210));
        var outcome = await fx.Reconciler.ReconcileHeartbeatAsync(fx.HostId, Array.Empty<Guid>());

        Assert.Equal(new[] { lease.Id }, outcome.ContainerLost);
        var stored = await fx.ReloadAsync(lease.Id);
        Assert.Equal(LeaseStatus.Ended, stored!.Status);
        Assert.Equal(180, stored.BillableSeconds); // capped at TTL, not 210s wall-clock
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

        // Move revivable through the full grace-expiry teardown (suspend → end as host_disconnect) so its
        // pre-drop hold is properly released back to the wallet, matching the state the reconciler leaves
        // a lease in when grace really expires (task #23 — the revive re-hold gate draws from this returned
        // balance). Suspending only revivable directly (not via SuspendHostLeasesAsync, which would take
        // dying with it) keeps dying Active so the container_lost set-diff arm still fires.
        await fx.Leases.TransitionStateAsync(revivable.Id, LeaseStatus.Suspended);
        await fx.Reconciler.EndSuspendedHostLeasesAsync(fx.HostId, T0);

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
    public async Task ReconcileHeartbeat_suspended_reported_lease_resumes_post_restart()
    {
        // Task #55: after a wisper-api restart the in-memory grace timer is gone, but the row is still
        // `suspended`. The coordinator routes the first heartbeat to ReconcileHeartbeatAsync (no in-memory
        // grace entry to route to the within-grace path). A suspended lease the host reports as still live
        // must resume — the container is still running.
        var fx = await ReadyAsync();
        var lease = await fx.SeedActiveLeaseAsync();

        // 60s healthy, then a suspend (simulates the pre-restart state).
        fx.Clock.Advance(TimeSpan.FromSeconds(60));
        await fx.Reconciler.SuspendHostLeasesAsync(fx.HostId, T0.AddSeconds(60));
        Assert.Equal(LeaseStatus.Suspended, (await fx.ReloadAsync(lease.Id))!.Status);
        Assert.NotNull((await fx.ReloadAsync(lease.Id))!.SuspendedAt);

        fx.Clock.Advance(TimeSpan.FromMinutes(2));
        var heartbeatAt = fx.Clock.GetUtcNow();
        var outcome = await fx.Reconciler.ReconcileHeartbeatAsync(fx.HostId, new[] { lease.Id });

        Assert.Equal(new[] { lease.Id }, outcome.Revived);
        Assert.Empty(outcome.ContainerLost);
        Assert.Empty(outcome.Orphaned);

        var resumed = await fx.ReloadAsync(lease.Id);
        Assert.Equal(LeaseStatus.Active, resumed!.Status);
        Assert.Null(resumed.SuspendedAt);                  // cleared on resume
        Assert.Equal(heartbeatAt, resumed.LastMeteredAt);  // gap never billed
        Assert.Equal(60, resumed.BillableSeconds);         // pre-drop usage preserved
    }

    [Fact]
    public async Task ReconcileHeartbeat_suspended_unreported_lease_ends_container_lost_post_restart()
    {
        // Task #55: after a wisper-api restart the in-memory grace timer is gone. A suspended lease the
        // host no longer reports must end as container_lost — the container is definitively gone, so we
        // must not strand the lease as suspended waiting for a timer that will never fire.
        var fx = await ReadyAsync();
        var lease = await fx.SeedActiveLeaseAsync();

        fx.Clock.Advance(TimeSpan.FromSeconds(60));
        await fx.Reconciler.SuspendHostLeasesAsync(fx.HostId, T0.AddSeconds(60));
        var suspendedAt = (await fx.ReloadAsync(lease.Id))!.SuspendedAt;
        Assert.NotNull(suspendedAt);

        // Host reconnects and reports NO live leases — container is gone.
        fx.Clock.Advance(TimeSpan.FromMinutes(2));
        var outcome = await fx.Reconciler.ReconcileHeartbeatAsync(fx.HostId, Array.Empty<Guid>());

        Assert.Equal(new[] { lease.Id }, outcome.ContainerLost);
        Assert.Empty(outcome.Revived);
        Assert.Empty(outcome.Orphaned);

        var ended = await fx.ReloadAsync(lease.Id);
        Assert.Equal(LeaseStatus.Ended, ended!.Status);
        Assert.Equal(LeaseEndReason.ContainerLost, ended.EndReason);
        Assert.Equal(suspendedAt, ended.EndedAt); // finalized at the durable suspend instant
        Assert.Equal(60, ended.BillableSeconds);  // never billed past last-healthy
    }

    // ---- Revive re-holds the wallet (task #23, docs/PAYMENTS.md §4) ----
    //
    // Grace expiry releases the pre-drop hold; a revived paid lease MUST re-place a hold covering its
    // remaining time before it returns to active, or it accrues charges with nothing earmarked (the
    // create-time 402 gate would never re-run). Insufficient funds at revive must not silently produce
    // an unbacked active lease; the lease is ended (payment_failed) and wisp's TTL reaper reclaims the
    // container.

    [Fact]
    public async Task RevivePostGrace_re_places_the_wallet_hold_for_the_remaining_lease_time()
    {
        // AC83: a revived paid lease has a wallet hold covering its remaining time.
        var fx = await ReadyAsync();
        var lease = await fx.SeedActiveLeaseAsync();

        // 60 healthy seconds → grace expiry: hold is released (3600¢ − 60¢ charged = 3540¢ back to wallet),
        // so the wallet now holds exactly the amount the revive path needs to re-earmark.
        fx.Clock.Advance(TimeSpan.FromSeconds(60));
        await fx.Reconciler.SuspendHostLeasesAsync(fx.HostId, T0.AddSeconds(60));
        await fx.Reconciler.EndSuspendedHostLeasesAsync(fx.HostId, T0.AddSeconds(60));
        var walletBeforeRevive = await fx.WalletCentsAsync();
        Assert.Equal(3540, walletBeforeRevive); // 3600 topped-up − 60 charged

        // Post-grace reconnect: revive re-places the hold.
        fx.Clock.Advance(TimeSpan.FromMinutes(5));
        var reconnectAt = fx.Clock.GetUtcNow();
        var outcome = await fx.Reconciler.RevivePostGraceAsync(fx.HostId, new[] { lease.Id }, reconnectAt);

        Assert.Equal(new[] { lease.Id }, outcome.Revived);
        var revived = await fx.ReloadAsync(lease.Id);
        Assert.Equal(LeaseStatus.Active, revived!.Status);

        // The revival hold sizes at ⌈(TtlSeconds − BillableSeconds)/60⌉·price = ⌈3540/60⌉·60 = 3540¢:
        // exactly what the grace-expiry release returned. Wallet is fully re-earmarked, lease_holds
        // shows the revival hold for this lease id, and the revive updated hold_txn_id to a new txn.
        Assert.Equal(0, await fx.WalletCentsAsync());
        Assert.Equal(3540, await fx.HoldsCentsForAsync(lease.Id));
        Assert.NotNull(revived.HoldTxnId);
    }

    [Fact]
    public async Task RevivePostGrace_denies_and_ends_the_lease_when_the_wallet_is_short()
    {
        // AC84: at revive with insufficient funds, do NOT leave the lease active-and-unbacked. Ended
        // (payment_failed) so wisp's TTL reaper reclaims the container; the lease is reported as orphaned
        // so the caller can also drive teardown.
        var fx = await ReadyAsync();
        var lease = await fx.SeedActiveLeaseAsync();

        fx.Clock.Advance(TimeSpan.FromSeconds(60));
        await fx.Reconciler.SuspendHostLeasesAsync(fx.HostId, T0.AddSeconds(60));
        await fx.Reconciler.EndSuspendedHostLeasesAsync(fx.HostId, T0.AddSeconds(60));

        // Drain the wallet after the release so the revive gate has nothing to work with (simulates a
        // consumer who spent the returned funds on a different lease in the outage window).
        await fx.DrainWalletAsync(3540, tag: "insufficient-funds-revive");
        Assert.Equal(0, await fx.WalletCentsAsync());

        fx.Clock.Advance(TimeSpan.FromMinutes(5));
        var reconnectAt = fx.Clock.GetUtcNow();
        var outcome = await fx.Reconciler.RevivePostGraceAsync(fx.HostId, new[] { lease.Id }, reconnectAt);

        Assert.Empty(outcome.Revived);
        Assert.Equal(new[] { lease.Id }, outcome.Orphaned);

        var stored = await fx.ReloadAsync(lease.Id);
        Assert.Equal(LeaseStatus.Ended, stored!.Status);
        Assert.Equal(LeaseEndReason.PaymentFailed, stored.EndReason);
        Assert.Equal(reconnectAt, stored.EndedAt); // re-stamped: end reason changed at revive-denied instant

        // Wallet stays empty, no phantom hold was placed for the lease id.
        Assert.Equal(0, await fx.WalletCentsAsync());
        Assert.Equal(0, await fx.HoldsCentsForAsync(lease.Id));
    }

    [Fact]
    public async Task RevivePostGrace_is_idempotent_and_does_not_stack_holds_on_repeated_flap()
    {
        // AC86: repeated revive/flap must not stack duplicate holds — hold ops are keyed by lease id in
        // the wallet gate. The second revive call sees the lease already Active and skips; even if it did
        // re-enter the wallet gate path, the revival hold key dedupes so no second hold posts.
        var fx = await ReadyAsync();
        var lease = await fx.SeedActiveLeaseAsync();

        fx.Clock.Advance(TimeSpan.FromSeconds(60));
        await fx.Reconciler.SuspendHostLeasesAsync(fx.HostId, T0.AddSeconds(60));
        await fx.Reconciler.EndSuspendedHostLeasesAsync(fx.HostId, T0.AddSeconds(60));

        fx.Clock.Advance(TimeSpan.FromMinutes(5));
        var reconnectAt = fx.Clock.GetUtcNow();

        // Two revive calls with the same reported set — a heartbeat flap after post-grace reconnect.
        var first = await fx.Reconciler.RevivePostGraceAsync(fx.HostId, new[] { lease.Id }, reconnectAt);
        var second = await fx.Reconciler.RevivePostGraceAsync(fx.HostId, new[] { lease.Id }, reconnectAt);

        Assert.Equal(new[] { lease.Id }, first.Revived);
        Assert.Empty(second.Revived); // second call sees lease Active, no re-revive

        // Exactly ONE revival hold posted (plus the original create-time hold = 2 lease_hold txns total).
        Assert.Equal(2, await fx.HoldTxnCountForAsync(lease.Id));
        // And the physical earmark for the lease matches the single revival hold, not stacked.
        Assert.Equal(3540, await fx.HoldsCentsForAsync(lease.Id));
    }

    [Fact]
    public async Task RevivePostGrace_repeated_wallet_gate_call_dedupes_at_ledger()
    {
        // AC86 (defense in depth): if the reconciler's state guard is bypassed, the wallet gate itself
        // dedupes on the revival hold's lease-id-keyed idempotency key so no second hold posts.
        var fx = await ReadyAsync();
        var lease = await fx.SeedActiveLeaseAsync();

        fx.Clock.Advance(TimeSpan.FromSeconds(60));
        await fx.Reconciler.SuspendHostLeasesAsync(fx.HostId, T0.AddSeconds(60));
        await fx.Reconciler.EndSuspendedHostLeasesAsync(fx.HostId, T0.AddSeconds(60));
        fx.Clock.Advance(TimeSpan.FromMinutes(5));

        // Fund extra so a second successful (non-dedup) post would visibly stack the earmark.
        await fx.TopUpAsync(cents: 3540, tag: "extra");

        var reconnectAt = fx.Clock.GetUtcNow();
        var first = await fx.WalletGate.PlaceRevivalHoldAsync(fx.ConsumerId, lease.Id, holdCents: 3540, "usd");
        var second = await fx.WalletGate.PlaceRevivalHoldAsync(fx.ConsumerId, lease.Id, holdCents: 3540, "usd");

        Assert.True(first.Allowed);
        Assert.True(second.Allowed);
        Assert.Equal(first.HoldTxnId, second.HoldTxnId); // dedup replay returns the same txn id

        // Physical earmark reflects a single hold, not two — the extra top-up is still in the wallet.
        Assert.Equal(3540, await fx.HoldsCentsForAsync(lease.Id));
        Assert.Equal(3540, await fx.WalletCentsAsync()); // the extra top-up untouched
    }

    [Fact]
    public async Task RevivePostGrace_paid_lease_never_transitions_to_active_when_hold_is_refused()
    {
        // AC83+AC84 combined: even in the edge case where funds were released but immediately drained, the
        // ended→active transition must not occur without a wallet hold in place.
        var fx = await ReadyAsync();
        var lease = await fx.SeedActiveLeaseAsync();

        fx.Clock.Advance(TimeSpan.FromSeconds(60));
        await fx.Reconciler.SuspendHostLeasesAsync(fx.HostId, T0.AddSeconds(60));
        await fx.Reconciler.EndSuspendedHostLeasesAsync(fx.HostId, T0.AddSeconds(60));

        await fx.DrainWalletAsync(3540, tag: "paid-lease-never-active-drain");

        fx.Clock.Advance(TimeSpan.FromMinutes(5));
        await fx.Reconciler.RevivePostGraceAsync(fx.HostId, new[] { lease.Id }, fx.Clock.GetUtcNow());

        var stored = await fx.ReloadAsync(lease.Id);
        Assert.NotEqual(LeaseStatus.Active, stored!.Status); // never revived unbacked
        Assert.Equal(0, await fx.HoldsCentsForAsync(lease.Id));
    }

    [Fact]
    public async Task RevivePostGrace_free_image_revives_without_placing_a_hold()
    {
        // A zero-priced image needs no hold to be leased in the first place (docs/PAYMENTS.md §4);
        // the revive path must degenerate cleanly the same way — the lease returns to active and no
        // lease_hold ever posts.
        var fx = await ReadyAsync();
        var lease = await fx.SeedActiveLeaseAsync(price: 0, holdCents: 0);

        fx.Clock.Advance(TimeSpan.FromSeconds(60));
        await fx.Reconciler.SuspendHostLeasesAsync(fx.HostId, T0.AddSeconds(60));
        await fx.Reconciler.EndSuspendedHostLeasesAsync(fx.HostId, T0.AddSeconds(60));

        fx.Clock.Advance(TimeSpan.FromMinutes(5));
        var outcome = await fx.Reconciler.RevivePostGraceAsync(
            fx.HostId, new[] { lease.Id }, fx.Clock.GetUtcNow());

        Assert.Equal(new[] { lease.Id }, outcome.Revived);
        Assert.Equal(LeaseStatus.Active, (await fx.ReloadAsync(lease.Id))!.Status);
        Assert.Equal(0, await fx.HoldsCentsForAsync(lease.Id));
        Assert.Equal(0, await fx.HoldTxnCountForAsync(lease.Id));
    }

    [Fact]
    public async Task RevivePostGrace_meter_still_restarts_at_reconnect_and_gap_is_never_billed()
    {
        // AC87: the #21 meter-restart-at-reconnect invariant is unchanged by task #23 — the revive path
        // still resets LastMeteredAt to the reconnect instant so the offline gap is never back-billed,
        // and a subsequent meter tick bills exactly the post-revive interval.
        var fx = await ReadyAsync();
        var lease = await fx.SeedActiveLeaseAsync();

        fx.Clock.Advance(TimeSpan.FromSeconds(60));
        await fx.Reconciler.SuspendHostLeasesAsync(fx.HostId, T0.AddSeconds(60));
        await fx.Reconciler.EndSuspendedHostLeasesAsync(fx.HostId, T0.AddSeconds(60));

        fx.Clock.Advance(TimeSpan.FromMinutes(5)); // T0+360
        var reconnectAt = fx.Clock.GetUtcNow();
        await fx.Reconciler.RevivePostGraceAsync(fx.HostId, new[] { lease.Id }, reconnectAt);

        var revived = await fx.ReloadAsync(lease.Id);
        Assert.Equal(reconnectAt, revived!.LastMeteredAt); // meter restart, not backfilled to last-healthy
        Assert.Equal(60, revived.BillableSeconds);          // gap not billed

        fx.Clock.Advance(TimeSpan.FromSeconds(60));
        await fx.Meter.RunTickAsync();
        var after = await fx.ReloadAsync(lease.Id);
        Assert.Equal(120, after!.BillableSeconds); // 60 pre-drop + 60 post-resume; 5-minute gap absent
    }

    [Fact]
    public async Task RevivePostGrace_stamps_new_hold_txn_id_so_second_release_does_not_dedup()
    {
        // Correctness of the release side after a revive: the revived lease's hold_txn_id changes, which
        // makes the ReleaseHoldAsync idempotency key distinct from the pre-revive release. A second end
        // therefore actually posts the release rather than dedup-replaying the first — the revival hold's
        // remainder returns to the wallet.
        var fx = await ReadyAsync();
        var lease = await fx.SeedActiveLeaseAsync();
        var originalHoldTxnId = (await fx.ReloadAsync(lease.Id))!.HoldTxnId;
        Assert.Null(originalHoldTxnId); // fixture seeds the lease without stamping HoldTxnId

        // Grace expiry: first hold released. Wallet gets 3540 back (3600 − 60 charged, but nothing was
        // charged yet, so 3600 back).
        await fx.Reconciler.SuspendHostLeasesAsync(fx.HostId, T0);
        await fx.Reconciler.EndSuspendedHostLeasesAsync(fx.HostId, T0);
        Assert.Equal(3600, await fx.WalletCentsAsync());

        // Revive: places a fresh hold, stamps hold_txn_id on the row.
        fx.Clock.Advance(TimeSpan.FromMinutes(5));
        await fx.Reconciler.RevivePostGraceAsync(fx.HostId, new[] { lease.Id }, fx.Clock.GetUtcNow());
        var afterRevive = await fx.ReloadAsync(lease.Id);
        Assert.NotNull(afterRevive!.HoldTxnId);
        Assert.Equal(0, await fx.WalletCentsAsync()); // fully re-earmarked

        // Second end (consumer DELETE-style) after a bit of runtime: the release must actually post,
        // returning the (uncharged) revival hold back to the wallet.
        await fx.WalletGate.ReleaseHoldAsync(lease.Id);
        Assert.Equal(3600, await fx.WalletCentsAsync()); // revival hold returned
        Assert.Equal(0, await fx.HoldsCentsForAsync(lease.Id));
    }

    // ---- Within-grace suspend → resume keeps exactly one hold (task #23 AC85) ----

    [Fact]
    public async Task Suspend_then_resume_within_grace_keeps_exactly_one_hold_intact()
    {
        // AC85: suspend does NOT release the hold (only end does); resume needs no new hold. The full
        // suspend → resume cycle must leave exactly one lease_hold in place — no duplicate posted, no
        // gap where the lease ran active with no hold.
        var fx = await ReadyAsync();
        var lease = await fx.SeedActiveLeaseAsync();
        var beforeHoldsForLease = await fx.HoldsCentsForAsync(lease.Id);
        var beforeHoldTxnCount = await fx.HoldTxnCountForAsync(lease.Id);
        Assert.Equal(3600, beforeHoldsForLease);
        Assert.Equal(1, beforeHoldTxnCount);

        // Tunnel drop within grace: suspend at last-healthy. The hold stays intact — a suspended lease
        // simply pauses metering, it does not touch lease_holds.
        fx.Clock.Advance(TimeSpan.FromSeconds(60));
        await fx.Reconciler.SuspendHostLeasesAsync(fx.HostId, T0.AddSeconds(60));
        var afterSuspend = await fx.HoldsCentsForAsync(lease.Id);
        var afterSuspendCharged = MeteringService.ChargeCentsFor(60, PricePerMin);
        Assert.Equal(beforeHoldsForLease - afterSuspendCharged, afterSuspend); // only the charge debited
        Assert.Equal(1, await fx.HoldTxnCountForAsync(lease.Id));               // still one hold txn

        // Reconnect within grace: resume. No new hold posted; the existing earmark carries through.
        fx.Clock.Advance(TimeSpan.FromMinutes(2));
        await fx.Reconciler.ReconcileHostAsync(
            fx.HostId, new[] { lease.Id }, lastHealthyAt: T0.AddSeconds(60));
        var resumed = await fx.ReloadAsync(lease.Id);
        Assert.Equal(LeaseStatus.Active, resumed!.Status);

        Assert.Equal(afterSuspend, await fx.HoldsCentsForAsync(lease.Id)); // physical earmark unchanged
        Assert.Equal(1, await fx.HoldTxnCountForAsync(lease.Id));           // still exactly one hold txn
    }

    [Fact]
    public async Task Suspend_then_resume_is_idempotent_across_repeated_flaps_on_the_single_hold()
    {
        // A flappy tunnel drives suspend → resume → suspend → resume many times; the single hold survives
        // every cycle. Number of lease_hold txns stays at 1 (create-time), and the physical earmark
        // shrinks only by the meter's charges — never re-earmarked or duplicated.
        var fx = await ReadyAsync();
        var lease = await fx.SeedActiveLeaseAsync();

        for (var flap = 0; flap < 3; flap++)
        {
            fx.Clock.Advance(TimeSpan.FromSeconds(60));
            var lastHealthy = fx.Clock.GetUtcNow();
            await fx.Reconciler.SuspendHostLeasesAsync(fx.HostId, lastHealthy);
            fx.Clock.Advance(TimeSpan.FromSeconds(30));
            await fx.Reconciler.ReconcileHostAsync(fx.HostId, new[] { lease.Id }, lastHealthy);
        }

        Assert.Equal(1, await fx.HoldTxnCountForAsync(lease.Id));
    }

    // ---- Agent-driven lease.ended (task #56, docs/TUNNEL.md §5, §8) ----
    //
    // The unsolicited lease.ended frame from wisp's local reaper is the host's explicit "this lease died"
    // signal (TTL, abnormal exit, etc.). Routing it through the reconciler finalizes billing with the
    // TTL-capped watermark, transitions the lease to ended with the mapped reason, and releases the wallet
    // hold — the same three-step end path the heartbeat set-diff runs for a silently-vanished container,
    // but driven by the host's explicit signal (so it fires up to a heartbeat sooner and carries the
    // correct `expired` reason where the set-diff would say `container_lost`).

    [Fact]
    public async Task EndLeaseFromAgent_expired_ends_lease_finalizes_billing_ttl_capped_and_releases_hold()
    {
        // AC185: lease.ended(reason=expired) on an active lease — the host's TTL reaper fired late.
        // The lease must end as `expired`, billing must be finalized TTL-capped (not to a runaway `now`),
        // and the wallet hold's unused remainder must return.
        var fx = await ReadyAsync();
        var lease = await fx.SeedActiveLeaseAsync(ttlSeconds: 180, holdCents: 180);

        // The agent's lease.ended arrives 30s past TTL (the reaper is slightly late, or we only noticed
        // now). The flush must cap at TTL (180s), not the 210s wall clock.
        fx.Clock.Advance(TimeSpan.FromSeconds(210));
        var outcome = await fx.Reconciler.EndLeaseFromAgentAsync(
            fx.HostId, lease.Id, LeaseEndReason.Expired);

        Assert.Equal(AgentLeaseEndOutcome.Ended, outcome);

        var stored = await fx.ReloadAsync(lease.Id);
        Assert.Equal(LeaseStatus.Ended, stored!.Status);
        Assert.Equal(LeaseEndReason.Expired, stored.EndReason);
        Assert.Equal(180, stored.BillableSeconds); // capped at TTL, not 210s wall-clock

        // Hold was 180¢, charged is exactly TTL (180s * 1¢/sec) = 180¢, so the release moves nothing back
        // — full hold consumed. The lease's earmark in lease_holds is zero.
        Assert.Equal(0, await fx.HoldsCentsForAsync(lease.Id));
    }

    [Fact]
    public async Task EndLeaseFromAgent_expired_on_a_lease_below_ttl_flushes_the_tail_and_returns_the_remainder()
    {
        // A short-lived lease the host reaped before TTL: the tail between the last tick and the stop
        // must be billed (task #34), and the unused hold remainder must return to the wallet.
        var fx = await ReadyAsync();
        var lease = await fx.SeedActiveLeaseAsync(); // TTL 3600s, hold 3600¢

        fx.Clock.Advance(TimeSpan.FromSeconds(90)); // 90s runtime, well under TTL
        var walletBefore = await fx.WalletCentsAsync();

        var outcome = await fx.Reconciler.EndLeaseFromAgentAsync(
            fx.HostId, lease.Id, LeaseEndReason.Expired);

        Assert.Equal(AgentLeaseEndOutcome.Ended, outcome);

        var stored = await fx.ReloadAsync(lease.Id);
        Assert.Equal(LeaseStatus.Ended, stored!.Status);
        Assert.Equal(LeaseEndReason.Expired, stored.EndReason);
        Assert.Equal(90, stored.BillableSeconds); // full 90s tail flushed

        // Hold released: 3600 − 90 charged = 3510 back to wallet.
        Assert.Equal(walletBefore + 3510, await fx.WalletCentsAsync());
        Assert.Equal(0, await fx.HoldsCentsForAsync(lease.Id));
    }

    [Fact]
    public async Task EndLeaseFromAgent_already_ended_lease_is_a_no_op_and_does_not_double_release()
    {
        // AC187: a lease.ended for an already-ended lease is a no-op — no double flush, no double hold
        // release. Simulates the consumer DELETE (or the heartbeat set-diff) winning the race and the
        // agent's frame arriving after.
        var fx = await ReadyAsync();
        var lease = await fx.SeedActiveLeaseAsync();

        // End the lease as `released` (consumer DELETE beat the agent frame). Manually simulate the
        // finalize + release path so the state matches what the release code would leave behind.
        fx.Clock.Advance(TimeSpan.FromSeconds(60));
        await fx.Meter.FinalizeLeaseAsync(lease.Id, fx.Clock.GetUtcNow());
        await fx.Leases.TransitionStateAsync(
            lease.Id, LeaseStatus.Ended, endReason: LeaseEndReason.Released, endedAt: fx.Clock.GetUtcNow());
        await fx.WalletGate.ReleaseHoldAsync(lease.Id);

        var walletAfterConsumerRelease = await fx.WalletCentsAsync();
        var holdsCentsAfterConsumerRelease = await fx.HoldsCentsForAsync(lease.Id);

        // The agent's lease.ended arrives now — must be a strict no-op.
        var outcome = await fx.Reconciler.EndLeaseFromAgentAsync(
            fx.HostId, lease.Id, LeaseEndReason.Expired);

        Assert.Equal(AgentLeaseEndOutcome.AlreadyTerminal, outcome);

        var stored = await fx.ReloadAsync(lease.Id);
        Assert.Equal(LeaseStatus.Ended, stored!.Status);
        Assert.Equal(LeaseEndReason.Released, stored.EndReason); // original reason preserved
        Assert.Equal(walletAfterConsumerRelease, await fx.WalletCentsAsync()); // wallet unchanged
        Assert.Equal(holdsCentsAfterConsumerRelease, await fx.HoldsCentsForAsync(lease.Id));
    }

    [Fact]
    public async Task EndLeaseFromAgent_on_a_suspended_lease_ends_without_re_flushing()
    {
        // A suspended lease was already flushed to last-healthy at suspend time; the agent's lease.ended
        // (e.g. the reaper fired during the outage) must end it and release the hold, but no additional
        // billing must accrue — BillableSeconds stays at last-healthy.
        var fx = await ReadyAsync();
        var lease = await fx.SeedActiveLeaseAsync();

        fx.Clock.Advance(TimeSpan.FromSeconds(60));
        await fx.Reconciler.SuspendHostLeasesAsync(fx.HostId, T0.AddSeconds(60));
        var billableBefore = (await fx.ReloadAsync(lease.Id))!.BillableSeconds;

        fx.Clock.Advance(TimeSpan.FromSeconds(30));
        var outcome = await fx.Reconciler.EndLeaseFromAgentAsync(
            fx.HostId, lease.Id, LeaseEndReason.Expired);

        Assert.Equal(AgentLeaseEndOutcome.Ended, outcome);
        var stored = await fx.ReloadAsync(lease.Id);
        Assert.Equal(LeaseStatus.Ended, stored!.Status);
        Assert.Equal(LeaseEndReason.Expired, stored.EndReason);
        Assert.Equal(billableBefore, stored.BillableSeconds); // no re-flush past suspend
    }

    [Fact]
    public async Task EndLeaseFromAgent_unknown_lease_is_not_found()
    {
        var fx = await ReadyAsync();

        var outcome = await fx.Reconciler.EndLeaseFromAgentAsync(
            fx.HostId, Guid.NewGuid(), LeaseEndReason.Expired);

        Assert.Equal(AgentLeaseEndOutcome.NotFound, outcome);
    }

    [Fact]
    public async Task EndLeaseFromAgent_wrong_host_id_is_not_found()
    {
        // A lease.ended frame carrying a lease belonging to a different host: never touch it.
        var fx = await ReadyAsync();
        var lease = await fx.SeedActiveLeaseAsync();

        var outcome = await fx.Reconciler.EndLeaseFromAgentAsync(
            Guid.NewGuid(), lease.Id, LeaseEndReason.Expired);

        Assert.Equal(AgentLeaseEndOutcome.NotFound, outcome);
        Assert.Equal(LeaseStatus.Active, (await fx.ReloadAsync(lease.Id))!.Status);
    }

    [Fact]
    public async Task EndLeaseFromAgent_is_idempotent_across_repeated_frames()
    {
        // Two lease.ended frames back-to-back (retry / duplicate delivery): first ends the lease, second
        // is a no-op. Wallet balance and lease_holds earmark are unchanged by the second call.
        var fx = await ReadyAsync();
        var lease = await fx.SeedActiveLeaseAsync();

        fx.Clock.Advance(TimeSpan.FromSeconds(60));
        var first = await fx.Reconciler.EndLeaseFromAgentAsync(
            fx.HostId, lease.Id, LeaseEndReason.Expired);
        var walletAfterFirst = await fx.WalletCentsAsync();
        var holdsAfterFirst = await fx.HoldsCentsForAsync(lease.Id);

        var second = await fx.Reconciler.EndLeaseFromAgentAsync(
            fx.HostId, lease.Id, LeaseEndReason.Expired);

        Assert.Equal(AgentLeaseEndOutcome.Ended, first);
        Assert.Equal(AgentLeaseEndOutcome.AlreadyTerminal, second);
        Assert.Equal(walletAfterFirst, await fx.WalletCentsAsync());
        Assert.Equal(holdsAfterFirst, await fx.HoldsCentsForAsync(lease.Id));
    }
}
