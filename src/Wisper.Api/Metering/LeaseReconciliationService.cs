using Microsoft.Extensions.Logging;
using Wisper.Api.Domain;
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
    private readonly TimeProvider _time;
    private readonly ILogger<LeaseReconciliationService> _logger;

    public LeaseReconciliationService(
        ILeaseRepository leases,
        MeteringService meter,
        TimeProvider time,
        ILogger<LeaseReconciliationService> logger)
    {
        _leases = leases;
        _meter = meter;
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

    private Task EndSuspendedAsync(
        Lease lease, LeaseEndReason reason, DateTimeOffset lastHealthyAt, CancellationToken ct) =>
        _leases.TransitionStateAsync(
            lease.Id, LeaseStatus.Ended, endReason: reason, endedAt: lastHealthyAt, ct: ct);
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
