namespace Wisper.Api.Payments;

/// <summary>
/// The high-level seam over the Stripe SDK for the host <b>Connect Express</b> writes/reads the onboarding
/// (P6.4) and payout (P6.5) paths need (docs/PAYMENTS.md §5, §6): create an Express account, mint a
/// hosted-onboarding <b>Account Link</b>, read an account's capability snapshot (charges/payouts enabled +
/// outstanding requirements), and create a platform-balance → connected-account <b>Transfer</b> that pays a
/// host out. It sits above <see cref="IStripeClient"/> (which only hands out the raw SDK client) so the
/// <see cref="Wisper.Api.Hosts.HostConnectService"/>, <see cref="Wisper.Api.Payments.PayoutService"/>, and the
/// <c>account.updated</c>/<c>transfer.*</c> webhook handlers are unit-testable against a fake and never reach
/// the network (Grunt has no Stripe). Wisper stores only the account id + derived status -- never any KYC data
/// (docs/PAYMENTS.md §10).
/// </summary>
public interface IStripeConnectGateway
{
    /// <summary>
    /// Creates a Stripe <b>Connect Express</b> account for <paramref name="request"/> and returns its id
    /// (<c>acct_…</c>). Called once, when a host first begins onboarding, to materialize
    /// <see cref="Wisper.Api.Domain.User.ConnectAccountId"/>. Stripe hosts the KYC/identity/bank UI.
    /// </summary>
    Task<string> CreateExpressAccountAsync(ConnectAccountRequest request, CancellationToken ct = default);

    /// <summary>
    /// Creates an <b>Account Link</b> for the connected account and returns the hosted-onboarding URL the host
    /// is redirected to (docs/PAYMENTS.md §5). A fresh link is minted on every call -- links are single-use and
    /// short-lived -- so <c>POST /v1/hosts/connect</c> both <i>creates</i> and <i>continues</i> onboarding.
    /// </summary>
    Task<string> CreateAccountLinkAsync(AccountLinkRequest request, CancellationToken ct = default);

    /// <summary>
    /// Reads the current capability snapshot for the connected account (docs/PAYMENTS.md §5): whether charges
    /// and payouts are enabled, whether details were submitted, and the outstanding onboarding requirements --
    /// the inputs the status endpoint surfaces and <c>connect_status</c> is derived from.
    /// </summary>
    Task<ConnectAccountSnapshot> GetAccountAsync(string accountId, CancellationToken ct = default);

    /// <summary>
    /// Creates a <b>Transfer</b> that moves <see cref="TransferRequest.AmountCents"/> from the platform
    /// balance to the host's connected account (docs/PAYMENTS.md §1, §6) -- how host earnings are paid out.
    /// The Stripe idempotency key is the <c>payouts.id</c> (<see cref="TransferRequest.IdempotencyKey"/>), so a
    /// retried payout run returns the same transfer rather than double-paying. A transfer that cannot be made
    /// (e.g. insufficient platform balance, a connected account that can't receive) fails synchronously here --
    /// the caller records the payout <c>failed</c> and posts no ledger txn, so earnings are retained (§6).
    /// </summary>
    Task<StripeTransfer> CreateTransferAsync(TransferRequest request, CancellationToken ct = default);
}

/// <summary>Inputs for creating a Connect Express account -- the host's identity for the connected account.</summary>
public sealed record ConnectAccountRequest(Guid UserId, string Email);

/// <summary>
/// Inputs for an Account Link: the connected account it onboards, plus the <c>refresh_url</c> (re-mint on an
/// expired link) and <c>return_url</c> (post-onboarding redirect) Stripe sends the host to (docs/PAYMENTS.md §5).
/// </summary>
public sealed record AccountLinkRequest(string AccountId, string RefreshUrl, string ReturnUrl);

/// <summary>
/// A connected account's capability snapshot (docs/PAYMENTS.md §5). <see cref="ChargesEnabled"/> +
/// <see cref="PayoutsEnabled"/> gate <c>connect_status = 'enabled'</c>; <see cref="DetailsSubmitted"/>
/// distinguishes an account still mid-onboarding (<c>pending</c>) from one that finished but Stripe has
/// restricted/is reviewing (<c>restricted</c>). The requirement lists + <see cref="DisabledReason"/> are the
/// "what's still required" surface the status endpoint returns.
/// </summary>
public sealed record ConnectAccountSnapshot(
    bool ChargesEnabled,
    bool PayoutsEnabled,
    bool DetailsSubmitted,
    string? DisabledReason,
    IReadOnlyList<string> CurrentlyDue,
    IReadOnlyList<string> PastDue,
    IReadOnlyList<string> EventuallyDue,
    IReadOnlyList<string> PendingVerification);

/// <summary>
/// Inputs for a payout Transfer (docs/PAYMENTS.md §6): the <see cref="PayoutId"/> (also the Stripe
/// <see cref="IdempotencyKey"/> and the ledger idempotency key), the <see cref="HostUserId"/> whose wallet is
/// paid (stamped into transfer metadata so the <c>transfer.*</c> webhook is a pure function of the event), the
/// destination <see cref="ConnectAccountId"/>, and the amount + currency.
/// </summary>
public sealed record TransferRequest(
    Guid PayoutId,
    Guid HostUserId,
    string ConnectAccountId,
    long AmountCents,
    string Currency,
    string IdempotencyKey);

/// <summary>A created payout Transfer -- its Stripe id (<c>tr_…</c>), pinned onto the <c>payouts</c> row (unique).</summary>
public sealed record StripeTransfer(string Id);

/// <summary>
/// The <see cref="Stripe.Transfer.Metadata"/> keys Wisper stamps on a payout transfer so the
/// <c>transfer.*</c> webhook resolves the <c>payouts</c> row it moves without any ordering assumption
/// (docs/PAYMENTS.md §6, §8). <see cref="TopupMetadata.UserId"/> also rides along, naming the paid host.
/// </summary>
public static class TransferMetadata
{
    /// <summary>Metadata key carrying the Wisper <c>payouts.id</c> (a GUID string) the transfer settles.</summary>
    public const string PayoutId = "wisper_payout_id";
}
