using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wisper.Api.Domain;
using Wisper.Api.Leases;
using Wisper.Api.Persistence.Hosts;
using Wisper.Api.Persistence.Users;
using Wisper.Api.Tests.TestSupport;
using Wisper.Api.Tunnel;
using Xunit;

namespace Wisper.Api.Tests.Persistence;

/// <summary>
/// The DB-less dev mode proven end-to-end (docs/DESIGN.md §16, docs/DATA_MODEL.md §1): with no connection
/// string the app registers the in-memory repositories for <b>every</b> interface, so the complete
/// self-hosted flow runs in-process with no Postgres. A single config API key (<c>Auth:ApiKeys</c>) carrying
/// <c>consumer</c>+<c>host</c> scopes drives the whole chain:
/// <list type="number">
///   <item><c>POST /v1/hosts</c> registers a host and issues a real <c>wht_live_</c> agent token, which the
///   DB-backed <see cref="IHostTokenValidator"/> resolves against the same in-memory host store;</item>
///   <item><c>PUT /v1/hosts/:id/images</c> prices an image at <b>0</b> cents/min (self-hosted operators, task #386);</item>
///   <item><c>GET /v1/catalog</c> lists it once the agent tunnel is live;</item>
///   <item><c>POST /v1/leases</c> with <c>env</c> places a 0-cent hold and carries the env to the tunnel frame.</item>
/// </list>
/// Only the tunnel-facing pieces (registry/capability/relay) and the wallet gate are faked, exactly as the
/// existing lease/catalog integration tests do -- the persistence layer is the real in-memory registration.
/// </summary>
public class InMemoryPersistenceFullPathTests
{
    private const string ApiKey = "wck_live_selfhost0operator";
    private const string OperatorSub = "self-host-operator";
    private const string OperatorEmail = "operator@self-hosted.test";
    private const string ImageRef = "reg/wisp-base:latest";

    private sealed class Fixture
    {
        public FakeHostRegistry Registry { get; } = new();
        public FakeHostCapabilitySource Capabilities { get; } = new();
        public FakeTunnelRelay Relay { get; } = new();

        // Boot with NO connection-string override: the app registers the in-memory repositories app-wide
        // (the mode under test). Only the tunnel-facing doubles + the wallet gate are swapped, as the
        // existing lease/catalog integration tests do -- the repositories stay the real in-memory ones.
        public WebApplicationFactory<Program> Build() =>
            new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                // A single config API key with consumer+host scopes -- the operator-bootstrap escape hatch
                // that lets a self-hosted operator drive /v1 with no Postgres and no Cognito. The named
                // subject must resolve to a real user (task #36) -- the fixture seeds it below.
                builder.UseSetting($"Auth:ApiKeys:{ApiKey}:UserId", OperatorSub);
                builder.UseSetting($"Auth:ApiKeys:{ApiKey}:Email", OperatorEmail);
                builder.UseSetting($"Auth:ApiKeys:{ApiKey}:Scopes:0", "consumer");
                builder.UseSetting($"Auth:ApiKeys:{ApiKey}:Scopes:1", "host");
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IHostRegistry>();
                    services.AddSingleton<IHostRegistry>(Registry);
                    services.RemoveAll<IHostCapabilitySource>();
                    services.AddSingleton<IHostCapabilitySource>(Capabilities);
                    services.RemoveAll<ITunnelRelay>();
                    services.AddSingleton<ITunnelRelay>(Relay);
                    // A permissive gate stands in for the DB-backed WalletLeaseGate; the real hold/charge
                    // behaviour (including the 0-cent hold) is covered by WalletLeaseGateTests.
                    services.RemoveAll<ILeaseWalletGate>();
                    services.AddSingleton<ILeaseWalletGate, AllowWalletGate>();
                });
            });
    }

    private static HttpClient Authed(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {ApiKey}");
        return client;
    }

    private static Task SeedOperator(IUserRepository users) => users.CreateAsync(new User
    {
        CognitoSub = OperatorSub,
        Email = OperatorEmail,
        Status = UserStatus.Active,
    });

    [Fact]
    public async Task Self_hosted_flow_runs_end_to_end_on_a_db_less_boot()
    {
        var fx = new Fixture();
        using var factory = fx.Build();
        // Seed the operator's user in the in-memory store so the config API key's subject resolves --
        // task #36 requires the config sub to name a real user (else 401), so the DB-less operator must
        // pre-provision this row. In production this would come from real user registration.
        await SeedOperator(factory.Services.GetRequiredService<IUserRepository>());
        var client = Authed(factory);

        // 1) Register a host. The api-key principal (host scope) drives POST /v1/hosts and the host lands
        //    in the in-memory host store, tied to the pre-seeded operator user.
        var registered = await (await client.PostAsJsonAsync("/v1/hosts", new { name = "home-server-1", label = "us" }))
            .EnsureCreated().Content.ReadFromJsonAsync<HostRegisteredDto>();
        Assert.NotNull(registered);
        Assert.StartsWith("wht_live_", registered!.AgentToken);
        var hostId = registered.Id;

        // The real DB-backed validator resolves the freshly-issued agent token against the SAME in-memory
        // host store -- the loop the tunnel-only boot could never close without Postgres.
        var validation = await factory.Services.GetRequiredService<IHostTokenValidator>()
            .ValidateAsync(registered.AgentToken);
        Assert.True(validation.Succeeded);
        Assert.Equal(hostId.ToString(), validation.HostId);

        // 2) Price an image at 0 cents/min. The agent advertises the image via its live capability; a
        //    self-hosted operator is allowed a 0-cent price (task #386).
        fx.Capabilities.Set(hostId, new HostCapabilitySnapshot(
            Images: new[] { ImageRef },
            Networks: new[] { NetworkMode.None, NetworkMode.Open },
            MaxTtlSeconds: 14400, MaxCpus: 8, MaxMemoryMb: 16384, MaxPids: 4096, Os: "linux"));

        var priced = await client.PutAsJsonAsync($"/v1/hosts/{hostId}/images", new
        {
            images = new[]
            {
                new { image_ref = ImageRef, price_cents_per_min = 0, networks = new[] { "none", "open" }, max_ttl_seconds = 3600 },
            },
        });
        Assert.Equal(HttpStatusCode.OK, priced.StatusCode);

        // 3) The agent tunnel goes live: register presence, then drive the REAL presence flip the tunnel
        //    handshake runs at hello.ack (task #392) -- NOT a seeded status. A self-hosted operator (no
        //    Connect) with an all-zero-priced allow-list clears the earning gate, so the host row flips
        //    online on its own; the catalog then lists the host and its 0-priced image.
        fx.Registry.SetOnline(hostId);
        await factory.Services.GetRequiredService<IHostPresence>()
            .GoOnlineIfEligibleAsync(hostId.ToString());

        var hosts = factory.Services.GetRequiredService<IHostRepository>();
        Assert.Equal(HostStatus.Online, (await hosts.GetByIdAsync(hostId))!.Status); // the real flip, not a seed

        var catalog = await client.GetFromJsonAsync<CatalogPageDto>("/v1/catalog");
        var item = Assert.Single(catalog!.Data);
        Assert.Equal(hostId, item.HostId);
        var listed = Assert.Single(item.Images);
        Assert.Equal(ImageRef, listed.ImageRef);
        Assert.Equal(0, listed.PriceCentsPerMin);

        // 4) Create a lease with env. The 0-priced image yields a 0-cent hold; env reaches the tunnel frame
        //    verbatim but is never persisted.
        var leaseRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/leases")
        {
            Content = JsonContent.Create(new
            {
                host_id = hostId.ToString(),
                host_image_id = listed.HostImageId.ToString(),
                network = "open",
                ttl_seconds = 3600,
                env = new Dictionary<string, string> { ["API_TOKEN"] = "top-secret-value" },
            }),
        };
        leaseRequest.Headers.Add("Idempotency-Key", "lease-key-1");

        var leaseResponse = await client.SendAsync(leaseRequest);
        Assert.Equal(HttpStatusCode.Created, leaseResponse.StatusCode);
        var lease = await leaseResponse.Content.ReadFromJsonAsync<CreateLeaseDto>();
        Assert.NotNull(lease);
        Assert.StartsWith("lease_", lease!.Id);
        Assert.Equal(0, lease.PriceCentsPerMin);
        Assert.Equal(0, lease.HoldCents); // self-hosted operator: a 0-cent hold
        Assert.Equal("linux", lease.Os);

        // env reached the tunnel create frame verbatim…
        var (_, spec) = Assert.Single(fx.Relay.CreateCalls);
        Assert.Equal("top-secret-value", spec.Env!["API_TOKEN"]);
        // …and never leaked into the 201 body.
        Assert.DoesNotContain("top-secret-value", await leaseResponse.Content.ReadAsStringAsync());
    }

    private sealed record HostRegisteredDto(
        [property: JsonPropertyName("id")] Guid Id,
        [property: JsonPropertyName("agent_token")] string AgentToken,
        [property: JsonPropertyName("status")] string Status);

    private sealed record CatalogPageDto(
        [property: JsonPropertyName("data")] IReadOnlyList<CatalogItemDto> Data);

    private sealed record CatalogItemDto(
        [property: JsonPropertyName("host_id")] Guid HostId,
        [property: JsonPropertyName("images")] IReadOnlyList<CatalogImageDto> Images);

    private sealed record CatalogImageDto(
        [property: JsonPropertyName("host_image_id")] Guid HostImageId,
        [property: JsonPropertyName("image_ref")] string ImageRef,
        [property: JsonPropertyName("price_cents_per_min")] long PriceCentsPerMin);

    private sealed record CreateLeaseDto(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("price_cents_per_min")] long PriceCentsPerMin,
        [property: JsonPropertyName("hold_cents")] long HoldCents,
        [property: JsonPropertyName("os")] string? Os);
}

/// <summary>Small assertion helper: fails loudly if a response is not 201 Created before reading its body.</summary>
internal static class HttpResponseAssertions
{
    public static HttpResponseMessage EnsureCreated(this HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return response;
    }
}
