using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Wisper.Api.Tunnel;
using Xunit;

namespace Wisper.Api.Tests.Tunnel;

/// <summary>
/// Integration tests that drive the DEV lease endpoints against the real app host while a
/// TestServer WebSocket client plays the FAKE AGENT: it connects + handshakes, reads the
/// relay's request frames (<c>lease.create</c>/<c>exec.run</c>), and replies with the matching
/// response frames so the pending rid/leaseId awaiters complete (docs/TUNNEL.md §5, §11).
/// Covers create(accepted+ready)+exec(result) happy paths, lease.failed, host_offline, and
/// upstream_timeout.
/// </summary>
public class TunnelRelayTests
{
    private const string DevToken = "dev-host-token";
    private const string DevHostId = "host-alpha";

    private static WebApplicationFactory<Program> CreateFactory(int relayTimeoutMs = 10000) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting($"Tunnel:HostTokens:{DevToken}", DevHostId);
            builder.UseSetting("Tunnel:EnableDevEndpoints", "true");
            builder.UseSetting("Tunnel:RelayRequestTimeoutMs", relayTimeoutMs.ToString());
        });

    [Fact]
    public async Task Create_lease_returns_the_lease_after_accepted_and_ready()
    {
        using var factory = CreateFactory();
        var ct = Token();
        var socket = await ConnectAndHandshakeAsync(factory, ct);

        // Fake agent: read lease.create, reply lease.accepted then lease.ready.
        var responder = Task.Run(async () =>
        {
            var req = await ReadControlAsync(socket, ct);
            Assert.Equal(FrameTypes.LeaseCreate, req.GetProperty("t").GetString());
            Assert.Equal("alpine", req.GetProperty("image").GetString());
            Assert.Equal(1024, req.GetProperty("resources").GetProperty("memory_mb").GetInt32());
            Assert.Equal(3600, req.GetProperty("ttl_seconds").GetInt32());

            var rid = req.GetProperty("rid").GetUInt32();
            var leaseId = req.GetProperty("leaseId").GetString();
            Assert.False(string.IsNullOrEmpty(leaseId));

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
        Assert.False(string.IsNullOrEmpty(json.GetProperty("leaseId").GetString()));
        Assert.Equal("wc_123", json.GetProperty("wispContractId").GetString());
        Assert.Equal("provisioning", json.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Create_lease_forwards_env_into_the_outgoing_lease_create_frame()
    {
        using var factory = CreateFactory();
        var ct = Token();
        var socket = await ConnectAndHandshakeAsync(factory, ct);

        // Fake agent asserts the opaque env map arrived on the lease.create frame, then answers.
        var responder = Task.Run(async () =>
        {
            var req = await ReadControlAsync(socket, ct);
            Assert.Equal(FrameTypes.LeaseCreate, req.GetProperty("t").GetString());
            var env = req.GetProperty("env");
            Assert.Equal("bar", env.GetProperty("FOO").GetString());
            Assert.Equal("s3cr3t", env.GetProperty("TOKEN").GetString());

            var rid = req.GetProperty("rid").GetUInt32();
            var leaseId = req.GetProperty("leaseId").GetString();
            await SendRawAsync(socket,
                $"{{\"t\":\"lease.accepted\",\"rid\":{rid},\"leaseId\":\"{leaseId}\"," +
                "\"wispContractId\":\"wc_env\",\"status\":\"provisioning\"}", ct);
            await SendRawAsync(socket, $"{{\"t\":\"lease.ready\",\"leaseId\":\"{leaseId}\"}}", ct);
        }, ct);

        var body = "{\"hostId\":\"" + DevHostId + "\",\"image\":\"alpine\",\"ttl_seconds\":3600," +
                   "\"env\":{\"FOO\":\"bar\",\"TOKEN\":\"s3cr3t\"}}";
        var response = await factory.CreateClient().PostAsync("/dev/leases", JsonContent(body), ct);
        await responder;

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await ReadJsonAsync(response, ct);
        Assert.Equal("wc_env", json.GetProperty("wispContractId").GetString());
    }

    [Fact]
    public async Task Create_lease_without_env_omits_it_from_the_outgoing_frame()
    {
        using var factory = CreateFactory();
        var ct = Token();
        var socket = await ConnectAndHandshakeAsync(factory, ct);

        var responder = Task.Run(async () =>
        {
            var req = await ReadControlAsync(socket, ct);
            Assert.Equal(FrameTypes.LeaseCreate, req.GetProperty("t").GetString());
            Assert.False(req.TryGetProperty("env", out _));

            var rid = req.GetProperty("rid").GetUInt32();
            var leaseId = req.GetProperty("leaseId").GetString();
            await SendRawAsync(socket,
                $"{{\"t\":\"lease.accepted\",\"rid\":{rid},\"leaseId\":\"{leaseId}\"," +
                "\"wispContractId\":\"wc_noenv\",\"status\":\"provisioning\"}", ct);
            await SendRawAsync(socket, $"{{\"t\":\"lease.ready\",\"leaseId\":\"{leaseId}\"}}", ct);
        }, ct);

        var body = "{\"hostId\":\"" + DevHostId + "\",\"image\":\"alpine\",\"ttl_seconds\":3600}";
        var response = await factory.CreateClient().PostAsync("/dev/leases", JsonContent(body), ct);
        await responder;

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Exec_returns_stdout_stderr_and_exit_code()
    {
        using var factory = CreateFactory();
        var ct = Token();
        var socket = await ConnectAndHandshakeAsync(factory, ct);

        var responder = Task.Run(async () =>
        {
            var req = await ReadControlAsync(socket, ct);
            Assert.Equal(FrameTypes.ExecRun, req.GetProperty("t").GetString());
            Assert.Equal("lease_abc", req.GetProperty("leaseId").GetString());
            Assert.Equal("echo hi", req.GetProperty("command").GetString());

            var rid = req.GetProperty("rid").GetUInt32();
            await SendRawAsync(socket,
                $"{{\"t\":\"exec.result\",\"rid\":{rid},\"stdout\":\"hi\\n\",\"stderr\":\"\",\"exit_code\":0}}", ct);
        }, ct);

        var body = "{\"hostId\":\"" + DevHostId + "\",\"command\":\"echo hi\"}";
        var response = await factory.CreateClient().PostAsync("/dev/leases/lease_abc/exec", JsonContent(body), ct);
        await responder;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await ReadJsonAsync(response, ct);
        Assert.Equal("hi\n", json.GetProperty("stdout").GetString());
        Assert.Equal("", json.GetProperty("stderr").GetString());
        Assert.Equal(0, json.GetProperty("exit_code").GetInt32());
    }

    [Fact]
    public async Task Release_completes_after_lease_released()
    {
        using var factory = CreateFactory();
        var ct = Token();
        var socket = await ConnectAndHandshakeAsync(factory, ct);

        var responder = Task.Run(async () =>
        {
            var req = await ReadControlAsync(socket, ct);
            Assert.Equal(FrameTypes.LeaseRelease, req.GetProperty("t").GetString());
            var rid = req.GetProperty("rid").GetUInt32();
            var leaseId = req.GetProperty("leaseId").GetString();
            await SendRawAsync(socket,
                $"{{\"t\":\"lease.released\",\"rid\":{rid},\"leaseId\":\"{leaseId}\"}}", ct);
        }, ct);

        var response = await factory.CreateClient()
            .DeleteAsync($"/dev/leases/lease_xyz?hostId={DevHostId}", ct);
        await responder;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Lease_failed_surfaces_as_an_error()
    {
        using var factory = CreateFactory();
        var ct = Token();
        var socket = await ConnectAndHandshakeAsync(factory, ct);

        var responder = Task.Run(async () =>
        {
            var req = await ReadControlAsync(socket, ct);
            Assert.Equal(FrameTypes.LeaseCreate, req.GetProperty("t").GetString());
            var rid = req.GetProperty("rid").GetUInt32();
            var leaseId = req.GetProperty("leaseId").GetString();
            await SendRawAsync(socket,
                $"{{\"t\":\"lease.failed\",\"rid\":{rid},\"leaseId\":\"{leaseId}\"," +
                "\"error\":\"image pull failed\"}", ct);
        }, ct);

        var body = "{\"hostId\":\"" + DevHostId + "\",\"image\":\"nope\",\"ttl_seconds\":60}";
        var response = await factory.CreateClient().PostAsync("/dev/leases", JsonContent(body), ct);
        await responder;

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var json = await ReadJsonAsync(response, ct);
        Assert.Equal("lease_failed", json.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Host_error_for_a_request_fails_fast_instead_of_timing_out()
    {
        // A long relay deadline: if the error frame were dropped (the pre-hardening default log-and-ignore
        // branch) the consumer would hang for the full window. It must instead fail immediately with the
        // host-reported typed error (docs/TUNNEL.md §5, §12).
        using var factory = CreateFactory(relayTimeoutMs: 60000);
        var ct = Token();
        var socket = await ConnectAndHandshakeAsync(factory, ct);

        var responder = Task.Run(async () =>
        {
            var req = await ReadControlAsync(socket, ct);
            Assert.Equal(FrameTypes.LeaseCreate, req.GetProperty("t").GetString());
            var rid = req.GetProperty("rid").GetUInt32();
            await SendRawAsync(socket,
                $"{{\"t\":\"error\",\"rid\":{rid},\"code\":\"wisp_error\",\"message\":\"wisp returned 500\"}}", ct);
        }, ct);

        var body = "{\"hostId\":\"" + DevHostId + "\",\"image\":\"alpine\",\"ttl_seconds\":60}";
        var response = await factory.CreateClient().PostAsync("/dev/leases", JsonContent(body), ct);
        await responder;

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var json = await ReadJsonAsync(response, ct);
        Assert.Equal("lease_failed", json.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("wisp returned 500", json.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task Host_error_not_ready_maps_to_the_typed_lease_not_ready_code()
    {
        using var factory = CreateFactory(relayTimeoutMs: 60000);
        var ct = Token();
        var socket = await ConnectAndHandshakeAsync(factory, ct);

        var responder = Task.Run(async () =>
        {
            var req = await ReadControlAsync(socket, ct);
            Assert.Equal(FrameTypes.ExecRun, req.GetProperty("t").GetString());
            var rid = req.GetProperty("rid").GetUInt32();
            await SendRawAsync(socket,
                $"{{\"t\":\"error\",\"rid\":{rid},\"code\":\"not_ready\",\"message\":\"lease not ready\"}}", ct);
        }, ct);

        var body = "{\"hostId\":\"" + DevHostId + "\",\"command\":\"echo hi\"}";
        var response = await factory.CreateClient().PostAsync("/dev/leases/lease_abc/exec", JsonContent(body), ct);
        await responder;

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var json = await ReadJsonAsync(response, ct);
        Assert.Equal("lease_not_ready", json.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Host_error_at_capacity_on_create_maps_to_the_typed_at_capacity_code()
    {
        // Race window (task #571): the agent forwards wisp's 409 as an at_capacity error for a lease.create.
        // The relay must map it to the typed at_capacity (409) API error, not the generic lease_failed (502).
        using var factory = CreateFactory(relayTimeoutMs: 60000);
        var ct = Token();
        var socket = await ConnectAndHandshakeAsync(factory, ct);

        var responder = Task.Run(async () =>
        {
            var req = await ReadControlAsync(socket, ct);
            Assert.Equal(FrameTypes.LeaseCreate, req.GetProperty("t").GetString());
            var rid = req.GetProperty("rid").GetUInt32();
            await SendRawAsync(socket,
                $"{{\"t\":\"error\",\"rid\":{rid},\"code\":\"at_capacity\",\"message\":\"host at capacity\"}}", ct);
        }, ct);

        var body = "{\"hostId\":\"" + DevHostId + "\",\"image\":\"alpine\",\"network\":\"none\"," +
                   "\"resources\":{\"cpus\":1,\"memory_mb\":512,\"pids\":128},\"ttl_seconds\":3600}";
        var response = await factory.CreateClient().PostAsync("/dev/leases", JsonContent(body), ct);
        await responder;

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var json = await ReadJsonAsync(response, ct);
        Assert.Equal("at_capacity", json.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Create_right_after_connect_waits_for_readiness_instead_of_racing_to_host_offline()
    {
        using var factory = CreateFactory();
        var ct = Token();

        // Issue the create BEFORE the agent has finished connecting/handshaking. The relay must wait
        // briefly for the host to become ready (registered + hello.ack sent, docs/TUNNEL.md §3) rather
        // than immediately returning host_offline on a freshly-connecting host.
        var body = "{\"hostId\":\"" + DevHostId + "\",\"image\":\"alpine\",\"network\":\"none\"," +
                   "\"resources\":{\"cpus\":2,\"memory_mb\":1024,\"pids\":256},\"ttl_seconds\":3600}";
        var createTask = factory.CreateClient().PostAsync("/dev/leases", JsonContent(body), ct);

        // Give the create a beat to reach the relay and start waiting on readiness, then bring the
        // agent up and answer the lease.create the relay sends once the host is ready.
        await Task.Delay(100, ct);
        var socket = await ConnectAndHandshakeAsync(factory, ct);

        var responder = Task.Run(async () =>
        {
            var req = await ReadControlAsync(socket, ct);
            Assert.Equal(FrameTypes.LeaseCreate, req.GetProperty("t").GetString());
            var rid = req.GetProperty("rid").GetUInt32();
            var leaseId = req.GetProperty("leaseId").GetString();
            await SendRawAsync(socket,
                $"{{\"t\":\"lease.accepted\",\"rid\":{rid},\"leaseId\":\"{leaseId}\"," +
                "\"wispContractId\":\"wc_race\",\"status\":\"provisioning\"}", ct);
            await SendRawAsync(socket, $"{{\"t\":\"lease.ready\",\"leaseId\":\"{leaseId}\"}}", ct);
        }, ct);

        var response = await createTask;
        await responder;

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await ReadJsonAsync(response, ct);
        Assert.Equal("wc_race", json.GetProperty("wispContractId").GetString());
    }

    [Fact]
    public async Task Unknown_host_is_host_offline()
    {
        using var factory = CreateFactory();
        var ct = Token();

        // No tunnel connected for this host id.
        var body = "{\"hostId\":\"ghost-host\",\"image\":\"alpine\",\"ttl_seconds\":60}";
        var response = await factory.CreateClient().PostAsync("/dev/leases", JsonContent(body), ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var json = await ReadJsonAsync(response, ct);
        Assert.Equal("host_offline", json.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Lease_ended_before_ready_fails_the_pending_create()
    {
        // AC188: the existing create-waiter completion behavior must survive the task #56 rewiring -- a
        // host that reaps the container before sending lease.ready, and reports lease.ended instead, must
        // fail the create call fast (with the reason surfaced in the message) rather than let it hang until
        // the relay deadline. Same behavior as before, just routed through the async lease-ended path.
        using var factory = CreateFactory(relayTimeoutMs: 60000);
        var ct = Token();
        var socket = await ConnectAndHandshakeAsync(factory, ct);

        // Fake agent: accept the create, then instead of lease.ready send an unsolicited lease.ended.
        var responder = Task.Run(async () =>
        {
            var req = await ReadControlAsync(socket, ct);
            Assert.Equal(FrameTypes.LeaseCreate, req.GetProperty("t").GetString());
            var rid = req.GetProperty("rid").GetUInt32();
            var leaseId = req.GetProperty("leaseId").GetString();

            await SendRawAsync(socket,
                $"{{\"t\":\"lease.accepted\",\"rid\":{rid},\"leaseId\":\"{leaseId}\"," +
                "\"wispContractId\":\"wc_reaped\",\"status\":\"provisioning\"}", ct);
            await SendRawAsync(socket,
                $"{{\"t\":\"lease.ended\",\"leaseId\":\"{leaseId}\",\"reason\":\"failed\"}}", ct);
        }, ct);

        var body = "{\"hostId\":\"" + DevHostId + "\",\"image\":\"alpine\",\"ttl_seconds\":60}";
        var response = await factory.CreateClient().PostAsync("/dev/leases", JsonContent(body), ct);
        await responder;

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var json = await ReadJsonAsync(response, ct);
        Assert.Equal("lease_failed", json.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains("lease ended before ready", json.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task No_response_is_upstream_timeout()
    {
        using var factory = CreateFactory(relayTimeoutMs: 400);
        var ct = Token();
        var socket = await ConnectAndHandshakeAsync(factory, ct);

        // Fake agent reads the request but never replies -- the relay must time out.
        var responder = Task.Run(async () =>
        {
            var req = await ReadControlAsync(socket, ct);
            Assert.Equal(FrameTypes.LeaseCreate, req.GetProperty("t").GetString());
        }, ct);

        var body = "{\"hostId\":\"" + DevHostId + "\",\"image\":\"alpine\",\"ttl_seconds\":60}";
        var response = await factory.CreateClient().PostAsync("/dev/leases", JsonContent(body), ct);
        await responder;

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        var json = await ReadJsonAsync(response, ct);
        Assert.Equal("upstream_timeout", json.GetProperty("error").GetProperty("code").GetString());
    }

    // ---- helpers ----

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

    private static StringContent JsonContent(string json) =>
        new(json, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    // A per-test deadline so a hung relay fails the test instead of hanging the run.
    private static CancellationToken Token() => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;
}
