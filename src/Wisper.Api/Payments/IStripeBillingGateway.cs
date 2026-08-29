namespace Wisper.Api.Payments;

/// <summary>
/// The high-level seam over the Stripe SDK for the billing writes P6.2 needs (docs/PAYMENTS.md §3): ensure
/// a customer, create a top-up <b>PaymentIntent</b>, and create a <b>SetupIntent</b> to save a method. It
/// sits above <see cref="IStripeClient"/> (which only hands out the raw SDK client) so the
/// <see cref="Wisper.Api.Billing.BillingService"/> is unit-testable against a fake and never reaches the
/// network (Grunt has no Stripe). Every write carries the caller-supplied idempotency key where one applies
/// (docs/PAYMENTS.md §1) so a retried request cannot double-create.
/// </summary>
public interface IStripeBillingGateway
{
    /// <summary>
    /// Creates a Stripe customer for <paramref name="request"/> and returns its id (<c>cus_…</c>). Called
    /// once, on a consumer's first top-up, to materialize <see cref="Wisper.Api.Domain.User.StripeCustomerId"/>.
    /// </summary>
    Task<string> CreateCustomerAsync(StripeCustomerRequest request, CancellationToken ct = default);

    /// <summary>
    /// Creates a top-up <b>PaymentIntent</b> for <paramref name="request"/> and returns its id +
    /// <c>client_secret</c> (docs/PAYMENTS.md §3). The idempotency key is passed to Stripe so a retry with
    /// the same key returns the same intent rather than creating a second charge.
    /// </summary>
    Task<StripePaymentIntent> CreatePaymentIntentAsync(
        StripePaymentIntentRequest request, CancellationToken ct = default);

    /// <summary>
    /// Creates a <b>SetupIntent</b> to save a payment method for future (off-session) top-ups and returns
    /// its id + <c>client_secret</c> (docs/PAYMENTS.md §3 -- saved methods / auto-recharge).
    /// </summary>
    Task<StripeSetupIntent> CreateSetupIntentAsync(
        StripeSetupIntentRequest request, CancellationToken ct = default);

    /// <summary>
    /// Creates a Stripe <b>Refund</b> against the top-up PaymentIntent in <paramref name="request"/> and
    /// returns its id (<c>re_…</c>) + amount (docs/PAYMENTS.md §3, §7 -- refund of unspent credits). The
    /// idempotency key is passed to Stripe so a retried refund returns the same object rather than refunding
    /// twice; the caller posts the <c>refund</c> ledger txn keyed by the returned refund id, which the
    /// <c>charge.refunded</c> webhook dedupes against.
    /// </summary>
    Task<StripeRefund> CreateRefundAsync(StripeRefundRequest request, CancellationToken ct = default);
}

/// <summary>Inputs for creating a Stripe customer -- the consumer's identity for card-on-file/receipts.</summary>
public sealed record StripeCustomerRequest(Guid UserId, string Email);

/// <summary>
/// Inputs for a top-up PaymentIntent: the amount + currency, the customer it funds, the Wisper user id to
/// stamp into <see cref="Stripe.PaymentIntent.Metadata"/> (so the webhook credits the right wallet, §3, §8),
/// and the API idempotency key forwarded as the Stripe idempotency key.
/// </summary>
public sealed record StripePaymentIntentRequest(
    Guid UserId, string CustomerId, long AmountCents, string Currency, string IdempotencyKey);

/// <summary>Inputs for a SetupIntent: the customer the saved method is attached to.</summary>
public sealed record StripeSetupIntentRequest(Guid UserId, string CustomerId);

/// <summary>A created PaymentIntent -- its id (<c>pi_…</c>) and the client secret the browser confirms with.</summary>
public sealed record StripePaymentIntent(string Id, string ClientSecret);

/// <summary>A created SetupIntent -- its id (<c>seti_…</c>) and the client secret the browser confirms with.</summary>
public sealed record StripeSetupIntent(string Id, string ClientSecret);

/// <summary>
/// Inputs for a refund of unspent credits (docs/PAYMENTS.md §3, §7): the <see cref="UserId"/> whose wallet is
/// debited (stamped into refund metadata so the <c>charge.refunded</c> webhook is a pure function of the
/// event), the <see cref="PaymentIntentId"/> (<c>pi_…</c>) of the top-up being refunded, the amount, and the
/// API idempotency key forwarded as the Stripe idempotency key.
/// </summary>
public sealed record StripeRefundRequest(
    Guid UserId, string PaymentIntentId, long AmountCents, string IdempotencyKey);

/// <summary>A created Refund -- its Stripe id (<c>re_…</c>) and the amount refunded, in cents.</summary>
public sealed record StripeRefund(string Id, long AmountCents);

/// <summary>
/// The <see cref="Stripe.PaymentIntent.Metadata"/> keys Wisper stamps on a top-up intent so the
/// <c>payment_intent.succeeded</c> webhook is a pure function of the event (docs/PAYMENTS.md §3, §8): it
/// credits the wallet named by <see cref="UserId"/> without any extra lookup or ordering assumption.
/// </summary>
public static class TopupMetadata
{
    /// <summary>Metadata key carrying the Wisper user id (a GUID string) whose wallet the top-up funds.</summary>
    public const string UserId = "wisper_user_id";

    /// <summary>Metadata key tagging the intent's purpose, so unrelated PaymentIntents are distinguishable.</summary>
    public const string Purpose = "wisper_purpose";

    /// <summary>The <see cref="Purpose"/> value for a wallet top-up.</summary>
    public const string WalletTopup = "wallet_topup";
}
