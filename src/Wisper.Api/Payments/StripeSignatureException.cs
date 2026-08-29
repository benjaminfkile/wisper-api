namespace Wisper.Api.Payments;

/// <summary>
/// Thrown by <see cref="IStripeSignatureVerifier"/> when the <c>Stripe-Signature</c> header is missing,
/// malformed, stale, or does not match the endpoint's signing secret (docs/PAYMENTS.md §8.1). The webhook
/// endpoint maps it to a <c>400</c> with <b>no processing</b> -- a forged or corrupt body never reaches a
/// handler or the event store.
/// </summary>
public sealed class StripeSignatureException : Exception
{
    public StripeSignatureException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}
