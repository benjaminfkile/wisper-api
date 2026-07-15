using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Wisper.Api.Tests.TestSupport;
using Wisper.Api.Tunnel;
using Wisper.Api.Tunnel.Messages;
using Xunit;

namespace Wisper.Api.Tests.Tunnel;

/// <summary>
/// The DEV-ONLY <c>POST /dev/leases</c> response surfaces the target host's advertised container OS
/// (task #316): it is read from the live <c>hello.capability</c> the tunnel registry tracks for that
/// dev host id (a plain string like <c>dev-host-1</c>, so it keys the registry directly, not by Guid).
/// It is additive and null-safe — a host with no live tunnel, or one whose (older) agent advertised no
/// <c>os</c>, leaves the field <c>null</c> and never faults. The relay is faked, so no live agent is
/// needed; only the registry-tracked capability drives the surfaced <c>os</c>.
/// </summary>
public class DevLeaseOsTests
{
    private const string DevHostId = "dev-host-1";

    private static WebApplicationFactory<Program> CreateFactory(FakeTunnelRelay relay) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Tunnel:EnableDevEndpoints", "true");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ITunnelRelay>();
                services.AddSingleton<ITunnelRelay>(relay);
            });
        });

    [Theory]
    [InlineData("linux")]
    [InlineData("windows")]
    public async Task Create_response_carries_the_hosts_container_os(string os)
    {
        var relay = new FakeTunnelRelay();
        using var factory = CreateFactory(relay);
        RegisterHost(factory, os);

        var body = await CreateLeaseAsync(factory);

        Assert.Equal(os, body.GetProperty("os").GetString());
    }

    [Fact]
    public async Task Create_response_os_is_null_when_the_capability_advertises_none()
    {
        var relay = new FakeTunnelRelay();
        using var factory = CreateFactory(relay);
        RegisterHost(factory, os: null); // an older agent that omitted os → surfaced as null

        var body = await CreateLeaseAsync(factory);

        Assert.Equal(JsonValueKind.Null, body.GetProperty("os").ValueKind);
    }

    [Fact]
    public async Task Create_response_os_is_null_when_the_host_has_no_live_tunnel()
    {
        var relay = new FakeTunnelRelay();
        using var factory = CreateFactory(relay);
        // No connection registered for the dev host — the relay still creates the lease (faked), but the
        // registry has no capability, so os is absent.

        var body = await CreateLeaseAsync(factory);

        Assert.Equal(JsonValueKind.Null, body.GetProperty("os").ValueKind);
    }

    /// <summary>Registers a live tunnel for the dev host carrying a hello capability with <paramref name="os"/>.</summary>
    private static void RegisterHost(WebApplicationFactory<Program> factory, string? os)
    {
        var registry = factory.Services.GetRequiredService<IHostRegistry>();
        var connection = new TunnelConnection(
            new StubWebSocket(), DevHostId, sessionId: "sess-dev", maxReceiveBytes: 65536, NullLogger.Instance)
        {
            Capability = new HelloCapability { Os = os },
        };
        registry.RegisterAsync(connection).GetAwaiter().GetResult();
    }

    private static async Task<JsonElement> CreateLeaseAsync(WebApplicationFactory<Program> factory)
    {
        var response = await factory.CreateClient().PostAsJsonAsync(
            "/dev/leases",
            new { hostId = DevHostId, image = "alpine", network = "none", ttl_seconds = 3600 });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>A do-nothing <see cref="WebSocket"/>: the registry never touches the socket on register.</summary>
    private sealed class StubWebSocket : WebSocket
    {
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus s, string? d, CancellationToken ct) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus s, string? d, CancellationToken ct) => Task.CompletedTask;
        public override void Dispose() { }
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> b, CancellationToken ct) =>
            throw new NotSupportedException();
        public override Task SendAsync(ArraySegment<byte> b, WebSocketMessageType t, bool e, CancellationToken ct) =>
            Task.CompletedTask;
    }
}
