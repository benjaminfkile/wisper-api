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
            var moved = await _leases.TransitionStateAsync(lease.Id, LeaseStatus.Suspended, ct: ct);
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
    /// accumulated usage ledger are preserved. Contracts that cannot be revived (not found, ended for another
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
            // preferred over teardown — the workload never died). Use UpdateAsync to clear end_reason and
            // ended_at; TransitionStateAsync's COALESCE would leave those stale values in place.
            var revival = lease with
            {
                Status = LeaseStatus.Active,
                EndReason = null,
                EndedAt = null,
                LastMeteredAt = reconnectAt,
            };
            await _leases.UpdateAsync(revival, ct);
            revived.Add(leaseId);
            _logger.LogInformation(
                "lease {LeaseId} revived (host {HostId} reconnected post-grace); billing restarts at {ReconnectAt:O}",
                leaseId, hostId, reconnectAt);
        }

        return new PostGraceReconcileOutcome(revived, orphaned);
    }

    /// <summary>
    /// On every heartbeat (steady-state and post-grace fall-through): set-diff the agent's reported live
    /// leases (<paramref name="reportedLeaseIds"/>) against the manager's <c>active</c> set for
    /// <paramref name="hostId"/> and heal any drift.
    /// <list type="bullet">
    /// <item><b>Active not reported</b> → silent container death: flush billing to <c>now</c> and end as
    /// <c>container_lost</c>, freeing capacity without a tunnel disconnect.</item>
    /// <item><b>Reported not active</b> → live contract without a manager lease: revive if it was ended as
    /// <c>host_disconnect</c> (the container kept running); flag orphaned otherwise so the host tears it
    /// down.</item>
    /// <item><b>Equal sets</b> → steady-state fast-path: <b>zero writes</b> — mirrors the
    /// <see cref="Wisper.Api.Tunnel.HostPresenceService"/> no-change guard.</item>
    /// </list>
    /// Suspended leases are left untouched; they are managed by the grace-window path.
    /// Idempotent: repeated heartbeats with the same reported set converge on a single outcome.
    /// </summary>
    public async Task<HeartbeatReconcileOutcome> ReconcileHeartbeatAsync(
        Guid hostId,
        IReadOnlyCollection<Guid> reportedLeaseIds,
        CancellationToken ct = default)
    {
        var reported = reportedLeaseIds as IReadOnlySet<Guid> ?? new HashSet<Guid>(reportedLeaseIds);

        // Only consider active (not suspended) leases; suspended ones are under grace-window management.
        var activeLeases = (await _leases.ListActiveByHostAsync(hostId, ct))
            .Where(l => l.Status == LeaseStatus.Active)
            .ToList();

        var activeIds = new HashSet<Guid>(activeLeases.Select(l => l.Id));

        // Steady-state fast-path: both sets equal → nothing to do, zero writes.
        if (reported.SetEquals(activeIds))
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

            await _meter.FlushLeaseAsync(lease, now, ct);
            await _leases.TransitionStateAsync(
                lease.Id, LeaseStatus.Ended, endReason: LeaseEndReason.ContainerLost, endedAt: now, ct: ct);
            await _walletGate.ReleaseHoldAsync(lease.Id, ct);
            containerLost.Add(lease.Id);
            _logger.LogInformation(
                "lease {LeaseId} ended (container_lost) on heartbeat for host {HostId}; finalized at {Now:O}",
                lease.Id, hostId, now);
        }

        // Reported contracts the manager has no active lease for → check for revival or orphan.
        foreach (var leaseId in reported)
        {
            if (activeIds.Contains(leaseId))
            {
                continue; // already live in the manager — no action needed
            }

            var lease = await _leases.GetByIdAsync(leaseId, ct);

            if (lease is null || lease.HostId != hostId)
            {
                orphaned.Add(leaseId);
                continue;
            }

            // Suspended is under grace management; already active is consistent — skip both.
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
            // semantics as RevivePostGraceAsync — billing restarts at now, offline gap never billed.
            var revival = lease with
            {
                Status = LeaseStatus.Active,
                EndReason = null,
                EndedAt = null,
                LastMeteredAt = now,
            };
            await _leases.UpdateAsync(revival, ct);
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
        // keyed by lease id, so a repeated grace/reconnect flap converges on a single hold_release.
        await _leases.TransitionStateAsync(
            lease.Id, LeaseStatus.Ended, endReason: reason, endedAt: lastHealthyAt, ct: ct);
        await _walletGate.ReleaseHoldAsync(lease.Id, ct);
    }
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
