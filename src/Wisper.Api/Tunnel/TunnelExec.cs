using System.Threading.Channels;
using Wisper.Api.Tunnel.Messages;

namespace Wisper.Api.Tunnel;

/// <summary>One chunk of streamed-exec output: the wire <c>channel</c> (1=stdout, 2=stderr) it
/// arrived on and the raw bytes.</summary>
public readonly record struct ExecChunk(byte Channel, byte[] Data);

/// <summary>
/// A handle over an open streamed-exec <c>sid</c> (docs/TUNNEL.md §6): a long-running command whose
/// output flows live and <b>unidirectionally</b> A→W as <c>stdout</c> (ch 1) / <c>stderr</c> (ch 2)
/// binary frames, terminated by <c>exec.exit</c>. Wisper is the receiver and grants credit back as
/// it drains bytes to the consumer (docs/TUNNEL.md §9). Returned by
/// <see cref="ITunnelRelay.OpenExecStreamAsync"/>.
/// </summary>
public interface ITunnelExec : IAsyncDisposable
{
    /// <summary>The Wisper-allocated stream id for this exec.</summary>
    uint Sid { get; }

    /// <summary>Channel-tagged output (stdout/stderr) arriving from the agent, to be relayed on.</summary>
    ChannelReader<ExecChunk> Output { get; }

    /// <summary>Grants <paramref name="byteCount"/> of credit back as those output bytes are drained.</summary>
    ValueTask AckDrainedAsync(int byteCount, CancellationToken ct = default);

    /// <summary>Completes when the exec ends (<c>exec.exit</c>, peer close, flow violation, or host offline).</summary>
    Task Completion { get; }

    /// <summary>The process exit code once <c>exec.exit</c> has arrived, else <c>null</c>.</summary>
    int? ExitCode { get; }

    /// <summary>The reason the stream closed, or <c>null</c> while it is still open.</summary>
    string? ClosedReason { get; }

    /// <summary>
    /// Tears the exec down: sends <c>stream.close</c> to the agent (unless the peer already closed)
    /// and completes <see cref="Output"/>. Idempotent.
    /// </summary>
    Task CloseAsync(string reason = "consumer_closed", CancellationToken ct = default);
}

/// <summary>
/// Default <see cref="ITunnelExec"/>: wraps a <see cref="TunnelStream"/> (reused as-is for receive
/// accounting + credit flow control) and layers channel preservation on top. Registered in
/// <see cref="TunnelConnection.Streams"/> as the sink for the exec <c>sid</c>: inbound binary frames
/// are accounted by the inner <see cref="TunnelStream"/> and then re-emitted <b>tagged with their
/// channel</b> onto <see cref="Output"/> (the inner stream's byte pipe discards the channel, which a
/// streamed exec must keep to split stdout from stderr).
/// </summary>
internal sealed class TunnelExec : ITunnelExec, ITunnelStreamSink
{
    private readonly TunnelConnection _connection;
    private readonly TunnelStream _stream;
    private readonly Channel<ExecChunk> _chunks = Channel.CreateUnbounded<ExecChunk>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    private int _exitCode;
    private volatile bool _hasExit;
    private int _closed;

    public TunnelExec(TunnelConnection connection, TunnelStream stream)
    {
        _connection = connection;
        _stream = stream;
    }

    public uint Sid => _stream.Sid;

    public ChannelReader<ExecChunk> Output => _chunks.Reader;

    public Task Completion => _stream.Completion;

    public int? ExitCode => _hasExit ? _exitCode : null;

    public string? ClosedReason => _stream.ClosedReason;

    public ValueTask AckDrainedAsync(int byteCount, CancellationToken ct = default) =>
        _stream.AckDrainedAsync(byteCount, ct);

    /// <summary>
    /// Accounts an inbound output frame via the inner <see cref="TunnelStream"/> (flow control) and
    /// re-emits the accepted bytes tagged with <see cref="BinaryFrame.Channel"/>. The connection's
    /// receive loop dispatches frames one at a time and awaits this, so the byte the inner stream
    /// just enqueued is exactly the one drained here -- no interleaving with another frame.
    /// </summary>
    public async ValueTask OnBinaryAsync(BinaryFrame frame, CancellationToken ct)
    {
        var channel = frame.Channel;
        await _stream.OnBinaryAsync(frame, ct);

        while (_stream.Output.TryRead(out var bytes))
        {
            _chunks.Writer.TryWrite(new ExecChunk(channel, bytes));
        }

        // A flow violation closes the inner stream without enqueuing; propagate the end downstream.
        if (_stream.IsClosed)
        {
            _chunks.Writer.TryComplete();
        }
    }

    public void OnCreditGranted(int bytes) => _stream.OnCreditGranted(bytes);

    public void OnPeerClosed(string reason)
    {
        _stream.OnPeerClosed(reason);
        _chunks.Writer.TryComplete();
    }

    /// <summary>
    /// Records the exit code from <c>exec.exit</c> and ends the stream normally: closes
    /// <see cref="Output"/> (the consumer drains any remaining chunks, then sees completion) and
    /// completes <see cref="Completion"/>. All output frames precede <c>exec.exit</c> on the wire
    /// and the receive loop is serialized, so every chunk is already enqueued by now.
    /// </summary>
    public void OnExit(int exitCode)
    {
        _exitCode = exitCode;
        _hasExit = true;
        _chunks.Writer.TryComplete();
        _ = _stream.CloseAsync("exec_exit");
    }

    public async Task CloseAsync(string reason = "consumer_closed", CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        _connection.Streams.TryRemove(Sid, out _);

        // Only ask the agent to cancel the exec if the stream didn't already end on the peer's side
        // (an exec.exit / peer close / flow violation already told the agent, or the host is gone).
        if (_stream.ClosedReason is null)
        {
            try
            {
                await _connection.SendControlAsync(new StreamClose { Sid = Sid }, ct);
            }
            catch
            {
                // Best effort -- the host may already be gone.
            }
        }

        _chunks.Writer.TryComplete();
        await _stream.CloseAsync(reason, ct);
    }

    public ValueTask DisposeAsync() => new(CloseAsync());
}
