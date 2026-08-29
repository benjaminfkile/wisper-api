using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Wisper.Api.Tunnel;
using Wisper.Api.Tunnel.Backplane;
using Xunit;

namespace Wisper.Api.Tests.Tunnel.Backplane;

/// <summary>
/// Boots the whole app with the multi-instance backplane <b>enabled</b> (loopback fabric -- no Redis) and
/// drives a lease through it end-to-end with a fake agent (docs/DESIGN.md §7, P8.1). This proves the
/// config toggle wires the distributed registry + relay in place of the in-memory pair with no DI cycle,
/// that a connecting host records presence, and that a consumer call resolves the owner (this single
/// instance) and drives the physical socket locally -- the same happy path the single-instance suite
/// covers, but through <see cref="DistributedTunnelRelay"/>/<see cref="DistributedHostRegistry"/>.
/// </summary>
public class BackplaneEnabledTunnelTests
{
    private const string DevToken = "dev-host-token";
    private const string DevHostId = "host-alpha";

    private static WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting($"Tunnel:HostTokens:{DevToken}", DevHostId);
            builder.UseSetting("Tunnel:EnableDevEndpoints", "true");
            builder.UseSetting("Tunnel:RelayRequestTimeoutMs", "10000");
            // Turn on the backplane; blank Redis configuration ⇒ in-process loopback + presence (no Redis).
            builder.UseSetting("Tunnel:Backplane:Enabled", "true");
            builder.UseSetting("Tunnel:Backplane:InstanceId", "test-instance");
        });

    [Fact]
    public void The_distributed_registry_and_relay_are_wired_when_enabled()
    {
        using var factory = CreateFactory();
        var registry = factory.Services.GetRequiredService<IHostRegistry>();
        var relay = factory.Services.GetRequiredService<ITunnelRelay>();

        Assert.IsType<DistributedHostRegistry>(registry);
        Assert.IsType<DistributedTunnelRelay>(relay);
    }

    [Fact]
    public async Task Create_lease_flows_through_the_backplane_relay_and_records_presence()
    {
        using var factory = CreateFactory();
        var ct = Token();
        var socket = await ConnectAndHandshakeAsync(factory, ct);

        // The connecting host must have registered its presence on this instance.
        var presence = factory.Services.GetRequiredService<IHostPresenceStore>();
        await WaitUntilAsync(async () => await presence.GetOwnerAsync(DevHostId) == "test-instance");

        var responder = Task.Run(async () =>
        {
            var req = await ReadControlAsync(socket, ct);
            Assert.Equal(FrameTypes.LeaseCreate, req.GetProperty("t").GetString());
            var rid = req.GetProperty("rid").GetUInt32();
            var leaseId = req.GetProperty("leaseId").GetString();
            await SendRawAsync(socket,
                $"{{\"t\":\"lease.accepted\",\"rid\":{rid},\"leaseId\":\"{leaseId}\"," +
                "\"wispContractId\":\"wc_123\",\"status\":\"provisioning\"}", ct);
            await SendRawAsync(socket, $"{{\"t\":\"lease.ready\",\"leaseId\":\"{leaseId}\"}}", ct);
        }, ct);

        var body = "{\"hostId\":\"" + DevHostId + "\",\"image\":\"alpine\",\"network\":\"none\"," +
                   "\"resources\":{\"cpus\":2,\"memory_mb\":1024,\"pids\":256},\"ttl_seconds\":3600}";
        var response = await factory.CreateClient().PostAsync("/dev/leases", JsonContent(body), ct);
        await responder;

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await ReadJsonAsync(response, ct);
        Assert.Equal("wc_123", json.GetProperty("wispContractId").GetString());
    }

    // ---- helpers (mirrors TunnelRelayTests' fake-agent harness) ----

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

    private static StringContent JsonContent(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail("condition not met before the deadline");
    }

    private static CancellationToken Token() => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;
}
