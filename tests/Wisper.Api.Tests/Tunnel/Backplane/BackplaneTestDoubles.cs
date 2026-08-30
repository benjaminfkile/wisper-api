using System.Collections.Concurrent;
using System.Threading.Channels;
using Wisper.Api.Infrastructure;
using Wisper.Api.Tests.TestSupport;
using Wisper.Api.Tunnel;
using Wisper.Api.Tunnel.Messages;

namespace Wisper.Api.Tests.Tunnel.Backplane;

/// <summary>
/// The <b>owner-side</b> <see cref="ITunnelRelay"/> a routing test wires as the instance that physically
/// holds the host's socket. It records the ops the backplane routes to it and returns preset results (or
/// throws a preset <see cref="ApiException"/> to exercise error round-tripping). Unlike the consumer
/// suite's <c>FakeTunnelRelay</c> it also serves shells, which the distributed relay bridges.
/// </summary>
public sealed class OwnerTunnelRelayStub : ITunnelRelay
{
    public LeaseResult LeaseResult { get; set; } = new("lease_owner", "wc_owner", "provisioning");

    public ExecResult ExecResult { get; set; } = new() { Stdout = "ok", Stderr = string.Empty, ExitCode = 0 };

    public FakeTunnelExec? ExecStreamToReturn { get; set; }

    public FakeTunnelShell? ShellToReturn { get; set; }

    public ApiException? CreateError { get; set; }

    public List<(string HostId, LeaseCreate Spec)> CreateCalls { get; } = new();

    public List<(string HostId, string LeaseId, string Command)> ExecCalls { get; } = new();

    public List<(string HostId, string LeaseId)> ReleaseCalls { get; } = new();

    public List<(string HostId, string LeaseId, int Cols, int Rows)> ShellCalls { get; } = new();

    public List<(string HostId, string LeaseId, string Command)> ExecStreamCalls { get; } = new();

    public Task<LeaseResult> CreateLeaseAsync(string hostId, LeaseCreate spec, CancellationToken ct = default)
    {
        CreateCalls.Add((hostId, spec));
        if (CreateError is not null)
        {
            throw CreateError;
        }

        return Task.FromResult(LeaseResult);
    }

    public Task<ExecResult> ExecAsync(string hostId, string leaseId, string command, CancellationToken ct = default)
    {
        ExecCalls.Add((hostId, leaseId, command));
        return Task.FromResult(ExecResult);
    }

    public Task ReleaseAsync(string hostId, string leaseId, CancellationToken ct = default)
    {
        ReleaseCalls.Add((hostId, leaseId));
        return Task.CompletedTask;
    }

    public Task<ITunnelShell> OpenShellAsync(
        string hostId, string leaseId, int cols, int rows, CancellationToken ct = default)
    {
        ShellCalls.Add((hostId, leaseId, cols, rows));
        return Task.FromResult<ITunnelShell>(
            ShellToReturn ?? throw new InvalidOperationException("no shell preset"));
    }

    public Task<ITunnelExec> OpenExecStreamAsync(
        string hostId, string leaseId, string command, CancellationToken ct = default)
    {
        ExecStreamCalls.Add((hostId, leaseId, command));
        return Task.FromResult<ITunnelExec>(
            ExecStreamToReturn ?? throw new InvalidOperationException("no exec stream preset"));
    }

    public FakeTunnelFileDownload? FileDownloadToReturn { get; set; }

    public List<(string HostId, string LeaseId, string Path)> FileReadCalls { get; } = new();

    public Task<ITunnelFileDownload> OpenFileReadAsync(
        string hostId, string leaseId, string path, CancellationToken ct = default)
    {
        FileReadCalls.Add((hostId, leaseId, path));
        return Task.FromResult<ITunnelFileDownload>(
            FileDownloadToReturn ?? throw new InvalidOperationException("no file download preset"));
    }

    public Task RouteAgentFrameAsync(
        TunnelConnection connection, string type, ReadOnlyMemory<byte> payload, CancellationToken ct) =>
        throw new NotSupportedException("owner stub does not route physical frames");

    public void OnConnectionClosed(TunnelConnection connection) =>
        throw new NotSupportedException("owner stub has no physical connections");
}

/// <summary>
/// A minimal <see cref="IHostRegistry"/> the backplane suite wires as an instance's <b>local</b> registry.
/// It records register/unregister and answers <see cref="TryGet"/> from the set of hosts registered here,
/// which is all <see cref="Wisper.Api.Tunnel.Backplane.DistributedTunnelRelay"/> needs to decide whether a
/// host's socket is physically on this instance. Unlike the catalog suite's <c>FakeHostRegistry</c>, its
/// register/unregister are no-ops (not throws), so it can back the distributed registry too.
/// </summary>
public sealed class LocalHostRegistryStub : IHostRegistry
{
    private readonly ConcurrentDictionary<string, byte> _local = new(StringComparer.Ordinal);

    /// <summary>Marks <paramref name="hostId"/>'s tunnel as physically owned by this instance.</summary>
    public void MarkLocal(string hostId) => _local[hostId] = 1;

    /// <summary>Host ids passed to <see cref="RegisterAsync"/>, in order.</summary>
    public List<string> Registered { get; } = new();

    /// <summary>Host ids passed to <see cref="Unregister"/>, in order.</summary>
    public List<string> Unregistered { get; } = new();

    public Task RegisterAsync(TunnelConnection connection, CancellationToken ct = default)
    {
        _local[connection.HostId] = 1;
        Registered.Add(connection.HostId);
        return Task.CompletedTask;
    }

    public void Unregister(TunnelConnection connection)
    {
        _local.TryRemove(connection.HostId, out _);
        Unregistered.Add(connection.HostId);
    }

    public bool TryGet(string hostId, out TunnelConnection? connection)
    {
        connection = null;
        return _local.ContainsKey(hostId);
    }

    public IReadOnlyCollection<TunnelConnection> Online => Array.Empty<TunnelConnection>();
}

/// <summary>
/// An <see cref="ITunnelShell"/> double for the shell-bridge tests: the "owner-side" shell the distributed
/// relay opens on the instance that holds the socket. A test preloads PTY output and completes it; the
/// double records the stdin, resize and drain-credit the bridge forwards from the caller instance.
/// </summary>
public sealed class FakeTunnelShell : ITunnelShell
{
    private readonly Channel<byte[]> _output =
        Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly TaskCompletionSource _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FakeTunnelShell(uint sid = 7) => Sid = sid;

    public uint Sid { get; }

    public ChannelReader<byte[]> Output => _output.Reader;

    public Task Completion => _completion.Task;

    public string? ClosedReason { get; private set; }

    /// <summary>Stdin the bridge forwarded from the caller, in order.</summary>
    public List<byte[]> StdinWrites { get; } = new();

    /// <summary>The last resize the bridge forwarded.</summary>
    public (int Cols, int Rows)? LastResize { get; private set; }

    /// <summary>Total bytes the caller acked as drained (credit replenishment).</summary>
    public int DrainedBytes { get; private set; }

    /// <summary>Whether the bridge tore the shell down.</summary>
    public bool Closed { get; private set; }

    /// <summary>Enqueues one PTY output frame the bridge will forward downstream.</summary>
    public void PushOutput(byte[] data) => _output.Writer.TryWrite(data);

    /// <summary>Ends the output stream (the bridge's read loop then emits its terminal frame).</summary>
    public void CompleteOutput() => _output.Writer.TryComplete();

    public Task WriteStdinAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        StdinWrites.Add(data.ToArray());
        return Task.CompletedTask;
    }

    public ValueTask AckOutputDrainedAsync(int byteCount, CancellationToken ct = default)
    {
        DrainedBytes += byteCount;
        return ValueTask.CompletedTask;
    }

    public Task ResizeAsync(int cols, int rows, CancellationToken ct = default)
    {
        LastResize = (cols, rows);
        return Task.CompletedTask;
    }

    public Task CloseAsync(string reason = "consumer_closed", CancellationToken ct = default)
    {
        Closed = true;
        ClosedReason ??= reason;
        _output.Writer.TryComplete();
        _completion.TrySetResult();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => new(CloseAsync());
}
