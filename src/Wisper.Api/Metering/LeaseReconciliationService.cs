using Microsoft.Extensions.Logging;
using Wisper.Api.Domain;
using Wisper.Api.Leases;
using Wisper.Api.Persistence.Leases;

namespace Wisper.Api.Metering;

/// <summary>
/// The tunnel disconnect / grace / reconnect reconciliation over lease + metering state
/// (docs/TUNNEL.md §8). Three idempotent, manager-authoritative operations:
/// <list type="bullet">
/// <item><b>Suspend</b> (<see cref="SuspendHostLeasesAsync"/>): on tunnel loss, flush each of a host's
/// active leases up to the last-healthy liveness point, then move it to <c>suspended</c> — billing pauses
/// there and the blind gap never accrues.</item>
/// <item><b>Reconcile</b> (<see cref="ReconcileHostAsync"/>): on reconnect within grace, set-diff the
/// agent's reported live leases against the host's <c>suspended</c> set — a lease still present
/// <i>resumes</i> (back to <c>active</c>, meter restarted at the reconnect instant so the gap is never
/// billed, same lease id); a lease no longer present is <i>ended</i> (<c>container_lost</c>) finalized at
/// last-healthy.</item>
/// <item><b>End on grace expiry</b> (<see cref="EndSuspendedHostLeasesAsync"/>): if no reconnect arrives,
/// end every still-<c>suspended</c> lease (<c>host_disconnect</c>) finalized at last-healthy.</item>
/// </list>
/// The meter only ever accrues over an <c>active</c> interval, so a suspended lease bills nothing regardless
/// of how long the outage lasts. Every operation is a plain set-diff over durable state, so repeated flaps
/// converge (docs/TUNNEL.md §8). This is the pure logic; the tunnel lifecycle drives it via
/// <see cref="Wisper.Api.Tunnel.TunnelDisconnectCoordinator"/>.
/// </summary>
public sealed class LeaseReconciliationService
{
    private readonly ILeaseRepository _leases;
    private readonly MeteringService _meter;
    private readonly ILeaseWalletGate _walletGate;
    private readonly TimeProvider _time;
    private readonly ILogger<LeaseReconciliationService> _logger;

    public LeaseReconciliationService(
        ILeaseRepository leases,
        MeteringService meter,
        ILeaseWalletGate walletGate,
        TimeProvider time,
        ILogger<LeaseReconciliationService> logger)
    {
        _leases = leases;
        _meter = meter;
        _walletGate = walletGate;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// On tunnel loss (docs/TUNNEL.md §8): for each <c>active</c> lease of <paramref name="hostId"/>, bill
    /// the healthy interval up to <paramref name="lastHealthyAt"/> and then move the lease to
    /// <c>suspended</c> — so billing pauses exactly at last-healthy and the disconnect gap never accrues.
    /// Idempotent: a lease already <c>suspended</c> is left as-is. Returns how many leases were newly
    /// suspended and how many are now suspended for the host (the set the grace window must resolve).
    /// </summary>
    public async Task<SuspendOutcome> SuspendHostLeasesAsync(
        Guid hostId, DateTimeOffset lastHealthyAt, CancellationToken ct = default)
    {
        var live = await _leases.ListActiveByHostAsync(hostId, ct);
        var newlySuspended = 0;
        var totalSuspended = 0;
        foreach (var lease in live)
        {
            if (lease.Status == LeaseStatus.Suspended)
            {
                totalSuspended++;
                continue;
            }

            if (lease.Status != LeaseStatus.Active)
            {
                continue;
            }

            // Bill the healthy interval up to last-healthy BEFORE suspending: the meter only accrues over
            // an active lease, so once suspended nothing more can bill (docs/TUNNEL.md §8).
            await _meter.FlushLeaseAsync(lease, lastHealthyAt, ct);
            // Stamp suspended_at with the wall-clock instant the suspend transition happens (task #55):
            // the durable grace sweep gates on `suspended_at < now() - grace`, so it must reflect when we
            // actually suspended — NOT last-healthy, which can be far in the past when a wisper-api restart
            // discovers a long-stale suspension. Stamping wall-clock preserves the full grace window on
            // discovery.
            var suspendedAt = _time.GetUtcNow();
            var moved = await _leases.TransitionStateAsync(
                lease.Id, LeaseStatus.Suspended, suspendedAt: suspendedAt, ct: ct);
            if (moved is not null)
            {
                newlySuspended++;
                totalSuspended++;
                _logger.LogInformation(
                    "lease {LeaseId} suspended (host {HostId} tunnel lost); billing paused at {LastHealthy:O}",
                    lease.Id, hostId, lastHealthyAt);
            }
        }

        return new SuspendOutcome(newlySuspended, totalSuspended);
    }

    /// <summary>
    /// On reconnect within grace (docs/TUNNEL.md §8): set-diff the agent's reported live leases
    /// (<paramref name="liveLeaseIds"/>) against the host's <c>suspended</c> set.
    /// <list type="bullet">
    /// <item>Present on the host → <b>resume</b>: back to <c>active</c>, keeping the lease id, price
    /// snapshot and usage ledger; the meter watermark is reset to the reconnect instant so the suspended
    /// gap is never billed.</item>
    /// <item>Absent from the host (container died in a host crash/restart) → <b>end</b>
    /// (<c>container_lost</c>), finalized at <paramref name="lastHealthyAt"/>.</item>
    /// </list>
    /// Idempotent, so repeated reconnect flaps converge.
    /// </summary>
    public async Task<ReconcileOutcome> ReconcileHostAsync(
        Guid hostId,
        IReadOnlyCollection<Guid> liveLeaseIds,
        DateTimeOffset lastHealthyAt,
        CancellationToken ct = default)
    {
        var reported = liveLeaseIds as IReadOnlySet<Guid> ?? new HashSet<Guid>(liveLeaseIds);
        var suspended = (await _leases.ListActiveByHostAsync(hostId, ct))
            .Where(l => l.Status == LeaseStatus.Suspended)
            .ToList();

        var now = _time.GetUtcNow();
        var resumed = new List<Guid>();
        var containerLost = new List<Guid>();
        foreach (var lease in suspended)
        {
            if (reported.Contains(lease.Id))
            {
                // Resume: restart the meter at `now` so the paused gap [last-healthy, now] is not billed
                // (billing "restarts", it does not back-fill the outage). Same lease id, unchanged usage.
                await _leases.TransitionStateAsync(
                    lease.Id, LeaseStatus.Active, lastMeteredAt: now, ct: ct);
                resumed.Add(lease.Id);
                _logger.LogInformation(
                    "lease {LeaseId} resumed on host {HostId} reconnect; billing restarts at {Now:O}",
                    lease.Id, hostId, now);
            }
            else
            {
                await EndSuspendedAsync(lease, LeaseEndReason.ContainerLost, lastHealthyAt, ct);
                containerLost.Add(lease.Id);
                _logger.LogInformation(
                    "lease {LeaseId} gone from host {HostId} on reconnect; ended (container_lost) at {LastHealthy:O}",
                    lease.Id, hostId, lastHealthyAt);
            }
        }

        return new ReconcileOutcome(resumed, containerLost);
    }

    /// <summary>
    /// On grace expiry with no reconnect (docs/TUNNEL.md §8): end every still-<c>suspended</c> lease of
    /// <paramref name="hostId"/> with <paramref name="reason"/> (<c>host_disconnect</c> by default),
    /// finalizing billing at <paramref name="lastHealthyAt"/>. wisp's local TTL reaper guarantees the
    /// abandoned containers are reclaimed regardless. Returns how many leases were ended.
    /// </summary>
    public async Task<int> EndSuspendedHostLeasesAsync(
        Guid hostId,
        DateTimeOffset lastHealthyAt,
        LeaseEndReason reason = LeaseEndReason.HostDisconnect,
        CancellationToken ct = default)
    {
        var suspended = (await _leases.ListActiveByHostAsync(hostId, ct))
            .Where(l => l.Status == LeaseStatus.Suspended)
            .ToList();
        foreach (var lease in suspended)
        {
            await EndSuspendedAsync(lease, reason, lastHealthyAt, ct);
            _logger.LogInformation(
                "lease {LeaseId} ended ({Reason}) on host {HostId} grace expiry; finalized at {LastHealthy:O}",
                lease.Id, PgEnum.ToSnakeLabel(reason), hostId, lastHealthyAt);
        }

        return suspended.Count;
    }

    /// <summary>
    /// On host reconnect <b>after</b> grace expiry (docs/TUNNEL.md §8): for each live contract the agent
    /// reports (<paramref name="liveLeaseIds"/>), look up the corresponding lease and, if it was ended as
    /// <c>host_disconnect</c> for this host (the container kept running through the agent restart), revive it
    /// back to <c>active</c>. The meter watermark is reset to <paramref name="reconnectAt"/> so the offline
    /// gap is never billed — identical semantics to the within-grace resume path. The same lease id and
    /// accumulated usage ledger are preserved. Before the ended→active transition, a fresh wallet hold is
    /// re-placed for the lease's remaining time at the lease price (docs/PAYMENTS.md §4, mirroring lease
    /// create): grace-expiry already released the pre-drop hold, so a revived paid lease would otherwise
    /// run active with no backing hold. If the consumer's wallet is now short of the revival hold, the
    /// lease is <b>ended (payment_failed)</b> instead of revived (wisp's TTL reaper reclaims the container)
    /// — no lease is ever revived unbacked. Contracts that cannot be revived (not found, ended for another
    /// reason, or belonging to a different host) are returned as orphaned; the caller may tear them down.
    /// </summary>
    public async Task<PostGraceReconcileOutcome> RevivePostGraceAsync(
        Guid hostId,
        IReadOnlyCollection<Guid> liveLeaseIds,
        DateTimeOffset reconnectAt,
        CancellationToken ct = default)
    {
        var revived = new List<Guid>();
        var orphaned = new List<Guid>();

        foreach (var leaseId in liveLeaseIds)
        {
            var lease = await _leases.GetByIdAsync(leaseId, ct);

            if (lease is null || lease.HostId != hostId)
            {
                orphaned.Add(leaseId);
                continue;
            }

            // Already active or suspended → already counted by the manager, no action needed.
            if (lease.Status is LeaseStatus.Active or LeaseStatus.Suspended)
            {
                continue;
            }

            if (lease.Status != LeaseStatus.Ended || lease.EndReason != LeaseEndReason.HostDisconnect)
            {
                // Ended for another reason (released, container_lost, etc.) — the container is orphaned
                // and should be torn down; we cannot revive a purposefully-ended lease.
                orphaned.Add(leaseId);
                continue;
            }

            // The container kept running through the agent restart: revive the lease (docs/TUNNEL.md §8,
            // preferred over teardown — the workload never died). Re-place the wallet hold BEFORE the
            // ended→active transition (task #23): grace-expiry released the pre-drop hold, so without a
            // fresh hold the revived lease would run active with no wallet earmark and the create-time
            // 402 gate would never re-run.
            if (!await TryRevivePaidLeaseAsync(lease, reconnectAt, orphaned, ct))
            {
                continue;
            }

            // Use UpdateAsync to clear end_reason and ended_at; TransitionStateAsync's COALESCE would leave
            // those stale values in place.
            var revival = await GetForRevivalAsync(leaseId, ct);
            var updated = revival with
            {
                Status = LeaseStatus.Active,
                EndReason = null,
                EndedAt = null,
                LastMeteredAt = reconnectAt,
                SuspendedAt = null,
            };
            await _leases.UpdateAsync(updated, ct);
            revived.Add(leaseId);
            _logger.LogInformation(
                "lease {LeaseId} revived (host {HostId} reconnected post-grace); billing restarts at {ReconnectAt:O}",
                leaseId, hostId, reconnectAt);
        }

        return new PostGraceReconcileOutcome(revived, orphaned);
    }

    /// <summary>
    /// On every heartbeat (steady-state and post-grace fall-through): set-diff the agent's reported live
    /// leases (<paramref name="reportedLeaseIds"/>) against the manager's <c>active + suspended</c> set for
    /// <paramref name="hostId"/> and heal any drift.
    /// <list type="bullet">
    /// <item><b>Active not reported</b> → silent container death: flush billing to <c>now</c> and end as
    /// <c>container_lost</c>, freeing capacity without a tunnel disconnect.</item>
    /// <item><b>Suspended reported</b> (restart case, task #55) → resume: the host says the container is
    /// still running and the in-memory grace timer was lost (restart / scale-in / heartbeat to a different
    /// instance). Same set-diff semantics as the within-grace reconnect path — back to <c>active</c> with
    /// the meter watermark reset to the heartbeat instant so the offline gap is never billed.</item>
    /// <item><b>Suspended not reported</b> (restart case, task #55) → end as <c>container_lost</c>
    /// finalized at <see cref="Lease.SuspendedAt"/> (the last durable instant we know billing was paused).
    /// The billable interval was already flushed to last-healthy at suspend time.</item>
    /// <item><b>Reported not live</b> → live contract without a manager lease: revive if it was ended as
    /// <c>host_disconnect</c> (the container kept running); flag orphaned otherwise so the host tears it
    /// down.</item>
    /// <item><b>Equal sets and no suspended present</b> → steady-state fast-path: <b>zero writes</b> —
    /// mirrors the <see cref="Wisper.Api.Tunnel.HostPresenceService"/> no-change guard.</item>
    /// </list>
    /// Idempotent: repeated heartbeats with the same reported set converge on a single outcome. This is
    /// the durable-grace complement (task #55) — the coordinator only routes here when NO in-memory grace
    /// entry exists on this instance, so acting on suspended leases here does not race the in-process
    /// timer; if a race can happen across instances, the CAS-guarded end transition serializes it.
    /// </summary>
    public async Task<HeartbeatReconcileOutcome> ReconcileHeartbeatAsync(
        Guid hostId,
        IReadOnlyCollection<Guid> reportedLeaseIds,
        CancellationToken ct = default)
    {
        var reported = reportedLeaseIds as IReadOnlySet<Guid> ?? new HashSet<Guid>(reportedLeaseIds);

        // Consider both active AND suspended leases (task #55): after a wisper-api restart the in-memory
        // grace timer is gone but the row is still `suspended`, and a reconnecting agent's first heartbeat
        // is what tells us whether the container is still running (resume) or gone (container_lost).
        var liveLeases = await _leases.ListActiveByHostAsync(hostId, ct);
        var activeLeases = liveLeases.Where(l => l.Status == LeaseStatus.Active).ToList();
        var suspendedLeases = liveLeases.Where(l => l.Status == LeaseStatus.Suspended).ToList();

        var activeIds = new HashSet<Guid>(activeLeases.Select(l => l.Id));

        // Steady-state fast-path: reported set matches the active set AND there is nothing suspended to
        // resolve. When suspended leases exist we always have work — either resuming (reported) or ending
        // (unreported) — so we cannot short-circuit.
        if (suspendedLeases.Count == 0 && reported.SetEquals(activeIds))
        {
            return HeartbeatReconcileOutcome.Empty;
        }

        var now = _time.GetUtcNow();
        var containerLost = new List<Guid>();
        var revived = new List<Guid>();
        var orphaned = new List<Guid>();

        // Active leases the host no longer reports → silent container death.
        foreach (var lease in activeLeases)
        {
            if (reported.Contains(lease.Id))
            {
                continue;
            }

            // Finalize (task #34): cap the tail flush at ttl and last-healthy — a container that ran
            // past its TTL before we noticed it silently died is not billable past the TTL, and the
            // heartbeat's `now` may otherwise over-bill the tail past what the lease was entitled to.
            await _meter.FinalizeLeaseAsync(lease, now, ct);
            await _leases.TransitionStateAsync(
                lease.Id, LeaseStatus.Ended, endReason: LeaseEndReason.ContainerLost, endedAt: now, ct: ct);
            await _walletGate.ReleaseHoldAsync(lease.Id, ct);
            containerLost.Add(lease.Id);
            _logger.LogInformation(
                "lease {LeaseId} ended (container_lost) on heartbeat for host {HostId}; finalized at {Now:O}",
                lease.Id, hostId, now);
        }

        // Suspended leases (task #55 restart case): the in-memory grace timer is gone but the row is
        // still suspended. Use the heartbeat's live set as the source of truth — resume the still-present
        // ones, container_lost the vanished ones.
        foreach (var lease in suspendedLeases)
        {
            if (reported.Contains(lease.Id))
            {
                // Resume: identical semantics to the within-grace reconnect path — back to active with the
                // meter watermark reset to `now` so the suspended gap is never billed (docs/TUNNEL.md §8).
                // suspended_at is auto-cleared by TransitionStateAsync's CASE guard on any transition to
                // a non-suspended status. The existing lease_hold carries through: within-grace resume
                // does not touch lease_holds (task #23), and this path mirrors it.
                await _leases.TransitionStateAsync(
                    lease.Id, LeaseStatus.Active, lastMeteredAt: now, ct: ct);
                revived.Add(lease.Id);
                _logger.LogInformation(
                    "lease {LeaseId} resumed on heartbeat for host {HostId} (post-restart, no in-memory grace);" +
                    " billing restarts at {Now:O}",
                    lease.Id, hostId, now);
            }
            else
            {
                // Vanished: end as container_lost, finalized at the durable suspended_at (the last instant
                // we know billing was already paused). Billing was flushed to last-healthy when the lease
                // suspended, so there is no tail to accrue here — the same path as grace expiry but with
                // container_lost (host reconnected, container is definitively gone) rather than
                // host_disconnect. CAS-guarded so a concurrent sweep on another instance cannot double-end.
                var endedAt = lease.SuspendedAt ?? now;
                var moved = await _leases.TransitionStateAsync(
                    lease.Id,
                    LeaseStatus.Ended,
                    endReason: LeaseEndReason.ContainerLost,
                    endedAt: endedAt,
                    expectedCurrentStatus: LeaseStatus.Suspended,
                    ct: ct);
                if (moved is null)
                {
                    continue; // lost the race with a sweep on another instance
                }

                await _walletGate.ReleaseHoldAsync(lease.Id, ct);
                containerLost.Add(lease.Id);
                _logger.LogInformation(
                    "lease {LeaseId} ended (container_lost) on heartbeat for host {HostId} (post-restart," +
                    " suspended and not reported); finalized at {EndedAt:O}",
                    lease.Id, hostId, endedAt);
            }
        }

        // Reported contracts the manager has no active or suspended lease for → check for revival or orphan.
        var liveIds = new HashSet<Guid>(activeIds);
        foreach (var lease in suspendedLeases)
        {
            liveIds.Add(lease.Id);
        }

        foreach (var leaseId in reported)
        {
            if (liveIds.Contains(leaseId))
            {
                continue; // already live in the manager (active pre-heartbeat, or just resumed above)
            }

            var lease = await _leases.GetByIdAsync(leaseId, ct);

            if (lease is null || lease.HostId != hostId)
            {
                orphaned.Add(leaseId);
                continue;
            }

            // Already active/suspended (a resume just above, or arrived here mid-transition) — consistent.
            if (lease.Status is LeaseStatus.Active or LeaseStatus.Suspended)
            {
                continue;
            }

            if (lease.Status != LeaseStatus.Ended || lease.EndReason != LeaseEndReason.HostDisconnect)
            {
                orphaned.Add(leaseId);
                continue;
            }

            // Container kept running through an agent restart / grace expiry: revive with the same
            // semantics as RevivePostGraceAsync — billing restarts at now, offline gap never billed. Same
            // re-hold gate: a paid lease must have a wallet hold covering its remaining time before it
            // returns to active (task #23), or it is ended (payment_failed) instead.
            if (!await TryRevivePaidLeaseAsync(lease, now, orphaned, ct))
            {
                continue;
            }

            var revival = await GetForRevivalAsync(leaseId, ct);
            var updated = revival with
            {
                Status = LeaseStatus.Active,
                EndReason = null,
                EndedAt = null,
                LastMeteredAt = now,
                SuspendedAt = null,
            };
            await _leases.UpdateAsync(updated, ct);
            revived.Add(leaseId);
            _logger.LogInformation(
                "lease {LeaseId} revived on heartbeat for host {HostId}; billing restarts at {Now:O}",
                leaseId, hostId, now);
        }

        return new HeartbeatReconcileOutcome(containerLost, revived, orphaned);
    }

    private async Task EndSuspendedAsync(
        Lease lease, LeaseEndReason reason, DateTimeOffset lastHealthyAt, CancellationToken ct)
    {
        // The lease was already flushed to last-healthy when it suspended, so its charged total is final;
        // end it, then return the unused hold remainder to the wallet (docs/PAYMENTS.md §4). Release is
        // keyed per hold generation (lease id + current hold_txn_id), so a repeated grace/reconnect flap
        // converges on a single hold_release for this generation.
        await _leases.TransitionStateAsync(
            lease.Id, LeaseStatus.Ended, endReason: reason, endedAt: lastHealthyAt, ct: ct);
        await _walletGate.ReleaseHoldAsync(lease.Id, ct);
    }

    /// <summary>
    /// The durable grace backstop (task #55, docs/TUNNEL.md §8). Finds leases whose <c>suspended_at</c> is
    /// older than the effective cutoff (grace window + a safety margin) AND whose host is NOT currently
    /// under an active in-process grace timer on THIS instance, then routes each through the same finalize
    /// + end(host_disconnect) + release-hold path the in-memory timer uses. This is what turns "grace lived
    /// only in process memory" into "grace survives a wisper-api restart":
    /// <list type="bullet">
    /// <item>Restart / crash / scale-in wipes <see cref="Wisper.Api.Tunnel.TunnelDisconnectCoordinator"/>'s
    /// timer, but the durable <c>suspended_at</c> stamp lets the next sweep tick discover and reap the
    /// stranded lease within one interval — no more wallet holds / concurrency slots stuck forever.</item>
    /// <item>Idempotent and safe under concurrent instances: the <c>suspended → ended</c> transition uses
    /// the repository's <c>expectedCurrentStatus</c> CAS guard, so exactly one sweep (or the in-memory
    /// timer, or a heartbeat) wins the state transition; the losers see zero rows updated and skip.</item>
    /// <item>Leases whose host is currently under an active in-process grace timer on THIS instance are
    /// skipped — the fast in-memory path is the source of truth while its timer is armed.</item>
    /// </list>
    /// Returns how many leases the sweep ended.
    /// </summary>
    public async Task<int> SweepStaleSuspendedLeasesAsync(
        TimeSpan graceWithSafetyMargin,
        IReadOnlySet<Guid> hostsUnderInProcessGrace,
        CancellationToken ct = default)
    {
        var now = _time.GetUtcNow();
        var cutoff = now - graceWithSafetyMargin;
        var candidates = await _leases.ListSuspendedOlderThanAsync(cutoff, ct);
        if (candidates.Count == 0)
        {
            return 0;
        }

        var ended = 0;
        foreach (var lease in candidates)
        {
            ct.ThrowIfCancellationRequested();

            // Skip a host whose grace timer is armed on THIS instance — the in-memory path is the fast
            // path (task #55). A host under grace on a DIFFERENT instance falls into this sweep on the
            // one where the timer was lost; the CAS guard below dedupes if the other instance's timer
            // actually fires first.
            if (hostsUnderInProcessGrace.Contains(lease.HostId))
            {
                continue;
            }

            // CAS-guarded transition: two concurrent sweeps (or a sweep and a late-firing in-memory timer)
            // race on suspended → ended, and only ONE update matches (the loser's WHERE clause fails
            // because status is no longer 'suspended'). Ledger release is keyed per hold generation, so
            // a duplicate release call after the winner posts would dedupe at the ledger anyway — the CAS
            // gives us zero-post idempotence on the state transition too.
            var moved = await _leases.TransitionStateAsync(
                lease.Id,
                LeaseStatus.Ended,
                endReason: LeaseEndReason.HostDisconnect,
                endedAt: lease.SuspendedAt ?? now,
                expectedCurrentStatus: LeaseStatus.Suspended,
                ct: ct);
            if (moved is null)
            {
                continue; // lost the race (another sweep / timer / heartbeat already transitioned it)
            }

            await _walletGate.ReleaseHoldAsync(lease.Id, ct);
            ended++;
            _logger.LogWarning(
                "lease {LeaseId} ended (host_disconnect) by durable grace sweep — host {HostId} " +
                "suspended at {SuspendedAt:O}, past cutoff {Cutoff:O}; hold released",
                lease.Id, lease.HostId, lease.SuspendedAt, cutoff);
        }

        return ended;
    }

    /// <summary>
    /// Re-establishes the wallet hold for a lease about to be revived (task #23, docs/PAYMENTS.md §4). The
    /// pre-drop hold was already released at grace expiry, so a revived paid lease must re-earmark funds
    /// covering its remaining billable time before it returns to <c>active</c>. On allow (or a free image),
    /// stamps the fresh <c>hold_txn_id</c> onto the lease row and returns <c>true</c>. On deny (wallet
    /// short of the revival hold), ends the lease as <c>payment_failed</c> instead of reviving unbacked,
    /// flags it as orphaned so the caller tears the container down, and returns <c>false</c>. Repeated
    /// revive attempts for the same lease are idempotent — the revival hold key dedupes at the ledger.
    /// </summary>
    private async Task<bool> TryRevivePaidLeaseAsync(
        Lease lease, DateTimeOffset reconnectAt, List<Guid> orphaned, CancellationToken ct)
    {
        var revivalHoldCents = LeaseHoldPricing.EstimateRevivalHoldCents(
            lease.TtlSeconds, lease.BillableSeconds, lease.PriceCentsPerMin);
        var outcome = await _walletGate.PlaceRevivalHoldAsync(
            lease.ConsumerUserId, lease.Id, revivalHoldCents, lease.Currency, ct);

        if (!outcome.Allowed)
        {
            // Do not revive into an unbacked active lease (docs/PAYMENTS.md §4, task #23): end the lease as
            // payment_failed so wisp's TTL reaper reclaims the container — no unpaid active runtime.
            await _leases.TransitionStateAsync(
                lease.Id, LeaseStatus.Ended,
                endReason: LeaseEndReason.PaymentFailed, endedAt: reconnectAt, ct: ct);
            orphaned.Add(lease.Id);
            _logger.LogWarning(
                "lease {LeaseId} not revived on host {HostId} reconnect: wallet short of revival hold" +
                " ({Available}¢ < {Required}¢); ended (payment_failed), container reclaimed by wisp TTL",
                lease.Id, lease.HostId, outcome.Decision.AvailableCents, outcome.Decision.RequiredCents);
            return false;
        }

        // On allow (paid or free), stamp the fresh hold generation onto the row so ReleaseHoldAsync's
        // key does not collide with the pre-revive release. A free image leaves HoldTxnId untouched
        // (still null); a paid lease overwrites the pre-drop txn id with the revival one.
        if (outcome.HoldTxnId is { } txnId)
        {
            await _leases.UpdateAsync(lease with { HoldTxnId = txnId }, ct);
        }

        return true;
    }

    /// <summary>
    /// Reloads the lease row so the post-hold-stamping revival transition sees the fresh
    /// <see cref="Lease.HoldTxnId"/>. Throws if the row disappeared mid-revive (a bug — nothing else
    /// should delete it while the reconciler holds the operation).
    /// </summary>
    private async Task<Lease> GetForRevivalAsync(Guid leaseId, CancellationToken ct) =>
        await _leases.GetByIdAsync(leaseId, ct)
            ?? throw new InvalidOperationException($"lease {leaseId} vanished during revival");
}

/// <summary>
/// The result of <see cref="LeaseReconciliationService.SuspendHostLeasesAsync"/>: how many of a host's
/// leases were newly moved to <c>suspended</c>, and how many are now suspended in total (the set the grace
/// window must eventually resume or end).
/// </summary>
public readonly record struct SuspendOutcome(int NewlySuspended, int TotalSuspended);

/// <summary>
/// The result of <see cref="LeaseReconciliationService.ReconcileHostAsync"/>: the ids resumed to
/// <c>active</c> and the ids ended as <c>container_lost</c> by the reconnect set-diff (docs/TUNNEL.md §8).
/// </summary>
public sealed record ReconcileOutcome(
    IReadOnlyList<Guid> Resumed,
    IReadOnlyList<Guid> ContainerLost);

/// <summary>
/// The result of <see cref="LeaseReconciliationService.RevivePostGraceAsync"/>: lease ids revived back to
/// <c>active</c> (container kept running through the agent restart) and lease ids whose containers could not
/// be revived (ended for a different reason, not found, or wrong host).
/// </summary>
public sealed record PostGraceReconcileOutcome(
    IReadOnlyList<Guid> Revived,
    IReadOnlyList<Guid> Orphaned);

/// <summary>
/// The result of <see cref="LeaseReconciliationService.ReconcileHeartbeatAsync"/>: lease ids ended as
/// <c>container_lost</c> (active but not reported), lease ids revived back to <c>active</c> (reported but
/// ended as <c>host_disconnect</c>), and lease ids orphaned (reported but not reviviable).
/// </summary>
public sealed record HeartbeatReconcileOutcome(
    IReadOnlyList<Guid> ContainerLost,
    IReadOnlyList<Guid> Revived,
    IReadOnlyList<Guid> Orphaned)
{
    /// <summary>Whether any drift was found and healed (false in the steady-state no-op path).</summary>
    public bool HasChanges => ContainerLost.Count > 0 || Revived.Count > 0 || Orphaned.Count > 0;

    /// <summary>The singleton empty outcome returned by the steady-state fast-path (zero writes).</summary>
    public static readonly HeartbeatReconcileOutcome Empty =
        new(Array.Empty<Guid>(), Array.Empty<Guid>(), Array.Empty<Guid>());
}
