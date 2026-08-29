using Wisper.Api.Payments;

namespace Wisper.Api.Tests.TestSupport;

/// <summary>
/// A test double for <see cref="IStripeConnectGateway"/> (Grunt has no Stripe). It records every call so
/// tests can assert an Express account was created once, the Account Link carried the account id + the
/// configured refresh/return URLs, etc., and returns canned ids/URLs plus a configurable account snapshot
/// without touching the network.
/// </summary>
public sealed class FakeStripeConnectGateway : IStripeConnectGateway
{
    public List<ConnectAccountRequest> AccountCalls { get; } = new();
    public List<AccountLinkRequest> LinkCalls { get; } = new();
    public List<string> AccountFetches { get; } = new();
    public List<TransferRequest> TransferCalls { get; } = new();

    /// <summary>When set, <see cref="CreateTransferAsync"/> throws it -- to exercise the failed-transfer path.</summary>
    public Exception? TransferError { get; set; }

    /// <summary>The transfer id handed back from <see cref="CreateTransferAsync"/> (per call, suffixed).</summary>
    public string TransferId { get; set; } = "tr_fake";

    /// <summary>The account id handed back from <see cref="CreateExpressAccountAsync"/>.</summary>
    public string AccountId { get; set; } = "acct_fake";

    /// <summary>The onboarding URL handed back from <see cref="CreateAccountLinkAsync"/>.</summary>
    public string OnboardingUrl { get; set; } = "https://connect.stripe.test/onboard/fake";

    /// <summary>The snapshot handed back from <see cref="GetAccountAsync"/> (default: fresh, mid-onboarding).</summary>
    public ConnectAccountSnapshot Snapshot { get; set; } = new(
        ChargesEnabled: false,
        PayoutsEnabled: false,
        DetailsSubmitted: false,
        DisabledReason: "requirements.past_due",
        CurrentlyDue: new[] { "external_account", "tos_acceptance.date" },
        PastDue: Array.Empty<string>(),
        EventuallyDue: Array.Empty<string>(),
        PendingVerification: Array.Empty<string>());

    public Task<string> CreateExpressAccountAsync(ConnectAccountRequest request, CancellationToken ct = default)
    {
        AccountCalls.Add(request);
        return Task.FromResult(AccountId);
    }

    public Task<string> CreateAccountLinkAsync(AccountLinkRequest request, CancellationToken ct = default)
    {
        LinkCalls.Add(request);
        return Task.FromResult(OnboardingUrl);
    }

    public Task<ConnectAccountSnapshot> GetAccountAsync(string accountId, CancellationToken ct = default)
    {
        AccountFetches.Add(accountId);
        return Task.FromResult(Snapshot);
    }

    public Task<StripeTransfer> CreateTransferAsync(TransferRequest request, CancellationToken ct = default)
    {
        TransferCalls.Add(request);
        if (TransferError is not null)
        {
            return Task.FromException<StripeTransfer>(TransferError);
        }

        // A stable per-payout id so a retried run (same payouts.id) can be told apart in assertions.
        return Task.FromResult(new StripeTransfer($"{TransferId}_{TransferCalls.Count}"));
    }
}
