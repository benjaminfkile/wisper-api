using Wisper.Api.Domain;
using Xunit;

namespace Wisper.Api.Tests.Domain;

/// <summary>
/// The Connect capability rules (docs/PAYMENTS.md §5): deriving <see cref="ConnectStatus"/> from a Stripe
/// account's flags (<see cref="ConnectStatusEvaluator"/>) and gating online/payouts on it
/// (<see cref="ConnectGate"/>). Pure logic — the single place the "enabled ⇒ earn, restricted ⇒ hold payouts"
/// rule is verified.
/// </summary>
public class ConnectGateTests
{
    [Fact]
    public void Both_capabilities_enabled_is_enabled()
    {
        Assert.Equal(
            ConnectStatus.Enabled,
            ConnectStatusEvaluator.Evaluate(chargesEnabled: true, payoutsEnabled: true, detailsSubmitted: true));
    }

    [Fact]
    public void Details_submitted_but_not_fully_enabled_is_restricted()
    {
        // Finished intake, Stripe holds the account (needs more info / reviewing) — restricted, not pending.
        Assert.Equal(
            ConnectStatus.Restricted,
            ConnectStatusEvaluator.Evaluate(chargesEnabled: false, payoutsEnabled: true, detailsSubmitted: true));
        Assert.Equal(
            ConnectStatus.Restricted,
            ConnectStatusEvaluator.Evaluate(chargesEnabled: true, payoutsEnabled: false, detailsSubmitted: true));
    }

    [Fact]
    public void Not_enabled_and_no_details_is_pending()
    {
        // Fresh account still mid-onboarding.
        Assert.Equal(
            ConnectStatus.Pending,
            ConnectStatusEvaluator.Evaluate(chargesEnabled: false, payoutsEnabled: false, detailsSubmitted: false));
    }

    [Theory]
    [InlineData(ConnectStatus.None, false)]
    [InlineData(ConnectStatus.Pending, false)]
    [InlineData(ConnectStatus.Restricted, false)]
    [InlineData(ConnectStatus.Enabled, true)]
    [InlineData(ConnectStatus.Disabled, false)]
    public void Only_enabled_may_go_online(ConnectStatus status, bool expected)
    {
        Assert.Equal(expected, ConnectGate.CanGoOnline(status));
    }

    [Theory]
    [InlineData(ConnectStatus.None, false)]
    [InlineData(ConnectStatus.Pending, false)]
    [InlineData(ConnectStatus.Restricted, false)] // restricted holds payouts (earnings still accrue)
    [InlineData(ConnectStatus.Enabled, true)]
    [InlineData(ConnectStatus.Disabled, false)]
    public void Only_enabled_receives_payouts(ConnectStatus status, bool expected)
    {
        Assert.Equal(expected, ConnectGate.CanReceivePayouts(status));
    }
}
