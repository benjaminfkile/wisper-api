using Stripe;
using Wisper.Api.Domain;
using Wisper.Api.Ledger;

namespace Wisper.Api.Payments.Handlers;

/// <summary>
/// The wallet top-up handler (docs/PAYMENTS.md §3, §8) -- the <b>only</b> place a top-up credits the wallet.
/// On <c>payment_intent.succeeded</c> it posts a <c>topup</c> ledger transaction (platform_cash + stripe_fees
/// → user_wallet) keyed by the <b>Stripe event id</b>, so a duplicate or out-of-order re-delivery dedupes at
/// the ledger and the wallet is credited <b>exactly once</b> -- never on the API response (§3: credit is
/// webhook-driven, never response-driven). On <c>payment_intent.payment_failed</c> it surfaces the failure
/// with no ledger effect. The target wallet is read from the intent's <see cref="TopupMetadata.UserId"/>
/// metadata, so the handler is a pure function of the event with no ordering assumption (§8.3).
/// </summary>
public sealed class TopupWebhookHandler : IStripeWebhookHandler
{
    private readonly LedgerService _ledger;
    private readonly ILogger<TopupWebhookHandler> _logger;

    public TopupWebhookHandler(LedgerService ledger, ILogger<TopupWebhookHandler> logger)
    {
        _ledger = ledger;
        _logger = logger;
    }

    public IReadOnlyCollection<string> EventTypes { get; } = new[]
    {
        Stripe.EventTypes.PaymentIntentSucceeded,
        Stripe.EventTypes.PaymentIntentPaymentFailed,
    };

    public async Task HandleAsync(Event evt, CancellationToken ct = default)
    {
        // Without a PaymentIntent there is no wallet to credit and no retry would help -- a logged no-op is
        // correct (and keeps a synthetic/id-only event a benign Processed rather than a wedged failure).
        if (evt.Data?.Object is not PaymentIntent intent)
        {
            _logger.LogWarning("stripe event {Id} ({Type}) carried no PaymentIntent object -- no-op",
                evt.Id, evt.Type);
            return;
        }

        if (evt.Type == Stripe.EventTypes.PaymentIntentPaymentFailed)
        {
            // Surface only -- a failed top-up has no ledger effect (docs/PAYMENTS.md §3).
            _logger.LogInformation("top-up payment failed for intent {Intent} ({Event}) -- no ledger effect",
                intent.Id, evt.Id);
            return;
        }

        // Not a wallet top-up (or missing/garbled metadata) → nothing to credit. Treat as a benign no-op so
        // a foreign PaymentIntent that happens to reach this endpoint doesn't wedge the pipeline.
        if (intent.Metadata is null ||
            !intent.Metadata.TryGetValue(TopupMetadata.UserId, out var rawUserId) ||
            !Guid.TryParse(rawUserId, out var userId))
        {
            _logger.LogWarning(
                "payment_intent.succeeded {Intent} ({Event}) has no {Key} metadata -- not crediting a wallet",
                intent.Id, evt.Id, TopupMetadata.UserId);
            return;
        }

        var currency = string.IsNullOrWhiteSpace(intent.Currency) ? "usd" : intent.Currency;
        var grossCents = intent.AmountReceived > 0 ? intent.AmountReceived : intent.Amount;
        if (grossCents <= 0)
        {
            _logger.LogWarning("top-up intent {Intent} ({Event}) has non-positive amount {Amount} -- skipping",
                intent.Id, evt.Id, grossCents);
            return;
        }

        // Platform absorbs the Stripe processing fee (docs/PAYMENTS.md §3); read it when the charge's balance
        // transaction is expanded on the event, else treat it as 0 (platform_cash takes the gross). Either
        // way the wallet is credited the GROSS, which is all that must be exactly-once.
        var stripeFeeCents = ResolveStripeFeeCents(intent);

        var wallet = await _ledger.GetOrCreateAccountAsync(LedgerAccountKind.UserWallet, userId, currency, ct);
        var platformCash = await _ledger.GetOrCreateAccountAsync(LedgerAccountKind.PlatformCash, null, currency, ct);
        var stripeFees = await _ledger.GetOrCreateAccountAsync(LedgerAccountKind.StripeFees, null, currency, ct);

        // Keyed by the STRIPE EVENT ID -- a re-delivery of the same event dedupes at the ledger and posts
        // nothing (docs/PAYMENTS.md §8.3). This is the exactly-once credit guarantee.
        var draft = LedgerFlows.Topup(
            wallet.Id, platformCash.Id, stripeFees.Id,
            grossAmountCents: grossCents,
            stripeFeeCents: stripeFeeCents,
            idempotencyKey: evt.Id,
            externalRef: intent.Id,
            memo: $"topup {intent.Id} for user {userId}");
        var posted = await _ledger.PostAsync(draft, ct);

        _logger.LogInformation(
            "top-up credited wallet for user {User}: +{Gross}¢ (fee {Fee}¢) from intent {Intent} ({Event}){Replay}",
            userId, grossCents, stripeFeeCents, intent.Id, evt.Id,
            posted.WasDeduplicated ? " [replay]" : string.Empty);
    }

    /// <summary>
    /// The Stripe processing fee for this top-up, in cents -- read from the latest charge's balance
    /// transaction when Stripe expanded it on the event, else <c>0</c>. The fee only splits the platform's
    /// own <c>platform_cash</c>/<c>stripe_fees</c> legs; it never changes the wallet credit.
    /// </summary>
    private static long ResolveStripeFeeCents(PaymentIntent intent)
    {
        var fee = intent.LatestCharge?.BalanceTransaction?.Fee ?? 0;
        return fee > 0 && fee <= (intent.AmountReceived > 0 ? intent.AmountReceived : intent.Amount) ? fee : 0;
    }
}
