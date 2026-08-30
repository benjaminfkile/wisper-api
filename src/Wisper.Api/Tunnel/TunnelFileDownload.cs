using System.Threading.Channels;
using Wisper.Api.Tunnel.Messages;

namespace Wisper.Api.Tunnel;

/// <summary>
/// A handle over an open <c>file.read</c> <c>sid</c> (docs/TUNNEL.md §5): a one-shot A→W byte stream
/// carrying the file's raw bytes on channel <see cref="Channels.Stdout"/>, terminated by
/// <c>file.eof</c>. Wisper is the receiver and grants credit back as it drains bytes to the HTTP
/// consumer (docs/TUNNEL.md §9). Returned by <see cref="ITunnelRelay.OpenFileReadAsync"/>.
/// </summary>
public interface ITunnelFileDownload : IAsyncDisposable
{
    /// <summary>The Wisper-allocated stream id for this download.</summary>
    uint Sid { get; }

    /// <summary>Total file size in bytes as reported by the agent, or <c>-1</c> when unknown.</summary>
    long Size { get; }

    /// <summary>The file's bytes as they arrive, in agent order.</summary>
    ChannelReader<byte[]> Bytes { get; }

    /// <summary>Grants <paramref name="byteCount"/> of credit back as those bytes are drained downstream.</summary>
    ValueTask AckDrainedAsync(int byteCount, CancellationToken ct = default);

    /// <summary>Completes when the stream ends (<c>file.eof</c>, peer close, flow violation, or host offline).</summary>
    Task Completion { get; }

    /// <summary>The reason the stream closed, or <c>null</c> while it is still open.</summary>
    string? ClosedReason { get; }

    /// <summary>
    /// Tears the download down: sends <c>stream.close</c> to the agent (unless the peer already
    /// closed) and completes <see cref="Bytes"/>. Idempotent.
    /// </summary>
    Task CloseAsync(string reason = "consumer_closed", CancellationToken ct = default);
}

/// <summary>
/// Default <see cref="ITunnelFileDownload"/>: wraps a <see cref="TunnelStream"/> (reused as-is for
/// receive accounting + credit flow control) and forwards the drained byte buffers to
/// <see cref="Bytes"/>. Registered in <see cref="TunnelConnection.Streams"/> as the sink for the
/// download <c>sid</c>. A <c>file.eof</c> completes <see cref="Bytes"/> normally; a peer-close /
/// flow violation completes it with an unset <see cref="Size"/>-independent end.
/// </summary>
internal sealed class TunnelFileDownload : ITunnelFileDownload, ITunnelStreamSink
{
    private readonly TunnelConnection _connection;
    private readonly TunnelStream _stream;
    private int _closed;
    private volatile bool _eofSeen;

    public TunnelFileDownload(TunnelConnection connection, TunnelStream stream, long size)
    {
        _connection = connection;
        _stream = stream;
        Size = size;
    }

    public uint Sid => _stream.Sid;

    public long Size { get; private set; }

    /// <summary>
    /// Records the total file size reported in <c>file.opened</c>. Called by the relay after the sink is
    /// already registered (the sink has to be in place BEFORE the request is sent so early binary frames
    /// are not dropped as unknown-sid).
    /// </summary>
    public void SetSize(long size) => Size = size;

    public ChannelReader<byte[]> Bytes => _stream.Output;

    public Task Completion => _stream.Completion;

    public string? ClosedReason => _stream.ClosedReason;

    public ValueTask AckDrainedAsync(int byteCount, CancellationToken ct = default) =>
        _stream.AckDrainedAsync(byteCount, ct);

    public ValueTask OnBinaryAsync(BinaryFrame frame, CancellationToken ct) =>
        _stream.OnBinaryAsync(frame, ct);

    public void OnCreditGranted(int bytes) => _stream.OnCreditGranted(bytes);

    public void OnPeerClosed(string reason) => _stream.OnPeerClosed(reason);

    /// <summary>
    /// Records <c>file.eof</c> and ends the inbound stream normally: closes <see cref="Bytes"/> after
    /// draining, so the HTTP consumer finishes writing the last chunk before seeing completion.
    /// </summary>
    public void OnEof()
    {
        _eofSeen = true;
        _ = _stream.CloseAsync("file_eof");
    }

    /// <summary>Whether <c>file.eof</c> was seen (used to distinguish a normal end from a peer error).</summary>
    public bool EofSeen => _eofSeen;

    public async Task CloseAsync(string reason = "consumer_closed", CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        _connection.Streams.TryRemove(Sid, out _);

        // Only ask the agent to cancel the download if the stream did not already end on the peer's side
        // (a file.eof / peer close / flow violation / host gone).
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

        await _stream.CloseAsync(reason, ct);
    }

    public ValueTask DisposeAsync() => new(CloseAsync());
}
