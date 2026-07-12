using Stripe;

namespace Wisper.Api.Payments.Handlers;

/// <summary>
/// Stub handler for Connect account + saved-method events (docs/PAYMENTS.md §5, §8.5): <c>account.updated</c>
/// (recompute <c>connect_status</c> from <c>charges_enabled</c>/<c>payouts_enabled</c> to gate a host going
/// <c>online</c>) and <c>setup_intent.succeeded</c> (mark a saved payment method usable). The real logic
/// lands with Connect onboarding (P6.4) and saved methods / auto-recharge (P6.2). For now it recognises and
/// acknowledges the events; each future effect stays idempotent on the account/current state.
/// </summary>
public sealed class AccountWebhookHandler : IStripeWebhookHandler
{
    private readonly ILogger<AccountWebhookHandler> _logger;

    public AccountWebhookHandler(ILogger<AccountWebhookHandler> logger) => _logger = logger;

    public IReadOnlyCollection<string> EventTypes { get; } = new[]
    {
        Stripe.EventTypes.AccountUpdated,
        Stripe.EventTypes.SetupIntentSucceeded,
    };

    public Task HandleAsync(Event evt, CancellationToken ct = default)
    {
        // Stub (P6.4 / P6.2): recognised, no state change yet.
        _logger.LogInformation("stripe account webhook {Type} ({Id}) received — handler is a stub (P6.4/P6.2)",
            evt.Type, evt.Id);
        return Task.CompletedTask;
    }
}
