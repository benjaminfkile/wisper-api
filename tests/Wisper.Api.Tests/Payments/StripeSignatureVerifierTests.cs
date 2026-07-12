using Microsoft.Extensions.Options;
using Wisper.Api.Payments;
using Xunit;

namespace Wisper.Api.Tests.Payments;

/// <summary>
/// Unit tests for the real <see cref="StripeSignatureVerifier"/> (docs/PAYMENTS.md §8.1). The HMAC check is
/// local (no network), so the failure paths are testable offline: an unset signing secret is a server
/// misconfiguration (<see cref="InvalidOperationException"/>), while a missing or non-matching
/// <c>Stripe-Signature</c> is a caller error surfaced as <see cref="StripeSignatureException"/> — which the
/// endpoint turns into a 400 with no processing.
/// </summary>
public class StripeSignatureVerifierTests
{
    private static StripeSignatureVerifier VerifierWith(string? signingSecret) =>
        new(Options.Create(new StripeOptions { WebhookSigningSecret = signingSecret }));

    [Fact]
    public void Unset_signing_secret_is_a_server_misconfiguration()
    {
        var verifier = VerifierWith(null);

        Assert.Throws<InvalidOperationException>(() => verifier.Verify("{}", "t=1,v1=abc"));
    }

    [Fact]
    public void Missing_signature_header_is_rejected()
    {
        var verifier = VerifierWith("whsec_test");

        Assert.Throws<StripeSignatureException>(() => verifier.Verify("{}", null));
        Assert.Throws<StripeSignatureException>(() => verifier.Verify("{}", ""));
    }

    [Fact]
    public void A_signature_that_does_not_match_is_rejected()
    {
        var verifier = VerifierWith("whsec_test");

        // A well-formed but wrong signature must not verify against the body.
        Assert.Throws<StripeSignatureException>(
            () => verifier.Verify("""{"id":"evt_1"}""", "t=1500000000,v1=deadbeefdeadbeef"));
    }
}
