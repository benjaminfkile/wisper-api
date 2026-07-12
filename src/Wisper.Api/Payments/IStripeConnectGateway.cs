namespace Wisper.Api.Payments;

/// <summary>
/// The high-level seam over the Stripe SDK for the host <b>Connect Express</b> onboarding writes/reads P6.4
/// needs (docs/PAYMENTS.md §5): create an Express account, mint a hosted-onboarding <b>Account Link</b>, and
/// read an account's capability snapshot (charges/payouts enabled + outstanding requirements). It sits above
/// <see cref="IStripeClient"/> (which only hands out the raw SDK client) so the
/// <see cref="Wisper.Api.Hosts.HostConnectService"/> and the <c>account.updated</c> webhook handler are
/// unit-testable against a fake and never reach the network (Grunt has no Stripe). Wisper stores only the
/// account id + derived status — never any KYC data (docs/PAYMENTS.md §10).
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
    /// is redirected to (docs/PAYMENTS.md §5). A fresh link is minted on every call — links are single-use and
    /// short-lived — so <c>POST /v1/hosts/connect</c> both <i>creates</i> and <i>continues</i> onboarding.
    /// </summary>
    Task<string> CreateAccountLinkAsync(AccountLinkRequest request, CancellationToken ct = default);

    /// <summary>
    /// Reads the current capability snapshot for the connected account (docs/PAYMENTS.md §5): whether charges
    /// and payouts are enabled, whether details were submitted, and the outstanding onboarding requirements —
    /// the inputs the status endpoint surfaces and <c>connect_status</c> is derived from.
    /// </summary>
    Task<ConnectAccountSnapshot> GetAccountAsync(string accountId, CancellationToken ct = default);
}

/// <summary>Inputs for creating a Connect Express account — the host's identity for the connected account.</summary>
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
