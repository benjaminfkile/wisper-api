using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Wisper.Api.Tunnel;
using Xunit;

namespace Wisper.Api.Tests.Tunnel;

/// <summary>
/// Streamed-exec + credit-flow-control tests (docs/TUNNEL.md §5, §6, §9; docs/API.md §7). The
/// integration tests POST the dev exec endpoint with <c>?stream=1</c> against the real app while a
/// TestServer WS client plays the FAKE AGENT: on <c>exec.open</c> it replies <c>exec.opened</c>,
/// emits a few stdout (ch 1) + a stderr (ch 2) binary chunk on the <c>sid</c>, then
/// <c>exec.exit{exit_code}</c>. The SSE response is parsed back into <c>chunk</c>/<c>exit</c>/
/// <c>error</c> events.
/// </summary>
public class TunnelExecStreamTests
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

    [Fact]
    public async Task Stream_exec_yields_chunk_events_then_exit()
    {
        using var factory = CreateFactory();
        var ct = Token();
        var agent = await FakeAgent.ConnectAsync(factory, ct);
        agent.StartExec(
            new[] { Out("build starting\n"), Out("step 1 done\n"), Err("warning: deprecated\n") },
            exitCode: 0,
            ct);

        var events = await PostStreamExecAsync(factory, "make build", ct);

        // Three chunk events with the right stream + data, followed by a terminal exit event.
        Assert.Equal(4, events.Count);

        Assert.Equal("chunk", events[0].Event);
        Assert.Equal("stdout", events[0].Data.GetProperty("stream").GetString());
        Assert.Equal("build starting\n", events[0].Data.GetProperty("data").GetString());

        Assert.Equal("chunk", events[1].Event);
        Assert.Equal("stdout", events[1].Data.GetProperty("stream").GetString());
        Assert.Equal("step 1 done\n", events[1].Data.GetProperty("data").GetString());

        Assert.Equal("chunk", events[2].Event);
        Assert.Equal("stderr", events[2].Data.GetProperty("stream").GetString());
        Assert.Equal("warning: deprecated\n", events[2].Data.GetProperty("data").GetString());

        Assert.Equal("exit", events[3].Event);
        Assert.Equal(0, events[3].Data.GetProperty("exit_code").GetInt32());
    }

    [Fact]
    public async Task Stream_exec_propagates_a_nonzero_exit_code()
    {
        using var factory = CreateFactory();
        var ct = Token();
        var agent = await FakeAgent.ConnectAsync(factory, ct);
        agent.StartExec(new[] { Err("boom\n") }, exitCode: 17, ct);

        var events = await PostStreamExecAsync(factory, "false", ct);

        Assert.Equal(2, events.Count);
        Assert.Equal("chunk", events[0].Event);
        Assert.Equal("stderr", events[0].Data.GetProperty("stream").GetString());
        Assert.Equal("exit", events[1].Event);
        Assert.Equal(17, events[1].Data.GetProperty("exit_code").GetInt32());
    }

    [Fact]
    public async Task Draining_stream_output_emits_stream_credit_to_the_agent()
    {
        // A small window makes the credit batch threshold small (window/2), so draining a modest
        // amount of output deterministically triggers a stream.credit back to the agent.
        using var factory = CreateFactory(initialWindowBytes: 64);
        var ct = Token();
        var agent = await FakeAgent.ConnectAsync(factory, ct);
        // Gate exec.exit on the credit: the agent emits 64 bytes of stdout and holds the exit until
        // a stream.credit comes back. This is deterministic — the stream stays open (so the drained
        // bytes actually replenish credit) until the credit is observed, then the exit follows.
        agent.StartExec(new[] { Out(new string('x', 64)) }, exitCode: 0, ct, gateExitOnCredit: true);

        var events = await PostStreamExecAsync(factory, "yes", ct);

        // The SSE consumer drained 64 bytes (>= threshold 32), so the relay granted credit back.
        var credit = await agent.WaitForControlAsync(FrameTypes.StreamCredit, ct);
        Assert.True(credit.GetProperty("sid").GetUInt32() > 0);
        Assert.True(credit.GetProperty("bytes").GetInt32() >= 32);

        Assert.Equal("exit", events[^1].Event);
    }

    [Fact]
    public async Task Stream_exec_against_an_offline_host_yields_an_error_event()
    {
        using var factory = CreateFactory();
        var ct = Token();
        // No agent connects, so the host has no live tunnel.

        var events = await PostStreamExecAsync(factory, "make build", ct, hostId: "host-nobody");

        var error = Assert.Single(events);
        Assert.Equal("error", error.Event);
        Assert.Equal("host_offline", error.Data.GetProperty("error").GetString());
    }

    // ---------- helpers ----------

    private static AgentChunk Out(string text) => new(Channels.Stdout, Encoding.UTF8.GetBytes(text));

    private static AgentChunk Err(string text) => new(Channels.Stderr, Encoding.UTF8.GetBytes(text));

    /// <summary>POSTs the streamed dev exec endpoint and parses the SSE body into its events.</summary>
    private static async Task<List<SseEvent>> PostStreamExecAsync(
        WebApplicationFactory<Program> factory, string command, CancellationToken ct, string? hostId = null)
    {
        var client = factory.CreateClient();
        var body = new StringContent(
            JsonSerializer.Serialize(new { hostId = hostId ?? DevHostId, command }),
            Encoding.UTF8,
            "application/json");

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/dev/leases/lease_abc/exec?stream=1")
        {
            Content = body,
        };

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        await using var payload = await response.Content.ReadAsStreamAsync(ct);
        return await ParseSseAsync(payload, ct);
    }

    private static async Task<List<SseEvent>> ParseSseAsync(Stream stream, CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var events = new List<SseEvent>();
        string? name = null;
        string? data = null;

        while (true)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null)
            {
                break;
            }

            if (line.Length == 0)
            {
                if (name is not null && data is not null)
                {
                    events.Add(new SseEvent(name, JsonDocument.Parse(data).RootElement.Clone()));
                }

                name = null;
                data = null;
                continue;
            }

            if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                name = line["event: ".Length..];
            }
            else if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                data = line["data: ".Length..];
            }
        }

        return events;
    }

    // A per-test deadline so a hung relay/bridge fails the test instead of hanging the run.
    private static CancellationToken Token() => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

    private readonly record struct SseEvent(string Event, JsonElement Data);

    /// <summary>An output chunk the fake agent emits on a given wire channel.</summary>
    private readonly record struct AgentChunk(byte Channel, byte[] Data);

    /// <summary>
    /// A TestServer WS client that plays the host's wisp-agent for streamed exec: completes the
    /// handshake, answers <c>exec.open</c> with <c>exec.opened</c>, emits the scripted output chunks
    /// as binary frames on the <c>sid</c>, then <c>exec.exit</c>, and records the control frames it
    /// receives so tests can assert on <c>stream.credit</c>.
    /// </summary>
    private sealed class FakeAgent
    {
        private readonly WebSocket _socket;
        private readonly Channel<JsonElement> _control = Channel.CreateUnbounded<JsonElement>();

        private FakeAgent(WebSocket socket) => _socket = socket;

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

        /// <summary>
        /// Runs the agent loop: on <c>exec.open</c> reply <c>exec.opened</c> and emit the chunks.
        /// When <paramref name="gateExitOnCredit"/> is false the <c>exec.exit</c> follows the chunks
        /// immediately; when true it is held until a <c>stream.credit</c> is received (so a drained /
        /// credited window is observable before the stream ends). The loop keeps reading throughout,
        /// so it can observe that inbound credit.
        /// </summary>
        public void StartExec(AgentChunk[] chunks, int exitCode, CancellationToken ct, bool gateExitOnCredit = false)
        {
            _ = Task.Run(async () =>
            {
                var buffer = new byte[64 * 1024];
                uint execSid = 0;
                try
                {
                    while (_socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
                    {
                        var result = await _socket.ReceiveAsync(buffer, ct);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            break;
                        }

                        if (result.MessageType != WebSocketMessageType.Text)
                        {
                            continue;
                        }

                        var element = JsonDocument.Parse(buffer.AsMemory(0, result.Count)).RootElement.Clone();
                        var type = element.GetProperty("t").GetString();
                        _control.Writer.TryWrite(element);

                        if (type == FrameTypes.ExecOpen)
                        {
                            execSid = await OpenAndEmitAsync(element, chunks, ct);
                            if (!gateExitOnCredit)
                            {
                                await SendExitAsync(execSid, exitCode, ct);
                            }
                        }
                        else if (type == FrameTypes.StreamCredit && gateExitOnCredit && execSid != 0)
                        {
                            await SendExitAsync(execSid, exitCode, ct);
                            execSid = 0;
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

        private async Task<uint> OpenAndEmitAsync(JsonElement open, AgentChunk[] chunks, CancellationToken ct)
        {
            var rid = open.GetProperty("rid").GetUInt32();
            var sid = open.GetProperty("sid").GetUInt32();

            await SendRawAsync($"{{\"t\":\"exec.opened\",\"rid\":{rid},\"sid\":{sid}}}", ct);

            foreach (var chunk in chunks)
            {
                var frame = new BinaryFrame(chunk.Channel, sid, chunk.Data).Encode();
                await _socket.SendAsync(frame, WebSocketMessageType.Binary, true, ct);
            }

            return sid;
        }

        private Task SendExitAsync(uint sid, int exitCode, CancellationToken ct) =>
            SendRawAsync($"{{\"t\":\"exec.exit\",\"sid\":{sid},\"exit_code\":{exitCode}}}", ct);

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
