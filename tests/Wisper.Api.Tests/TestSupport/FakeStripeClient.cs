using Wisper.Api.Payments;

namespace Wisper.Api.Tests.TestSupport;

/// <summary>
/// A test double for <see cref="IStripeClient"/> (Grunt has no Stripe). It reports configured and exposes a
/// Stripe SDK client built from a dummy test key — the billing services that land in P6.2+ depend on
/// <see cref="IStripeClient"/>, so they can be constructed in unit tests against this without touching the
/// network (their own tests stub the individual Stripe service calls). <see cref="Configured"/> can be
/// flipped to exercise the Stripe-less / fail-closed branches.
/// </summary>
public sealed class FakeStripeClient : IStripeClient
{
    private readonly Stripe.IStripeClient _sdk = new Stripe.StripeClient("sk_test_fake");

    /// <summary>Whether this double reports as configured (default true).</summary>
    public bool Configured { get; set; } = true;

    public bool IsConfigured => Configured;

    public Stripe.IStripeClient Sdk => Configured
        ? _sdk
        : throw new InvalidOperationException("FakeStripeClient is set unconfigured.");
}
