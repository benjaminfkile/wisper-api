using System.Net;
using System.Net.Http.Json;
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
/// Integration tests over the real app host for the consumer lease surface (docs/API.md §5): the consumer
/// gate, the required <c>Idempotency-Key</c> and its replay/conflict semantics, the 201 shape, the
/// caller-scoped GET/LIST/DELETE, and the tunnel-error envelope. The JWT validator, repositories and the
/// tunnel relay are swapped for in-memory doubles (Grunt has no Cognito/Postgres/tunnel).
/// </summary>
public class LeaseEndpointsTests
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

        public WebApplicationFactory<Program> Build() =>
            new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
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
                    // The lease HTTP surface (auth/idempotency/envelope/shape) is exercised without the
                    // ledger; a permissive gate stands in for the DB-backed WalletLeaseGate. The real
                    // hold/charge/release behaviour is covered by WalletLeaseGateTests.
                    services.RemoveAll<ILeaseWalletGate>();
                    services.AddSingleton<ILeaseWalletGate, AllowWalletGate>();
                }));

        public async Task SeedImageAsync(NetworkMode[]? networks = null, long price = 5)
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
            Image = await Images.CreateAsync(new HostImage
            {
                HostId = host.Id,
                ImageRef = "reg/wisp-base:latest",
                PriceCentsPerMin = price,
                Networks = networks ?? new[] { NetworkMode.None, NetworkMode.Open },
                MaxTtlSeconds = 14400,
                MaxCpus = 4,
                MaxMemoryMb = 8192,
                MaxPids = 1024,
                CreatedAt = T0,
                UpdatedAt = T0,
            });
        }

        public object CreateBody() => new
        {
            host_id = Host!.Id.ToString(),
            host_image_id = Image!.Id.ToString(),
            network = "open",
            ttl_seconds = 3600,
            userdata = "apt-get install -y git",
        };

        public object CreateBodyWithEnv(object env) => new
        {
            host_id = Host!.Id.ToString(),
            host_image_id = Image!.Id.ToString(),
            network = "open",
            ttl_seconds = 3600,
            userdata = "apt-get install -y git",
            env,
        };

        /// <summary>Declares the live capability (carrying the container OS) for the seeded host.</summary>
        public void SetHostOs(string? os) => Capabilities.Set(Host!.Id, new HostCapabilitySnapshot(
            Array.Empty<string>(), Array.Empty<NetworkMode>(),
            MaxTtlSeconds: 3600, MaxCpus: 4, MaxMemoryMb: 8192, MaxPids: 1024, Os: os));
    }

    private static HttpClient Authed(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", "Bearer good");
        return client;
    }

    private static HttpRequestMessage Post(object body, string? idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/leases")
        {
            Content = JsonContent.Create(body),
        };
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return request;
    }

    [Fact]
    public async Task Post_without_a_token_is_401_unauthenticated()
    {
        var fx = new Fixture();
        await fx.SeedImageAsync();
        using var factory = fx.Build();

        var response = await factory.CreateClient().SendAsync(Post(fx.CreateBody(), "key-1"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Post_creates_a_lease_and_returns_201()
    {
        var fx = new Fixture();
        await fx.SeedImageAsync(price: 5);
        using var factory = fx.Build();

        var response = await Authed(factory).SendAsync(Post(fx.CreateBody(), "key-1"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<CreateLeaseDto>();
        Assert.NotNull(created);
        Assert.StartsWith("lease_", created!.Id);
        Assert.Equal("active", created.Status);
        Assert.Equal(5, created.PriceCentsPerMin);
        Assert.Equal(300, created.HoldCents); // ceil(3600/60) * 5
        Assert.Equal(3600, created.TtlSeconds);
        Assert.Single(fx.Relay.CreateCalls);
        Assert.Single(await fx.Leases.ListByConsumerAsync((await fx.Users.GetByCognitoSubAsync("fake-sub"))!.Id));
    }

    [Fact]
    public async Task Post_carries_env_to_the_lease_create_frame_and_never_persists_it()
    {
        var fx = new Fixture();
        await fx.SeedImageAsync();
        using var factory = fx.Build();
        var body = fx.CreateBodyWithEnv(
            new Dictionary<string, string> { ["API_TOKEN"] = "top-secret-value", ["REGION"] = "eu" });

        var response = await Authed(factory).SendAsync(Post(body, "key-1"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        // env reached the tunnel frame verbatim.
        var (_, spec) = Assert.Single(fx.Relay.CreateCalls);
        Assert.Equal("top-secret-value", spec.Env!["API_TOKEN"]);

        // …but never landed on the persisted lease row (env may carry secrets).
        var userId = (await fx.Users.GetByCognitoSubAsync("fake-sub"))!.Id;
        var stored = Assert.Single(await fx.Leases.ListByConsumerAsync(userId));
        var serialized = System.Text.Json.JsonSerializer.Serialize(stored);
        Assert.DoesNotContain("top-secret-value", serialized);

        // …nor echoed in the 201 response body.
        var responseBody = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("top-secret-value", responseBody);
    }

    [Fact]
    public async Task Post_over_the_env_cap_is_400_validation_error_and_hides_the_value()
    {
        var fx = new Fixture();
        await fx.SeedImageAsync();
        using var factory = fx.Build();
        var big = new string('x', 256 * 1024 + 1);
        var body = fx.CreateBodyWithEnv(new Dictionary<string, string> { ["BLOB"] = big });

        var response = await Authed(factory).SendAsync(Post(body, "key-1"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var responseBody = await response.Content.ReadAsStringAsync();
        Assert.Contains("validation_error", responseBody);
        Assert.DoesNotContain(big, responseBody); // the secret value is never echoed
        Assert.Empty(fx.Relay.CreateCalls);
    }

    [Fact]
    public async Task Post_with_a_resources_object_is_400_validation_error()
    {
        // Resources are fixed by the offer now (task #570): a body still carrying `resources` is rejected with
        // the uniform validation_error envelope, and never reaches the tunnel.
        var fx = new Fixture();
        await fx.SeedImageAsync();
        using var factory = fx.Build();
        var body = new
        {
            host_id = fx.Host!.Id.ToString(),
            host_image_id = fx.Image!.Id.ToString(),
            network = "open",
            resources = new { cpus = 2, memory_mb = 4096 },
            ttl_seconds = 3600,
        };

        var response = await Authed(factory).SendAsync(Post(body, "key-1"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ErrorEnvelopeDto>();
        Assert.Equal("validation_error", envelope!.Error.Code);
        Assert.Empty(fx.Relay.CreateCalls);
    }

    [Fact]
    public async Task Post_with_a_gpu_count_is_400_validation_error()
    {
        var fx = new Fixture();
        await fx.SeedImageAsync();
        using var factory = fx.Build();
        var body = new
        {
            host_id = fx.Host!.Id.ToString(),
            host_image_id = fx.Image!.Id.ToString(),
            network = "open",
            ttl_seconds = 3600,
            gpus = 2,
        };

        var response = await Authed(factory).SendAsync(Post(body, "key-1"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ErrorEnvelopeDto>();
        Assert.Equal("validation_error", envelope!.Error.Code);
        Assert.Empty(fx.Relay.CreateCalls);
    }

    [Fact]
    public async Task Post_201_carries_the_hosts_container_os()
    {
        var fx = new Fixture();
        await fx.SeedImageAsync();
        using var factory = fx.Build();
        fx.SetHostOs("linux");

        var response = await Authed(factory).SendAsync(Post(fx.CreateBody(), "key-1"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<CreateLeaseDto>();
        Assert.Equal("linux", created!.Os);
    }

    [Fact]
    public async Task Post_without_an_idempotency_key_is_400_validation_error()
    {
        var fx = new Fixture();
        await fx.SeedImageAsync();
        using var factory = fx.Build();

        var response = await Authed(factory).SendAsync(Post(fx.CreateBody(), idempotencyKey: null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ErrorEnvelopeDto>();
        Assert.Equal("validation_error", envelope!.Error.Code);
        Assert.Empty(fx.Relay.CreateCalls);
    }

    [Fact]
    public async Task Post_replays_the_same_key_and_body_without_reprovisioning()
    {
        var fx = new Fixture();
        await fx.SeedImageAsync();
        using var factory = fx.Build();
        var client = Authed(factory);

        var first = await client.SendAsync(Post(fx.CreateBody(), "key-1"));
        var second = await client.SendAsync(Post(fx.CreateBody(), "key-1"));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var a = await first.Content.ReadFromJsonAsync<CreateLeaseDto>();
        var b = await second.Content.ReadFromJsonAsync<CreateLeaseDto>();
        Assert.Equal(a!.Id, b!.Id); // same stored response replayed
        Assert.Single(fx.Relay.CreateCalls); // provisioned exactly once
    }

    [Fact]
    public async Task Post_same_key_different_body_is_409_conflict()
    {
        var fx = new Fixture();
        await fx.SeedImageAsync();
        using var factory = fx.Build();
        var client = Authed(factory);

        await client.SendAsync(Post(fx.CreateBody(), "key-1"));
        var conflicting = new
        {
            host_id = fx.Host!.Id.ToString(),
            host_image_id = fx.Image!.Id.ToString(),
            network = "none",
            ttl_seconds = 3600,
        };
        var response = await client.SendAsync(Post(conflicting, "key-1"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ErrorEnvelopeDto>();
        Assert.Equal("conflict", envelope!.Error.Code);
    }

    [Fact]
    public async Task Post_unknown_image_is_400_image_not_allowed()
    {
        var fx = new Fixture();
        await fx.SeedImageAsync();
        using var factory = fx.Build();
        var body = new
        {
            host_id = fx.Host!.Id.ToString(),
            host_image_id = Guid.NewGuid().ToString(),
            network = "open",
            ttl_seconds = 3600,
        };

        var response = await Authed(factory).SendAsync(Post(body, "key-1"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ErrorEnvelopeDto>();
        Assert.Equal("image_not_allowed", envelope!.Error.Code);
    }

    [Fact]
    public async Task Post_surfaces_host_offline_and_frees_the_key_for_retry()
    {
        var fx = new Fixture();
        await fx.SeedImageAsync();
        fx.Relay.CreateError = new ApiException(ApiErrorCode.HostOffline, "no live tunnel");
        using var factory = fx.Build();
        var client = Authed(factory);

        var failed = await client.SendAsync(Post(fx.CreateBody(), "key-1"));
        Assert.Equal(HttpStatusCode.Conflict, failed.StatusCode);
        var envelope = await failed.Content.ReadFromJsonAsync<ErrorEnvelopeDto>();
        Assert.Equal("host_offline", envelope!.Error.Code);

        // The failed attempt released the lock: the SAME key can be retried and now succeeds.
        fx.Relay.CreateError = null;
        var retry = await client.SendAsync(Post(fx.CreateBody(), "key-1"));
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
    }

    [Fact]
    public async Task Post_surfaces_upstream_timeout_as_504()
    {
        var fx = new Fixture();
        await fx.SeedImageAsync();
        fx.Relay.CreateError = new ApiException(ApiErrorCode.UpstreamTimeout, "no response");
        using var factory = fx.Build();

        var response = await Authed(factory).SendAsync(Post(fx.CreateBody(), "key-1"));

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ErrorEnvelopeDto>();
        Assert.Equal("upstream_timeout", envelope!.Error.Code);
    }

    [Fact]
    public async Task Get_returns_the_lease_by_id()
    {
        var fx = new Fixture();
        await fx.SeedImageAsync();
        using var factory = fx.Build();
        var client = Authed(factory);
        var created = await (await client.SendAsync(Post(fx.CreateBody(), "key-1")))
            .Content.ReadFromJsonAsync<CreateLeaseDto>();

        var view = await client.GetFromJsonAsync<LeaseViewDto>($"/v1/leases/{created!.Id}");

        Assert.Equal(created.Id, view!.Id);
        Assert.Equal("active", view.Status);
        Assert.Equal("reg/wisp-base:latest", view.ImageRef);
        Assert.Equal("open", view.Network);
        Assert.Null(view.EndReason);
    }

    [Fact]
    public async Task Get_unknown_lease_is_404()
    {
        var fx = new Fixture();
        await fx.SeedImageAsync();
        using var factory = fx.Build();

        var response = await Authed(factory).GetAsync($"/v1/leases/lease_{Guid.NewGuid():N}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_malformed_id_is_404()
    {
        var fx = new Fixture();
        await fx.SeedImageAsync();
        using var factory = fx.Build();

        var response = await Authed(factory).GetAsync("/v1/leases/not-a-lease-id");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task List_returns_the_callers_leases()
    {
        var fx = new Fixture();
        await fx.SeedImageAsync();
        using var factory = fx.Build();
        var client = Authed(factory);
        await client.SendAsync(Post(fx.CreateBody(), "key-1"));

        var page = await client.GetFromJsonAsync<LeasePageDto>("/v1/leases");

        Assert.Single(page!.Data);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task List_rejects_an_unknown_status_filter()
    {
        var fx = new Fixture();
        await fx.SeedImageAsync();
        using var factory = fx.Build();

        var response = await Authed(factory).GetAsync("/v1/leases?status=nope");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ErrorEnvelopeDto>();
        Assert.Equal("validation_error", envelope!.Error.Code);
    }

    [Fact]
    public async Task Delete_releases_and_marks_the_lease_ended()
    {
        var fx = new Fixture();
        await fx.SeedImageAsync();
        using var factory = fx.Build();
        var client = Authed(factory);
        var created = await (await client.SendAsync(Post(fx.CreateBody(), "key-1")))
            .Content.ReadFromJsonAsync<CreateLeaseDto>();

        var response = await client.DeleteAsync($"/v1/leases/{created!.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var view = await response.Content.ReadFromJsonAsync<LeaseViewDto>();
        Assert.Equal("ended", view!.Status);
        Assert.Equal("released", view.EndReason);
        Assert.Single(fx.Relay.ReleaseCalls);
    }

    [Fact]
    public async Task Delete_is_idempotent_and_safe_to_retry()
    {
        var fx = new Fixture();
        await fx.SeedImageAsync();
        using var factory = fx.Build();
        var client = Authed(factory);
        var created = await (await client.SendAsync(Post(fx.CreateBody(), "key-1")))
            .Content.ReadFromJsonAsync<CreateLeaseDto>();

        var first = await client.DeleteAsync($"/v1/leases/{created!.Id}");
        var second = await client.DeleteAsync($"/v1/leases/{created.Id}");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Single(fx.Relay.ReleaseCalls); // the retry did not re-hit the tunnel
    }

    [Fact]
    public async Task Delete_unknown_lease_is_404()
    {
        var fx = new Fixture();
        await fx.SeedImageAsync();
        using var factory = fx.Build();

        var response = await Authed(factory).DeleteAsync($"/v1/leases/lease_{Guid.NewGuid():N}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private sealed record CreateLeaseDto(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("price_cents_per_min")] long PriceCentsPerMin,
        [property: JsonPropertyName("currency")] string Currency,
        [property: JsonPropertyName("hold_cents")] long HoldCents,
        [property: JsonPropertyName("ttl_seconds")] int TtlSeconds,
        [property: JsonPropertyName("os")] string? Os = null);

    private sealed record LeaseViewDto(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("image_ref")] string ImageRef,
        [property: JsonPropertyName("network")] string Network,
        [property: JsonPropertyName("end_reason")] string? EndReason);

    private sealed record LeasePageDto(
        [property: JsonPropertyName("data")] IReadOnlyList<LeaseViewDto> Data,
        [property: JsonPropertyName("next_cursor")] string? NextCursor);

    private sealed record ErrorEnvelopeDto(
        [property: JsonPropertyName("error")] ErrorBodyDto Error);

    private sealed record ErrorBodyDto(
        [property: JsonPropertyName("code")] string Code,
        [property: JsonPropertyName("message")] string Message);
}
