using Microsoft.Extensions.Logging.Abstractions;
using Wisper.Api.Domain;
using Wisper.Api.Payments;
using Wisper.Api.Persistence.Stripe;
using Wisper.Api.Tests.TestSupport;
using Xunit;

namespace Wisper.Api.Tests.Payments;

/// <summary>
/// Unit tests for the webhook ingest pipeline (docs/PAYMENTS.md §8) with a fake signature verifier + the
/// in-memory event store (Grunt has no Stripe/Postgres). Covers: bad signature is rejected before any
/// storage; a first sight is stored + dispatched; an unrecognised type is <c>ignored</c>; a true duplicate
/// dedupes without re-invoking the handler; a handler failure is recorded <c>failed</c> without throwing;
/// and a re-delivered failure is retried.
/// </summary>
public class StripeWebhookServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 12, 0, 0, 0, TimeSpan.Zero);

    private static Stripe.Event Event(string id, string type) => new() { Id = id, Type = type };

    // A single builder that keeps every collaborator addressable, so assertions can read the store.
    private static (StripeWebhookService Service, InMemoryStripeEventRepository Events, FakeTimeProvider Clock)
        Build(FakeStripeSignatureVerifier verifier, params IStripeWebhookHandler[] handlers)
    {
        var events = new InMemoryStripeEventRepository();
        var clock = new FakeTimeProvider(T0);
        var dispatcher = new StripeEventDispatcher(handlers);
        var service = new StripeWebhookService(
            verifier, events, dispatcher, clock, NullLogger<StripeWebhookService>.Instance);
        return (service, events, clock);
    }

    [Fact]
    public async Task Bad_signature_throws_and_stores_nothing()
    {
        var (service, events, _) = Build(FakeStripeSignatureVerifier.Rejecting());

        await Assert.ThrowsAsync<StripeSignatureException>(
            () => service.IngestAsync("{}", "t=1,v1=deadbeef"));

        Assert.Null(await events.GetAsync("evt_1"));
    }

    [Fact]
    public async Task First_sight_of_a_handled_event_is_stored_and_processed()
    {
        var handler = new FakeStripeWebhookHandler("payment_intent.succeeded");
        var verifier = FakeStripeSignatureVerifier.Returning(Event("evt_1", "payment_intent.succeeded"));
        var (service, events, _) = Build(verifier, handler);

        var result = await service.IngestAsync("""{"id":"evt_1"}""", "sig");

        Assert.Equal(StripeWebhookOutcome.Processed, result.Outcome);
        Assert.True(result.IsAck);
        Assert.Equal(1, handler.Calls);

        var stored = await events.GetAsync("evt_1");
        Assert.NotNull(stored);
        Assert.Equal(StripeEventStatus.Processed, stored!.Status);
        Assert.Equal(T0, stored.ProcessedAt);
        Assert.Equal("""{"id":"evt_1"}""", stored.Payload); // the raw signed body is persisted verbatim
        Assert.Null(stored.Error);
    }

    [Fact]
    public async Task Unrecognised_type_is_stored_ignored_with_no_handler()
    {
        var handler = new FakeStripeWebhookHandler("payment_intent.succeeded");
        var verifier = FakeStripeSignatureVerifier.Returning(Event("evt_x", "invoice.created"));
        var (service, events, _) = Build(verifier, handler);

        var result = await service.IngestAsync("{}", "sig");

        Assert.Equal(StripeWebhookOutcome.Ignored, result.Outcome);
        Assert.True(result.IsAck);
        Assert.Equal(0, handler.Calls);
        Assert.Equal(StripeEventStatus.Ignored, (await events.GetAsync("evt_x"))!.Status);
    }

    [Fact]
    public async Task Duplicate_of_a_processed_event_dedupes_without_reinvoking_the_handler()
    {
        var handler = new FakeStripeWebhookHandler("account.updated");
        var verifier = FakeStripeSignatureVerifier.Returning(Event("evt_1", "account.updated"));
        var (service, events, _) = Build(verifier, handler);

        var first = await service.IngestAsync("{}", "sig");
        var second = await service.IngestAsync("{}", "sig");

        Assert.Equal(StripeWebhookOutcome.Processed, first.Outcome);
        Assert.Equal(StripeWebhookOutcome.Deduplicated, second.Outcome);
        Assert.True(second.IsAck);
        Assert.Equal(1, handler.Calls); // the duplicate must not re-run the handler
        Assert.Equal(StripeEventStatus.Processed, (await events.GetAsync("evt_1"))!.Status);
    }

    [Fact]
    public async Task Handler_failure_is_recorded_failed_and_signals_retry_without_throwing()
    {
        var handler = new FakeStripeWebhookHandler("transfer.created") { Throw = true };
        var verifier = FakeStripeSignatureVerifier.Returning(Event("evt_9", "transfer.created"));
        var (service, events, _) = Build(verifier, handler);

        var result = await service.IngestAsync("{}", "sig");

        Assert.Equal(StripeWebhookOutcome.Failed, result.Outcome);
        Assert.False(result.IsAck); // the endpoint answers 500 so Stripe re-delivers

        var stored = await events.GetAsync("evt_9");
        Assert.Equal(StripeEventStatus.Failed, stored!.Status);
        Assert.Contains("boom", stored.Error);
    }

    [Fact]
    public async Task Redelivery_of_a_failed_event_is_retried_and_can_succeed()
    {
        var handler = new FakeStripeWebhookHandler("transfer.created") { Throw = true };
        var verifier = FakeStripeSignatureVerifier.Returning(Event("evt_9", "transfer.created"));
        var (service, events, _) = Build(verifier, handler);

        var first = await service.IngestAsync("{}", "sig");
        Assert.Equal(StripeWebhookOutcome.Failed, first.Outcome);

        // Whatever made it fail is resolved; Stripe re-delivers the same id.
        handler.Throw = false;
        var second = await service.IngestAsync("{}", "sig");

        Assert.Equal(StripeWebhookOutcome.Processed, second.Outcome);
        Assert.Equal(2, handler.Calls); // the failed row was re-dispatched, not deduped
        var stored = await events.GetAsync("evt_9");
        Assert.Equal(StripeEventStatus.Processed, stored!.Status);
        Assert.Null(stored.Error); // the prior error is cleared on success
    }
}
