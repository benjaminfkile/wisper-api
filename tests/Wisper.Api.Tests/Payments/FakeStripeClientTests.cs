using Wisper.Api.Tests.TestSupport;
using Xunit;

namespace Wisper.Api.Tests.Payments;

/// <summary>
/// Sanity checks for the <see cref="FakeStripeClient"/> test double: it satisfies the
/// <see cref="Wisper.Api.Payments.IStripeClient"/> contract that downstream billing tests build on —
/// configured by default with a usable SDK client, and fail-closed when flipped unconfigured.
/// </summary>
public class FakeStripeClientTests
{
    [Fact]
    public void Configured_by_default_and_exposes_an_sdk_client()
    {
        var client = new FakeStripeClient();

        Assert.True(client.IsConfigured);
        Assert.NotNull(client.Sdk);
    }

    [Fact]
    public void Fails_closed_when_flipped_unconfigured()
    {
        var client = new FakeStripeClient { Configured = false };

        Assert.False(client.IsConfigured);
        Assert.Throws<InvalidOperationException>(() => client.Sdk);
    }
}
