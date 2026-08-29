using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wisper.Api.Auth;
using Wisper.Api.Domain;
using Wisper.Api.Leases;
using Wisper.Api.Persistence.Leases;
using Wisper.Api.Persistence.Users;
using Wisper.Api.Tests.TestSupport;
using Wisper.Api.Tunnel;
using Wisper.Api.Tunnel.Messages;
using Xunit;

namespace Wisper.Api.Tests.Leases;

/// <summary>
/// Integration tests over the real app host for the consumer interactive shell (docs/API.md §7): the
/// JWT-authenticated <c>POST /v1/leases/:id/shell-ticket</c> mint (single-use, short-TTL,
/// <c>(user, lease)</c>-bound, with the 404/409 ownership+ready checks) and the ticket-authenticated
/// <c>WS /v1/leases/:id/shell</c> that redeems the ticket once and bridges the consumer socket to a
/// tunnel shell stream. The JWT validator and repositories are in-memory doubles; the tunnel relay is the
/// REAL one, driven by a TestServer WS client that plays the fake agent (echoing stdin→stdout).
/// </summary>
public class LeaseShellEndpointsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 12, 0, 0, 0, TimeSpan.Zero);

    private const string HostToken = "shell-host-token";
    private static readonly Guid HostId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private sealed class Fixture
    {
        public InMemoryLeaseRepository Leases { get; } = new();
        public InMemoryUserRepository Users { get; } = new();
        public FakeJwtValidator Validator { get; } = new();

        public WebApplicationFactory<Program> Build() =>
            new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                // The fake agent authenticates as HostId, so the relay finds its tunnel by that key.
                builder.UseSetting($"Tunnel:HostTokens:{HostToken}", HostId.ToString());
                builder.UseSetting("Tunnel:RelayRequestTimeoutMs", "10000");
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IJwtValidator>();
                    services.AddSingleton<IJwtValidator>(Validator);
                    services.RemoveAll<ILeaseRepository>();
                    services.AddSingleton<ILeaseRepository>(Leases);
                    services.RemoveAll<IUserRepository>();
                    services.AddSingleton<IUserRepository>(Users);
                    // NB: the real ITunnelRelay is kept -- the WS bridge drives a live tunnel to the fake agent.
                });
            });

        /// <summary>Creates a lease in the given state, owned by <paramref name="consumerUserId"/>, on HostId.</summary>
        public async Task<string> SeedLeaseAsync(Guid consumerUserId, LeaseStatus status = LeaseStatus.Active)
        {
            var id = Guid.NewGuid();
            await Leases.CreateAsync(new Lease
            {
                Id = id,
                ConsumerUserId = consumerUserId,
                HostId = HostId,
                HostImageId = Guid.NewGuid(),
                ImageRef = "reg/wisp-base:latest",
                Network = NetworkMode.Open,
                TtlSeconds = 3600,
                PriceCentsPerMin = 5,
                Currency = "usd",
                Status = status,
                WispContractId = "wisp-contract-1",
                CreatedAt = T0,
                StartedAt = status == LeaseStatus.Provisioning ? null : T0,
                LastMeteredAt = T0,
                BillableSeconds = 0,
            });
            return TunnelLeaseId.Format(id);
        }

        public async Task<Guid> CallerIdAsync() =>
            (await Users.GetByCognitoSubAsync("fake-sub"))!.Id;
    }

    private static HttpClient Authed(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", "Bearer good");
        return client;
    }

    private static async Task<Guid> BootstrapCallerAsync(WebApplicationFactory<Program> factory, Fixture fx)
    {
        var me = await Authed(factory).GetAsync("/v1/me");
        me.EnsureSuccessStatusCode();
        return await fx.CallerIdAsync();
    }

    private static async Task<string> MintTicketAsync(WebApplicationFactory<Program> factory, string leaseId)
    {
        var response = await Authed(factory).PostAsync($"/v1/leases/{leaseId}/shell-ticket", content: null);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<TicketDto>();
        return body!.Ticket;
    }

    // ---------- ticket mint ----------

    [Fact]
    public async Task Ticket_without_a_token_is_401()
    {
        var fx = new Fixture();
        using var factory = fx.Build();

        var response = await factory.CreateClient()
            .PostAsync($"/v1/leases/lease_{Guid.NewGuid():N}/shell-ticket", content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Ticket_mint_returns_a_single_use_short_ttl_ticket()
    {
        var fx = new Fixture();
        using var factory = fx.Build();
        var caller = await BootstrapCallerAsync(factory, fx);
        var leaseId = await fx.SeedLeaseAsync(caller);

        var response = await Authed(factory).PostAsync($"/v1/leases/{leaseId}/shell-ticket", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<TicketDto>();
        Assert.StartsWith("tkt_", body!.Ticket);
        Assert.Equal((int)InMemoryShellTicketStore.Ttl.TotalSeconds, body.ExpiresIn);
    }

    [Fact]
    public async Task Ticket_mint_on_unknown_lease_is_404()
    {
        var fx = new Fixture();
        using var factory = fx.Build();
        await BootstrapCallerAsync(factory, fx);

        var response = await Authed(factory)
            .PostAsync($"/v1/leases/lease_{Guid.NewGuid():N}/shell-ticket", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Ticket_mint_on_a_lease_owned_by_another_user_is_404()
    {
        var fx = new Fixture();
        using var factory = fx.Build();
        await BootstrapCallerAsync(factory, fx);
        var otherUsersLease = await fx.SeedLeaseAsync(Guid.NewGuid());

        var response = await Authed(factory)
            .PostAsync($"/v1/leases/{otherUsersLease}/shell-ticket", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Ticket_mint_before_the_lease_is_active_is_409_lease_not_ready()
    {
        var fx = new Fixture();
        using var factory = fx.Build();
        var caller = await BootstrapCallerAsync(factory, fx);
        var leaseId = await fx.SeedLeaseAsync(caller, LeaseStatus.Provisioning);

        var response = await Authed(factory).PostAsync($"/v1/leases/{leaseId}/shell-ticket", content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ErrorEnvelopeDto>();
        Assert.Equal("lease_not_ready", envelope!.Error.Code);
    }

    // ---------- WS shell ----------

    [Fact]
    public async Task Shell_ws_bridges_stdin_to_stdout()
    {
        var fx = new Fixture();
        using var factory = fx.Build();
        var ct = Token();
        var caller = await BootstrapCallerAsync(factory, fx);
        var leaseId = await fx.SeedLeaseAsync(caller);

        // A live agent for HostId so the relay's shell.open is answered and stdin echoes back as stdout.
        var agent = await FakeAgent.ConnectAsync(factory, ct);
        agent.StartEcho(ct);

        var ticket = await MintTicketAsync(factory, leaseId);
        var consumer = await ConnectShellAsync(factory, leaseId, ticket, ct);

        var payload = Encoding.UTF8.GetBytes("echo round-trip");
        await consumer.SendAsync(payload, WebSocketMessageType.Binary, true, ct);

        var echoed = await ReceiveBinaryAsync(consumer, ct);
        Assert.Equal(payload, echoed);

        await consumer.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", ct);
    }

    [Fact]
    public async Task Shell_ws_forwards_a_resize_control_frame()
    {
        var fx = new Fixture();
        using var factory = fx.Build();
        var ct = Token();
        var caller = await BootstrapCallerAsync(factory, fx);
        var leaseId = await fx.SeedLeaseAsync(caller);

        var agent = await FakeAgent.ConnectAsync(factory, ct);
        agent.StartEcho(ct);

        var ticket = await MintTicketAsync(factory, leaseId);
        var consumer = await ConnectShellAsync(factory, leaseId, ticket, ct);

        await consumer.SendAsync(
            Encoding.UTF8.GetBytes("{\"t\":\"resize\",\"cols\":120,\"rows\":40}"),
            WebSocketMessageType.Text, true, ct);

        var resize = await agent.WaitForControlAsync(FrameTypes.ShellResize, ct);
        Assert.Equal(120, resize.GetProperty("cols").GetInt32());
        Assert.Equal(40, resize.GetProperty("rows").GetInt32());

        await consumer.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", ct);
    }

    [Fact]
    public async Task Shell_ws_with_an_invalid_ticket_rejects_the_handshake()
    {
        var fx = new Fixture();
        using var factory = fx.Build();
        var ct = Token();
        var caller = await BootstrapCallerAsync(factory, fx);
        var leaseId = await fx.SeedLeaseAsync(caller);

        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => ConnectShellAsync(factory, leaseId, "tkt_not-a-real-ticket", ct));
        // The TestServer surfaces the rejected upgrade's status in the handshake failure message.
        Assert.Contains("401", ex.Message);
    }

    [Fact]
    public async Task Shell_ws_ticket_is_single_use()
    {
        var fx = new Fixture();
        using var factory = fx.Build();
        var ct = Token();
        var caller = await BootstrapCallerAsync(factory, fx);
        var leaseId = await fx.SeedLeaseAsync(caller);

        var agent = await FakeAgent.ConnectAsync(factory, ct);
        agent.StartEcho(ct);

        var ticket = await MintTicketAsync(factory, leaseId);

        // First connect redeems the ticket and bridges.
        var first = await ConnectShellAsync(factory, leaseId, ticket, ct);
        await first.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", ct);

        // The same ticket can't be redeemed again -- the second handshake is rejected.
        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => ConnectShellAsync(factory, leaseId, ticket, ct));
        Assert.Contains("401", ex.Message);
    }

    [Fact]
    public async Task Shell_ws_without_a_ticket_rejects_the_handshake()
    {
        var fx = new Fixture();
        using var factory = fx.Build();
        var ct = Token();
        var caller = await BootstrapCallerAsync(factory, fx);
        var leaseId = await fx.SeedLeaseAsync(caller);

        var client = factory.Server.CreateWebSocketClient();
        var uri = new Uri(factory.Server.BaseAddress, $"v1/leases/{leaseId}/shell");

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectAsync(uri, ct));
        Assert.Contains("401", ex.Message);
    }

    // ---------- helpers ----------

    private static async Task<WebSocket> ConnectShellAsync(
        WebApplicationFactory<Program> factory, string leaseId, string ticket, CancellationToken ct)
    {
        var client = factory.Server.CreateWebSocketClient();
        var uri = new Uri(factory.Server.BaseAddress, $"v1/leases/{leaseId}/shell?ticket={ticket}");
        return await client.ConnectAsync(uri, ct);
    }

    private static async Task<byte[]> ReceiveBinaryAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var result = await socket.ReceiveAsync(buffer, ct);
        Assert.Equal(WebSocketMessageType.Binary, result.MessageType);
        return buffer.AsSpan(0, result.Count).ToArray();
    }

    private static CancellationToken Token() => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

    private sealed record TicketDto(
        [property: JsonPropertyName("ticket")] string Ticket,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);

    private sealed record ErrorEnvelopeDto(
        [property: JsonPropertyName("error")] ErrorBodyDto Error);

    private sealed record ErrorBodyDto(
        [property: JsonPropertyName("code")] string Code,
        [property: JsonPropertyName("message")] string Message);

    /// <summary>
    /// A TestServer WS client that plays the host's wisp-agent: completes the handshake, answers
    /// <c>shell.open</c> with <c>shell.opened</c>, echoes stdin→stdout, and records control frames so
    /// tests can assert on <c>shell.resize</c>. Mirrors the fake agent in the tunnel-shell suite.
    /// </summary>
    private sealed class FakeAgent
    {
        private readonly WebSocket _socket;
        private readonly Channel<JsonElement> _control = Channel.CreateUnbounded<JsonElement>();

        private FakeAgent(WebSocket socket) => _socket = socket;

        public static async Task<FakeAgent> ConnectAsync(WebApplicationFactory<Program> factory, CancellationToken ct)
        {
            var client = factory.Server.CreateWebSocketClient();
            client.ConfigureRequest = request => request.Headers["Authorization"] = $"Bearer {HostToken}";
            var socket = await client.ConnectAsync(new Uri(factory.Server.BaseAddress, "agent"), ct);

            var hello =
                "{\"t\":\"hello\",\"proto\":" + TunnelProtocol.ProtocolVersion +
                ",\"agentVersion\":\"1.0.0\",\"wispVersion\":\"0.9.0\"," +
                "\"capability\":{\"images\":[\"alpine\"],\"default\":\"alpine\"," +
                "\"limits\":{\"max_ttl_seconds\":3600,\"max_cpus\":2,\"max_memory_mb\":1024,\"pids_limit\":256,\"networks\":[\"none\"]}}," +
                "\"capacity\":{\"maxLeases\":10,\"maxStreams\":50}}";
            await socket.SendAsync(Encoding.UTF8.GetBytes(hello), WebSocketMessageType.Text, true, ct);

            var ackBuffer = new byte[64 * 1024];
            await socket.ReceiveAsync(ackBuffer, ct); // hello.ack

            return new FakeAgent(socket);
        }

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

        private async Task HandleControlAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            var element = JsonDocument.Parse(data).RootElement.Clone();
            var type = element.GetProperty("t").GetString();

            if (type == FrameTypes.ShellOpen)
            {
                var rid = element.GetProperty("rid").GetUInt32();
                var sid = element.GetProperty("sid").GetUInt32();
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

        private Task SendRawAsync(string json, CancellationToken ct) =>
            _socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct);
    }
}
