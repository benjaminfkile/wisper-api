using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wisper.Api.Auth;
using Wisper.Api.Ledger;
using Wisper.Api.Payments;
using Wisper.Api.Persistence.Idempotency;
using Wisper.Api.Persistence.Leases;
using Wisper.Api.Persistence.Policy;
using Wisper.Api.Persistence.Stripe;
using Wisper.Api.Persistence.Users;
using Wisper.Api.Tests.TestSupport;
using Xunit;

namespace Wisper.Api.Tests.Billing;

/// <summary>
/// Integration tests over the real app host for the consumer billing surface (docs/API.md §5,
/// docs/PAYMENTS.md §3): the consumer gate, the required <c>Idempotency-Key</c> on top-up and its replay,
/// the <c>client_secret</c> shape, balance + usage, the paginated ledger view, and the end-to-end
/// exactly-once credit — <c>topup</c> creates a PaymentIntent (no credit) and only the
/// <c>payment_intent.succeeded</c> webhook credits the wallet, once. All externals are in-memory doubles.
/// </summary>
public class BillingEndpointsTests
{
    private sealed class Fixture
    {
        public InMemoryUserRepository Users { get; } = new();
        public InMemoryLeaseRepository Leases { get; } = new();
        public InMemoryIdempotencyKeyRepository Idempotency { get; } = new();
        public InMemoryLedgerStore Ledger { get; } = new();
        public InMemoryStripeEventRepository Events { get; } = new();
        public InMemoryPlatformPolicyRepository Policies { get; } = new();
        public FakeStripeBillingGateway Gateway { get; } = new();
        public FakeJwtValidator Validator { get; } = new();

        /// <summary>The event the (fake) signature verifier returns on the next webhook POST.</summary>
        public Stripe.Event? NextWebhookEvent { get; set; }

        public WebApplicationFactory<Program> Build() =>
            new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IJwtValidator>();
                    services.AddSingleton<IJwtValidator>(Validator);
                    services.RemoveAll<IUserRepository>();
                    services.AddSingleton<IUserRepository>(Users);
                    services.RemoveAll<ILeaseRepository>();
                    services.AddSingleton<ILeaseRepository>(Leases);
                    services.RemoveAll<IIdempotencyKeyRepository>();
                    services.AddSingleton<IIdempotencyKeyRepository>(Idempotency);
                    services.RemoveAll<ILedgerStore>();
                    services.AddSingleton<ILedgerStore>(Ledger);
                    services.RemoveAll<IStripeEventRepository>();
                    services.AddSingleton<IStripeEventRepository>(Events);
                    services.RemoveAll<IPlatformPolicyRepository>();
                    services.AddSingleton<IPlatformPolicyRepository>(Policies);
                    services.RemoveAll<IStripeBillingGateway>();
                    services.AddSingleton<IStripeBillingGateway>(Gateway);
                    services.RemoveAll<IStripeSignatureVerifier>();
                    services.AddSingleton<IStripeSignatureVerifier>(
                        new FakeStripeSignatureVerifier((_, _) => NextWebhookEvent!));
                }));

        public Guid BootstrappedUserId() => Users.GetByCognitoSubAsync("fake-sub").Result!.Id;
    }

    private static HttpClient Authed(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", "Bearer good");
        return client;
    }

    private static HttpRequestMessage Topup(object body, string? idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/billing/topup")
        {
            Content = JsonContent.Create(body),
        };
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return request;
    }

    private static StringContent WebhookBody() => new("{}", Encoding.UTF8, "application/json");

    private static Stripe.Event SucceededEvent(string eventId, Guid userId, long amountCents)
    {
        var intent = new Stripe.PaymentIntent
        {
            Id = $"pi_{eventId}",
            Amount = amountCents,
            AmountReceived = amountCents,
            Currency = "usd",
            Metadata = new Dictionary<string, string> { [TopupMetadata.UserId] = userId.ToString() },
        };
        return new Stripe.Event
        {
            Id = eventId,
            Type = Stripe.EventTypes.PaymentIntentSucceeded,
            Data = new Stripe.EventData { Object = intent },
        };
    }

    [Fact]
    public async Task Topup_without_a_token_is_401()
    {
        var fx = new Fixture();
        using var factory = fx.Build();

        var response = await factory.CreateClient().SendAsync(Topup(new { amount_cents = 5000 }, "key-1"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Topup_returns_a_client_secret_without_crediting_the_wallet()
    {
        var fx = new Fixture();
        using var factory = fx.Build();

        var response = await Authed(factory).SendAsync(Topup(new { amount_cents = 5000 }, "key-1"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<TopupDto>();
        Assert.Equal("pi_fake_secret", body!.ClientSecret);
        Assert.Single(fx.Gateway.PaymentIntentCalls);

        // No credit on the response path.
        var billing = await Authed(factory).GetFromJsonAsync<BillingDto>("/v1/billing");
        Assert.Equal(0, billing!.BalanceCents);
    }

    [Fact]
    public async Task Topup_without_an_idempotency_key_is_400()
    {
        var fx = new Fixture();
        using var factory = fx.Build();

        var response = await Authed(factory).SendAsync(Topup(new { amount_cents = 5000 }, idempotencyKey: null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(fx.Gateway.PaymentIntentCalls);
    }

    [Fact]
    public async Task Topup_replays_the_same_key_and_body()
    {
        var fx = new Fixture();
        using var factory = fx.Build();
        var client = Authed(factory);

        var first = await client.SendAsync(Topup(new { amount_cents = 5000 }, "key-1"));
        var second = await client.SendAsync(Topup(new { amount_cents = 5000 }, "key-1"));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var a = await first.Content.ReadFromJsonAsync<TopupDto>();
        var b = await second.Content.ReadFromJsonAsync<TopupDto>();
        Assert.Equal(a!.ClientSecret, b!.ClientSecret);
        Assert.Single(fx.Gateway.PaymentIntentCalls); // exactly one PaymentIntent created
    }

    [Fact]
    public async Task Webhook_credits_the_wallet_exactly_once()
    {
        var fx = new Fixture();
        using var factory = fx.Build();
        var client = Authed(factory);

        // Bootstrap the account (first authed call) so we know the user id to stamp on the webhook.
        await client.GetAsync("/v1/billing");
        var userId = fx.BootstrappedUserId();

        fx.NextWebhookEvent = SucceededEvent("evt_topup_1", userId, amountCents: 5000);
        var first = await client.PostAsync("/stripe/webhook", WebhookBody());
        var second = await client.PostAsync("/stripe/webhook", WebhookBody()); // Stripe re-delivers

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var billing = await client.GetFromJsonAsync<BillingDto>("/v1/billing");
        Assert.Equal(5000, billing!.BalanceCents); // credited once, not twice
    }

    [Fact]
    public async Task Transactions_returns_the_ledger_view_after_a_credit()
    {
        var fx = new Fixture();
        using var factory = fx.Build();
        var client = Authed(factory);
        await client.GetAsync("/v1/billing");
        var userId = fx.BootstrappedUserId();

        fx.NextWebhookEvent = SucceededEvent("evt_topup_1", userId, amountCents: 5000);
        await client.PostAsync("/stripe/webhook", WebhookBody());

        var page = await client.GetFromJsonAsync<TransactionPageDto>("/v1/billing/transactions");

        var row = Assert.Single(page!.Data);
        Assert.Equal("topup", row.Kind);
        Assert.Equal(5000, row.AmountCents);
    }

    [Fact]
    public async Task Transactions_rejects_a_malformed_cursor()
    {
        var fx = new Fixture();
        using var factory = fx.Build();

        var response = await Authed(factory).GetAsync("/v1/billing/transactions?cursor=not-a-cursor");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Payment_methods_returns_a_setup_intent_secret()
    {
        var fx = new Fixture();
        using var factory = fx.Build();

        var response = await Authed(factory).PostAsync("/v1/billing/payment-methods", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<SetupIntentDto>();
        Assert.Equal("seti_fake_secret", body!.ClientSecret);
    }

    private sealed record TopupDto(
        [property: JsonPropertyName("client_secret")] string ClientSecret);

    private sealed record SetupIntentDto(
        [property: JsonPropertyName("client_secret")] string ClientSecret);

    private sealed record BillingDto(
        [property: JsonPropertyName("balance_cents")] long BalanceCents,
        [property: JsonPropertyName("currency")] string Currency);

    private sealed record TransactionDto(
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("amount_cents")] long AmountCents);

    private sealed record TransactionPageDto(
        [property: JsonPropertyName("data")] IReadOnlyList<TransactionDto> Data,
        [property: JsonPropertyName("next_cursor")] string? NextCursor);
}
