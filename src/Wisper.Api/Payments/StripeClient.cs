using Microsoft.Extensions.Options;

namespace Wisper.Api.Payments;

/// <summary>
/// Real <see cref="IStripeClient"/> -- a thin, config-driven wrapper over the Stripe SDK
/// (docs/PAYMENTS.md §1). The <see cref="Stripe.StripeClient"/> is built once from the secret key in
/// <see cref="StripeOptions"/> (lazily, so a Stripe-less boot never touches it); it is thread-safe and
/// intended to be shared, hence the singleton registration. Not exercised by the unit suite (Grunt has
/// no Stripe) -- the fake double is.
/// </summary>
public sealed class StripeClient : IStripeClient
{
    private readonly StripeOptions _options;
    private readonly Lazy<Stripe.IStripeClient> _sdk;

    public StripeClient(IOptions<StripeOptions> options)
    {
        _options = options.Value;
        _sdk = new Lazy<Stripe.IStripeClient>(() => new Stripe.StripeClient(_options.SecretKey));
    }

    public bool IsConfigured => _options.HasSecretKey;

    public Stripe.IStripeClient Sdk => IsConfigured
        ? _sdk.Value
        : throw new InvalidOperationException(
            "Stripe is not configured -- set Stripe:SecretKey (secrets manager) before making Stripe calls.");
}
