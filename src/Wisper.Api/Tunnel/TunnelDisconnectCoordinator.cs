using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wisper.Api.Domain;
using Wisper.Api.Infrastructure;
using Wisper.Api.Leases;
using Wisper.Api.Metering;

namespace Wisper.Api.Tunnel;

/// <summary>
/// Drives the disconnect / grace / reconnect policy (docs/TUNNEL.md §8) from the tunnel lifecycle onto the
/// pure <see cref="LeaseReconciliationService"/>. On tunnel loss it suspends the host's leases at
/// last-healthy and arms a bounded grace timer (<see cref="TunnelOptions.GraceSeconds"/>); a reconnect
/// within the window cancels the timer and the first heartbeat's live-lease list drives the resume/end
/// set-diff; if grace expires with no reconnect the still-suspended leases are ended
/// (<c>host_disconnect</c>). It also drives host <b>presence</b> (<see cref="IHostPresence"/>, task #392):
/// the host is flipped <c>offline</c> once the loss is durable -- grace expired, or a close with no leases
/// to protect -- so a momentary blip or a superseding reconnect keeps it online. Every hook is
/// exception-safe -- a reconciliation or presence failure never disrupts the tunnel plumbing -- and a host
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
    // Resolved lazily on first orphan teardown: constructor-injecting ITunnelRelay would form a DI cycle
    // (TunnelRelay itself takes an optional TunnelDisconnectCoordinator so it can route lease.ended frames
    // back through reconciliation -- task #56). The factory is invoked only when there is an orphan to
    // tear down, so tests that never trigger a teardown pass null.
    private readonly Func<ITunnelRelay>? _tunnelRelayFactory;
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
        IHostPresence? presence = null,
        Func<ITunnelRelay>? tunnelRelayFactory = null)
    {
        _reconciler = reconciler;
        _options = options;
        _time = time;
        _logger = logger;
        _delay = delay ?? ((span, ct) => Task.Delay(span, time, ct));
        _presence = presence;
        _tunnelRelayFactory = tunnelRelayFactory;
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

        // A new disconnect resets any pending post-grace state from a prior reconnect -- the current
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
            return; // reconnected (or superseded) within grace -- the reconnect path resolves the leases
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
    /// Flip <paramref name="host"/> offline via the presence hook, if one is wired. Exception-safe -- a
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
    /// On every heartbeat: reconciles the manager's lease state against what the agent actually runs
    /// (docs/TUNNEL.md §8). Three paths, each falling through to the continuous reconciliation step:
    /// <list type="bullet">
    /// <item><b>Within grace</b> -- set-diff the reported live leases against the host's <c>suspended</c> set
    /// (resume the ones still present, end <c>container_lost</c> the ones gone). Returns early -- the grace
    /// path already yields a fully consistent active set.</item>
    /// <item><b>Post-grace</b> -- leases were already ended as <c>host_disconnect</c> when grace expired, but
    /// the containers kept running. Revive any live contracts that map to those ended leases so the manager
    /// count matches what the host actually has. Falls through to the continuous step.</item>
    /// <item><b>Continuous (every beat)</b> -- set-diff the reported set against the manager's <c>active</c>
    /// set for the host: end silently-dead containers as <c>container_lost</c>, revive live contracts that
    /// were ended as <c>host_disconnect</c>, flag true orphans. Zero writes in the steady-state common case
    /// (reported set == active set). A no-op for a non-Guid host id.</item>
    /// </list>
    /// </summary>
    public Task OnHeartbeatAsync(
        string hostId, IReadOnlyCollection<Guid> liveLeaseIds, CancellationToken ct = default) =>
        OnHeartbeatInternalAsync(connection: null, hostId, liveLeaseIds, ct);

    /// <summary>
    /// Connection-aware overload used by the live agent endpoint: runs the same reconciliation as the
    /// string-id overload, then -- for each reported lease the continuous set-diff flagged as
    /// <see cref="HeartbeatReconcileOutcome.TerminalOrphaned"/> (host-reported and the manager has an
    /// actual terminal row that cannot be revived) -- best-effort relays a <c>lease.release</c> back over
    /// <paramref name="connection"/> so the container is torn down immediately instead of pinning host
    /// capacity until wisp's TTL reaper fires (task #73). Reported ids the reconciler flagged as
    /// <see cref="HeartbeatReconcileOutcome.UnknownReported"/> (no manager row at all -- possibly a
    /// mid-create where the row has not yet been inserted, task #75) are deliberately NOT torn down;
    /// wisp's TTL reaper is the backstop for any true garbage. The per-connection
    /// <see cref="TunnelConnection.TerminalTeardownRelayed"/> set dedupes so a repeated heartbeat that
    /// keeps reporting the same orphan does not spam relays or logs; a supersede/reconnect starts with
    /// an empty set and gets one fresh attempt per lease. Relay failures (host_offline,
    /// upstream_timeout) are swallowed after logging -- wisp's TTL reaper is the backstop.
    /// </summary>
    public Task OnHeartbeatAsync(
        TunnelConnection connection, IReadOnlyCollection<Guid> liveLeaseIds, CancellationToken ct = default) =>
        OnHeartbeatInternalAsync(connection, connection.HostId, liveLeaseIds, ct);

    private async Task OnHeartbeatInternalAsync(
        TunnelConnection? connection,
        string hostId,
        IReadOnlyCollection<Guid> liveLeaseIds,
        CancellationToken ct)
    {
        if (!Guid.TryParse(hostId, out var host))
        {
            return;
        }

        // Primary path: reconnect within the grace window -- the leases are still suspended and the agent
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
            // The grace path yields a fully consistent active set; no further reconciliation needed.
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

        // Continuous path: runs on every non-grace heartbeat (post-grace fall-through and steady-state).
        // Set-diff the reported set against the manager's active set and heal any drift: silently-dead
        // containers are ended (container_lost), live contracts without a manager lease are revived or
        // orphaned. Zero writes in the common case (reported set == active set).
        HeartbeatReconcileOutcome? heartbeat = null;
        try
        {
            heartbeat = await _reconciler.ReconcileHeartbeatAsync(host, liveLeaseIds, ct);
            if (heartbeat.HasChanges)
            {
                _logger.LogInformation(
                    "host {HostId} heartbeat reconciled: {Lost} container_lost, {Revived} revived," +
                    " {TerminalOrphaned} terminal_orphaned, {UnknownReported} unknown_reported",
                    host, heartbeat.ContainerLost.Count, heartbeat.Revived.Count,
                    heartbeat.TerminalOrphaned.Count, heartbeat.UnknownReported.Count);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "heartbeat: continuous reconciliation for host {HostId} failed", host);
        }

        // Best-effort teardown of orphans (task #73): a host-reported lease the reconciler classified as
        // TerminalOrphaned is a container running on the host that we can never bill or reason about --
        // telling wisp to release it now frees the pinned capacity slot immediately instead of waiting
        // up to a full lease TTL for the local reaper. Only fires when we have a live tunnel
        // (connection + relay both wired); the string-id overload used by unit fixtures skips this. We
        // deliberately do NOT relay for the UnknownReported bucket: those ids have no manager row at
        // all, and LeaseService.CreateAsync provisions the container over the tunnel BEFORE inserting
        // the lease row, so a heartbeat landing in that window reports the fresh lease id while
        // GetByIdAsync still returns null (task #75). Before task #75, both buckets were relayed and
        // that mid-create window killed the container in-flight -- wisp's TTL reaper remains the backstop
        // for any true garbage id, and the next heartbeat's set-diff (once the row is inserted)
        // converges either to a happy active lease or to a container_lost. The per-connection dedupe
        // (TerminalTeardownRelayed) keeps a stable orphan from re-emitting a relay or a log line on
        // every heartbeat, mirroring the transition-edge discipline in HeartbeatDegradedApply (task #65).
        if (heartbeat is not null
            && connection is not null
            && _tunnelRelayFactory is not null
            && heartbeat.TerminalOrphaned.Count > 0)
        {
            await RelayOrphanTeardownsAsync(connection, hostId, heartbeat.TerminalOrphaned, ct);
        }
    }

    private async Task RelayOrphanTeardownsAsync(
        TunnelConnection connection,
        string hostId,
        IReadOnlyList<Guid> orphaned,
        CancellationToken ct)
    {
        var relay = _tunnelRelayFactory!();
        foreach (var leaseId in orphaned)
        {
            // Dedupe first: an orphan the host keeps reporting must NOT trigger a relay (or log line) on
            // every heartbeat. TryAdd returns false on the second+ observation of the same lease id on
            // this connection lifetime. On failure we still mark it tried -- retrying every beat is exactly
            // the spam the task calls out; the reconnect resets the set for one fresh attempt.
            if (!connection.TerminalTeardownRelayed.TryAdd(leaseId, 0))
            {
                continue;
            }

            var externalId = TunnelLeaseId.Format(leaseId);
            try
            {
                await relay.ReleaseAsync(hostId, externalId, ct);
                _logger.LogInformation(
                    "heartbeat teardown: host {HostId} relayed lease.release for orphan {LeaseId}",
                    hostId, leaseId);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ApiException ex)
            {
                // host_offline / upstream_timeout -- the tunnel is unhealthy right now. The TTL reaper on
                // wisp remains the backstop, and a superseding reconnect will get a fresh attempt.
                _logger.LogWarning(
                    "heartbeat teardown: host {HostId} relay for orphan {LeaseId} failed ({Code}: {Message});" +
                    " wisp TTL reaper is the backstop",
                    hostId, leaseId, ex.Code, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "heartbeat teardown: host {HostId} relay for orphan {LeaseId} threw unexpected;" +
                    " wisp TTL reaper is the backstop",
                    hostId, leaseId);
            }
        }
    }

    /// <summary>
    /// On an unsolicited <c>lease.ended</c> frame from the agent (docs/TUNNEL.md §5, §8): route into the
    /// reconciliation path so billing is finalized (TTL + last-healthy capped), the lease transitions to
    /// <c>ended</c> with the mapped <paramref name="reason"/>, and the wallet hold is released -- the same
    /// three-step end path the heartbeat set-diff runs for a silently-vanished container, but driven by
    /// the host's explicit signal (so it fires up to a heartbeat sooner, and carries the correct
    /// <c>expired</c> reason where the set-diff would say <c>container_lost</c>). Idempotent -- an already-
    /// terminal lease is a no-op. Exception-safe: a reconciliation failure logs but never disrupts the
    /// tunnel plumbing (mirrors the heartbeat hook). A no-op for a non-Guid host id (the Phase-1 dev/no-DB
    /// harness).
    /// </summary>
    public async Task OnLeaseEndedAsync(
        string hostId, Guid leaseId, LeaseEndReason reason, CancellationToken ct = default)
    {
        if (!Guid.TryParse(hostId, out var host))
        {
            return;
        }

        try
        {
            var outcome = await _reconciler.EndLeaseFromAgentAsync(host, leaseId, reason, ct);
            _logger.LogInformation(
                "host {HostId} lease.ended: lease {LeaseId} outcome={Outcome} reason={Reason}",
                host, leaseId, outcome, PgEnum.ToSnakeLabel(reason));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(
                ex, "lease.ended: routing lease {LeaseId} for host {HostId} failed", leaseId, host);
        }
    }

    /// <summary>Whether a grace window is currently pending for <paramref name="hostId"/> (diagnostics/tests).</summary>
    public bool HasPendingGrace(string hostId) =>
        Guid.TryParse(hostId, out var host) && _grace.ContainsKey(host);

    /// <summary>
    /// The set of host ids that currently hold an in-memory grace timer on THIS instance (task #55). The
    /// durable grace sweep (<see cref="Wisper.Api.Metering.SuspensionSweepService"/>) skips leases whose
    /// host is in this set -- the fast in-memory path is the source of truth while its timer is armed, and
    /// the sweep is the durable backstop for leases whose grace timer was lost across a restart /
    /// scale-in / crash (multi-instance rule: cross-request state must live in shared storage). A snapshot,
    /// so a concurrent add/remove during the sweep pass does not throw.
    /// </summary>
    public IReadOnlySet<Guid> HostsUnderInProcessGrace() => _grace.Keys.ToHashSet();

    /// <summary>
    /// Whether a post-grace reconciliation is pending for <paramref name="hostId"/> -- the host reconnected
    /// after its grace window expired and the first heartbeat has not yet run the revival pass
    /// (diagnostics/tests).
    /// </summary>
    public bool HasPendingPostGraceReconcile(string hostId) =>
        Guid.TryParse(hostId, out var host) && _pendingPostGraceReconcile.ContainsKey(host);
}
