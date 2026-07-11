using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Wisper.Api.Tunnel;
using Xunit;

namespace Wisper.Api.Tests.Tunnel;

/// <summary>
/// Integration tests over the real app host driving the raw <c>GET /agent</c> tunnel with
/// the TestServer WebSocket client (docs/TUNNEL.md §3): handshake success, token rejection
/// (4401), protocol mismatch (4409), and supersede-on-reconnect.
/// </summary>
public class AgentTunnelTests
{
    private const string DevToken = "dev-host-token";
    private const string DevHostId = "host-alpha";

    private static WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting($"Tunnel:HostTokens:{DevToken}", DevHostId);
        });

    private static WebSocketClient CreateClient(WebApplicationFactory<Program> factory, string? token)
    {
        var client = factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = request =>
        {
            if (token is not null)
            {
                request.Headers["Authorization"] = $"Bearer {token}";
            }
        };
        return client;
    }

    private static Uri AgentUri(WebApplicationFactory<Program> factory) =>
        new(factory.Server.BaseAddress, "agent");

    private static async Task SendHelloAsync(WebSocket socket, int proto, CancellationToken ct)
    {
        var hello =
            "{\"t\":\"hello\",\"proto\":" + proto + ",\"agentVersion\":\"1.2.3\",\"wispVersion\":\"0.9.0\"," +
            "\"capability\":{\"images\":[\"alpine\"],\"default\":\"alpine\"," +
            "\"limits\":{\"max_ttl_seconds\":3600,\"max_cpus\":2,\"max_memory_mb\":1024,\"pids_limit\":256,\"networks\":[\"none\"]}}," +
            "\"capacity\":{\"maxLeases\":10,\"maxStreams\":50}}";
        await socket.SendAsync(Encoding.UTF8.GetBytes(hello), WebSocketMessageType.Text, true, ct);
    }

    private static async Task<JsonElement> ReceiveControlAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var result = await socket.ReceiveAsync(buffer, ct);
        Assert.Equal(WebSocketMessageType.Text, result.MessageType);
        return JsonDocument.Parse(buffer.AsMemory(0, result.Count)).RootElement.Clone();
    }

    [Fact]
    public async Task Valid_token_and_hello_receives_hello_ack_and_registers()
    {
        using var factory = CreateFactory();
        var ct = Token();

        var socket = await CreateClient(factory, DevToken).ConnectAsync(AgentUri(factory), ct);
        await SendHelloAsync(socket, TunnelProtocol.ProtocolVersion, ct);

        var ack = await ReceiveControlAsync(socket, ct);

        Assert.Equal("hello.ack", ack.GetProperty("t").GetString());
        Assert.Equal(1, ack.GetProperty("proto").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(ack.GetProperty("sessionId").GetString()));
        Assert.Equal(30000, ack.GetProperty("pingIntervalMs").GetInt32());
        Assert.Equal(32768, ack.GetProperty("maxFrameBytes").GetInt32());
        Assert.Equal(262144, ack.GetProperty("initialWindowBytes").GetInt32());
        Assert.Equal(90, ack.GetProperty("graceSeconds").GetInt32());

        var registry = factory.Services.GetRequiredService<IHostRegistry>();
        Assert.True(registry.TryGet(DevHostId, out var connection));
        Assert.NotNull(connection);
        Assert.Contains(registry.Online, c => c.HostId == DevHostId);

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", ct);
    }

    [Fact]
    public async Task Missing_token_is_closed_4401()
    {
        using var factory = CreateFactory();
        var ct = Token();

        var socket = await CreateClient(factory, token: null).ConnectAsync(AgentUri(factory), ct);

        var closeStatus = await ReadCloseStatusAsync(socket, ct);
        Assert.Equal(CloseCodes.BadToken, closeStatus);
    }

    [Fact]
    public async Task Invalid_token_is_closed_4401()
    {
        using var factory = CreateFactory();
        var ct = Token();

        var socket = await CreateClient(factory, "not-the-token").ConnectAsync(AgentUri(factory), ct);

        var closeStatus = await ReadCloseStatusAsync(socket, ct);
        Assert.Equal(CloseCodes.BadToken, closeStatus);
    }

    [Fact]
    public async Task Unsupported_proto_is_closed_4409()
    {
        using var factory = CreateFactory();
        var ct = Token();

        var socket = await CreateClient(factory, DevToken).ConnectAsync(AgentUri(factory), ct);
        await SendHelloAsync(socket, proto: 999, ct);

        var closeStatus = await ReadCloseStatusAsync(socket, ct);
        Assert.Equal(CloseCodes.ProtocolIncompatible, closeStatus);
    }

    [Fact]
    public async Task Second_connection_for_same_host_supersedes_the_first()
    {
        using var factory = CreateFactory();
        var ct = Token();

        var first = await CreateClient(factory, DevToken).ConnectAsync(AgentUri(factory), ct);
        await SendHelloAsync(first, TunnelProtocol.ProtocolVersion, ct);
        await ReceiveControlAsync(first, ct);

        var second = await CreateClient(factory, DevToken).ConnectAsync(AgentUri(factory), ct);
        await SendHelloAsync(second, TunnelProtocol.ProtocolVersion, ct);
        await ReceiveControlAsync(second, ct);

        // The first connection is closed normally (docs/TUNNEL.md §16).
        var firstClose = await ReadCloseStatusAsync(first, ct);
        Assert.Equal(CloseCodes.Normal, firstClose);

        // Exactly one live tunnel for the host, and it is the second connection.
        var registry = factory.Services.GetRequiredService<IHostRegistry>();
        Assert.True(registry.TryGet(DevHostId, out var connection));
        Assert.NotNull(connection);
        Assert.Single(registry.Online, c => c.HostId == DevHostId);

        await second.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", ct);
    }

    // A per-test deadline so a hung handshake fails the test instead of hanging the run.
    private static CancellationToken Token() => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

    private static async Task<int> ReadCloseStatusAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[1024];
        while (socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                break;
            }
        }

        Assert.NotNull(socket.CloseStatus);
        return (int)socket.CloseStatus!.Value;
    }
}
