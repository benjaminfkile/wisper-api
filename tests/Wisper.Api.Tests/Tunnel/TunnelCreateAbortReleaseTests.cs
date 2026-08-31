using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Wisper.Api.Infrastructure;
using Wisper.Api.Tunnel;
using Wisper.Api.Tunnel.Messages;
using Xunit;

namespace Wisper.Api.Tests.Tunnel;

/// <summary>
/// Abort-release tests for the relay's create path (docs/TUNNEL.md §5): once the <c>lease.create</c>
/// frame has reached the host, a create that fails anyway -- the consumer aborted, or the host never
/// answered within the relay deadline -- must fire a best-effort <c>lease.release</c> carrying the SAME
/// minted <c>leaseId</c>, so the container the host may have provisioned does not squat on its capacity
/// until the TTL. A TestServer WS client plays the FAKE AGENT and asserts on the frames it receives;
/// the relay is driven directly so the test owns the create call's cancellation token.
/// </summary>
public class TunnelCreateAbortReleaseTests
{
    private const string DevToken = "dev-host-token";
    private const string DevHostId = "host-alpha";

    private static WebApplicationFactory<Program> CreateFactory(int relayTimeoutMs = 10000) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting($"Tunnel:HostTokens:{DevToken}", DevHostId);
            builder.UseSetting("Tunnel:RelayRequestTimeoutMs", relayTimeoutMs.ToString());
        });

    [Fact]
    public async Task Cancelled_create_after_the_frame_sends_a_release_for_the_same_lease()
    {
        using var factory = CreateFactory();
        var ct = Token();
        var socket = await ConnectAndHandshakeAsync(factory, ct);
        var relay = factory.Services.GetRequiredService<ITunnelRelay>();

        using var createCts = new CancellationTokenSource();
        var createTask = relay.CreateLeaseAsync(
            DevHostId, new LeaseCreate { Image = "alpine", TtlSeconds = 60 }, createCts.Token);

        // The fake agent receives the create: the frame is out, so the host may be provisioning.
        var create = await ReadControlAsync(socket, ct);
        Assert.Equal(FrameTypes.LeaseCreate, create.GetProperty("t").GetString());
        var leaseId = create.GetProperty("leaseId").GetString();
        Assert.False(string.IsNullOrEmpty(leaseId));

        // The consumer aborts before lease.accepted / lease.ready ever arrive.
        createCts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => createTask);

        // The relay does not walk away silently: a lease.release for the SAME minted id reaches the agent.
        var release = await ReadControlAsync(socket, ct);
        Assert.Equal(FrameTypes.LeaseRelease, release.GetProperty("t").GetString());
        Assert.Equal(leaseId, release.GetProperty("leaseId").GetString());
        Assert.True(release.GetProperty("rid").GetUInt32() > 0);
    }

    [Fact]
    public async Task Timed_out_create_after_the_frame_sends_a_release_for_the_same_lease()
    {
        using var factory = CreateFactory(relayTimeoutMs: 400);
        var ct = Token();
        var socket = await ConnectAndHandshakeAsync(factory, ct);
        var relay = factory.Services.GetRequiredService<ITunnelRelay>();

        var createTask = relay.CreateLeaseAsync(
            DevHostId, new LeaseCreate { Image = "alpine", TtlSeconds = 60 }, ct);

        // The agent reads the create but never replies -- the relay must hit its deadline.
        var create = await ReadControlAsync(socket, ct);
        Assert.Equal(FrameTypes.LeaseCreate, create.GetProperty("t").GetString());
        var leaseId = create.GetProperty("leaseId").GetString();
        Assert.False(string.IsNullOrEmpty(leaseId));

        var ex = await Assert.ThrowsAsync<ApiException>(() => createTask);
        Assert.Equal(ApiErrorCode.UpstreamTimeout, ex.Code);

        var release = await ReadControlAsync(socket, ct);
        Assert.Equal(FrameTypes.LeaseRelease, release.GetProperty("t").GetString());
        Assert.Equal(leaseId, release.GetProperty("leaseId").GetString());
    }

    // ---- helpers (mirroring TunnelRelayTests) ----

    private static async Task<WebSocket> ConnectAndHandshakeAsync(
        WebApplicationFactory<Program> factory, CancellationToken ct)
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
        await SendRawAsync(socket, hello, ct);

        var ack = await ReadControlAsync(socket, ct);
        Assert.Equal(FrameTypes.HelloAck, ack.GetProperty("t").GetString());
        return socket;
    }

    private static Task SendRawAsync(WebSocket socket, string json, CancellationToken ct) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct);

    private static async Task<JsonElement> ReadControlAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var result = await socket.ReceiveAsync(buffer, ct);
        Assert.Equal(WebSocketMessageType.Text, result.MessageType);
        return JsonDocument.Parse(buffer.AsMemory(0, result.Count)).RootElement.Clone();
    }

    // A per-test deadline so a hung relay fails the test instead of hanging the run.
    private static CancellationToken Token() => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;
}
