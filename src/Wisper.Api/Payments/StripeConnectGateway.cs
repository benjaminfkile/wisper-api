using Stripe;

namespace Wisper.Api.Payments;

/// <summary>
/// Real <see cref="IStripeConnectGateway"/> over the Stripe SDK (docs/PAYMENTS.md §5, §6). Each call constructs
/// the relevant Stripe service from the shared <see cref="IStripeClient.Sdk"/>. The connected account is created
/// <b>Express</b> with the <c>transfers</c> capability requested -- Wisper uses separate charges &amp; transfers,
/// so the platform holds funds and pays hosts by Transfer to their connected balance (docs/PAYMENTS.md §1, §6).
/// Not exercised by the unit suite (Grunt has no Stripe) -- the fake double backs the tests; this type is
/// covered by the separate live-integration verification.
/// </summary>
public sealed class StripeConnectGateway : IStripeConnectGateway
{
    private readonly IStripeClient _client;

    public StripeConnectGateway(IStripeClient client) => _client = client;

    public async Task<string> CreateExpressAccountAsync(
        ConnectAccountRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var service = new AccountService(_client.Sdk);
        var account = await service.CreateAsync(
            new AccountCreateOptions
            {
                Type = "express",
                Email = request.Email,
                // Separate charges & transfers: the connected account only needs to receive Transfers.
                Capabilities = new AccountCapabilitiesOptions
                {
                    Transfers = new AccountCapabilitiesTransfersOptions { Requested = true },
                },
                Metadata = new Dictionary<string, string>
                {
                    [TopupMetadata.UserId] = request.UserId.ToString(),
                },
            },
            cancellationToken: ct);
        return account.Id;
    }

    public async Task<string> CreateAccountLinkAsync(
        AccountLinkRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var service = new AccountLinkService(_client.Sdk);
        var link = await service.CreateAsync(
            new AccountLinkCreateOptions
            {
                Account = request.AccountId,
                RefreshUrl = request.RefreshUrl,
                ReturnUrl = request.ReturnUrl,
                Type = "account_onboarding",
            },
            cancellationToken: ct);
        return link.Url;
    }

    public async Task<ConnectAccountSnapshot> GetAccountAsync(string accountId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        var service = new AccountService(_client.Sdk);
        var account = await service.GetAsync(accountId, cancellationToken: ct);
        return ToSnapshot(account);
    }

    public async Task<StripeTransfer> CreateTransferAsync(TransferRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var service = new TransferService(_client.Sdk);
        var transfer = await service.CreateAsync(
            new TransferCreateOptions
            {
                Amount = request.AmountCents,
                Currency = request.Currency,
                Destination = request.ConnectAccountId,
                Metadata = new Dictionary<string, string>
                {
                    [TransferMetadata.PayoutId] = request.PayoutId.ToString(),
                    [TopupMetadata.UserId] = request.HostUserId.ToString(),
                },
            },
            // The idempotency key = the payouts.id, so a retried run returns the same transfer rather than
            // paying twice (docs/PAYMENTS.md §6). payouts.stripe_transfer_id is unique as the DB backstop.
            new RequestOptions { IdempotencyKey = request.IdempotencyKey },
            ct);
        return new StripeTransfer(transfer.Id);
    }

    /// <summary>Projects a Stripe <see cref="Account"/> to the capability snapshot Wisper reasons over.</summary>
    internal static ConnectAccountSnapshot ToSnapshot(Account account)
    {
        ArgumentNullException.ThrowIfNull(account);
        var requirements = account.Requirements;
        return new ConnectAccountSnapshot(
            ChargesEnabled: account.ChargesEnabled,
            PayoutsEnabled: account.PayoutsEnabled,
            DetailsSubmitted: account.DetailsSubmitted,
            DisabledReason: requirements?.DisabledReason,
            CurrentlyDue: requirements?.CurrentlyDue ?? new List<string>(),
            PastDue: requirements?.PastDue ?? new List<string>(),
            EventuallyDue: requirements?.EventuallyDue ?? new List<string>(),
            PendingVerification: requirements?.PendingVerification ?? new List<string>());
    }
}
