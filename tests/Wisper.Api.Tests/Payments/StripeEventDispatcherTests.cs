using Wisper.Api.Domain;
using Wisper.Api.Payments;
using Wisper.Api.Tests.TestSupport;
using Xunit;

namespace Wisper.Api.Tests.Payments;

/// <summary>
/// Unit tests for <see cref="StripeEventDispatcher"/> (docs/PAYMENTS.md §8.5): a recognised type routes to
/// its handler and reports <c>processed</c>; an unrecognised type is an <c>ignored</c> no-op; and a type
/// claimed by two handlers is a wiring bug caught at construction.
/// </summary>
public class StripeEventDispatcherTests
{
    private static Stripe.Event Event(string type) => new() { Id = "evt", Type = type };

    [Fact]
    public async Task Routes_a_recognised_type_to_its_handler_and_reports_processed()
    {
        var payments = new FakeStripeWebhookHandler("payment_intent.succeeded", "charge.refunded");
        var transfers = new FakeStripeWebhookHandler("transfer.created");
        var dispatcher = new StripeEventDispatcher(new IStripeWebhookHandler[] { payments, transfers });

        var status = await dispatcher.DispatchAsync(Event("charge.refunded"));

        Assert.Equal(StripeEventStatus.Processed, status);
        Assert.Equal(1, payments.Calls);
        Assert.Equal(0, transfers.Calls);
    }

    [Fact]
    public async Task Unrecognised_type_is_ignored_and_invokes_no_handler()
    {
        var payments = new FakeStripeWebhookHandler("payment_intent.succeeded");
        var dispatcher = new StripeEventDispatcher(new IStripeWebhookHandler[] { payments });

        var status = await dispatcher.DispatchAsync(Event("invoice.finalized"));

        Assert.Equal(StripeEventStatus.Ignored, status);
        Assert.Equal(0, payments.Calls);
    }

    [Fact]
    public async Task Handler_exception_propagates_to_the_caller()
    {
        var handler = new FakeStripeWebhookHandler("transfer.created") { Throw = true };
        var dispatcher = new StripeEventDispatcher(new IStripeWebhookHandler[] { handler });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => dispatcher.DispatchAsync(Event("transfer.created")));
    }

    [Fact]
    public void Two_handlers_claiming_the_same_type_fail_fast()
    {
        var a = new FakeStripeWebhookHandler("account.updated");
        var b = new FakeStripeWebhookHandler("account.updated");

        Assert.Throws<InvalidOperationException>(
            () => new StripeEventDispatcher(new IStripeWebhookHandler[] { a, b }));
    }
}
