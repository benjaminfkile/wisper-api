using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Wisper.Api.Tunnel;
using Wisper.Api.Tunnel.Messages;
using Xunit;

namespace Wisper.Api.Tests.Tunnel;

/// <summary>
/// Tunnel-level integration tests for <c>file.read</c> (docs/TUNNEL.md §5): a FakeAgent plays the host,
/// answers a <c>file.read</c> with <c>file.opened{size}</c>, streams the file bytes over binary frames on
/// the <c>sid</c>, and ends with <c>file.eof</c>. The manager relays those bytes end-to-end onto the
/// <c>GET /dev/leases/:id/files</c> HTTP response as <c>application/octet-stream</c>, and drains credit
/// back to the agent per byte written (backpressure honored end-to-end).
/// </summary>
public class TunnelFileDownloadTests
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
    public async Task File_download_streams_bytes_end_to_end_with_content_length()
    {
        using var factory = CreateFactory();
        var ct = Token();
        var agent = await FakeAgent.ConnectAsync(factory, ct);
        var payload = Encoding.UTF8.GetBytes("hello world, streamed");
        agent.StartFileServer(new[] { payload }, size: payload.Length, ct);

        var response = await factory.CreateClient()
            .GetAsync($"/dev/leases/lease_abc/files?hostId={DevHostId}&path=/etc/hello", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(payload.Length, response.Content.Headers.ContentLength);
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        Assert.Equal(payload, bytes);

        // The fake agent observed the file.read request the relay sent (path forwarded verbatim).
        var request = await agent.WaitForControlAsync(FrameTypes.FileRead, ct);
        Assert.Equal("/etc/hello", request.GetProperty("path").GetString());
    }

    [Fact]
    public async Task File_download_streams_multiple_chunks_in_order()
    {
        using var factory = CreateFactory();
        var ct = Token();
        var agent = await FakeAgent.ConnectAsync(factory, ct);
        var chunks = new[]
        {
            Encoding.UTF8.GetBytes("aaaa"),
            Encoding.UTF8.GetBytes("bbbb"),
            Encoding.UTF8.GetBytes("cccc"),
        };
        var total = chunks.Sum(c => c.Length);
        agent.StartFileServer(chunks, size: total, ct);

        var response = await factory.CreateClient()
            .GetAsync($"/dev/leases/lease_abc/files?hostId={DevHostId}&path=/etc/multi", ct);

        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        Assert.Equal("aaaabbbbcccc", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public async Task File_download_agent_error_maps_to_typed_code()
    {
        using var factory = CreateFactory();
        var ct = Token();
        var agent = await FakeAgent.ConnectAsync(factory, ct);
        agent.StartFileServer(chunks: null, size: 0, ct, respondError: "not_found");

        var response = await factory.CreateClient()
            .GetAsync($"/dev/leases/lease_abc/files?hostId={DevHostId}&path=/etc/missing", ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.Contains("not_found", body);
    }

    [Fact]
    public async Task File_download_drains_credit_back_to_the_agent()
    {
        // A small window makes the credit batch threshold small (window/2), so draining a modest amount
        // of file bytes deterministically triggers a stream.credit back to the agent.
        using var factory = CreateFactory(initialWindowBytes: 64);
        var ct = Token();
        var agent = await FakeAgent.ConnectAsync(factory, ct);
        agent.StartFileServer(new[] { new byte[64] }, size: 64, ct, gateEofOnCredit: true);

        var response = await factory.CreateClient()
            .GetAsync($"/dev/leases/lease_abc/files?hostId={DevHostId}&path=/etc/big", ct);
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        Assert.Equal(64, bytes.Length);

        var credit = await agent.WaitForControlAsync(FrameTypes.StreamCredit, ct);
        Assert.True(credit.GetProperty("bytes").GetInt32() >= 32);
    }

    [Fact]
    public async Task Manager_receive_loop_accepts_a_large_lease_create_text_frame()
    {
        // Files ride the lease.create outbound frame, but the manager's inbound text-frame limit still
        // caps how large a text frame the receive loop will accept. Prove it: the agent sends back a
        // large capability.update text frame (unhandled but structurally accepted) and the tunnel stays
        // healthy afterwards. If the raised MaxControlFrameBytes cap regressed to the old 64 KiB the
        // receive loop would close the socket on the oversize frame.
        using var factory = CreateFactory();
        var ct = Token();
        var agent = await FakeAgent.ConnectAsync(factory, ct);

        // A ~1.5 MiB text frame -- larger than the old 64 KiB handshake cap but well under the raised
        // 2 MiB inbound control-frame limit that a max-size files array requires.
        var pad = new string('x', 1_500_000);
        var big = "{\"t\":\"capability.update\",\"pad\":\"" + pad + "\"}";
        await agent.SendRawAsync(big, ct);

        // Follow up with a normal file.read request; if the socket had been torn down the request would
        // stall until the relay deadline and fail. Keeping it healthy proves the large text frame was
        // accepted (dropped as unhandled, not treated as oversize).
        agent.StartFileServer(new[] { Encoding.UTF8.GetBytes("ok") }, size: 2, ct);
        var response = await factory.CreateClient()
            .GetAsync($"/dev/leases/lease_abc/files?hostId={DevHostId}&path=/etc/x", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", await response.Content.ReadAsStringAsync(ct));
    }

    // ---- helpers ----

    private static CancellationToken Token() => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

    /// <summary>
    /// A TestServer WS client that plays the host's wisp-agent for file downloads: on <c>file.read</c> it
    /// answers <c>file.opened{size}</c>, emits the scripted byte chunks on the <c>sid</c>, then
    /// <c>file.eof</c>. When <c>respondError</c> is set it answers with a typed error frame instead.
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

            var buffer = new byte[64 * 1024];
            var ack = await socket.ReceiveAsync(buffer, ct);
            Assert.Equal(WebSocketMessageType.Text, ack.MessageType);
            return new FakeAgent(socket);
        }

        /// <summary>
        /// Runs the agent loop: on <c>file.read</c>, either answers with a typed error (when
        /// <paramref name="respondError"/> is set) or emits <c>file.opened</c>, the byte chunks, and
        /// finally <c>file.eof</c>. <paramref name="gateEofOnCredit"/> holds the eof until the manager
        /// grants a <c>stream.credit</c> so a credited-window scenario is observable.
        /// </summary>
        public void StartFileServer(
            byte[][]? chunks, long size, CancellationToken ct,
            string? respondError = null, bool gateEofOnCredit = false)
        {
            _ = Task.Run(async () =>
            {
                var buffer = new byte[64 * 1024];
                uint fileSid = 0;
                try
                {
                    while (_socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
                    {
                        var result = await ReceiveMessageAsync(_socket, buffer, ct);
                        if (result.Type == WebSocketMessageType.Close)
                        {
                            break;
                        }

                        if (result.Type != WebSocketMessageType.Text)
                        {
                            continue;
                        }

                        var element = JsonDocument.Parse(result.Data).RootElement.Clone();
                        var type = element.GetProperty("t").GetString();
                        _control.Writer.TryWrite(element);

                        if (type == FrameTypes.FileRead)
                        {
                            var rid = element.GetProperty("rid").GetUInt32();
                            var sid = element.GetProperty("sid").GetUInt32();

                            if (respondError is not null)
                            {
                                await SendRawAsync(
                                    $"{{\"t\":\"error\",\"rid\":{rid},\"code\":\"{respondError}\",\"message\":\"agent {respondError}\"}}", ct);
                                continue;
                            }

                            await SendRawAsync(
                                $"{{\"t\":\"file.opened\",\"rid\":{rid},\"sid\":{sid},\"size\":{size}}}", ct);

                            foreach (var chunk in chunks ?? Array.Empty<byte[]>())
                            {
                                var frame = new BinaryFrame(Channels.Stdout, sid, chunk).Encode();
                                await _socket.SendAsync(frame, WebSocketMessageType.Binary, true, ct);
                            }

                            fileSid = sid;
                            if (!gateEofOnCredit)
                            {
                                await SendRawAsync($"{{\"t\":\"file.eof\",\"sid\":{sid}}}", ct);
                            }
                        }
                        else if (type == FrameTypes.StreamCredit && gateEofOnCredit && fileSid != 0)
                        {
                            await SendRawAsync($"{{\"t\":\"file.eof\",\"sid\":{fileSid}}}", ct);
                            fileSid = 0;
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

        public Task SendRawAsync(string json, CancellationToken ct) =>
            _socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct);

        private static async Task<(WebSocketMessageType Type, byte[] Data)> ReceiveMessageAsync(
            WebSocket socket, byte[] buffer, CancellationToken ct)
        {
            using var accumulator = new MemoryStream();
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return (WebSocketMessageType.Close, Array.Empty<byte>());
                }

                accumulator.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                {
                    return (result.MessageType, accumulator.ToArray());
                }
            }
        }
    }
}
