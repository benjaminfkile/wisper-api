using Wisper.Api.Payments;

namespace Wisper.Api.Tests.TestSupport;

/// <summary>
/// A controllable <see cref="IStripeSignatureVerifier"/> for unit tests (Grunt has no Stripe signing
/// secret, so the real HMAC verifier can't run against fixtures). The verify step is the trust boundary of
/// the webhook, so the ingest tests inject this to simulate a good signature (returns a prepared
/// <see cref="Stripe.Event"/>) or a bad one (throws <see cref="StripeSignatureException"/>) without any
/// real cryptography.
/// </summary>
public sealed class FakeStripeSignatureVerifier : IStripeSignatureVerifier
{
    private readonly Func<string, string?, Stripe.Event> _impl;

    /// <summary>The (payload, signature) pairs seen — lets a test assert the raw body reached the verifier.</summary>
    public List<(string Payload, string? Signature)> Calls { get; } = new();

    public FakeStripeSignatureVerifier(Func<string, string?, Stripe.Event> impl) => _impl = impl;

    public Stripe.Event Verify(string payload, string? signatureHeader)
    {
        Calls.Add((payload, signatureHeader));
        return _impl(payload, signatureHeader);
    }

    /// <summary>A verifier that accepts any body and returns the given event.</summary>
    public static FakeStripeSignatureVerifier Returning(Stripe.Event evt) => new((_, _) => evt);

    /// <summary>A verifier that rejects every body as a bad signature.</summary>
    public static FakeStripeSignatureVerifier Rejecting() =>
        new((_, _) => throw new StripeSignatureException("bad signature (fake)"));
}
