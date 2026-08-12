using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wisper.Api.Domain;
using Wisper.Api.Metering;

namespace Wisper.Api.Tunnel;

/// <summary>
/// Drives the disconnect / grace / reconnect policy (docs/TUNNEL.md §8) from the tunnel lifecycle onto the
/// pure <see cref="LeaseReconciliationService"/>. On tunnel loss it suspends the host's leases at
/// last-healthy and arms a bounded grace timer (<see cref="TunnelOptions.GraceSeconds"/>); a reconnect
/// within the window cancels the timer and the first heartbeat's live-lease list drives the resume/end
/// set-diff; if grace expires with no reconnect the still-suspended leases are ended
/// (<c>host_disconnect</c>). It also drives host <b>presence</b> (<see cref="IHostPresence"/>, task #392):
/// the host is flipped <c>offline</c> once the loss is durable — grace expired, or a close with no leases
/// to protect — so a momentary blip or a superseding reconnect keeps it online. Every hook is
/// exception-safe — a reconciliation or presence failure never disrupts the tunnel plumbing — and a host
/// id that is not a Guid (the Phase-1 dev/no-DB harness) is a no-op.
/// <para>
/// The grace timer runs as a background task per host, cancellable by reconnect. <see
/// cref="OnDisconnectedAsync"/> awaits only the synchronous suspend and returns the background grace task,
/// which production discards (fire-and-forget) and tests may await for determinism.
/// </para>
/// </summary>
public sealed class TunnelDisconnectCoordinator
{
    private readonly LeaseReconciliationService _reconciler;
    private readonly IOptionsMonitor<TunnelOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<TunnelDisconnectCoordinator> _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly IHostPresence? _presence;
    private readonly ConcurrentDictionary<Guid, GraceEntry> _grace = new();

    // Hosts that reconnected AFTER their grace window already expired: their leases were ended as
    // host_disconnect but the containers kept running (wisp stops the agent, not the containers). The first
    // heartbeat after such a reconnect must revive any matching leases so the manager count stays accurate
    // (docs/TUNNEL.md §8). Used as a concurrent set; the bool value is always true.
    private readonly ConcurrentDictionary<Guid, bool> _pendingPostGraceReconcile = new();

    public TunnelDisconnectCoordinator(
        LeaseReconciliationService reconciler,
        IOptionsMonitor<TunnelOptions> options,
        TimeProvider time,
        ILogger<TunnelDisconnectCoordinator> logger,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        IHostPresence? presence = null)
    {
        _reconciler = reconciler;
        _options = options;
        _time = time;
        _logger = logger;
        _delay = delay ?? ((span, ct) => Task.Delay(span, time, ct));
        _presence = presence;
    }

    private sealed class GraceEntry
    {
        public required DateTimeOffset LastHealthyAt { get; init; }
        public required CancellationTokenSource Cts { get; init; }
    }

    /// <summary>
    /// On tunnel loss: suspend <paramref name="hostId"/>'s active leases at <paramref name="lastHealthyAt"/>
    /// and, if anything is now suspended, arm the grace timer. Returns the background grace-window task
    /// (production discards it; the suspend itself is already complete when this task's awaited outer step
    /// finishes). A no-op for a non-Guid host id.
    /// </summary>
    public async Task<Task> OnDisconnectedAsync(
        string hostId, DateTimeOffset lastHealthyAt, CancellationToken ct = default)
    {
        if (!Guid.TryParse(hostId, out var host))
        {
            return Task.CompletedTask;
        }

        // A new disconnect resets any pending post-grace state from a prior reconnect — the current
        // disconnect will manage the lease set from here on.
        _pendingPostGraceReconcile.TryRemove(host, out _);

        SuspendOutcome outcome;
        try
        {
            outcome = await _reconciler.SuspendHostLeasesAsync(host, lastHealthyAt, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "disconnect: suspending leases for host {HostId} failed", host);
            return Task.CompletedTask;
        }

        if (outcome.TotalSuspended == 0)
        {
            // Nothing live to protect (no leases in grace): the host is simply gone, so flip it offline
            // immediately rather than arming an empty grace window (docs/TUNNEL.md §8, task #392).
            await MarkOfflineSafeAsync(host, lastHealthyAt);
            return Task.CompletedTask;
        }

        var grace = TimeSpan.FromSeconds(Math.Max(0, _options.CurrentValue.GraceSeconds));
        _logger.LogInformation(
            "host {HostId} tunnel lost: {New} lease(s) suspended ({Total} suspended); grace {Grace}s",
            host, outcome.NewlySuspended, outcome.TotalSuspended, grace.TotalSeconds);
        return ArmGrace(host, lastHealthyAt, grace);
    }

    private Task ArmGrace(Guid host, DateTimeOffset lastHealthyAt, TimeSpan grace)
    {
        var cts = new CancellationTokenSource();
        var entry = new GraceEntry { LastHealthyAt = lastHealthyAt, Cts = cts };

        // Supersede any prior grace window for this host (a re-drop before the first resolved).
        if (_grace.TryRemove(host, out var prior))
        {
            prior.Cts.Cancel();
            prior.Cts.Dispose();
        }

        _grace[host] = entry;
        return RunGraceAsync(host, entry, grace);
    }

    private async Task RunGraceAsync(Guid host, GraceEntry entry, TimeSpan grace)
    {
        try
        {
            await _delay(grace, entry.Cts.Token);
        }
        catch (OperationCanceledException)
        {
            return; // reconnected (or superseded) within grace — the reconnect path resolves the leases
        }

        // Expire only if this exact entry is still current (not already resolved by a reconnect).
        if (!_grace.TryRemove(new KeyValuePair<Guid, GraceEntry>(host, entry)))
        {
            return;
        }

        entry.Cts.Dispose();
        try
        {
            var ended = await _reconciler.EndSuspendedHostLeasesAsync(
                host, entry.LastHealthyAt, LeaseEndReason.HostDisconnect, CancellationToken.None);
            _logger.LogInformation(
                "host {HostId} grace expired with no reconnect: {Count} lease(s) ended (host_disconnect)",
                host, ended);

            // Mark the host for post-grace reconciliation: if it reconnects later, its containers may still
            // be running (wisp stops the agent, not the containers). The first heartbeat will revive any
            // live contracts that map to the leases we just ended, keeping the manager count accurate
            // (docs/TUNNEL.md §8).
            if (ended > 0)
            {
                _pendingPostGraceReconcile[host] = true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "grace expiry: ending suspended leases for host {HostId} failed", host);
        }

        // The loss is now durable (no reconnect within grace): flip the host offline so the catalog drops
        // it, stamping last-seen at last-healthy (docs/TUNNEL.md §8, task #392).
        await MarkOfflineSafeAsync(host, entry.LastHealthyAt);
    }

    /// <summary>
    /// Flip <paramref name="host"/> offline via the presence hook, if one is wired. Exception-safe — a
    /// presence failure never disrupts the grace/reconnect plumbing (mirrors the lease hooks). A no-op when
    /// no <see cref="IHostPresence"/> was supplied (the unit fixtures that only exercise lease reconciliation).
    /// </summary>
    private async Task MarkOfflineSafeAsync(Guid host, DateTimeOffset lastHealthyAt)
    {
        if (_presence is null)
        {
            return;
        }

        try
        {
            await _presence.GoOfflineAsync(host, lastHealthyAt, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "disconnect: flipping host {HostId} offline failed", host);
        }
    }

    /// <summary>
    /// On (re)connect: cancel any pending grace timer for <paramref name="hostId"/> so the leases are not
    /// ended out from under a returning agent. The suspended leases are left in place for the first
    /// heartbeat to reconcile (<see cref="OnHeartbeatAsync"/>). A no-op for a non-Guid host id or a host
    /// with no pending grace (a first connect).
    /// </summary>
    public void OnReconnected(string hostId)
    {
        if (Guid.TryParse(hostId, out var host) && _grace.TryGetValue(host, out var entry))
        {
            entry.Cts.Cancel(); // stop the expiry timer; keep the entry so the heartbeat reconciles
        }
    }

    /// <summary>
    /// On the first heartbeat after a reconnect: reconciles the manager's lease state against what the agent
    /// actually runs (docs/TUNNEL.md §8). Two paths:
    /// <list type="bullet">
    /// <item><b>Within grace</b> — set-diff the reported live leases against the host's <c>suspended</c> set
    /// (resume the ones still present, end <c>container_lost</c> the ones gone).</item>
    /// <item><b>Post-grace</b> — leases were already ended as <c>host_disconnect</c> when grace expired, but
    /// the containers kept running. Revive any live contracts that map to those ended leases so the manager
    /// count matches what the host actually has.</item>
    /// </list>
    /// Steady-state heartbeats (no pending grace and no post-grace flag) are a no-op. A no-op for a
    /// non-Guid host id.
    /// </summary>
    public async Task OnHeartbeatAsync(
        string hostId, IReadOnlyCollection<Guid> liveLeaseIds, CancellationToken ct = default)
    {
        if (!Guid.TryParse(hostId, out var host))
        {
            return;
        }

        // Primary path: reconnect within the grace window — the leases are still suspended and the agent
        // reported its live set, so we can set-diff and resume/end accordingly.
        if (_grace.TryRemove(host, out var entry))
        {
            entry.Cts.Cancel();
            entry.Cts.Dispose();
            try
            {
                var outcome = await _reconciler.ReconcileHostAsync(host, liveLeaseIds, entry.LastHealthyAt, ct);
                _logger.LogInformation(
                    "host {HostId} reconnect reconciled: {Resumed} resumed, {Lost} container_lost",
                    host, outcome.Resumed.Count, outcome.ContainerLost.Count);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "reconnect: reconciling leases for host {HostId} failed", host);
            }
            return;
        }

        // Secondary path: reconnect AFTER grace expiry. Leases were ended as host_disconnect, but the
        // containers kept running (wisp stops the agent, not the containers). Revive any live contracts that
        // map to those ended leases so the manager capacity count matches what the host actually has
        // (docs/TUNNEL.md §8). Without this, the catalog advertises free capacity while wisp is full.
        if (_pendingPostGraceReconcile.TryRemove(host, out _))
        {
            var reconnectAt = _time.GetUtcNow();
            try
            {
                var outcome = await _reconciler.RevivePostGraceAsync(host, liveLeaseIds, reconnectAt, ct);
                _logger.LogInformation(
                    "host {HostId} post-grace reconnect reconciled: {Revived} revived, {Orphaned} orphaned",
                    host, outcome.Revived.Count, outcome.Orphaned.Count);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "post-grace reconnect: reviving leases for host {HostId} failed", host);
            }
        }
    }

    /// <summary>Whether a grace window is currently pending for <paramref name="hostId"/> (diagnostics/tests).</summary>
    public bool HasPendingGrace(string hostId) =>
        Guid.TryParse(hostId, out var host) && _grace.ContainsKey(host);

    /// <summary>
    /// Whether a post-grace reconciliation is pending for <paramref name="hostId"/> — the host reconnected
    /// after its grace window expired and the first heartbeat has not yet run the revival pass
    /// (diagnostics/tests).
    /// </summary>
    public bool HasPendingPostGraceReconcile(string hostId) =>
        Guid.TryParse(hostId, out var host) && _pendingPostGraceReconcile.ContainsKey(host);
}
