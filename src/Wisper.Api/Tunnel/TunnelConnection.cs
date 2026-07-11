using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Wisper.Api.Tunnel.Messages;

namespace Wisper.Api.Tunnel;

/// <summary>
/// Wraps an accepted agent <see cref="WebSocket"/> for the lifetime of one tunnel
/// connection (docs/TUNNEL.md §3). Provides serialized control/binary sends (concurrent
/// <see cref="WebSocket.SendAsync"/> is illegal, so all writes go through a single-writer
/// lock) and a receive loop that routes frames by opcode, tracks activity for liveness
/// (§7), and handles <c>host.heartbeat</c>. Frame types this task does not own (lease/
/// exec/shell/stream) are surfaced to overridable hooks so the next task's relay can
/// consume them without touching the connection plumbing.
/// </summary>
public class TunnelConnection
{
    private readonly WebSocket _socket;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly int _maxReceiveBytes;

    private long _lastActivityTicks;
    private long _lastHeartbeatTicks;
    private int _ridCounter;

    public TunnelConnection(
        WebSocket socket,
        string hostId,
        string sessionId,
        int maxReceiveBytes,
        ILogger logger)
    {
        _socket = socket;
        HostId = hostId;
        SessionId = sessionId;
        _maxReceiveBytes = maxReceiveBytes;
        _logger = logger;
        ConnectedAtUtc = DateTime.UtcNow;
        _lastActivityTicks = ConnectedAtUtc.Ticks;
    }

    /// <summary>The stable host id this connection authenticated as.</summary>
    public string HostId { get; }

    /// <summary>
    /// Routes inbound control frames this connection does not own (lease/exec responses) to the
    /// relay. Set by the endpoint after construction; when null the default hook just logs.
    /// Wisper owns the id space, so responses are correlated by the <c>rid</c>/<c>leaseId</c>
    /// the relay allocated (docs/TUNNEL.md §1, §5).
    /// </summary>
    public Func<TunnelConnection, string, ReadOnlyMemory<byte>, CancellationToken, Task>? ControlFrameRouter { get; set; }

    /// <summary>
    /// Allocates the next monotonic per-connection request id (docs/TUNNEL.md §2). Starts at 1
    /// so it is never 0 — <see cref="ControlEnvelope.Rid"/> treats 0 as "omitted".
    /// </summary>
    public uint NextRid() => (uint)Interlocked.Increment(ref _ridCounter);

    /// <summary>The server-assigned session id (echoed to the agent in <c>hello.ack</c>).</summary>
    public string SessionId { get; }

    /// <summary>When the connection was accepted (UTC).</summary>
    public DateTime ConnectedAtUtc { get; }

    /// <summary>UTC time of the most recently received frame (any opcode) — the liveness clock.</summary>
    public DateTime LastActivityUtc =>
        new(Interlocked.Read(ref _lastActivityTicks), DateTimeKind.Utc);

    /// <summary>UTC time of the most recent <c>host.heartbeat</c>, or <see cref="ConnectedAtUtc"/> if none yet.</summary>
    public DateTime LastHeartbeatUtc
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastHeartbeatTicks);
            return ticks == 0 ? ConnectedAtUtc : new DateTime(ticks, DateTimeKind.Utc);
        }
    }

    /// <summary>Serializes <paramref name="message"/> to JSON and sends it as a TEXT frame.</summary>
    public async Task SendControlAsync<T>(T message, CancellationToken ct = default)
    {
        var bytes = Encoding.UTF8.GetBytes(ControlJson.Serialize(message));
        await SendAsync(bytes, WebSocketMessageType.Text, ct);
    }

    /// <summary>Encodes <paramref name="frame"/> and sends it as a BINARY frame.</summary>
    public async Task SendBinaryAsync(BinaryFrame frame, CancellationToken ct = default)
    {
        await SendAsync(frame.Encode(), WebSocketMessageType.Binary, ct);
    }

    private async Task SendAsync(byte[] payload, WebSocketMessageType type, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await _socket.SendAsync(payload, type, endOfMessage: true, ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Closes the socket with the given tunnel close <paramref name="code"/> (docs/TUNNEL.md §3),
    /// serialized against in-flight sends. Only the close frame is sent (no wait on the peer's
    /// reply), so a dead peer cannot stall the close. Safe to call more than once.
    /// </summary>
    public async Task CloseAsync(int code, string description, CancellationToken ct = default)
    {
        try
        {
            await _writeLock.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            await CloseSocketAsync(_socket, code, description, ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Runs the connection until the peer closes, the token cancels, or the liveness watchdog
    /// fires. On a liveness timeout the socket is closed with <see cref="CloseCodes.LivenessTimeout"/>.
    /// </summary>
    public async Task RunAsync(TimeSpan livenessTimeout, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var receive = ReceiveLoopAsync(linked.Token);
        var watchdog = WatchdogAsync(livenessTimeout, linked);

        await Task.WhenAny(receive, watchdog);
        linked.Cancel();

        await SwallowCancellation(receive);
        await SwallowCancellation(watchdog);
    }

    /// <summary>
    /// Reads frames until close/cancel, updating <see cref="LastActivityUtc"/> on every frame
    /// and routing by opcode: TEXT → control dispatch, BINARY → <see cref="BinaryFrame"/> decode.
    /// </summary>
    public async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            WsMessage message;
            try
            {
                message = await ReceiveMessageAsync(_socket, _maxReceiveBytes, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (WebSocketException ex)
            {
                _logger.LogDebug(ex, "tunnel {SessionId}: receive ended", SessionId);
                break;
            }

            if (message.Type == WebSocketMessageType.Close)
            {
                // The peer initiated a graceful close; complete the handshake by echoing a
                // close frame back, then exit the loop.
                await CloseAsync(CloseCodes.Normal, "peer closed", CancellationToken.None);
                break;
            }

            Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);

            if (message.Type == WebSocketMessageType.Text)
            {
                await HandleControlAsync(message.Data, ct);
            }
            else
            {
                await HandleBinaryAsync(message.Data, ct);
            }
        }
    }

    private async Task HandleControlAsync(byte[] data, CancellationToken ct)
    {
        var type = ControlJson.PeekType(data);
        if (type is null)
        {
            _logger.LogWarning("tunnel {SessionId}: dropping malformed control frame", SessionId);
            return;
        }

        switch (type)
        {
            case FrameTypes.HostHeartbeat:
                HandleHeartbeat(data);
                break;

            case FrameTypes.Hello:
                // The handshake hello is consumed by the endpoint before the loop starts; a
                // second hello mid-session is unexpected but not fatal.
                _logger.LogWarning("tunnel {SessionId}: unexpected duplicate hello", SessionId);
                break;

            default:
                await OnControlFrameAsync(type, data, ct);
                break;
        }
    }

    private void HandleHeartbeat(byte[] data)
    {
        try
        {
            var heartbeat = ControlJson.Deserialize<HostHeartbeat>(Encoding.UTF8.GetString(data));
            if (heartbeat is null)
            {
                return;
            }

            Interlocked.Exchange(ref _lastHeartbeatTicks, DateTime.UtcNow.Ticks);
            OnHeartbeat(heartbeat);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "tunnel {SessionId}: dropping malformed host.heartbeat", SessionId);
        }
    }

    private async Task HandleBinaryAsync(byte[] data, CancellationToken ct)
    {
        BinaryFrame frame;
        try
        {
            frame = BinaryFrame.Decode(data);
        }
        catch (BinaryFrameException ex)
        {
            _logger.LogWarning(ex, "tunnel {SessionId}: dropping malformed binary frame", SessionId);
            return;
        }

        await OnBinaryFrameAsync(frame, ct);
    }

    /// <summary>
    /// Hook for control frames this task does not handle (lease/exec/shell/stream). The default
    /// logs and ignores — unknown control frames are never fatal (docs/TUNNEL.md §4). The next
    /// task's relay overrides this to dispatch by <paramref name="type"/>.
    /// </summary>
    protected virtual Task OnControlFrameAsync(string type, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (ControlFrameRouter is not null)
        {
            return ControlFrameRouter(this, type, payload, ct);
        }

        _logger.LogDebug("tunnel {SessionId}: unhandled control frame {FrameType}", SessionId, type);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Hook for inbound binary (stream) frames. The default logs and ignores; the next task's
    /// relay overrides this to route bytes onto the addressed <c>sid</c>.
    /// </summary>
    protected virtual Task OnBinaryFrameAsync(BinaryFrame frame, CancellationToken ct)
    {
        _logger.LogDebug(
            "tunnel {SessionId}: unhandled binary frame sid={Sid} ch={Channel} bytes={Bytes}",
            SessionId, frame.Sid, frame.Channel, frame.Data.Length);
        return Task.CompletedTask;
    }

    /// <summary>Hook invoked after a valid <c>host.heartbeat</c> is parsed (state reconciliation, §8).</summary>
    protected virtual void OnHeartbeat(HostHeartbeat heartbeat)
    {
    }

    private async Task WatchdogAsync(TimeSpan timeout, CancellationTokenSource linked)
    {
        var ct = linked.Token;
        var interval = TimeSpan.FromMilliseconds(Math.Max(1000, timeout.TotalMilliseconds / 3));

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (DateTime.UtcNow - LastActivityUtc <= timeout)
            {
                continue;
            }

            _logger.LogWarning(
                "tunnel {SessionId}: liveness timeout for host {HostId}; closing {CloseCode}",
                SessionId, HostId, CloseCodes.LivenessTimeout);
            await CloseAsync(CloseCodes.LivenessTimeout, "liveness timeout", CancellationToken.None);
            linked.Cancel();
            return;
        }
    }

    private static async Task SwallowCancellation(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Sends a close frame with a raw tunnel close code (the range 4400+ is outside the
    /// <see cref="WebSocketCloseStatus"/> enum, so it is cast). Swallows the expected races
    /// against a peer that has already gone away.
    /// </summary>
    internal static async Task CloseSocketAsync(WebSocket socket, int code, string description, CancellationToken ct)
    {
        if (socket.State != WebSocketState.Open && socket.State != WebSocketState.CloseReceived)
        {
            return;
        }

        try
        {
            await socket.CloseOutputAsync((WebSocketCloseStatus)code, description, ct);
        }
        catch (WebSocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Reads one complete WebSocket message (reassembling fragments) into a byte array.
    /// Throws <see cref="WebSocketException"/> if the message exceeds <paramref name="maxBytes"/>.
    /// </summary>
    internal static async Task<WsMessage> ReceiveMessageAsync(WebSocket socket, int maxBytes, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            using var accumulator = new MemoryStream();
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer, ct);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return new WsMessage(WebSocketMessageType.Close, Array.Empty<byte>());
                }

                accumulator.Write(buffer, 0, result.Count);
                if (accumulator.Length > maxBytes)
                {
                    throw new WebSocketException(
                        WebSocketError.InvalidMessageType,
                        $"Inbound message exceeds {maxBytes} bytes.");
                }

                if (result.EndOfMessage)
                {
                    return new WsMessage(result.MessageType, accumulator.ToArray());
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

/// <summary>A fully-reassembled inbound WebSocket message.</summary>
internal readonly record struct WsMessage(WebSocketMessageType Type, byte[] Data);
