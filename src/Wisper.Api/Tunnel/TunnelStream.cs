using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Wisper.Api.Tunnel.Messages;

namespace Wisper.Api.Tunnel;

/// <summary>
/// The inbound side of a stream as seen by <see cref="TunnelConnection"/>: the connection's
/// receive loop routes binary/credit/closed frames for a <c>sid</c> to the owning sink. Keeps
/// the connection decoupled from <see cref="TunnelStream"/>'s internals.
/// </summary>
public interface ITunnelStreamSink
{
    /// <summary>Accepts an inbound data frame for this stream (receive accounting + downstream enqueue).</summary>
    ValueTask OnBinaryAsync(BinaryFrame frame, CancellationToken ct);

    /// <summary>The peer granted <paramref name="bytes"/> more send credit for this stream (docs/TUNNEL.md §9).</summary>
    void OnCreditGranted(int bytes);

    /// <summary>The peer tore the stream down (<c>stream.closed</c>) with the given reason.</summary>
    void OnPeerClosed(string reason);
}

/// <summary>
/// One multiplexed byte stream over a tunnel <c>sid</c> (docs/TUNNEL.md §6, §9) with per-stream
/// credit flow control on <b>both</b> directions:
/// <list type="bullet">
/// <item><b>Send window</b> — the bytes this side may still send before the peer grants more.
/// Initialised to the peer's implied initial window; decremented by every binary frame sent;
/// at 0 the sender <i>blocks</i> (backpressure) until a <c>stream.credit</c> replenishes it.
/// Bytes are never dropped — the producer is slowed.</item>
/// <item><b>Receive accounting</b> — inbound bytes are enqueued for a downstream drain; as they
/// drain (<see cref="AckDrainedAsync"/>) a batched <c>stream.credit</c> is emitted to replenish
/// the peer's window. If the peer sends past the window it was granted the stream is closed with
/// <c>stream.closed{reason:"flow_violation"}</c> (overflow is a protocol fault, §9).</item>
/// </list>
/// </summary>
public sealed class TunnelStream : ITunnelStreamSink, IAsyncDisposable
{
    /// <summary>Reason recorded when a peer overflows its granted credit (docs/TUNNEL.md §9).</summary>
    public const string FlowViolationReason = "flow_violation";

    private readonly uint _sid;
    private readonly int _maxFrameBytes;
    private readonly int _creditThreshold;
    private readonly Func<BinaryFrame, CancellationToken, Task> _sendBinary;
    private readonly Func<StreamCredit, CancellationToken, Task> _sendCredit;
    private readonly Func<StreamClosed, CancellationToken, Task> _sendClosed;
    private readonly ILogger _logger;

    private readonly Channel<byte[]> _inbound = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly TaskCompletionSource _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Send window: bytes this side may still send. Guarded by _sendLock; _creditSignal wakes a
    // sender blocked at window 0 when credit is granted (or the stream closes).
    private readonly object _sendLock = new();
    private long _sendWindow;
    private TaskCompletionSource _creditSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Receive accounting: bytes the peer may still send us; guarded by _recvLock. _pendingCredit
    // batches drained bytes so we emit one credit frame per threshold, not per byte.
    private readonly object _recvLock = new();
    private long _recvWindow;
    private long _pendingCredit;

    private volatile bool _closed;
    private string? _closedReason;

    public TunnelStream(
        uint sid,
        int initialWindowBytes,
        int maxFrameBytes,
        int creditThreshold,
        Func<BinaryFrame, CancellationToken, Task> sendBinary,
        Func<StreamCredit, CancellationToken, Task> sendCredit,
        Func<StreamClosed, CancellationToken, Task> sendClosed,
        ILogger logger)
    {
        _sid = sid;
        _sendWindow = initialWindowBytes;
        _recvWindow = initialWindowBytes;
        _maxFrameBytes = Math.Clamp(maxFrameBytes, 1, BinaryFrame.MaxPayload);
        _creditThreshold = Math.Max(1, creditThreshold);
        _sendBinary = sendBinary;
        _sendCredit = sendCredit;
        _sendClosed = sendClosed;
        _logger = logger;
    }

    /// <summary>The Wisper-allocated stream id (docs/TUNNEL.md §2).</summary>
    public uint Sid => _sid;

    /// <summary>The current send window in bytes — how many more bytes may be sent before blocking.</summary>
    public long SendWindow
    {
        get { lock (_sendLock) { return _sendWindow; } }
    }

    /// <summary>Inbound (drained) bytes for the downstream consumer to read and write out.</summary>
    public ChannelReader<byte[]> Output => _inbound.Reader;

    /// <summary>Completes when the stream is torn down (peer close, flow violation, or local close).</summary>
    public Task Completion => _completion.Task;

    /// <summary>The reason the stream closed, or <c>null</c> while it is still open.</summary>
    public string? ClosedReason => _closedReason;

    /// <summary>Whether the stream has been torn down.</summary>
    public bool IsClosed => _closed;

    /// <summary>
    /// Sends <paramref name="data"/> on <paramref name="channel"/> for this stream, honouring the
    /// send window: the payload is chunked to at most one frame's worth and, when the window is
    /// exhausted, the call <i>blocks</i> until the peer grants credit (backpressure, never a drop).
    /// </summary>
    /// <exception cref="InvalidOperationException">The stream has been closed.</exception>
    public async Task WriteAsync(byte channel, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        var offset = 0;
        while (offset < data.Length)
        {
            var want = Math.Min(data.Length - offset, _maxFrameBytes);
            var take = await AcquireSendCreditAsync(want, ct);
            await _sendBinary(new BinaryFrame(channel, _sid, data.Slice(offset, take)), ct);
            offset += take;
        }
    }

    /// <summary>
    /// Waits until at least one byte of send credit is available, then reserves up to
    /// <paramref name="desired"/> bytes of it and returns the reserved count.
    /// </summary>
    private async Task<int> AcquireSendCreditAsync(int desired, CancellationToken ct)
    {
        while (true)
        {
            Task wait;
            lock (_sendLock)
            {
                if (_closed)
                {
                    throw new InvalidOperationException($"tunnel stream {_sid} is closed");
                }

                if (_sendWindow > 0)
                {
                    var take = (int)Math.Min(_sendWindow, desired);
                    _sendWindow -= take;
                    return take;
                }

                wait = _creditSignal.Task;
            }

            await wait.WaitAsync(ct);
        }
    }

    /// <inheritdoc />
    public void OnCreditGranted(int bytes)
    {
        if (bytes <= 0)
        {
            return;
        }

        lock (_sendLock)
        {
            _sendWindow += bytes;
            SignalSendWaitersLocked();
        }
    }

    /// <inheritdoc />
    public async ValueTask OnBinaryAsync(BinaryFrame frame, CancellationToken ct)
    {
        if (_closed)
        {
            return;
        }

        var len = frame.Data.Length;
        bool violation;
        lock (_recvLock)
        {
            _recvWindow -= len;
            violation = _recvWindow < 0;
        }

        if (violation)
        {
            _logger.LogWarning(
                "tunnel stream {Sid}: peer sent past its granted credit; closing {Reason}",
                _sid, FlowViolationReason);
            await CloseInternalAsync(FlowViolationReason, notifyPeer: true, ct);
            return;
        }

        // Copy out of the frame's buffer so the receive loop can reuse it.
        _inbound.Writer.TryWrite(frame.Data.ToArray());
    }

    /// <summary>
    /// Accounts <paramref name="byteCount"/> bytes as drained downstream and, once the batch
    /// threshold is reached, emits a <c>stream.credit</c> to replenish the peer's send window
    /// (docs/TUNNEL.md §9).
    /// </summary>
    public async ValueTask AckDrainedAsync(int byteCount, CancellationToken ct = default)
    {
        if (byteCount <= 0 || _closed)
        {
            return;
        }

        int grant;
        lock (_recvLock)
        {
            _recvWindow += byteCount;
            _pendingCredit += byteCount;
            if (_pendingCredit < _creditThreshold)
            {
                return;
            }

            grant = (int)_pendingCredit;
            _pendingCredit = 0;
        }

        await _sendCredit(new StreamCredit { Sid = _sid, Bytes = grant }, ct);
    }

    /// <inheritdoc />
    public void OnPeerClosed(string reason)
    {
        // The peer already tore its side down; complete locally without echoing stream.closed.
        _ = CloseInternalAsync(reason, notifyPeer: false, CancellationToken.None);
    }

    /// <summary>
    /// Closes the stream locally (e.g. a consumer disconnect handled by the shell). Does not emit
    /// <c>stream.closed</c> — the caller sends <c>stream.close</c> to the agent when appropriate.
    /// </summary>
    public Task CloseAsync(string reason, CancellationToken ct = default) =>
        CloseInternalAsync(reason, notifyPeer: false, ct);

    private async Task CloseInternalAsync(string reason, bool notifyPeer, CancellationToken ct)
    {
        lock (_sendLock)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            _closedReason = reason;
            // Wake any sender blocked at window 0 so it observes the close and unwinds.
            SignalSendWaitersLocked();
        }

        _inbound.Writer.TryComplete();
        _completion.TrySetResult();

        if (notifyPeer)
        {
            try
            {
                await _sendClosed(new StreamClosed { Sid = _sid, Reason = reason }, ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "tunnel stream {Sid}: emitting stream.closed failed", _sid);
            }
        }
    }

    private void SignalSendWaitersLocked()
    {
        var old = _creditSignal;
        _creditSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        old.TrySetResult();
    }

    public async ValueTask DisposeAsync()
    {
        await CloseInternalAsync("disposed", notifyPeer: false, CancellationToken.None);
    }
}
