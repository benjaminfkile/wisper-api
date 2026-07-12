namespace Wisper.Api.Payments;

/// <summary>
/// Verifies the <c>Stripe-Signature</c> of a raw webhook body against the endpoint's signing secret and
/// parses it into a <see cref="Stripe.Event"/> (docs/PAYMENTS.md §8.1). This is the security boundary of
/// the webhook: nothing about an inbound event is trusted until it verifies here. Behind an interface so
/// the ingest path can be unit-tested with a fake (Grunt has no Stripe signing secret); the real
/// implementation delegates to Stripe's own <c>EventUtility</c>.
/// </summary>
public interface IStripeSignatureVerifier
{
    /// <summary>
    /// Verifies <paramref name="payload"/> (the exact raw request body) against
    /// <paramref name="signatureHeader"/> (the <c>Stripe-Signature</c> value) and returns the parsed
    /// event. Throws <see cref="StripeSignatureException"/> when the header is absent or the signature
    /// does not verify.
    /// </summary>
    Stripe.Event Verify(string payload, string? signatureHeader);
}
