using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wisper.Api.Infrastructure;
using Wisper.Api.Tunnel.Messages;

namespace Wisper.Api.Tunnel.Backplane;

/// <summary>
/// Multi-instance <see cref="ITunnelRelay"/> (docs/DESIGN.md §7, docs/TUNNEL.md §11). A consumer request
/// can land on any instance, but a host's tunnel is pinned to whichever instance the agent dialed. This
/// relay looks the host up in <see cref="IHostPresenceStore"/>:
/// <list type="bullet">
/// <item>owner is <b>this</b> instance → drive the socket directly via the local <see cref="TunnelRelay"/>;</item>
/// <item>owner is <b>another</b> instance → publish the operation to that instance's RPC channel with a
/// correlation id and await the reply routed back (the owner runs it against its own local relay, which
/// owns the socket + its id space); byte streams are bridged over the backplane the same way.</item>
/// </list>
/// The frame-router / connection-closed hooks are inherently local (they act on a physical
/// <see cref="TunnelConnection"/> on this instance), so they delegate straight to the local relay.
/// </summary>
public sealed class DistributedTunnelRelay : ITunnelRelay, IHostedService, IAsyncDisposable
{
    private readonly ITunnelRelay _local;
    private readonly IHostRegistry _registry;
    private readonly IHostPresenceStore _presence;
    private readonly ITunnelBackplane _backplane;
    private readonly WisperInstanceIdentity _identity;
    private readonly BackplaneOptions _options;
    private readonly ILogger<DistributedTunnelRelay> _logger;

    private readonly ConcurrentDictionary<string, TaskCompletionSource<RpcReply>> _pending = new(StringComparer.Ordinal);
    private readonly object _subscriptionsGate = new();
    private readonly List<IAsyncDisposable> _subscriptions = new();
    private int _disposed;

    public DistributedTunnelRelay(
        ITunnelRelay local,
        IHostRegistry registry,
        IHostPresenceStore presence,
        ITunnelBackplane backplane,
        WisperInstanceIdentity identity,
        IOptions<BackplaneOptions> options,
        ILogger<DistributedTunnelRelay> logger)
    {
        _local = local;
        _registry = registry;
        _presence = presence;
        _backplane = backplane;
        _identity = identity;
        _options = options.Value;
        _logger = logger;
    }

    private string Prefix => _options.ChannelPrefix;

    private TimeSpan RpcTimeout => TimeSpan.FromMilliseconds(_options.RpcTimeoutMs);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var request = await _backplane.SubscribeAsync(
            BackplaneChannels.Request(Prefix, _identity.InstanceId), OnRequestAsync, cancellationToken);
        var reply = await _backplane.SubscribeAsync(
            BackplaneChannels.Reply(Prefix, _identity.InstanceId), OnReplyAsync, cancellationToken);

        lock (_subscriptionsGate)
        {
            _subscriptions.Add(request);
            _subscriptions.Add(reply);
        }

        _logger.LogInformation(
            "backplane: instance {InstanceId} listening for routed relay requests", _identity.InstanceId);
    }

    public async Task StopAsync(CancellationToken cancellationToken) => await DisposeAsync();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        IAsyncDisposable[] subscriptions;
        lock (_subscriptionsGate)
        {
            subscriptions = _subscriptions.ToArray();
            _subscriptions.Clear();
        }

        foreach (var subscription in subscriptions)
        {
            await subscription.DisposeAsync();
        }
    }

    // ----- ITunnelRelay: route local vs remote ---------------------------------------------------

    public async Task<LeaseResult> CreateLeaseAsync(string hostId, LeaseCreate spec, CancellationToken ct = default)
    {
        var owner = await ResolveOwnerAsync(hostId, ct);
        if (owner is null)
        {
            return await _local.CreateLeaseAsync(hostId, spec, ct);
        }

        var reply = await CallAsync(owner, new RpcRequest { Op = nameof(RelayOp.CreateLease), HostId = hostId, Spec = spec }, ct);
        return reply.Lease ?? throw new ApiException(ApiErrorCode.Internal, "routed lease create returned no result");
    }

    public async Task<ExecResult> ExecAsync(string hostId, string leaseId, string command, CancellationToken ct = default)
    {
        var owner = await ResolveOwnerAsync(hostId, ct);
        if (owner is null)
        {
            return await _local.ExecAsync(hostId, leaseId, command, ct);
        }

        var reply = await CallAsync(
            owner, new RpcRequest { Op = nameof(RelayOp.Exec), HostId = hostId, LeaseId = leaseId, Command = command }, ct);
        return reply.Exec ?? throw new ApiException(ApiErrorCode.Internal, "routed exec returned no result");
    }

    public async Task ReleaseAsync(string hostId, string leaseId, CancellationToken ct = default)
    {
        var owner = await ResolveOwnerAsync(hostId, ct);
        if (owner is null)
        {
            await _local.ReleaseAsync(hostId, leaseId, ct);
            return;
        }

        await CallAsync(owner, new RpcRequest { Op = nameof(RelayOp.Release), HostId = hostId, LeaseId = leaseId }, ct);
    }

    public async Task<ITunnelShell> OpenShellAsync(
        string hostId, string leaseId, int cols, int rows, CancellationToken ct = default)
    {
        var owner = await ResolveOwnerAsync(hostId, ct);
        if (owner is null)
        {
            return await _local.OpenShellAsync(hostId, leaseId, cols, rows, ct);
        }

        var correlationId = Guid.NewGuid().ToString("N");
        var shell = new RemoteTunnelShell(_backplane, BackplaneChannels.StreamUp(Prefix, correlationId), sid: 0);

        // Subscribe to the down channel BEFORE the open request so no early PTY output is lost.
        var subscription = await _backplane.SubscribeAsync(
            BackplaneChannels.StreamDown(Prefix, correlationId), shell.HandleDownAsync, ct);
        shell.AttachSubscription(subscription);

        try
        {
            var reply = await CallAsync(
                owner,
                new RpcRequest
                {
                    Op = nameof(RelayOp.OpenShell), HostId = hostId, LeaseId = leaseId, Cols = cols, Rows = rows,
                },
                ct,
                correlationId);
            return shell.WithSid(reply.Sid);
        }
        catch
        {
            await shell.CloseAsync("open_failed", CancellationToken.None);
            throw;
        }
    }

    public async Task<ITunnelExec> OpenExecStreamAsync(
        string hostId, string leaseId, string command, CancellationToken ct = default)
    {
        var owner = await ResolveOwnerAsync(hostId, ct);
        if (owner is null)
        {
            return await _local.OpenExecStreamAsync(hostId, leaseId, command, ct);
        }

        var correlationId = Guid.NewGuid().ToString("N");
        var exec = new RemoteTunnelExec(_backplane, BackplaneChannels.StreamUp(Prefix, correlationId), sid: 0);

        var subscription = await _backplane.SubscribeAsync(
            BackplaneChannels.StreamDown(Prefix, correlationId), exec.HandleDownAsync, ct);
        exec.AttachSubscription(subscription);

        try
        {
            var reply = await CallAsync(
                owner,
                new RpcRequest { Op = nameof(RelayOp.OpenExecStream), HostId = hostId, LeaseId = leaseId, Command = command },
                ct,
                correlationId);
            return exec.WithSid(reply.Sid);
        }
        catch
        {
            await exec.CloseAsync("open_failed", CancellationToken.None);
            throw;
        }
    }

    // The frame router and connection-closed hooks only ever concern a physical socket on THIS
    // instance, so they are pure local concerns — delegate to the local relay.
    public Task RouteAgentFrameAsync(
        TunnelConnection connection, string type, ReadOnlyMemory<byte> payload, CancellationToken ct) =>
        _local.RouteAgentFrameAsync(connection, type, payload, ct);

    public void OnConnectionClosed(TunnelConnection connection) => _local.OnConnectionClosed(connection);

    // ----- routing internals ---------------------------------------------------------------------

    /// <summary>
    /// Resolves which instance owns <paramref name="hostId"/>'s tunnel. Returns <c>null</c> when this
    /// instance is the owner (drive locally); the owning instance id otherwise. Throws
    /// <c>host_offline</c> when no instance owns the host.
    /// </summary>
    private async Task<string?> ResolveOwnerAsync(string hostId, CancellationToken ct)
    {
        // Fast path + correctness floor: the local registry is the authority for sockets physically here,
        // so if the tunnel is on this instance, drive it locally without a presence round-trip — and
        // regardless of any presence-write lag (the socket is here now).
        if (_registry.TryGet(hostId, out _))
        {
            return null;
        }

        var owner = await _presence.GetOwnerAsync(hostId, ct);
        if (owner is null)
        {
            throw new ApiException(ApiErrorCode.HostOffline, $"host {hostId} has no live tunnel on any instance");
        }

        // Presence may name this instance even though the socket isn't in the registry (a just-cleared or
        // stale record); treat that as a local drive so the local relay returns the honest host_offline.
        return owner == _identity.InstanceId ? null : owner;
    }

    private async Task<RpcReply> CallAsync(
        string ownerInstance, RpcRequest request, CancellationToken ct, string? correlationId = null)
    {
        correlationId ??= Guid.NewGuid().ToString("N");
        request = request with { CorrelationId = correlationId, ReplyToInstance = _identity.InstanceId };

        var waiter = new TaskCompletionSource<RpcReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[correlationId] = waiter;
        try
        {
            await _backplane.PublishAsync(
                BackplaneChannels.Request(Prefix, ownerInstance), BackplaneJson.Serialize(request), ct);

            RpcReply reply;
            try
            {
                reply = await waiter.Task.WaitAsync(RpcTimeout, ct);
            }
            catch (TimeoutException)
            {
                throw new ApiException(
                    ApiErrorCode.UpstreamTimeout, $"instance {ownerInstance} did not respond in time");
            }

            if (!reply.Ok)
            {
                var code = Enum.TryParse<ApiErrorCode>(reply.ErrorCode, out var parsed) ? parsed : ApiErrorCode.Internal;
                throw new ApiException(code, reply.ErrorMessage ?? "routed relay request failed");
            }

            return reply;
        }
        finally
        {
            _pending.TryRemove(correlationId, out _);
        }
    }

    private Task OnReplyAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var reply = BackplaneJson.Deserialize<RpcReply>(payload);
        if (reply is not null && _pending.TryRemove(reply.CorrelationId, out var waiter))
        {
            waiter.TrySetResult(reply);
        }

        return Task.CompletedTask;
    }

    private async Task OnRequestAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var request = BackplaneJson.Deserialize<RpcRequest>(payload);
        if (request is null)
        {
            return;
        }

        RpcReply reply;
        try
        {
            reply = await ExecuteLocallyAsync(request, ct);
        }
        catch (ApiException ex)
        {
            reply = Failure(ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "backplane: routed op {Op} for host {HostId} faulted", request.Op, request.HostId);
            reply = Failure(ApiErrorCode.Internal, ex.Message);
        }

        reply = reply with { CorrelationId = request.CorrelationId };
        await _backplane.PublishAsync(
            BackplaneChannels.Reply(Prefix, request.ReplyToInstance), BackplaneJson.Serialize(reply), ct);
    }

    private async Task<RpcReply> ExecuteLocallyAsync(RpcRequest request, CancellationToken ct)
    {
        switch (request.Op)
        {
            case nameof(RelayOp.CreateLease):
                var lease = await _local.CreateLeaseAsync(
                    request.HostId,
                    request.Spec ?? throw new ApiException(ApiErrorCode.ValidationError, "missing lease spec"),
                    ct);
                return Success() with { Lease = lease };

            case nameof(RelayOp.Exec):
                var exec = await _local.ExecAsync(request.HostId, request.LeaseId!, request.Command!, ct);
                return Success() with { Exec = exec };

            case nameof(RelayOp.Release):
                await _local.ReleaseAsync(request.HostId, request.LeaseId!, ct);
                return Success();

            case nameof(RelayOp.OpenShell):
                var shell = await _local.OpenShellAsync(
                    request.HostId, request.LeaseId!, request.Cols, request.Rows, ct);
                await BackplaneStreamBridge.RunShellBridgeAsync(_backplane, Prefix, request.CorrelationId, shell, _logger);
                return Success() with { Sid = shell.Sid };

            case nameof(RelayOp.OpenExecStream):
                var execStream = await _local.OpenExecStreamAsync(request.HostId, request.LeaseId!, request.Command!, ct);
                await BackplaneStreamBridge.RunExecBridgeAsync(_backplane, Prefix, request.CorrelationId, execStream, _logger);
                return Success() with { Sid = execStream.Sid };

            default:
                throw new ApiException(ApiErrorCode.ValidationError, $"unknown routed op {request.Op}");
        }
    }

    private static RpcReply Success() => new() { Ok = true };

    private static RpcReply Failure(ApiErrorCode code, string message) =>
        new() { Ok = false, ErrorCode = code.ToString(), ErrorMessage = message };
}
