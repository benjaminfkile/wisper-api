using Stripe;

namespace Wisper.Api.Payments.Handlers;

/// <summary>
/// Stub handler for host-payout events (docs/PAYMENTS.md §6, §8.5): platform <c>transfer.*</c>
/// (created/failed/reversed → update <c>payouts.status</c> and commit/roll back the <c>payout</c> ledger
/// txn) and the informational connected <c>payout.*</c> (transfer→bank status). The real logic lands with
/// host payouts (P6.5). For now it recognises and acknowledges the events; each future effect is keyed by
/// the transfer / payout id so a re-delivery cannot double-post.
/// </summary>
public sealed class TransferWebhookHandler : IStripeWebhookHandler
{
    private readonly ILogger<TransferWebhookHandler> _logger;

    public TransferWebhookHandler(ILogger<TransferWebhookHandler> logger) => _logger = logger;

    // Note: docs/PAYMENTS.md §8.5 lists "transfer.failed", but Stripe emits no such webhook — a platform
    // Transfer to a connected account fails synchronously on the API call, and asynchronous reversals arrive
    // as `transfer.reversed`. So the failure path is covered by the API response (P6.5) + `transfer.reversed`.
    public IReadOnlyCollection<string> EventTypes { get; } = new[]
    {
        Stripe.EventTypes.TransferCreated,
        Stripe.EventTypes.TransferReversed,
        Stripe.EventTypes.PayoutPaid,
        Stripe.EventTypes.PayoutFailed,
    };

    public Task HandleAsync(Event evt, CancellationToken ct = default)
    {
        // Stub (P6.5): recognised, no payout state change yet.
        _logger.LogInformation("stripe transfer webhook {Type} ({Id}) received — handler is a stub (P6.5)",
            evt.Type, evt.Id);
        return Task.CompletedTask;
    }
}
