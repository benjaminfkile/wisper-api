using Stripe;

namespace Wisper.Api.Payments.Handlers;

/// <summary>
/// Stub handler for the refund + dispute events (docs/PAYMENTS.md §7, §8.5): <c>charge.refunded</c> and
/// <c>charge.dispute.created</c>/<c>.closed</c>. The real ledger effects — post <c>refund</c> /
/// <c>chargeback</c> and suspend on dispute — land with refunds/disputes (P6.6). The top-up path
/// (<c>payment_intent.succeeded</c>) is now live in <see cref="TopupWebhookHandler"/>. For now this
/// recognises and acknowledges its events so the ingest pipeline, dedupe, and status recording stay
/// exercisable end-to-end; each future effect will be keyed by the Stripe event id so re-delivery is a no-op.
/// </summary>
public sealed class PaymentWebhookHandler : IStripeWebhookHandler
{
    private readonly ILogger<PaymentWebhookHandler> _logger;

    public PaymentWebhookHandler(ILogger<PaymentWebhookHandler> logger) => _logger = logger;

    public IReadOnlyCollection<string> EventTypes { get; } = new[]
    {
        Stripe.EventTypes.ChargeRefunded,
        Stripe.EventTypes.ChargeDisputeCreated,
        Stripe.EventTypes.ChargeDisputeClosed,
    };

    public Task HandleAsync(Event evt, CancellationToken ct = default)
    {
        // Stub (P6.6): recognised, no ledger effect yet.
        _logger.LogInformation("stripe payment webhook {Type} ({Id}) received — handler is a stub (P6.6)",
            evt.Type, evt.Id);
        return Task.CompletedTask;
    }
}
