using System.Threading.Channels;
using Wisper.Api.Tunnel.Messages;

namespace Wisper.Api.Tunnel;

/// <summary>
/// A duplex handle over an open shell <c>sid</c> (docs/TUNNEL.md §6): consumer keystrokes go in
/// as <c>stdin</c> (ch 0) binary frames — flow-controlled — and agent PTY output arrives on
/// <see cref="Output"/> as <c>stdout</c> (ch 1). The consumer grants credit back with
/// <see cref="AckOutputDrainedAsync"/> as it writes bytes out. Returned by
/// <see cref="ITunnelRelay.OpenShellAsync"/>.
/// </summary>
public interface ITunnelShell : IAsyncDisposable
{
    /// <summary>The Wisper-allocated stream id for this shell.</summary>
    uint Sid { get; }

    /// <summary>Sends consumer keystrokes to the PTY as <c>stdin</c> (ch 0), honouring flow control.</summary>
    Task WriteStdinAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default);

    /// <summary>PTY output (<c>stdout</c>, ch 1) arriving from the agent, to be written to the consumer.</summary>
    ChannelReader<byte[]> Output { get; }

    /// <summary>Grants <paramref name="byteCount"/> of credit back as those output bytes are written out.</summary>
    ValueTask AckOutputDrainedAsync(int byteCount, CancellationToken ct = default);

    /// <summary>Forwards a terminal window resize to the PTY (<c>shell.resize</c>).</summary>
    Task ResizeAsync(int cols, int rows, CancellationToken ct = default);

    /// <summary>
    /// Tears the shell down: sends <c>stream.close</c> to the agent (unless the peer already
    /// closed) and completes <see cref="Output"/>. Idempotent.
    /// </summary>
    Task CloseAsync(string reason = "consumer_closed", CancellationToken ct = default);

    /// <summary>Completes when the stream ends (peer close, flow violation, host offline, or local close).</summary>
    Task Completion { get; }

    /// <summary>The reason the stream closed, or <c>null</c> while it is still open.</summary>
    string? ClosedReason { get; }
}

/// <summary>
/// Default <see cref="ITunnelShell"/>: wraps a <see cref="TunnelStream"/> (flow control + byte
/// pipe) plus the owning <see cref="TunnelConnection"/> for the control frames a shell needs
/// (<c>shell.resize</c>, <c>stream.close</c>).
/// </summary>
internal sealed class TunnelShell : ITunnelShell
{
    private readonly TunnelConnection _connection;
    private readonly TunnelStream _stream;
    private int _closed;

    public TunnelShell(TunnelConnection connection, TunnelStream stream)
    {
        _connection = connection;
        _stream = stream;
    }

    public uint Sid => _stream.Sid;

    public ChannelReader<byte[]> Output => _stream.Output;

    public Task Completion => _stream.Completion;

    public string? ClosedReason => _stream.ClosedReason;

    public Task WriteStdinAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) =>
        _stream.WriteAsync(Channels.Stdin, data, ct);

    public ValueTask AckOutputDrainedAsync(int byteCount, CancellationToken ct = default) =>
        _stream.AckDrainedAsync(byteCount, ct);

    public Task ResizeAsync(int cols, int rows, CancellationToken ct = default) =>
        _connection.SendControlAsync(new ShellResize { Sid = Sid, Cols = cols, Rows = rows }, ct);

    public async Task CloseAsync(string reason = "consumer_closed", CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        _connection.Streams.TryRemove(Sid, out _);

        // Only ask the agent to cancel the shell if the stream didn't already end on the peer's
        // side (a peer close / flow violation already told the agent, or the host is gone).
        if (_stream.ClosedReason is null)
        {
            try
            {
                await _connection.SendControlAsync(new StreamClose { Sid = Sid }, ct);
            }
            catch
            {
                // Best effort — the host may already be gone.
            }
        }

        await _stream.CloseAsync(reason, ct);
    }

    public ValueTask DisposeAsync() => new(CloseAsync());
}
