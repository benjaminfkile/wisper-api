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

    private TimeSpan Timeout => TimeSpan.FromMilliseconds(_options.CurrentValue.RelayRequestTimeoutMs);

    public async Task<LeaseResult> CreateLeaseAsync(string hostId, LeaseCreate spec, CancellationToken ct = default)
    {
        var connection = Resolve(hostId);
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
        var connection = Resolve(hostId);
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
        var connection = Resolve(hostId);
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

    public Task RouteAgentFrameAsync(TunnelConnection connection, string type, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        switch (type)
        {
            case FrameTypes.LeaseAccepted:
            case FrameTypes.LeaseReleased:
            case FrameTypes.ExecResult:
                CompleteByRid(connection, payload);
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

            default:
                _logger.LogDebug("relay: ignoring unhandled agent frame {FrameType}", type);
                break;
        }

        return Task.CompletedTask;
    }

    public void OnConnectionClosed(TunnelConnection connection)
    {
        var offline = new ApiException(ApiErrorCode.HostOffline, $"host {connection.HostId} tunnel closed");

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

    private TunnelConnection Resolve(string hostId)
    {
        if (_registry.TryGet(hostId, out var connection) && connection is not null)
        {
            return connection;
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
        var ex = new ApiException(ApiErrorCode.LeaseFailed, error);

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
