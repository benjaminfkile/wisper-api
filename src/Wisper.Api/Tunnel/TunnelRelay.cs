using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wisper.Api.Infrastructure;
using Wisper.Api.Tunnel.Messages;

namespace Wisper.Api.Tunnel;

/// <summary>
/// Singleton <see cref="ITunnelRelay"/>: resolves a host's <see cref="TunnelConnection"/> via
/// <see cref="IHostRegistry"/>, sends rid-tagged request frames, and awaits the correlated
/// responses using pending-request maps of <see cref="TaskCompletionSource{TResult}"/> keyed by
/// (connection, rid) and (connection, leaseId). A per-request deadline
/// (<see cref="TunnelOptions.RelayRequestTimeoutMs"/>) turns a silent host into a typed
/// <c>upstream_timeout</c>; a missing tunnel is a typed <c>host_offline</c>.
/// </summary>
public sealed class TunnelRelay : ITunnelRelay
{
    private readonly IHostRegistry _registry;
    private readonly IOptionsMonitor<TunnelOptions> _options;
    private readonly ILogger<TunnelRelay> _logger;

    // Responses that echo a request rid (lease.accepted, lease.failed, lease.released, exec.result).
    private readonly ConcurrentDictionary<(TunnelConnection, uint), TaskCompletionSource<byte[]>> _ridWaiters = new();

    // The unsolicited lease.ready / terminal lease.failed, correlated by the server-issued leaseId.
    private readonly ConcurrentDictionary<(TunnelConnection, string), TaskCompletionSource<byte[]>> _leaseWaiters = new();

    public TunnelRelay(
        IHostRegistry registry,
        IOptionsMonitor<TunnelOptions> options,
        ILogger<TunnelRelay> logger)
    {
        _registry = registry;
        _options = options;
        _logger = logger;
    }

    // How often the readiness wait re-checks the registry for a host that is not registered yet (its
    // agent may still be mid-connect). Small enough to be responsive, coarse enough to not spin.
    private static readonly TimeSpan ReadinessPollInterval = TimeSpan.FromMilliseconds(25);

    private TimeSpan Timeout => TimeSpan.FromMilliseconds(_options.CurrentValue.RelayRequestTimeoutMs);

    public async Task<LeaseResult> CreateLeaseAsync(string hostId, LeaseCreate spec, CancellationToken ct = default)
    {
        var connection = await ResolveAsync(hostId, ct);
        var rid = connection.NextRid();
        var leaseId = "lease_" + Guid.NewGuid().ToString("N");

        // Wisper owns the id space (docs/TUNNEL.md §1): stamp the server rid + leaseId onto the spec.
        var frame = spec with { T = FrameTypes.LeaseCreate, Rid = rid, LeaseId = leaseId, Sid = 0 };

        var acceptedWaiter = RegisterRid(connection, rid);
        var readyWaiter = RegisterLease(connection, leaseId);
        try
        {
            await connection.SendControlAsync(frame, ct);

            var acceptedPayload = await AwaitResponseAsync(acceptedWaiter.Task, ct);
            var accepted = Deserialize<LeaseAccepted>(acceptedPayload);

            // The container isn't usable until wisp reaches ready — wait for it (or lease.failed).
            await AwaitResponseAsync(readyWaiter.Task, ct);

            _logger.LogInformation(
                "relay: lease {LeaseId} ready on host {HostId} (contract {WispContractId})",
                leaseId, hostId, accepted.WispContractId);

            return new LeaseResult(leaseId, accepted.WispContractId, accepted.Status);
        }
        finally
        {
            _ridWaiters.TryRemove((connection, rid), out _);
            _leaseWaiters.TryRemove((connection, leaseId), out _);
        }
    }

    public async Task<ExecResult> ExecAsync(string hostId, string leaseId, string command, CancellationToken ct = default)
    {
        var connection = await ResolveAsync(hostId, ct);
        var rid = connection.NextRid();

        var waiter = RegisterRid(connection, rid);
        try
        {
            await connection.SendControlAsync(
                new ExecRun { Rid = rid, LeaseId = leaseId, Command = command }, ct);

            var payload = await AwaitResponseAsync(waiter.Task, ct);
            return Deserialize<ExecResult>(payload);
        }
        finally
        {
            _ridWaiters.TryRemove((connection, rid), out _);
        }
    }

    public async Task ReleaseAsync(string hostId, string leaseId, CancellationToken ct = default)
    {
        var connection = await ResolveAsync(hostId, ct);
        var rid = connection.NextRid();

        var waiter = RegisterRid(connection, rid);
        try
        {
            await connection.SendControlAsync(
                new LeaseRelease { Rid = rid, LeaseId = leaseId }, ct);

            await AwaitResponseAsync(waiter.Task, ct);
        }
        finally
        {
            _ridWaiters.TryRemove((connection, rid), out _);
        }
    }

    public async Task<ITunnelShell> OpenShellAsync(
        string hostId, string leaseId, int cols, int rows, CancellationToken ct = default)
    {
        var connection = await ResolveAsync(hostId, ct);
        var rid = connection.NextRid();
        var sid = connection.NextSid();
        var opts = _options.CurrentValue;

        // The stream owns per-stream credit flow control (docs/TUNNEL.md §9): it decrements the
        // send window as it sends, blocks at 0, replenishes on stream.credit, and emits credit as
        // received bytes drain. Overflow → stream.closed{flow_violation}.
        var stream = new TunnelStream(
            sid,
            opts.InitialWindowBytes,
            opts.MaxFrameBytes,
            Math.Max(1, opts.InitialWindowBytes / 2),
            (frame, c) => connection.SendBinaryAsync(frame, c),
            (credit, c) => connection.SendControlAsync(credit, c),
            (closed, c) => connection.SendControlAsync(closed, c),
            _logger);
        connection.Streams[sid] = stream;

        var openedWaiter = RegisterRid(connection, rid);
        try
        {
            await connection.SendControlAsync(
                new ShellOpen { Rid = rid, Sid = sid, LeaseId = leaseId, Cols = cols, Rows = rows }, ct);

            await AwaitResponseAsync(openedWaiter.Task, ct);

            _logger.LogInformation(
                "relay: shell stream {Sid} opened on host {HostId} (lease {LeaseId})", sid, hostId, leaseId);

            return new TunnelShell(connection, stream);
        }
        catch
        {
            connection.Streams.TryRemove(sid, out _);
            await stream.DisposeAsync();
            throw;
        }
        finally
        {
            _ridWaiters.TryRemove((connection, rid), out _);
        }
    }

    public async Task<ITunnelExec> OpenExecStreamAsync(
        string hostId, string leaseId, string command, CancellationToken ct = default)
    {
        var connection = await ResolveAsync(hostId, ct);
        var rid = connection.NextRid();
        var sid = connection.NextSid();
        var opts = _options.CurrentValue;

        // Streamed exec is unidirectional A→W: Wisper is the receiver, so the stream's receive
        // accounting + credit flow control (docs/TUNNEL.md §9) is what matters — the send window
        // goes unused. The exec wrapper reuses this stream as-is and layers channel preservation on
        // top (stdout ch 1 vs stderr ch 2, which the byte pipe alone discards).
        var stream = new TunnelStream(
            sid,
            opts.InitialWindowBytes,
            opts.MaxFrameBytes,
            Math.Max(1, opts.InitialWindowBytes / 2),
            (frame, c) => connection.SendBinaryAsync(frame, c),
            (credit, c) => connection.SendControlAsync(credit, c),
            (closed, c) => connection.SendControlAsync(closed, c),
            _logger);
        var exec = new TunnelExec(connection, stream);
        connection.Streams[sid] = exec;

        var openedWaiter = RegisterRid(connection, rid);
        try
        {
            await connection.SendControlAsync(
                new ExecOpen { Rid = rid, Sid = sid, LeaseId = leaseId, Command = command }, ct);

            await AwaitResponseAsync(openedWaiter.Task, ct);

            _logger.LogInformation(
                "relay: exec stream {Sid} opened on host {HostId} (lease {LeaseId})", sid, hostId, leaseId);

            return exec;
        }
        catch
        {
            connection.Streams.TryRemove(sid, out _);
            await exec.DisposeAsync();
            throw;
        }
        finally
        {
            _ridWaiters.TryRemove((connection, rid), out _);
        }
    }

    public Task RouteAgentFrameAsync(TunnelConnection connection, string type, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        switch (type)
        {
            case FrameTypes.LeaseAccepted:
            case FrameTypes.LeaseReleased:
            case FrameTypes.ExecResult:
            case FrameTypes.ShellOpened:
            case FrameTypes.ExecOpened:
                CompleteByRid(connection, payload);
                break;

            case FrameTypes.ExecExit:
                HandleExecExit(connection, payload);
                break;

            case FrameTypes.StreamCredit:
                HandleStreamCredit(connection, payload);
                break;

            case FrameTypes.StreamClosed:
                HandleStreamClosed(connection, payload);
                break;

            case FrameTypes.LeaseReady:
                CompleteLeaseReady(connection, payload);
                break;

            case FrameTypes.LeaseFailed:
                FailLease(connection, payload);
                break;

            case FrameTypes.LeaseEnded:
                HandleLeaseEnded(connection, payload);
                break;

            case FrameTypes.Error:
                HandleError(connection, payload);
                break;

            default:
                _logger.LogDebug("relay: ignoring unhandled agent frame {FrameType}", type);
                break;
        }

        return Task.CompletedTask;
    }

    public void OnConnectionClosed(TunnelConnection connection)
    {
        var offline = new ApiException(ApiErrorCode.HostOffline, $"host {connection.HostId} tunnel closed");

        // Tear down every open stream so bridged consumers unblock and close (docs/TUNNEL.md §8).
        foreach (var sid in connection.Streams.Keys)
        {
            if (connection.Streams.TryRemove(sid, out var stream))
            {
                stream.OnPeerClosed("host_offline");
            }
        }

        foreach (var key in _ridWaiters.Keys)
        {
            if (ReferenceEquals(key.Item1, connection) && _ridWaiters.TryRemove(key, out var tcs))
            {
                tcs.TrySetException(offline);
            }
        }

        foreach (var key in _leaseWaiters.Keys)
        {
            if (ReferenceEquals(key.Item1, connection) && _leaseWaiters.TryRemove(key, out var tcs))
            {
                tcs.TrySetException(offline);
            }
        }
    }

    /// <summary>
    /// Resolves a host's live, <b>ready</b> tunnel (docs/TUNNEL.md §3). A ready tunnel returns on the
    /// fast path. A freshly-connected agent that is still completing its hello handshake — either not
    /// registered yet, or registered but not yet ready — is waited on for up to
    /// <see cref="TunnelOptions.HostReadinessTimeoutMs"/>, closing the connection-readiness race so a
    /// create right after connect does not spuriously fail. A host that never becomes ready in that
    /// window (or is genuinely absent) surfaces the typed, retryable <c>host_offline</c> (409).
    /// </summary>
    private async Task<TunnelConnection> ResolveAsync(string hostId, CancellationToken ct)
    {
        var timeout = TimeSpan.FromMilliseconds(Math.Max(0, _options.CurrentValue.HostReadinessTimeoutMs));
        var deadline = DateTime.UtcNow + timeout;

        while (true)
        {
            if (_registry.TryGet(hostId, out var connection) && connection is not null)
            {
                if (connection.IsReady)
                {
                    return connection;
                }

                // Registered but still completing its hello handshake — wait briefly for readiness
                // instead of racing to host_offline. Returns false on timeout or if the tunnel was
                // abandoned before it became ready; either way fall through and re-evaluate.
                var remaining = deadline - DateTime.UtcNow;
                if (remaining > TimeSpan.Zero && await connection.WaitUntilReadyAsync(remaining, ct))
                {
                    return connection;
                }
            }

            if (DateTime.UtcNow >= deadline)
            {
                break;
            }

            // Not registered yet (the agent may still be mid-connect), or the connection we just saw
            // was abandoned — a superseding one may register. Re-check after a short beat.
            await Task.Delay(ReadinessPollInterval, ct);
        }

        throw new ApiException(ApiErrorCode.HostOffline, $"host {hostId} has no live tunnel");
    }

    private TaskCompletionSource<byte[]> RegisterRid(TunnelConnection connection, uint rid)
    {
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ridWaiters[(connection, rid)] = tcs;
        return tcs;
    }

    private TaskCompletionSource<byte[]> RegisterLease(TunnelConnection connection, string leaseId)
    {
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        _leaseWaiters[(connection, leaseId)] = tcs;
        return tcs;
    }

    /// <summary>
    /// Awaits a pending response with the configured deadline. A timeout becomes a typed
    /// <c>upstream_timeout</c>; a <c>lease.failed</c> surfaces as the <c>ApiException</c> the
    /// router set on the task; a cancelled <paramref name="ct"/> propagates unchanged.
    /// </summary>
    private async Task<byte[]> AwaitResponseAsync(Task<byte[]> task, CancellationToken ct)
    {
        try
        {
            return await task.WaitAsync(Timeout, ct);
        }
        catch (TimeoutException)
        {
            throw new ApiException(ApiErrorCode.UpstreamTimeout, "the host did not respond in time");
        }
    }

    private void CompleteByRid(TunnelConnection connection, ReadOnlyMemory<byte> payload)
    {
        var rid = PeekRid(payload.Span);
        if (rid == 0)
        {
            _logger.LogWarning("relay: response with no rid dropped");
            return;
        }

        if (_ridWaiters.TryRemove((connection, rid), out var tcs))
        {
            tcs.TrySetResult(payload.ToArray());
        }
        else
        {
            _logger.LogDebug("relay: no pending request for rid {Rid} (late or duplicate response)", rid);
        }
    }

    private void CompleteLeaseReady(TunnelConnection connection, ReadOnlyMemory<byte> payload)
    {
        var ready = TryDeserialize<LeaseReady>(payload);
        if (ready is null || string.IsNullOrEmpty(ready.LeaseId))
        {
            return;
        }

        if (_leaseWaiters.TryRemove((connection, ready.LeaseId), out var tcs))
        {
            tcs.TrySetResult(payload.ToArray());
        }
    }

    private void FailLease(TunnelConnection connection, ReadOnlyMemory<byte> payload)
    {
        var failed = TryDeserialize<LeaseFailed>(payload);
        if (failed is null)
        {
            return;
        }

        var error = string.IsNullOrEmpty(failed.Error) ? "lease provisioning failed" : failed.Error;
        var ex = new ApiException(MapAgentErrorCode(failed.Code), error);

        // lease.failed carries both rid and leaseId; fail whichever awaiter is still outstanding
        // (the rid one before accepted, the leaseId one if it failed after accepted).
        if (failed.Rid != 0 && _ridWaiters.TryRemove((connection, failed.Rid), out var ridTcs))
        {
            ridTcs.TrySetException(ex);
        }

        if (!string.IsNullOrEmpty(failed.LeaseId) && _leaseWaiters.TryRemove((connection, failed.LeaseId), out var leaseTcs))
        {
            leaseTcs.TrySetException(ex);
        }
    }

    private void HandleStreamCredit(TunnelConnection connection, ReadOnlyMemory<byte> payload)
    {
        var credit = TryDeserialize<StreamCredit>(payload);
        if (credit is null || credit.Sid == 0)
        {
            return;
        }

        if (connection.Streams.TryGetValue(credit.Sid, out var stream))
        {
            stream.OnCreditGranted(credit.Bytes);
        }
        else
        {
            _logger.LogDebug("relay: stream.credit for unknown sid {Sid} dropped", credit.Sid);
        }
    }

    private void HandleStreamClosed(TunnelConnection connection, ReadOnlyMemory<byte> payload)
    {
        var closed = TryDeserialize<StreamClosed>(payload);
        if (closed is null || closed.Sid == 0)
        {
            return;
        }

        if (connection.Streams.TryRemove(closed.Sid, out var stream))
        {
            var reason = string.IsNullOrEmpty(closed.Reason) ? "peer_closed" : closed.Reason;
            _logger.LogInformation(
                "relay: host {HostId} closed stream {Sid} ({Reason})", connection.HostId, closed.Sid, reason);
            stream.OnPeerClosed(reason);
        }
    }

    private void HandleExecExit(TunnelConnection connection, ReadOnlyMemory<byte> payload)
    {
        var exit = TryDeserialize<ExecExit>(payload);
        if (exit is null || exit.Sid == 0)
        {
            return;
        }

        // exec.exit terminates a streamed exec: hand the exit code to the owning exec stream so it
        // completes its output and surfaces the code (docs/TUNNEL.md §5, §6). Only route it to an
        // exec stream — a shell sid never sees exec.exit.
        if (connection.Streams.TryGetValue(exit.Sid, out var sink) && sink is TunnelExec exec)
        {
            connection.Streams.TryRemove(exit.Sid, out _);
            _logger.LogInformation(
                "relay: host {HostId} exec stream {Sid} exited ({ExitCode})",
                connection.HostId, exit.Sid, exit.ExitCode);
            exec.OnExit(exit.ExitCode);
        }
        else
        {
            _logger.LogDebug("relay: exec.exit for unknown sid {Sid} dropped", exit.Sid);
        }
    }

    private void HandleLeaseEnded(TunnelConnection connection, ReadOnlyMemory<byte> payload)
    {
        var ended = TryDeserialize<LeaseEnded>(payload);
        if (ended is null || string.IsNullOrEmpty(ended.LeaseId))
        {
            return;
        }

        // Phase 1: log the unsolicited end and complete any waiter still blocked on this lease
        // (e.g. a create awaiting ready that the host reaped first). Full grace/reconcile is §8.
        _logger.LogInformation(
            "relay: host {HostId} reported lease {LeaseId} ended ({Reason})",
            connection.HostId, ended.LeaseId, ended.Reason);

        if (_leaseWaiters.TryRemove((connection, ended.LeaseId), out var tcs))
        {
            tcs.TrySetException(new ApiException(
                ApiErrorCode.LeaseFailed, $"lease ended before ready: {ended.Reason}"));
        }
    }

    /// <summary>
    /// Fails an in-flight request the host reported a typed <c>error</c> for (docs/TUNNEL.md §5, §12).
    /// Without this the frame would fall through to the default log-and-ignore branch and the consumer's
    /// call would hang until the full <see cref="TunnelOptions.RelayRequestTimeoutMs"/> deadline instead
    /// of failing fast: the rid waiter (lease.create-before-accepted, exec.run, lease.release,
    /// shell/exec.open) is completed with the mapped typed error, and any owning stream named by <c>sid</c>
    /// is torn down so a bridged consumer unblocks. Idempotent — a late/duplicate error whose waiter and
    /// stream are already gone is a no-op debug log.
    /// </summary>
    private void HandleError(TunnelConnection connection, ReadOnlyMemory<byte> payload)
    {
        var error = TryDeserialize<ErrorMessage>(payload);
        if (error is null)
        {
            return;
        }

        var code = MapAgentErrorCode(error.Code);
        var message = string.IsNullOrEmpty(error.Message)
            ? $"the host reported error '{(string.IsNullOrEmpty(error.Code) ? "unspecified" : error.Code)}'"
            : error.Message;
        var ex = new ApiException(code, message);

        var handled = false;

        // Fail the correlated request. Wisper owns the rid space (docs/TUNNEL.md §1), so an error echoing a
        // live rid is the response to exactly that request.
        if (error.Rid != 0 && _ridWaiters.TryRemove((connection, error.Rid), out var ridTcs))
        {
            ridTcs.TrySetException(ex);
            handled = true;
        }

        // A mid-stream error (or one for a stream whose open already completed) names the sid; tear the
        // stream down so the bridged consumer sees the end rather than a stall.
        if (error.Sid != 0 && connection.Streams.TryRemove(error.Sid, out var stream))
        {
            stream.OnPeerClosed(string.IsNullOrEmpty(error.Code) ? "error" : error.Code);
            handled = true;
        }

        if (handled)
        {
            _logger.LogInformation(
                "relay: host {HostId} reported error code={Code} rid={Rid} sid={Sid}: {Message}",
                connection.HostId, error.Code, error.Rid, error.Sid, message);
        }
        else
        {
            _logger.LogDebug(
                "relay: host {HostId} error code={Code} rid={Rid} sid={Sid} had no pending waiter (late or duplicate)",
                connection.HostId, error.Code, error.Rid, error.Sid);
        }
    }

    /// <summary>
    /// Maps a tunnel <c>error.code</c> (docs/TUNNEL.md §12) to the typed API error the consumer sees.
    /// Host-known request failures map to their specific code; everything else (a local wisp non-2xx,
    /// overflow, unsupported, internal, or an unrecognised code) surfaces as a <c>lease_failed</c> (502
    /// bad-gateway) — the host could not fulfil the request.
    /// </summary>
    private static ApiErrorCode MapAgentErrorCode(string? code) => code switch
    {
        "not_ready" => ApiErrorCode.LeaseNotReady,
        "unknown_lease" => ApiErrorCode.NotFound,
        "at_capacity" => ApiErrorCode.AtCapacity,
        _ => ApiErrorCode.LeaseFailed,
    };

    private static uint PeekRid(ReadOnlySpan<byte> payload)
    {
        try
        {
            var env = JsonSerializer.Deserialize<ControlEnvelope>(payload, ControlJson.Options);
            return env?.Rid ?? 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    private static T Deserialize<T>(byte[] payload) where T : class =>
        JsonSerializer.Deserialize<T>(payload, ControlJson.Options)
            ?? throw new ApiException(ApiErrorCode.Internal, $"could not decode {typeof(T).Name}");

    private static T? TryDeserialize<T>(ReadOnlyMemory<byte> payload) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(payload.Span, ControlJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
