using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wisper.Api.Auth;
using Wisper.Api.Domain;
using Wisper.Api.Payments;
using Wisper.Api.Persistence.Stripe;
using Wisper.Api.Persistence.Users;
using Wisper.Api.Tests.TestSupport;
using Xunit;

namespace Wisper.Api.Tests.Hosts;

/// <summary>
/// Integration tests over the real app host for the host Connect onboarding surface (docs/API.md §6,
/// docs/PAYMENTS.md §5): the host-role gate, <c>POST /v1/hosts/connect</c> returning an onboarding URL and
/// pinning the account, <c>GET /v1/hosts/connect/status</c> deriving <c>connect_status</c> + requirements,
/// and the end-to-end <c>account.updated</c> webhook flipping the stored status. All externals are in-memory
/// doubles / fakes (Grunt has no Stripe/Postgres).
/// </summary>
public class HostConnectEndpointsTests
{
    private sealed class Fixture
    {
        public InMemoryUserRepository Users { get; } = new();
        public InMemoryStripeEventRepository Events { get; } = new();
        public FakeStripeConnectGateway Gateway { get; } = new();
        public FakeJwtValidator Validator { get; } = new()
        {
            // A host (additive: also a consumer) — the connect endpoints are host-gated (§6).
            Principal = WisperPrincipal.Create("host-sub", "host@example.com", new[] { "host" }),
        };

        public Stripe.Event? NextWebhookEvent { get; set; }

        public WebApplicationFactory<Program> Build() =>
            new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IJwtValidator>();
                    services.AddSingleton<IJwtValidator>(Validator);
                    services.RemoveAll<IUserRepository>();
                    services.AddSingleton<IUserRepository>(Users);
                    services.RemoveAll<IStripeEventRepository>();
                    services.AddSingleton<IStripeEventRepository>(Events);
                    services.RemoveAll<IStripeConnectGateway>();
                    services.AddSingleton<IStripeConnectGateway>(Gateway);
                    services.RemoveAll<IStripeSignatureVerifier>();
                    services.AddSingleton<IStripeSignatureVerifier>(
                        new FakeStripeSignatureVerifier((_, _) => NextWebhookEvent!));
                    services.Configure<StripeOptions>(o =>
                    {
                        o.ConnectRefreshUrl = "https://host.wisper.test/connect/refresh";
                        o.ConnectReturnUrl = "https://host.wisper.test/connect/return";
                    });
                }));

        public Guid BootstrappedUserId() => Users.GetByCognitoSubAsync("host-sub").Result!.Id;
    }

    private static HttpClient Authed(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", "Bearer good");
        return client;
    }

    [Fact]
    public async Task Connect_requires_a_token()
    {
        var fx = new Fixture();
        using var factory = fx.Build();

        var response = await factory.CreateClient().PostAsync("/v1/hosts/connect", content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Connect_requires_the_host_role()
    {
        var fx = new Fixture();
        // A plain consumer (no host group) must be forbidden from a host action (§6).
        fx.Validator.Principal = WisperPrincipal.Create("consumer-sub", "c@example.com", Array.Empty<string>());
        using var factory = fx.Build();

        var response = await Authed(factory).PostAsync("/v1/hosts/connect", content: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Connect_returns_an_onboarding_url_and_pins_the_account()
    {
        var fx = new Fixture();
        using var factory = fx.Build();

        var response = await Authed(factory).PostAsync("/v1/hosts/connect", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ConnectDto>();
        Assert.Equal(fx.Gateway.OnboardingUrl, body!.OnboardingUrl);
        Assert.Equal("pending", body.ConnectStatus);

        var user = await fx.Users.GetByIdAsync(fx.BootstrappedUserId());
        Assert.Equal("acct_fake", user!.ConnectAccountId);
        Assert.Equal(ConnectStatus.Pending, user.ConnectStatus);
    }

    [Fact]
    public async Task Status_reports_derived_state_and_requirements()
    {
        var fx = new Fixture();
        using var factory = fx.Build();
        var client = Authed(factory);

        await client.PostAsync("/v1/hosts/connect", content: null); // create the account first
        fx.Gateway.Snapshot = new ConnectAccountSnapshot(
            ChargesEnabled: false,
            PayoutsEnabled: false,
            DetailsSubmitted: true,
            DisabledReason: "requirements.past_due",
            CurrentlyDue: new[] { "external_account" },
            PastDue: new[] { "external_account" },
            EventuallyDue: Array.Empty<string>(),
            PendingVerification: Array.Empty<string>());

        var status = await client.GetFromJsonAsync<StatusDto>("/v1/hosts/connect/status");

        Assert.Equal("restricted", status!.ConnectStatus);
        Assert.False(status.CanGoOnline);
        Assert.Equal("requirements.past_due", status.Requirements.DisabledReason);
        Assert.Contains("external_account", status.Requirements.CurrentlyDue);
    }

    [Fact]
    public async Task Account_updated_webhook_gates_online_via_connect_status()
    {
        var fx = new Fixture();
        using var factory = fx.Build();
        var client = Authed(factory);

        // Onboard so the user has a linked connect_account_id the webhook can resolve.
        await client.PostAsync("/v1/hosts/connect", content: null);
        var accountId = (await fx.Users.GetByIdAsync(fx.BootstrappedUserId()))!.ConnectAccountId!;

        fx.NextWebhookEvent = new Stripe.Event
        {
            Id = "evt_acct_enabled",
            Type = Stripe.EventTypes.AccountUpdated,
            Data = new Stripe.EventData
            {
                Object = new Stripe.Account
                {
                    Id = accountId,
                    ChargesEnabled = true,
                    PayoutsEnabled = true,
                    DetailsSubmitted = true,
                },
            },
        };
        var webhook = await client.PostAsync("/stripe/webhook", new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, webhook.StatusCode);
        var user = await fx.Users.GetByIdAsync(fx.BootstrappedUserId());
        Assert.Equal(ConnectStatus.Enabled, user!.ConnectStatus);
        Assert.True(ConnectGate.CanGoOnline(user.ConnectStatus));
    }

    private sealed record ConnectDto(
        [property: JsonPropertyName("onboarding_url")] string OnboardingUrl,
        [property: JsonPropertyName("connect_status")] string ConnectStatus);

    private sealed record StatusDto(
        [property: JsonPropertyName("connect_status")] string ConnectStatus,
        [property: JsonPropertyName("can_go_online")] bool CanGoOnline,
        [property: JsonPropertyName("requirements")] RequirementsDto Requirements);

    private sealed record RequirementsDto(
        [property: JsonPropertyName("disabled_reason")] string? DisabledReason,
        [property: JsonPropertyName("currently_due")] IReadOnlyList<string> CurrentlyDue);
}
