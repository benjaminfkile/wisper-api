using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging.Abstractions;
using Wisper.Api.Tunnel;
using Wisper.Api.Tunnel.Messages;
using Xunit;

namespace Wisper.Api.Tests.Tunnel;

/// <summary>
/// Interactive-shell + credit-flow-control tests (docs/TUNNEL.md §6, §9). The integration tests
/// drive the dev shell WebSocket against the real app while a TestServer WS client plays the FAKE
/// AGENT: it handshakes and, on <c>shell.open</c>, replies <c>shell.opened</c> then ECHOES any
/// stdin (ch 0) binary it receives back as stdout (ch 1) on the same <c>sid</c>. A separate unit
/// test exercises <see cref="TunnelStream"/>'s flow control directly.
/// </summary>
public class TunnelShellTests
{
    private const string DevToken = "dev-host-token";
    private const string DevHostId = "host-alpha";

    private static WebApplicationFactory<Program> CreateFactory(int initialWindowBytes = 262144) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting($"Tunnel:HostTokens:{DevToken}", DevHostId);
            builder.UseSetting("Tunnel:EnableDevEndpoints", "true");
            builder.UseSetting("Tunnel:RelayRequestTimeoutMs", "10000");
            builder.UseSetting("Tunnel:InitialWindowBytes", initialWindowBytes.ToString());
        });

    // ---------- integration: fake-agent driven ----------

    [Fact]
    public async Task Shell_round_trips_stdin_to_stdout()
    {
        using var factory = CreateFactory();
        var ct = Token();
        var agent = await FakeAgent.ConnectAsync(factory, ct);
        agent.StartEcho(ct);

        var consumer = await ConnectShellAsync(factory, ct);

        var payload = Encoding.UTF8.GetBytes("echo round-trip");
        await consumer.SendAsync(payload, WebSocketMessageType.Binary, true, ct);

        var echoed = await ReceiveBinaryAsync(consumer, ct);
        Assert.Equal(payload, echoed);

        await consumer.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", ct);
    }

    [Fact]
    public async Task Draining_stdout_emits_stream_credit_to_the_agent()
    {
        // A small window makes the credit batch threshold small (window/2), so draining a modest
        // amount of stdout deterministically triggers a stream.credit back to the agent.
        using var factory = CreateFactory(initialWindowBytes: 64);
        var ct = Token();
        var agent = await FakeAgent.ConnectAsync(factory, ct);
        agent.StartEcho(ct);

        var consumer = await ConnectShellAsync(factory, ct);

        // 64 bytes of stdin echo back as 64 bytes of stdout; draining them (>= threshold 32) emits credit.
        var payload = Encoding.UTF8.GetBytes(new string('x', 64));
        await consumer.SendAsync(payload, WebSocketMessageType.Binary, true, ct);

        // Drain the echoed stdout on the consumer so the relay grants credit downstream.
        var drained = 0;
        while (drained < payload.Length)
        {
            drained += (await ReceiveBinaryAsync(consumer, ct)).Length;
        }

        var credit = await agent.WaitForControlAsync(FrameTypes.StreamCredit, ct);
        Assert.True(credit.GetProperty("sid").GetUInt32() > 0);
        Assert.True(credit.GetProperty("bytes").GetInt32() >= 32);

        await consumer.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", ct);
    }

    [Fact]
    public async Task Resize_control_forwards_a_shell_resize()
    {
        using var factory = CreateFactory();
        var ct = Token();
        var agent = await FakeAgent.ConnectAsync(factory, ct);
        agent.StartEcho(ct);

        var consumer = await ConnectShellAsync(factory, ct);

        await consumer.SendAsync(
            Encoding.UTF8.GetBytes("{\"t\":\"resize\",\"cols\":120,\"rows\":40}"),
            WebSocketMessageType.Text, true, ct);

        var resize = await agent.WaitForControlAsync(FrameTypes.ShellResize, ct);
        Assert.Equal(120, resize.GetProperty("cols").GetInt32());
        Assert.Equal(40, resize.GetProperty("rows").GetInt32());
        Assert.True(resize.GetProperty("sid").GetUInt32() > 0);

        await consumer.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", ct);
    }

    [Fact]
    public async Task Closing_the_consumer_sends_stream_close_and_tears_the_stream_down()
    {
        using var factory = CreateFactory();
        var ct = Token();
        var agent = await FakeAgent.ConnectAsync(factory, ct);
        agent.StartEcho(ct);

        var consumer = await ConnectShellAsync(factory, ct);

        await consumer.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", ct);

        var close = await agent.WaitForControlAsync(FrameTypes.StreamClose, ct);
        // By the time stream.close arrives the agent has already handled shell.open (opened the sid).
        Assert.Equal(agent.OpenedSid, close.GetProperty("sid").GetUInt32());
        Assert.True(close.GetProperty("sid").GetUInt32() > 0);
    }

    // ---------- unit: TunnelStream flow control ----------

    [Fact]
    public async Task Send_window_decrements_blocks_at_zero_and_replenishes_on_credit()
    {
        var binary = new List<BinaryFrame>();
        var stream = new TunnelStream(
            sid: 7,
            initialWindowBytes: 10,
            maxFrameBytes: 32768,
            creditThreshold: 5,
            sendBinary: (f, _) => { lock (binary) { binary.Add(f); } return Task.CompletedTask; },
            sendCredit: (_, _) => Task.CompletedTask,
            sendClosed: (_, _) => Task.CompletedTask,
            logger: NullLogger.Instance);

        Assert.Equal(10, stream.SendWindow);

        // A 4-byte send decrements the window by exactly the payload length.
        await stream.WriteAsync(Channels.Stdin, new byte[4]);
        Assert.Equal(6, stream.SendWindow);

        // Exhaust the window.
        await stream.WriteAsync(Channels.Stdin, new byte[6]);
        Assert.Equal(0, stream.SendWindow);

        // A further send blocks at 0 — nothing new is written until credit arrives.
        var blocked = stream.WriteAsync(Channels.Stdin, new byte[3]);
        var finishedFirst = await Task.WhenAny(blocked, Task.Delay(200));
        Assert.NotSame(blocked, finishedFirst);

        int framesBeforeCredit;
        lock (binary) { framesBeforeCredit = binary.Count; }
        Assert.Equal(2, framesBeforeCredit);

        // Replenish; the blocked write now completes and the window reflects the spend.
        stream.OnCreditGranted(10);
        await blocked.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(7, stream.SendWindow);

        lock (binary)
        {
            Assert.Equal(3, binary.Count);
            Assert.Equal(3, binary[2].Data.Length);
        }
    }

    [Fact]
    public async Task Over_grant_receive_triggers_stream_closed_flow_violation()
    {
        var closed = new List<StreamClosed>();
        var stream = new TunnelStream(
            sid: 9,
            initialWindowBytes: 8,
            maxFrameBytes: 32768,
            creditThreshold: 4,
            sendBinary: (_, _) => Task.CompletedTask,
            sendCredit: (_, _) => Task.CompletedTask,
            sendClosed: (c, _) => { lock (closed) { closed.Add(c); } return Task.CompletedTask; },
            logger: NullLogger.Instance);

        // Receiving within the granted window is fine and enqueues bytes.
        await stream.OnBinaryAsync(new BinaryFrame(Channels.Stdout, 9, new byte[8]), default);
        Assert.False(stream.IsClosed);

        // One more byte over the granted window is a protocol fault → stream.closed{flow_violation}.
        await stream.OnBinaryAsync(new BinaryFrame(Channels.Stdout, 9, new byte[1]), default);

        Assert.True(stream.IsClosed);
        Assert.Equal(TunnelStream.FlowViolationReason, stream.ClosedReason);
        Assert.True(stream.Completion.IsCompleted);
        lock (closed)
        {
            Assert.Single(closed);
            Assert.Equal(9u, closed[0].Sid);
            Assert.Equal(TunnelStream.FlowViolationReason, closed[0].Reason);
        }
    }

    [Fact]
    public async Task Drained_bytes_emit_batched_credit_and_bound_the_receive_window()
    {
        var credits = new List<StreamCredit>();
        var stream = new TunnelStream(
            sid: 3,
            initialWindowBytes: 100,
            maxFrameBytes: 32768,
            creditThreshold: 50,
            sendBinary: (_, _) => Task.CompletedTask,
            sendCredit: (c, _) => { lock (credits) { credits.Add(c); } return Task.CompletedTask; },
            sendClosed: (_, _) => Task.CompletedTask,
            logger: NullLogger.Instance);

        // Below the threshold: no credit yet (batched, not one-per-byte).
        await stream.AckDrainedAsync(20);
        lock (credits) { Assert.Empty(credits); }

        // Crossing the threshold flushes a single credit for the accumulated bytes.
        await stream.AckDrainedAsync(40);
        lock (credits)
        {
            Assert.Single(credits);
            Assert.Equal(60, credits[0].Bytes);
            Assert.Equal(3u, credits[0].Sid);
        }
    }

    // ---------- helpers ----------

    private static async Task<WebSocket> ConnectShellAsync(WebApplicationFactory<Program> factory, CancellationToken ct)
    {
        var client = factory.Server.CreateWebSocketClient();
        var uri = new Uri(factory.Server.BaseAddress, $"dev/leases/lease_abc/shell?hostId={DevHostId}&cols=80&rows=24");
        return await client.ConnectAsync(uri, ct);
    }

    private static async Task<byte[]> ReceiveBinaryAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var result = await socket.ReceiveAsync(buffer, ct);
        Assert.Equal(WebSocketMessageType.Binary, result.MessageType);
        return buffer.AsSpan(0, result.Count).ToArray();
    }

    // A per-test deadline so a hung relay/bridge fails the test instead of hanging the run.
    private static CancellationToken Token() => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

    /// <summary>
    /// A TestServer WS client that plays the host's wisp-agent: completes the handshake, answers
    /// <c>shell.open</c> with <c>shell.opened</c>, echoes stdin→stdout, and records the control
    /// frames it receives so tests can assert on <c>shell.resize</c>/<c>stream.close</c>/<c>stream.credit</c>.
    /// </summary>
    private sealed class FakeAgent
    {
        private readonly WebSocket _socket;
        private readonly Channel<JsonElement> _control = Channel.CreateUnbounded<JsonElement>();

        private uint _openedSid;

        private FakeAgent(WebSocket socket) => _socket = socket;

        public uint OpenedSid => Volatile.Read(ref _openedSid);

        public static async Task<FakeAgent> ConnectAsync(WebApplicationFactory<Program> factory, CancellationToken ct)
        {
            var client = factory.Server.CreateWebSocketClient();
            client.ConfigureRequest = request => request.Headers["Authorization"] = $"Bearer {DevToken}";
            var socket = await client.ConnectAsync(new Uri(factory.Server.BaseAddress, "agent"), ct);

            var hello =
                "{\"t\":\"hello\",\"proto\":" + TunnelProtocol.ProtocolVersion +
                ",\"agentVersion\":\"1.0.0\",\"wispVersion\":\"0.9.0\"," +
                "\"capability\":{\"images\":[\"alpine\"],\"default\":\"alpine\"," +
                "\"limits\":{\"max_ttl_seconds\":3600,\"max_cpus\":2,\"max_memory_mb\":1024,\"pids_limit\":256,\"networks\":[\"none\"]}}," +
                "\"capacity\":{\"maxLeases\":10,\"maxStreams\":50}}";
            await socket.SendAsync(Encoding.UTF8.GetBytes(hello), WebSocketMessageType.Text, true, ct);

            // Read the hello.ack.
            var ackBuffer = new byte[64 * 1024];
            var ack = await socket.ReceiveAsync(ackBuffer, ct);
            Assert.Equal(WebSocketMessageType.Text, ack.MessageType);

            return new FakeAgent(socket);
        }

        /// <summary>Runs the agent's receive loop: reply shell.opened, echo stdin, record control frames.</summary>
        public void StartEcho(CancellationToken ct)
        {
            _ = Task.Run(async () =>
            {
                var buffer = new byte[64 * 1024];
                try
                {
                    while (_socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
                    {
                        var result = await _socket.ReceiveAsync(buffer, ct);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            break;
                        }

                        var slice = buffer.AsMemory(0, result.Count);
                        if (result.MessageType == WebSocketMessageType.Text)
                        {
                            await HandleControlAsync(slice, ct);
                        }
                        else
                        {
                            await EchoBinaryAsync(slice, ct);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (WebSocketException)
                {
                }
            }, ct);
        }

        private async Task HandleControlAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            var element = JsonDocument.Parse(data).RootElement.Clone();
            var type = element.GetProperty("t").GetString();

            if (type == FrameTypes.ShellOpen)
            {
                var rid = element.GetProperty("rid").GetUInt32();
                var sid = element.GetProperty("sid").GetUInt32();
                Volatile.Write(ref _openedSid, sid);
                await SendRawAsync($"{{\"t\":\"shell.opened\",\"rid\":{rid},\"sid\":{sid}}}", ct);
            }

            _control.Writer.TryWrite(element);
        }

        private async Task EchoBinaryAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            var frame = BinaryFrame.Decode(data.Span);
            if (frame.Channel != Channels.Stdin)
            {
                return;
            }

            var echo = new BinaryFrame(Channels.Stdout, frame.Sid, frame.Data).Encode();
            await _socket.SendAsync(echo, WebSocketMessageType.Binary, true, ct);
        }

        public async Task<JsonElement> WaitForControlAsync(string type, CancellationToken ct)
        {
            while (await _control.Reader.WaitToReadAsync(ct))
            {
                while (_control.Reader.TryRead(out var element))
                {
                    if (element.GetProperty("t").GetString() == type)
                    {
                        return element;
                    }
                }
            }

            throw new InvalidOperationException($"agent never received a {type} frame");
        }

        private Task SendRawAsync(string json, CancellationToken ct) =>
            _socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct);
    }
}
