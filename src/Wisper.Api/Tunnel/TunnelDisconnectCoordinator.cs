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
/// (<c>host_disconnect</c>). Every hook is exception-safe — a reconciliation failure never disrupts the
/// tunnel plumbing — and a host id that is not a Guid (the Phase-1 dev/no-DB harness) is a no-op.
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
    private readonly ConcurrentDictionary<Guid, GraceEntry> _grace = new();

    public TunnelDisconnectCoordinator(
        LeaseReconciliationService reconciler,
        IOptionsMonitor<TunnelOptions> options,
        TimeProvider time,
        ILogger<TunnelDisconnectCoordinator> logger,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _reconciler = reconciler;
        _options = options;
        _time = time;
        _logger = logger;
        _delay = delay ?? ((span, ct) => Task.Delay(span, time, ct));
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
            return Task.CompletedTask; // nothing live to protect
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
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "grace expiry: ending suspended leases for host {HostId} failed", host);
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
    /// On the first heartbeat after a reconnect: set-diff the agent's reported live leases against the
    /// host's suspended set — resume the ones still present, end (<c>container_lost</c>) the ones gone
    /// (docs/TUNNEL.md §8). A no-op when there is no pending grace window (steady-state heartbeats do not
    /// reconcile here) or for a non-Guid host id.
    /// </summary>
    public async Task OnHeartbeatAsync(
        string hostId, IReadOnlyCollection<Guid> liveLeaseIds, CancellationToken ct = default)
    {
        if (!Guid.TryParse(hostId, out var host) || !_grace.TryRemove(host, out var entry))
        {
            return;
        }

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
    }

    /// <summary>Whether a grace window is currently pending for <paramref name="hostId"/> (diagnostics/tests).</summary>
    public bool HasPendingGrace(string hostId) =>
        Guid.TryParse(hostId, out var host) && _grace.ContainsKey(host);
}
