using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wisper.Api.Auth;
using Wisper.Api.Domain;
using Wisper.Api.Infrastructure;
using Wisper.Api.Leases;
using Wisper.Api.Persistence.HostImages;
using Wisper.Api.Persistence.Hosts;
using Wisper.Api.Persistence.Idempotency;
using Wisper.Api.Persistence.Leases;
using Wisper.Api.Persistence.Users;
using Wisper.Api.Tests.TestSupport;
using Wisper.Api.Tunnel;
using Xunit;
using Host = Wisper.Api.Domain.Host;

namespace Wisper.Api.Tests.Leases;

/// <summary>
/// Integration tests for <c>GET /v1/leases/:id/files?path=</c> (docs/API.md §5): owner-only auth, active
/// lease required (409 <c>lease_not_ready</c> otherwise), <c>application/octet-stream</c> body with
/// <c>Content-Length</c> set when known, and each of the pinned error mappings against the fake tunnel
/// relay (host_offline, upstream_timeout, not_found, file_too_large). Post-cap 413 is exercised via the
/// manager-side <c>MaxDownloadBytes</c> cap.
/// </summary>
public class LeaseFileEndpointsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 12, 0, 0, 0, TimeSpan.Zero);

    private sealed class Fixture
    {
        public InMemoryLeaseRepository Leases { get; } = new();
        public InMemoryHostRepository Hosts { get; } = new();
        public InMemoryHostImageRepository Images { get; } = new();
        public InMemoryUserRepository Users { get; } = new();
        public InMemoryIdempotencyKeyRepository Idempotency { get; } = new();
        public FakeTunnelRelay Relay { get; } = new();
        public FakeHostCapabilitySource Capabilities { get; } = new();
        public FakeJwtValidator Validator { get; } = new();

        public Host? Host { get; private set; }
        public HostImage? Image { get; private set; }

        public WebApplicationFactory<Program> Build(long? maxDownloadBytes = null) =>
            new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                if (maxDownloadBytes is { } cap)
                {
                    builder.UseSetting("Leases:MaxDownloadBytes", cap.ToString());
                }

                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IJwtValidator>();
                    services.AddSingleton<IJwtValidator>(Validator);
                    services.RemoveAll<ILeaseRepository>();
                    services.AddSingleton<ILeaseRepository>(Leases);
                    services.RemoveAll<IHostRepository>();
                    services.AddSingleton<IHostRepository>(Hosts);
                    services.RemoveAll<IHostImageRepository>();
                    services.AddSingleton<IHostImageRepository>(Images);
                    services.RemoveAll<IUserRepository>();
                    services.AddSingleton<IUserRepository>(Users);
                    services.RemoveAll<IIdempotencyKeyRepository>();
                    services.AddSingleton<IIdempotencyKeyRepository>(Idempotency);
                    services.RemoveAll<ITunnelRelay>();
                    services.AddSingleton<ITunnelRelay>(Relay);
                    services.RemoveAll<IHostCapabilitySource>();
                    services.AddSingleton<IHostCapabilitySource>(Capabilities);
                    services.RemoveAll<ILeaseWalletGate>();
                    services.AddSingleton<ILeaseWalletGate, AllowWalletGate>();
                });
            });

        public async Task<Lease> SeedActiveLeaseAsync()
        {
            var host = await Hosts.CreateAsync(new Host
            {
                Id = Guid.NewGuid(),
                OwnerUserId = Guid.NewGuid(),
                Name = "home-server-1",
                Label = "us",
                Status = HostStatus.Online,
                AgentTokenHash = "hash",
                CreatedAt = T0,
                UpdatedAt = T0,
            });
            Host = host;
            var image = await Images.CreateAsync(new HostImage
            {
                HostId = host.Id,
                ImageRef = "reg/wisp-base:latest",
                PriceCentsPerMin = 5,
                Networks = new[] { NetworkMode.None, NetworkMode.Open },
                MaxTtlSeconds = 14400,
                MaxCpus = 4,
                MaxMemoryMb = 8192,
                MaxPids = 1024,
                CreatedAt = T0,
                UpdatedAt = T0,
            });
            Image = image;

            // Bootstrap the caller so the lease has an owner matching FakeJwtValidator's fake-sub.
            var user = await Users.CreateAsync(new Wisper.Api.Domain.User
            {
                Id = Guid.NewGuid(),
                CognitoSub = "fake-sub",
                Email = "caller@example.test",
                Status = UserStatus.Active,
                CreatedAt = T0,
                UpdatedAt = T0,
            });

            return await Leases.CreateAsync(new Lease
            {
                Id = Guid.NewGuid(),
                ConsumerUserId = user.Id,
                HostId = host.Id,
                HostImageId = image.Id,
                ImageRef = image.ImageRef,
                Network = NetworkMode.Open,
                TtlSeconds = 3600,
                PriceCentsPerMin = 5,
                Currency = "usd",
                Status = LeaseStatus.Active,
                WispContractId = "wisp-contract-1",
                CreatedAt = T0,
                StartedAt = T0,
                LastMeteredAt = T0,
            });
        }
    }

    private static HttpClient Authed(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", "Bearer good");
        return client;
    }

    [Fact]
    public async Task Get_streams_the_file_bytes_with_content_length_set()
    {
        var fx = new Fixture();
        var lease = await fx.SeedActiveLeaseAsync();
        using var factory = fx.Build();
        var payload = Encoding.UTF8.GetBytes("hello world");
        fx.Relay.FileRead = new FakeTunnelFileDownload(new[] { payload }, size: payload.Length);

        var response = await Authed(factory)
            .GetAsync($"/v1/leases/{TunnelLeaseId.Format(lease.Id)}/files?path=/etc/hello");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(payload.Length, response.Content.Headers.ContentLength);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(payload, bytes);

        // Relay routed to the resolved lease target, and the download was drained then torn down.
        var (hostId, leaseId, path) = Assert.Single(fx.Relay.FileReadCalls);
        Assert.Equal(fx.Host!.Id.ToString(), hostId);
        Assert.Equal(TunnelLeaseId.Format(lease.Id), leaseId);
        Assert.Equal("/etc/hello", path);
        Assert.Equal(payload.Length, fx.Relay.FileRead.DrainedBytes);
        Assert.True(fx.Relay.FileRead.Closed);
    }

    [Fact]
    public async Task Get_without_a_token_is_401()
    {
        var fx = new Fixture();
        var lease = await fx.SeedActiveLeaseAsync();
        using var factory = fx.Build();

        var response = await factory.CreateClient()
            .GetAsync($"/v1/leases/{TunnelLeaseId.Format(lease.Id)}/files?path=/etc/hello");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Get_for_another_users_lease_is_404()
    {
        // The lease exists but is owned by a different user; ownership failures return 404 (docs/API.md §3).
        var fx = new Fixture();
        // Do NOT seed via SeedActiveLeaseAsync (which creates the caller's user + lease); instead build a
        // lease owned by a stranger and let the caller bootstrap themselves via the JWT path.
        var host = await fx.Hosts.CreateAsync(new Host
        {
            Id = Guid.NewGuid(),
            OwnerUserId = Guid.NewGuid(),
            Name = "home-server-1",
            Label = "us",
            Status = HostStatus.Online,
            AgentTokenHash = "hash",
            CreatedAt = T0,
            UpdatedAt = T0,
        });
        var image = await fx.Images.CreateAsync(new HostImage
        {
            HostId = host.Id,
            ImageRef = "reg/wisp-base:latest",
            PriceCentsPerMin = 5,
            Networks = new[] { NetworkMode.None, NetworkMode.Open },
            MaxTtlSeconds = 14400,
            MaxCpus = 4,
            MaxMemoryMb = 8192,
            MaxPids = 1024,
            CreatedAt = T0,
            UpdatedAt = T0,
        });
        var stranger = Guid.NewGuid();
        var lease = await fx.Leases.CreateAsync(new Lease
        {
            Id = Guid.NewGuid(),
            ConsumerUserId = stranger,
            HostId = host.Id,
            HostImageId = image.Id,
            ImageRef = image.ImageRef,
            Network = NetworkMode.Open,
            TtlSeconds = 3600,
            PriceCentsPerMin = 5,
            Currency = "usd",
            Status = LeaseStatus.Active,
            WispContractId = "wisp-contract-1",
            CreatedAt = T0,
            StartedAt = T0,
            LastMeteredAt = T0,
        });
        using var factory = fx.Build();

        var response = await Authed(factory)
            .GetAsync($"/v1/leases/{TunnelLeaseId.Format(lease.Id)}/files?path=/etc/hello");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_when_lease_not_active_is_409_lease_not_ready()
    {
        var fx = new Fixture();
        var lease = await fx.SeedActiveLeaseAsync();
        // Flip the lease off the active state.
        await fx.Leases.UpdateAsync(lease with { Status = LeaseStatus.Suspended });
        using var factory = fx.Build();

        var response = await Authed(factory)
            .GetAsync($"/v1/leases/{TunnelLeaseId.Format(lease.Id)}/files?path=/etc/hello");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ErrorEnvelopeDto>();
        Assert.Equal("lease_not_ready", envelope!.Error.Code);
    }

    [Fact]
    public async Task Get_missing_path_query_is_400_validation_error()
    {
        var fx = new Fixture();
        var lease = await fx.SeedActiveLeaseAsync();
        using var factory = fx.Build();

        var response = await Authed(factory)
            .GetAsync($"/v1/leases/{TunnelLeaseId.Format(lease.Id)}/files");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ErrorEnvelopeDto>();
        Assert.Equal("validation_error", envelope!.Error.Code);
    }

    [Theory]
    [InlineData("relative/x")]
    [InlineData("/a/../b")]
    [InlineData("/a\\b")]
    public async Task Get_invalid_path_shape_is_400_validation_error(string path)
    {
        var fx = new Fixture();
        var lease = await fx.SeedActiveLeaseAsync();
        using var factory = fx.Build();

        var response = await Authed(factory)
            .GetAsync($"/v1/leases/{TunnelLeaseId.Format(lease.Id)}/files?path={Uri.EscapeDataString(path)}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ErrorEnvelopeDto>();
        Assert.Equal("validation_error", envelope!.Error.Code);
    }

    [Fact]
    public async Task Get_agent_not_found_is_404()
    {
        var fx = new Fixture();
        var lease = await fx.SeedActiveLeaseAsync();
        using var factory = fx.Build();
        fx.Relay.FileReadError = new ApiException(ApiErrorCode.NotFound, "no such file");

        var response = await Authed(factory)
            .GetAsync($"/v1/leases/{TunnelLeaseId.Format(lease.Id)}/files?path=/etc/hello");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ErrorEnvelopeDto>();
        Assert.Equal("not_found", envelope!.Error.Code);
    }

    [Fact]
    public async Task Get_agent_file_too_large_is_413()
    {
        var fx = new Fixture();
        var lease = await fx.SeedActiveLeaseAsync();
        using var factory = fx.Build();
        fx.Relay.FileReadError = new ApiException(ApiErrorCode.FileTooLarge, "wisp reported cap");

        var response = await Authed(factory)
            .GetAsync($"/v1/leases/{TunnelLeaseId.Format(lease.Id)}/files?path=/etc/hello");

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ErrorEnvelopeDto>();
        Assert.Equal("file_too_large", envelope!.Error.Code);
    }

    [Fact]
    public async Task Get_manager_cap_returns_413_before_any_body_is_written()
    {
        var fx = new Fixture();
        var lease = await fx.SeedActiveLeaseAsync();
        using var factory = fx.Build(maxDownloadBytes: 10);
        // Agent reports a size larger than the cap: pre-body reject with 413.
        fx.Relay.FileRead = new FakeTunnelFileDownload(new[] { new byte[64] }, size: 64);

        var response = await Authed(factory)
            .GetAsync($"/v1/leases/{TunnelLeaseId.Format(lease.Id)}/files?path=/etc/hello");

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ErrorEnvelopeDto>();
        Assert.Equal("file_too_large", envelope!.Error.Code);
        Assert.True(fx.Relay.FileRead.Closed);
    }

    [Fact]
    public async Task Get_host_offline_is_409()
    {
        var fx = new Fixture();
        var lease = await fx.SeedActiveLeaseAsync();
        using var factory = fx.Build();
        fx.Relay.FileReadError = new ApiException(ApiErrorCode.HostOffline, "no live tunnel");

        var response = await Authed(factory)
            .GetAsync($"/v1/leases/{TunnelLeaseId.Format(lease.Id)}/files?path=/etc/hello");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ErrorEnvelopeDto>();
        Assert.Equal("host_offline", envelope!.Error.Code);
    }

    [Fact]
    public async Task Get_upstream_timeout_is_504()
    {
        var fx = new Fixture();
        var lease = await fx.SeedActiveLeaseAsync();
        using var factory = fx.Build();
        fx.Relay.FileReadError = new ApiException(ApiErrorCode.UpstreamTimeout, "no response");

        var response = await Authed(factory)
            .GetAsync($"/v1/leases/{TunnelLeaseId.Format(lease.Id)}/files?path=/etc/hello");

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ErrorEnvelopeDto>();
        Assert.Equal("upstream_timeout", envelope!.Error.Code);
    }

    private sealed record ErrorEnvelopeDto(
        [property: JsonPropertyName("error")] ErrorBodyDto Error);

    private sealed record ErrorBodyDto(
        [property: JsonPropertyName("code")] string Code,
        [property: JsonPropertyName("message")] string Message);
}
