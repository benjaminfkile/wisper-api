namespace Wisper.Api.Payments;

/// <summary>
/// A handler for one family of Stripe webhook event types (docs/PAYMENTS.md §8.5). The dispatcher routes a
/// verified event to the handler whose <see cref="EventTypes"/> claim its <c>type</c>. Handlers are
/// <b>idempotent</b> and <b>order-independent</b>: each is a pure function of the event plus current ledger
/// state, keyed so a re-delivery is a no-op (§8.3) — so a handler may be invoked more than once for the
/// same event (Stripe re-delivery, sweeper re-attempt) and must converge to the same state. A handler that
/// throws leaves the event <c>failed</c> for retry (§8.4).
/// </summary>
public interface IStripeWebhookHandler
{
    /// <summary>The Stripe event <c>type</c> strings this handler claims (e.g. <c>payment_intent.succeeded</c>).</summary>
    IReadOnlyCollection<string> EventTypes { get; }

    /// <summary>
    /// Applies the (already signature-verified) <paramref name="evt"/>. Returning normally marks the event
    /// <c>processed</c>; throwing marks it <c>failed</c> and lets Stripe re-deliver.
    /// </summary>
    Task HandleAsync(Stripe.Event evt, CancellationToken ct = default);
}
