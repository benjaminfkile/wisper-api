using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wisper.Api.Auth;
using Wisper.Api.Domain;
using Wisper.Api.Persistence.HostImages;
using Wisper.Api.Persistence.Hosts;
using Wisper.Api.Tests.TestSupport;
using Wisper.Api.Tunnel;
using Xunit;
using Host = Wisper.Api.Domain.Host;

namespace Wisper.Api.Tests.Catalog;

/// <summary>
/// Integration tests over the real app host for the consumer catalog surface (docs/API.md §5):
/// the consumer gate, query-param validation, cursor pagination on <c>GET /v1/catalog</c>, and the
/// <c>GET /v1/hosts/:id</c> public detail (including the 404-on-invisible rule, §3). The JWT validator,
/// host/image repositories, and tunnel registry are swapped for in-memory doubles (Grunt has no
/// Cognito/Postgres/tunnel).
/// </summary>
public class CatalogEndpointsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed class Fixture
    {
        public InMemoryHostRepository Hosts { get; } = new();
        public InMemoryHostImageRepository Images { get; } = new();
        public FakeHostRegistry Registry { get; } = new();
        public FakeJwtValidator Validator { get; } = new();

        public WebApplicationFactory<Program> Build() =>
            new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IJwtValidator>();
                    services.AddSingleton<IJwtValidator>(Validator);
                    services.RemoveAll<IHostRepository>();
                    services.AddSingleton<IHostRepository>(Hosts);
                    services.RemoveAll<IHostImageRepository>();
                    services.AddSingleton<IHostImageRepository>(Images);
                    services.RemoveAll<IHostRegistry>();
                    services.AddSingleton<IHostRegistry>(Registry);
                }));

        public async Task<Host> SeedOnlineHostAsync(
            string name, string image, long price,
            IReadOnlyList<string>? gpuClasses = null, int gpuCount = 0, int maxGpus = 0)
        {
            var host = await Hosts.CreateAsync(new Host
            {
                Id = Guid.NewGuid(),
                OwnerUserId = Guid.NewGuid(),
                Name = name,
                Label = "us",
                Status = HostStatus.Online,
                AgentTokenHash = "hash",
                GpuClasses = gpuClasses ?? HostGpu.NoClasses,
                GpuCount = gpuCount,
                CreatedAt = T0,
                UpdatedAt = T0,
            });
            Registry.SetOnline(host.Id);
            await Images.CreateAsync(new HostImage
            {
                HostId = host.Id,
                ImageRef = image,
                PriceCentsPerMin = price,
                Networks = new[] { NetworkMode.None, NetworkMode.Open },
                MaxTtlSeconds = 3600,
                MaxCpus = 4,
                MaxMemoryMb = 8192,
                MaxPids = 1024,
                MaxGpus = maxGpus,
                CreatedAt = T0,
                UpdatedAt = T0,
            });
            return host;
        }
    }

    private static HttpClient Authed(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", "Bearer good");
        return client;
    }

    [Fact]
    public async Task Catalog_without_a_token_is_401_unauthenticated()
    {
        var fx = new Fixture();
        using var factory = fx.Build();

        var response = await factory.CreateClient().GetAsync("/v1/catalog");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ErrorEnvelopeDto>();
        Assert.Equal("unauthenticated", envelope!.Error.Code);
    }

    [Fact]
    public async Task Catalog_lists_online_hosts_and_their_images()
    {
        var fx = new Fixture();
        await fx.SeedOnlineHostAsync("home-server-1", "reg/wisp-base:latest", price: 5);
        using var factory = fx.Build();

        var page = await Authed(factory).GetFromJsonAsync<CatalogPageDto>("/v1/catalog");

        var item = Assert.Single(page!.Data);
        Assert.Equal("home-server-1", item.Label);
        Assert.Equal("us", item.Region);
        Assert.True(item.Online);
        var image = Assert.Single(item.Images);
        Assert.Equal("reg/wisp-base:latest", image.ImageRef);
        Assert.Equal(5, image.PriceCentsPerMin);
        Assert.Equal("usd", image.Currency);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task Catalog_paginates_across_two_pages()
    {
        var fx = new Fixture();
        for (var i = 0; i < 3; i++)
        {
            await fx.SeedOnlineHostAsync($"h{i}", $"img{i}", price: 5);
        }

        using var factory = fx.Build();
        var client = Authed(factory);

        var first = await client.GetFromJsonAsync<CatalogPageDto>("/v1/catalog?limit=2");
        Assert.Equal(2, first!.Data.Count);
        Assert.NotNull(first.NextCursor);

        var second = await client.GetFromJsonAsync<CatalogPageDto>(
            $"/v1/catalog?limit=2&cursor={Uri.EscapeDataString(first.NextCursor!)}");
        Assert.Single(second!.Data);
        Assert.Null(second.NextCursor);
    }

    [Theory]
    [InlineData("/v1/catalog?limit=0")]
    [InlineData("/v1/catalog?limit=101")]
    [InlineData("/v1/catalog?limit=abc")]
    [InlineData("/v1/catalog?network=lan")]
    [InlineData("/v1/catalog?max_price_cents_per_min=-1")]
    [InlineData("/v1/catalog?min_gpus=-1")]
    [InlineData("/v1/catalog?min_gpus=abc")]
    [InlineData("/v1/catalog?cursor=not-a-cursor!!")]
    public async Task Catalog_rejects_malformed_query_params(string url)
    {
        var fx = new Fixture();
        using var factory = fx.Build();

        var response = await Authed(factory).GetAsync(url);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ErrorEnvelopeDto>();
        Assert.Equal("validation_error", envelope!.Error.Code);
    }

    [Fact]
    public async Task Catalog_surfaces_gpu_fields_and_filters_by_min_gpus_and_gpu_class()
    {
        var fx = new Fixture();
        await fx.SeedOnlineHostAsync("gpu-host", "reg/gpu:1", price: 5,
            gpuClasses: new[] { "nvidia-a100" }, gpuCount: 4, maxGpus: 2);
        await fx.SeedOnlineHostAsync("cpu-host", "reg/cpu:1", price: 5);
        using var factory = fx.Build();
        var client = Authed(factory);

        // Both filters set: only the GPU host with an offer whose ceiling ≥ 1 survives.
        var page = await client.GetFromJsonAsync<CatalogPageDto>(
            "/v1/catalog?min_gpus=1&gpu_class=nvidia-a100");

        var item = Assert.Single(page!.Data);
        Assert.Equal("gpu-host", item.Label);
        Assert.Equal(new[] { "nvidia-a100" }, item.GpuClasses);
        Assert.Equal(4, item.GpuCount);
        Assert.Equal(2, Assert.Single(item.Images).MaxGpus);
    }

    [Fact]
    public async Task Catalog_without_gpu_filters_lists_every_host_with_gpu_fields()
    {
        var fx = new Fixture();
        await fx.SeedOnlineHostAsync("gpu-host", "reg/gpu:1", price: 5,
            gpuClasses: new[] { "nvidia-a100" }, gpuCount: 4, maxGpus: 2);
        await fx.SeedOnlineHostAsync("cpu-host", "reg/cpu:1", price: 5);
        using var factory = fx.Build();

        var page = await Authed(factory).GetFromJsonAsync<CatalogPageDto>("/v1/catalog");

        // No filters → both hosts; the CPU host surfaces empty/0 GPU fields.
        Assert.Equal(2, page!.Data.Count);
        var cpu = Assert.Single(page.Data, d => d.Label == "cpu-host");
        Assert.Empty(cpu.GpuClasses);
        Assert.Equal(0, cpu.GpuCount);
        Assert.Equal(0, Assert.Single(cpu.Images).MaxGpus);
    }

    [Fact]
    public async Task Catalog_ignores_unknown_query_params()
    {
        var fx = new Fixture();
        await fx.SeedOnlineHostAsync("h", "img", price: 5);
        using var factory = fx.Build();

        var response = await Authed(factory).GetAsync("/v1/catalog?wat=1&sort=nope");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Get_host_returns_public_detail()
    {
        var fx = new Fixture();
        var host = await fx.SeedOnlineHostAsync("home", "reg/img:1", price: 8);
        await fx.Hosts.SetAdvertisedGpuAsync(host.Id, new[] { "nvidia-a100" }, 2, T0);
        using var factory = fx.Build();

        var detail = await Authed(factory).GetFromJsonAsync<HostDetailDto>($"/v1/hosts/{host.Id}");

        Assert.Equal(host.Id, detail!.HostId);
        Assert.Equal("home", detail.Label);
        Assert.True(detail.Online);
        var image = Assert.Single(detail.Images);
        Assert.Equal("reg/img:1", image.ImageRef);
        // The host detail surfaces the advertised GPU read-only (task #521).
        Assert.Equal(new[] { "nvidia-a100" }, detail.GpuClasses);
        Assert.Equal(2, detail.GpuCount);
    }

    [Fact]
    public async Task Get_host_is_404_for_unknown_id()
    {
        var fx = new Fixture();
        using var factory = fx.Build();

        var response = await Authed(factory).GetAsync($"/v1/hosts/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ErrorEnvelopeDto>();
        Assert.Equal("not_found", envelope!.Error.Code);
    }

    [Fact]
    public async Task Get_host_is_404_for_a_non_uuid_id()
    {
        var fx = new Fixture();
        using var factory = fx.Build();

        var response = await Authed(factory).GetAsync("/v1/hosts/not-a-uuid");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ErrorEnvelopeDto>();
        Assert.Equal("not_found", envelope!.Error.Code);
    }

    private sealed record CatalogPageDto(
        [property: JsonPropertyName("data")] IReadOnlyList<CatalogItemDto> Data,
        [property: JsonPropertyName("next_cursor")] string? NextCursor);

    private sealed record CatalogItemDto(
        [property: JsonPropertyName("host_id")] Guid HostId,
        [property: JsonPropertyName("label")] string? Label,
        [property: JsonPropertyName("region")] string? Region,
        [property: JsonPropertyName("images")] IReadOnlyList<CatalogImageDto> Images,
        [property: JsonPropertyName("online")] bool Online,
        [property: JsonPropertyName("gpu_classes")] IReadOnlyList<string> GpuClasses,
        [property: JsonPropertyName("gpu_count")] int GpuCount);

    private sealed record HostDetailDto(
        [property: JsonPropertyName("host_id")] Guid HostId,
        [property: JsonPropertyName("label")] string? Label,
        [property: JsonPropertyName("region")] string? Region,
        [property: JsonPropertyName("online")] bool Online,
        [property: JsonPropertyName("images")] IReadOnlyList<CatalogImageDto> Images,
        [property: JsonPropertyName("gpu_classes")] IReadOnlyList<string> GpuClasses,
        [property: JsonPropertyName("gpu_count")] int GpuCount);

    private sealed record CatalogImageDto(
        [property: JsonPropertyName("host_image_id")] Guid HostImageId,
        [property: JsonPropertyName("image_ref")] string ImageRef,
        [property: JsonPropertyName("price_cents_per_min")] long PriceCentsPerMin,
        [property: JsonPropertyName("currency")] string Currency,
        [property: JsonPropertyName("max_gpus")] int MaxGpus);

    private sealed record ErrorEnvelopeDto(
        [property: JsonPropertyName("error")] ErrorBodyDto Error);

    private sealed record ErrorBodyDto(
        [property: JsonPropertyName("code")] string Code,
        [property: JsonPropertyName("message")] string Message);
}
