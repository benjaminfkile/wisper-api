using Stripe;

namespace Wisper.Api.Payments.Handlers;

/// <summary>
/// Stub handler for the consumer-money events (docs/PAYMENTS.md §8.5): PaymentIntent success/failure,
/// refunds, and disputes. The real ledger effects — post <c>topup</c> and credit the wallet on
/// <c>payment_intent.succeeded</c>, post <c>refund</c> / <c>chargeback</c>, suspend on dispute — land with
/// wallet top-up (P6.2) and refunds/disputes (P6.6). For now it recognises and acknowledges the events so
/// the ingest pipeline, dedupe, and status recording are exercisable end-to-end; each future effect will
/// be keyed by the Stripe event id so re-delivery stays a no-op.
/// </summary>
public sealed class PaymentWebhookHandler : IStripeWebhookHandler
{
    private readonly ILogger<PaymentWebhookHandler> _logger;

    public PaymentWebhookHandler(ILogger<PaymentWebhookHandler> logger) => _logger = logger;

    public IReadOnlyCollection<string> EventTypes { get; } = new[]
    {
        Stripe.EventTypes.PaymentIntentSucceeded,
        Stripe.EventTypes.PaymentIntentPaymentFailed,
        Stripe.EventTypes.ChargeRefunded,
        Stripe.EventTypes.ChargeDisputeCreated,
        Stripe.EventTypes.ChargeDisputeClosed,
    };

    public Task HandleAsync(Event evt, CancellationToken ct = default)
    {
        // Stub (P6.2 / P6.6): recognised, no ledger effect yet.
        _logger.LogInformation("stripe payment webhook {Type} ({Id}) received — handler is a stub (P6.2/P6.6)",
            evt.Type, evt.Id);
        return Task.CompletedTask;
    }
}
