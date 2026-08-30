using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Wisper.Api.Infrastructure;
using Wisper.Api.Tests.TestSupport;
using Wisper.Api.Tunnel;
using Wisper.Api.Tunnel.Messages;
using Xunit;

namespace Wisper.Api.Tests.Tunnel;

/// <summary>
/// Dev-harness parity for lease files (task #229): the same optional <c>files</c> array on
/// <c>POST /dev/leases</c> and the same <c>GET /dev/leases/:id/files?path=</c> download so dev-mode
/// consumers can exercise the /v1 shape without accounts/billing. Uses <see cref="FakeTunnelRelay"/>
/// so no live agent is needed; validation and error mapping cover the same caps the /v1 surface does.
/// </summary>
public class DevLeaseFilesTests
{
    private const string DevHostId = "dev-host-1";

    private static WebApplicationFactory<Program> CreateFactory(FakeTunnelRelay relay, long? maxDownloadBytes = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Tunnel:EnableDevEndpoints", "true");
            if (maxDownloadBytes is { } cap)
            {
                builder.UseSetting("Leases:MaxDownloadBytes", cap.ToString());
            }

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ITunnelRelay>();
                services.AddSingleton<ITunnelRelay>(relay);
            });
        });

    [Fact]
    public async Task Post_dev_leases_forwards_files_to_the_lease_create_frame()
    {
        var relay = new FakeTunnelRelay();
        using var factory = CreateFactory(relay);

        var content = Convert.ToBase64String(Encoding.UTF8.GetBytes("hello"));
        var response = await factory.CreateClient().PostAsJsonAsync("/dev/leases", new
        {
            hostId = DevHostId,
            image = "alpine",
            ttl_seconds = 3600,
            files = new[] { new { path = "/etc/hello.txt", content_base64 = content } },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var (_, spec) = Assert.Single(relay.CreateCalls);
        Assert.NotNull(spec.Files);
        var single = Assert.Single(spec.Files!);
        Assert.Equal("/etc/hello.txt", single.Path);
        Assert.Equal(content, single.ContentBase64);
    }

    [Fact]
    public async Task Post_dev_leases_rejects_over_the_file_count_cap()
    {
        var relay = new FakeTunnelRelay();
        using var factory = CreateFactory(relay);

        var files = Enumerable.Range(0, 17)
            .Select(i => new { path = $"/f{i}", content_base64 = "" }).ToArray();
        var response = await factory.CreateClient().PostAsJsonAsync("/dev/leases", new
        {
            hostId = DevHostId,
            image = "alpine",
            ttl_seconds = 3600,
            files,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("validation_error", body);
        Assert.Empty(relay.CreateCalls);
    }

    [Fact]
    public async Task Post_dev_leases_rejects_bad_path_shape()
    {
        var relay = new FakeTunnelRelay();
        using var factory = CreateFactory(relay);

        var response = await factory.CreateClient().PostAsJsonAsync("/dev/leases", new
        {
            hostId = DevHostId,
            image = "alpine",
            ttl_seconds = 3600,
            files = new[] { new { path = "/../etc/hidden", content_base64 = "" } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(relay.CreateCalls);
    }

    [Fact]
    public async Task Get_dev_leases_files_streams_body_and_sets_content_length()
    {
        var relay = new FakeTunnelRelay();
        var payload = Encoding.UTF8.GetBytes("hello dev");
        relay.FileRead = new FakeTunnelFileDownload(new[] { payload }, size: payload.Length);
        using var factory = CreateFactory(relay);

        var response = await factory.CreateClient()
            .GetAsync($"/dev/leases/lease_abc/files?hostId={DevHostId}&path=/etc/hello");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(payload.Length, response.Content.Headers.ContentLength);
        Assert.Equal(payload, await response.Content.ReadAsByteArrayAsync());

        var (hostId, leaseId, path) = Assert.Single(relay.FileReadCalls);
        Assert.Equal(DevHostId, hostId);
        Assert.Equal("lease_abc", leaseId);
        Assert.Equal("/etc/hello", path);
    }

    [Fact]
    public async Task Get_dev_leases_files_missing_hostid_is_400()
    {
        var relay = new FakeTunnelRelay();
        using var factory = CreateFactory(relay);

        var response = await factory.CreateClient()
            .GetAsync("/dev/leases/lease_abc/files?path=/etc/hello");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Get_dev_leases_files_file_too_large_from_agent_is_413()
    {
        var relay = new FakeTunnelRelay();
        relay.FileReadError = new ApiException(ApiErrorCode.FileTooLarge, "agent reported cap");
        using var factory = CreateFactory(relay);

        var response = await factory.CreateClient()
            .GetAsync($"/dev/leases/lease_abc/files?hostId={DevHostId}&path=/etc/big");

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("file_too_large", body);
    }

    [Fact]
    public async Task Get_dev_leases_files_agent_not_found_is_404()
    {
        var relay = new FakeTunnelRelay();
        relay.FileReadError = new ApiException(ApiErrorCode.NotFound, "no such file");
        using var factory = CreateFactory(relay);

        var response = await factory.CreateClient()
            .GetAsync($"/dev/leases/lease_abc/files?hostId={DevHostId}&path=/etc/missing");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_dev_leases_files_manager_cap_returns_413_before_body()
    {
        var relay = new FakeTunnelRelay();
        relay.FileRead = new FakeTunnelFileDownload(new[] { new byte[64] }, size: 64);
        using var factory = CreateFactory(relay, maxDownloadBytes: 10);

        var response = await factory.CreateClient()
            .GetAsync($"/dev/leases/lease_abc/files?hostId={DevHostId}&path=/etc/big");

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }
}
