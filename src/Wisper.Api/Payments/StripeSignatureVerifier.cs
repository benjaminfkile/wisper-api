using Microsoft.Extensions.Options;

namespace Wisper.Api.Payments;

/// <summary>
/// Real <see cref="IStripeSignatureVerifier"/> — delegates to Stripe's <see cref="Stripe.EventUtility"/>,
/// which recomputes the HMAC over the raw body with the endpoint signing secret and enforces the
/// timestamp tolerance (replay guard) before parsing the event (docs/PAYMENTS.md §8.1). Any verification
/// failure — missing header, bad signature, stale timestamp — surfaces as a
/// <see cref="StripeSignatureException"/> so the endpoint answers <c>400</c> with no processing. An
/// unset signing secret is a <b>server misconfiguration</b> (surfaced as <c>500</c>), deliberately
/// distinct from a caller sending a bad signature. Not exercised by the unit suite; the fake verifier is.
/// </summary>
public sealed class StripeSignatureVerifier : IStripeSignatureVerifier
{
    private readonly StripeOptions _options;

    public StripeSignatureVerifier(IOptions<StripeOptions> options) => _options = options.Value;

    public Stripe.Event Verify(string payload, string? signatureHeader)
    {
        if (!_options.HasWebhookSecret)
        {
            throw new InvalidOperationException(
                "Stripe webhook signing secret is not configured — set Stripe:WebhookSigningSecret.");
        }

        if (string.IsNullOrEmpty(signatureHeader))
        {
            throw new StripeSignatureException("Missing Stripe-Signature header.");
        }

        try
        {
            // throwOnApiVersionMismatch:false — the event's API version is Stripe's, not ours; a skew
            // between the account's version and the pinned SDK must not reject a legitimately signed body.
            return Stripe.EventUtility.ConstructEvent(
                payload,
                signatureHeader,
                _options.WebhookSigningSecret,
                _options.WebhookToleranceSeconds,
                throwOnApiVersionMismatch: false);
        }
        catch (Stripe.StripeException ex)
        {
            throw new StripeSignatureException("Stripe-Signature verification failed.", ex);
        }
    }
}
