using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wisper.Api.Auth;
using Wisper.Api.Domain;
using Wisper.Api.Ledger;
using Wisper.Api.Persistence.HostImages;
using Wisper.Api.Persistence.Hosts;
using Wisper.Api.Persistence.Payouts;
using Wisper.Api.Persistence.Users;
using Wisper.Api.Tests.TestSupport;
using Wisper.Api.Tunnel;
using Xunit;

namespace Wisper.Api.Tests.Hosts;

/// <summary>
/// Integration tests over the real app host for the host registration + pricing surface (docs/API.md §6,
/// P7.1): the host-role gate, <c>POST /v1/hosts</c> issuing the agent token once + <c>manager_ws</c>,
/// <c>GET /v1/hosts/mine</c>, token rotation closing the tunnel (4402), and the priced allow-list validated
/// against the live advertised capability. All externals are in-memory doubles / fakes (Grunt has none).
/// </summary>
public class HostEndpointsTests
{
    private const string ManagerWs = "wss://wisper.test/agent";

    private sealed class Fixture
    {
        public InMemoryUserRepository Users { get; } = new();
        public InMemoryHostRepository Hosts { get; } = new();
        public InMemoryHostImageRepository Images { get; } = new();
        public FakeHostCapabilitySource Capabilities { get; } = new();
        public FakeAgentTunnelCloser TunnelCloser { get; } = new();
        public FakeJwtValidator Validator { get; } = new()
        {
            Principal = WisperPrincipal.Create("host-sub", "host@example.com", new[] { "host" }),
        };

        public WebApplicationFactory<Program> Build() =>
            new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseSetting($"Tunnel:{nameof(TunnelOptions.ManagerWebSocketUrl)}", ManagerWs);
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IJwtValidator>();
                    services.AddSingleton<IJwtValidator>(Validator);
                    services.RemoveAll<IUserRepository>();
                    services.AddSingleton<IUserRepository>(Users);
                    services.RemoveAll<IHostRepository>();
                    services.AddSingleton<IHostRepository>(Hosts);
                    services.RemoveAll<IHostImageRepository>();
                    services.AddSingleton<IHostImageRepository>(Images);
                    services.RemoveAll<IHostCapabilitySource>();
                    services.AddSingleton<IHostCapabilitySource>(Capabilities);
                    services.RemoveAll<IAgentTunnelCloser>();
                    services.AddSingleton<IAgentTunnelCloser>(TunnelCloser);
                    // The earnings summary on /v1/hosts/mine reads the ledger; back it in-memory (no Postgres).
                    services.RemoveAll<ILedgerStore>();
                    services.AddSingleton<ILedgerStore, InMemoryLedgerStore>();
                    services.RemoveAll<IPayoutRepository>();
                    services.AddSingleton<IPayoutRepository, InMemoryPayoutRepository>();
                });
            });
    }

    private static HttpClient Authed(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", "Bearer good");
        return client;
    }

    /// <summary>
    /// Marks the bootstrapped host owner Connect-enabled so a priced allow-list is accepted (docs/API.md §6):
    /// charging a non-zero price requires Connect onboarding (task #392). The owner is the user provisioned
    /// from the host JWT (<c>host-sub</c>).
    /// </summary>
    private static async Task EnableOwnerConnectAsync(Fixture fx)
    {
        var user = await fx.Users.GetByCognitoSubAsync("host-sub")
            ?? throw new InvalidOperationException("host owner not provisioned yet");
        await fx.Users.UpdateAsync(user with { ConnectStatus = ConnectStatus.Enabled });
    }

    [Fact]
    public async Task Register_requires_a_token()
    {
        var fx = new Fixture();
        using var factory = fx.Build();

        var response = await factory.CreateClient().PostAsync("/v1/hosts", content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Register_requires_the_host_role()
    {
        var fx = new Fixture();
        fx.Validator.Principal = WisperPrincipal.Create("consumer-sub", "c@example.com", Array.Empty<string>());
        using var factory = fx.Build();

        var response = await Authed(factory).PostAsync("/v1/hosts", content: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Register_issues_the_token_once_with_manager_ws()
    {
        var fx = new Fixture();
        using var factory = fx.Build();

        var response = await Authed(factory).PostAsJsonAsync(
            "/v1/hosts", new { name = "home-server-1", label = "us" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<HostRegisteredDto>();
        Assert.StartsWith("wht_live_", body!.AgentToken);
        Assert.StartsWith("wht_live_", body.AgentTokenPrefix);
        Assert.Equal(ManagerWs, body.ManagerWs);
        Assert.Equal("offline", body.Status);

        // Only the hash is stored; the row never carries the clear token.
        var stored = await fx.Hosts.GetByIdAsync(body.Id);
        Assert.NotNull(stored);
        Assert.NotEqual(body.AgentToken, stored!.AgentTokenHash);
    }

    [Fact]
    public async Task Mine_lists_the_callers_hosts_with_earnings()
    {
        var fx = new Fixture();
        using var factory = fx.Build();
        var client = Authed(factory);

        var registered = await (await client.PostAsJsonAsync("/v1/hosts", new { name = "h1" }))
            .Content.ReadFromJsonAsync<HostRegisteredDto>();

        var mine = await client.GetFromJsonAsync<HostsMineDto>("/v1/hosts/mine");

        Assert.Single(mine!.Data);
        Assert.Equal(registered!.Id, mine.Data[0].Id);
        Assert.False(mine.Data[0].Online);
        Assert.Equal("usd", mine.Earnings.Currency);
    }

    [Fact]
    public async Task Rotate_returns_a_new_token_and_closes_the_tunnel()
    {
        var fx = new Fixture();
        using var factory = fx.Build();
        var client = Authed(factory);

        var registered = await (await client.PostAsJsonAsync("/v1/hosts", new { name = "h1" }))
            .Content.ReadFromJsonAsync<HostRegisteredDto>();
        fx.TunnelCloser.SetLive(registered!.Id);

        var response = await client.PostAsync($"/v1/hosts/{registered.Id}/agent-token", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<RotatedDto>();
        Assert.StartsWith("wht_live_", body!.AgentToken);
        Assert.NotEqual(registered.AgentToken, body.AgentToken);
        Assert.True(body.TunnelClosed);

        Assert.True(fx.TunnelCloser.Closes.TryDequeue(out var close));
        Assert.Equal(CloseCodes.Revoked, close.CloseCode);
    }

    [Fact]
    public async Task Put_images_validates_against_capability_and_persists()
    {
        var fx = new Fixture();
        using var factory = fx.Build();
        var client = Authed(factory);

        var registered = await (await client.PostAsJsonAsync("/v1/hosts", new { name = "h1" }))
            .Content.ReadFromJsonAsync<HostRegisteredDto>();
        fx.Capabilities.Set(registered!.Id, new HostCapabilitySnapshot(
            Images: new[] { "alpine:latest" },
            Networks: new[] { NetworkMode.None, NetworkMode.Open },
            MaxTtlSeconds: 14400, MaxCpus: 8, MaxMemoryMb: 16384, MaxPids: 4096));
        await EnableOwnerConnectAsync(fx); // a priced image requires Connect (task #392)

        var ok = await client.PutAsJsonAsync($"/v1/hosts/{registered.Id}/images", new
        {
            images = new[]
            {
                new { image_ref = "alpine:latest", price_cents_per_min = 5, networks = new[] { "none", "open" }, max_ttl_seconds = 3600 },
            },
        });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var list = await ok.Content.ReadFromJsonAsync<HostImagesDto>();
        Assert.Single(list!.Data);
        Assert.Equal("alpine:latest", list.Data[0].ImageRef);

        // An image the host does not advertise is a 400.
        var bad = await client.PutAsJsonAsync($"/v1/hosts/{registered.Id}/images", new
        {
            images = new[]
            {
                new { image_ref = "nope:latest", price_cents_per_min = 5, networks = new[] { "none" }, max_ttl_seconds = 3600 },
            },
        });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Put_images_on_offline_host_is_host_offline()
    {
        var fx = new Fixture();
        using var factory = fx.Build();
        var client = Authed(factory);

        var registered = await (await client.PostAsJsonAsync("/v1/hosts", new { name = "h1" }))
            .Content.ReadFromJsonAsync<HostRegisteredDto>();
        // No capability declared → offline.

        var response = await client.PutAsJsonAsync($"/v1/hosts/{registered!.Id}/images", new
        {
            images = new[]
            {
                new { image_ref = "alpine:latest", price_cents_per_min = 5, networks = new[] { "none" }, max_ttl_seconds = 3600 },
            },
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Put_images_priced_without_connect_is_validation_error()
    {
        // Enabling a non-zero-priced image without Connect onboarding is rejected at the mutation (task #392):
        // the owner is provisioned Connect-none, so charging money is not yet allowed.
        var fx = new Fixture();
        using var factory = fx.Build();
        var client = Authed(factory);

        var registered = await (await client.PostAsJsonAsync("/v1/hosts", new { name = "h1" }))
            .Content.ReadFromJsonAsync<HostRegisteredDto>();
        fx.Capabilities.Set(registered!.Id, new HostCapabilitySnapshot(
            new[] { "alpine:latest" }, new[] { NetworkMode.None }, 14400, 8, 16384, 4096));

        var priced = await client.PutAsJsonAsync($"/v1/hosts/{registered.Id}/images", new
        {
            images = new[]
            {
                new { image_ref = "alpine:latest", price_cents_per_min = 5, networks = new[] { "none" }, max_ttl_seconds = 3600 },
            },
        });
        Assert.Equal(HttpStatusCode.BadRequest, priced.StatusCode);
        Assert.Empty(await fx.Images.ListByHostAsync(registered.Id));

        // The same image at price 0 (self-hosted) is accepted with no Connect.
        var free = await client.PutAsJsonAsync($"/v1/hosts/{registered.Id}/images", new
        {
            images = new[]
            {
                new { image_ref = "alpine:latest", price_cents_per_min = 0, networks = new[] { "none" }, max_ttl_seconds = 3600 },
            },
        });
        Assert.Equal(HttpStatusCode.OK, free.StatusCode);
    }

    [Fact]
    public async Task Patch_image_updates_price_and_enabled()
    {
        var fx = new Fixture();
        using var factory = fx.Build();
        var client = Authed(factory);

        var registered = await (await client.PostAsJsonAsync("/v1/hosts", new { name = "h1" }))
            .Content.ReadFromJsonAsync<HostRegisteredDto>();
        fx.Capabilities.Set(registered!.Id, new HostCapabilitySnapshot(
            new[] { "alpine:latest" }, new[] { NetworkMode.None }, 14400, 8, 16384, 4096));
        await EnableOwnerConnectAsync(fx); // a priced image requires Connect (task #392)
        await client.PutAsJsonAsync($"/v1/hosts/{registered.Id}/images", new
        {
            images = new[]
            {
                new { image_ref = "alpine:latest", price_cents_per_min = 5, networks = new[] { "none" }, max_ttl_seconds = 3600 },
            },
        });
        var imageId = (await fx.Images.ListByHostAsync(registered.Id))[0].Id;

        var response = await client.PatchAsJsonAsync(
            $"/v1/hosts/{registered.Id}/images/{imageId}", new { price_cents_per_min = 11, enabled = false });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var view = await response.Content.ReadFromJsonAsync<HostImageDto>();
        Assert.Equal(11, view!.PriceCentsPerMin);
        Assert.False(view.Enabled);
    }

    [Fact]
    public async Task Another_owners_host_is_not_found()
    {
        var fx = new Fixture();
        using var factory = fx.Build();

        var registered = await (await Authed(factory).PostAsJsonAsync("/v1/hosts", new { name = "h1" }))
            .Content.ReadFromJsonAsync<HostRegisteredDto>();

        // A different host account must not see or rotate the first account's host (§3 → 404).
        fx.Validator.Principal = WisperPrincipal.Create("other-sub", "other@example.com", new[] { "host" });
        var response = await Authed(factory).PostAsync($"/v1/hosts/{registered!.Id}/agent-token", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private sealed record HostRegisteredDto(
        [property: JsonPropertyName("id")] Guid Id,
        [property: JsonPropertyName("agent_token")] string AgentToken,
        [property: JsonPropertyName("agent_token_prefix")] string AgentTokenPrefix,
        [property: JsonPropertyName("manager_ws")] string ManagerWs,
        [property: JsonPropertyName("status")] string Status);

    private sealed record RotatedDto(
        [property: JsonPropertyName("agent_token")] string AgentToken,
        [property: JsonPropertyName("tunnel_closed")] bool TunnelClosed);

    private sealed record HostsMineDto(
        [property: JsonPropertyName("data")] IReadOnlyList<HostSummaryDto> Data,
        [property: JsonPropertyName("earnings")] EarningsDto Earnings);

    private sealed record HostSummaryDto(
        [property: JsonPropertyName("id")] Guid Id,
        [property: JsonPropertyName("online")] bool Online);

    private sealed record EarningsDto(
        [property: JsonPropertyName("currency")] string Currency);

    private sealed record HostImagesDto(
        [property: JsonPropertyName("data")] IReadOnlyList<HostImageDto> Data);

    private sealed record HostImageDto(
        [property: JsonPropertyName("image_ref")] string ImageRef,
        [property: JsonPropertyName("price_cents_per_min")] long PriceCentsPerMin,
        [property: JsonPropertyName("enabled")] bool Enabled);
}
