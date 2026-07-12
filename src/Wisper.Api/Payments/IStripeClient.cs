namespace Wisper.Api.Payments;

/// <summary>
/// Wisper's wrapper over the Stripe SDK (docs/PAYMENTS.md §1) — the single, config-driven seam every
/// outbound Stripe write goes through, so the billing services (P6.2+) depend on this interface rather
/// than the concrete SDK and can be unit-tested against a fake (Grunt has no Stripe). The real
/// implementation constructs a <see cref="Stripe.StripeClient"/> from the secret key in
/// <see cref="StripeOptions"/>; the test double returns a client that never reaches the network.
/// </summary>
public interface IStripeClient
{
    /// <summary>
    /// True when a Stripe secret key is configured. When false, <see cref="Sdk"/> throws — callers that
    /// might run on a Stripe-less boot should gate on this rather than assume a client exists.
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>
    /// The configured Stripe SDK client, used to construct the service objects later tasks need
    /// (<c>new PaymentIntentService(client.Sdk)</c>, <c>TransferService</c>, <c>AccountService</c>, …).
    /// Throws <see cref="InvalidOperationException"/> when <see cref="IsConfigured"/> is false.
    /// </summary>
    Stripe.IStripeClient Sdk { get; }
}
