using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wisper.Api.Domain;
using Wisper.Api.Payments;
using Wisper.Api.Persistence.Stripe;
using Wisper.Api.Tests.TestSupport;
using Xunit;

namespace Wisper.Api.Tests.Payments;

/// <summary>
/// Endpoint tests over the real app host for <c>POST /stripe/webhook</c> (docs/API.md §4,
/// docs/PAYMENTS.md §8): unauthenticated, signature-verified, reading the exact raw body. The real
/// signature verifier and Dapper event store are swapped for a fake verifier + in-memory store (Grunt has
/// no Stripe/Postgres), so the wiring -- route, raw-body read, dedupe, status → HTTP mapping -- is exercised
/// end-to-end without external services.
/// </summary>
public class StripeWebhookEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public StripeWebhookEndpointTests(WebApplicationFactory<Program> factory) => _factory = factory;

    /// <summary>A verifier that JSON-parses id/type from the body, or rejects when the signature is "bad".</summary>
    private static FakeStripeSignatureVerifier BodyDrivenVerifier() => new((payload, signature) =>
    {
        if (signature is null or "bad")
        {
            throw new StripeSignatureException("bad signature (fake)");
        }

        using var doc = JsonDocument.Parse(payload);
        return new Stripe.Event
        {
            Id = doc.RootElement.GetProperty("id").GetString()!,
            Type = doc.RootElement.GetProperty("type").GetString()!,
        };
    });

    private HttpClient ClientWith(IStripeEventRepository events, IStripeSignatureVerifier verifier) =>
        _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IStripeSignatureVerifier>();
                services.AddSingleton(verifier);
                services.RemoveAll<IStripeEventRepository>();
                services.AddSingleton(events);
            })).CreateClient();

    private static HttpRequestMessage Post(string json, string? signature)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/stripe/webhook")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        if (signature is not null)
        {
            request.Headers.Add("Stripe-Signature", signature);
        }

        return request;
    }

    [Fact]
    public async Task Valid_signature_acks_200_and_stores_the_event()
    {
        var events = new InMemoryStripeEventRepository();
        var client = ClientWith(events, BodyDrivenVerifier());

        var response = await client.SendAsync(
            Post("""{"id":"evt_ok","type":"payment_intent.succeeded"}""", "t=1,v1=good"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AckDto>();
        Assert.True(body!.Received);
        Assert.Equal("evt_ok", body.Id);
        Assert.Equal(StripeEventStatus.Processed, (await events.GetAsync("evt_ok"))!.Status);
    }

    [Fact]
    public async Task Bad_signature_returns_400_validation_error_and_stores_nothing()
    {
        var events = new InMemoryStripeEventRepository();
        var client = ClientWith(events, BodyDrivenVerifier());

        var response = await client.SendAsync(
            Post("""{"id":"evt_forged","type":"payment_intent.succeeded"}""", "bad"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ErrorEnvelopeDto>();
        Assert.Equal("validation_error", envelope!.Error.Code);
        Assert.Null(await events.GetAsync("evt_forged"));
    }

    [Fact]
    public async Task Duplicate_delivery_dedupes_to_200()
    {
        var events = new InMemoryStripeEventRepository();
        var client = ClientWith(events, BodyDrivenVerifier());
        var payload = """{"id":"evt_dupe","type":"account.updated"}""";

        var first = await client.SendAsync(Post(payload, "t=1,v1=good"));
        var second = await client.SendAsync(Post(payload, "t=1,v1=good"));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal("deduplicated", (await second.Content.ReadFromJsonAsync<AckDto>())!.Status);
    }

    private sealed record AckDto(bool Received, string Id, string Status);

    private sealed record ErrorEnvelopeDto(ErrorBodyDto Error);

    private sealed record ErrorBodyDto(string Code, string Message);
}
