using Microsoft.Extensions.Options;
using Wisper.Api.Payments;
using Xunit;

namespace Wisper.Api.Tests.Payments;

/// <summary>
/// Unit tests for the real <see cref="StripeClient"/> wrapper (docs/PAYMENTS.md §1): it is config-driven and
/// <b>fails closed</b> — with no secret key it reports unconfigured and refuses to hand out an SDK client,
/// so a Stripe-less boot cannot silently make calls. With a key it constructs the shared SDK client.
/// </summary>
public class StripeClientTests
{
    private static StripeClient ClientWith(string? secretKey) =>
        new(Options.Create(new StripeOptions { SecretKey = secretKey }));

    [Fact]
    public void Unconfigured_when_no_secret_key()
    {
        var client = ClientWith(null);

        Assert.False(client.IsConfigured);
        Assert.Throws<InvalidOperationException>(() => client.Sdk);
    }

    [Fact]
    public void Blank_secret_key_is_treated_as_unconfigured()
    {
        var client = ClientWith("   ");

        Assert.False(client.IsConfigured);
        Assert.Throws<InvalidOperationException>(() => client.Sdk);
    }

    [Fact]
    public void Configured_client_exposes_a_shared_sdk_instance()
    {
        var client = ClientWith("sk_test_123");

        Assert.True(client.IsConfigured);
        Assert.NotNull(client.Sdk);
        Assert.Same(client.Sdk, client.Sdk); // built once, shared
    }
}
