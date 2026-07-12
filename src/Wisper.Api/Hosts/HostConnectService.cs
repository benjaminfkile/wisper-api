using Microsoft.Extensions.Options;
using Wisper.Api.Domain;
using Wisper.Api.Infrastructure;
using Wisper.Api.Payments;
using Wisper.Api.Persistence.Users;

namespace Wisper.Api.Hosts;

/// <summary>
/// The host Stripe Connect onboarding surface (docs/API.md §6, docs/PAYMENTS.md §5). It drives
/// <c>POST /v1/hosts/connect</c> (ensure an Express account on the caller's <c>users</c> row → mint a hosted
/// Account Link → return the onboarding URL) and <c>GET /v1/hosts/connect/status</c> (read the account's
/// capability snapshot → derive <c>connect_status</c> + surface outstanding requirements). Onboarding
/// completion is never inferred from an HTTP response — it is confirmed only by the <c>account.updated</c>
/// webhook (docs/PAYMENTS.md §5, §8), which is authoritative; the status read here reconciles opportunistically.
/// Everything depends on interfaces so the unit suite runs without Stripe/Postgres.
/// </summary>
public sealed class HostConnectService
{
    private readonly IStripeConnectGateway _stripe;
    private readonly IUserRepository _users;
    private readonly StripeOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<HostConnectService> _logger;

    public HostConnectService(
        IStripeConnectGateway stripe,
        IUserRepository users,
        IOptions<StripeOptions> options,
        TimeProvider time,
        ILogger<HostConnectService> logger)
    {
        _stripe = stripe;
        _users = users;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Creates or continues Connect Express onboarding for the caller (docs/PAYMENTS.md §5): materializes a
    /// Connect account on first use (storing <c>connect_account_id</c> and moving <c>connect_status</c> to
    /// <c>pending</c>), then mints a fresh single-use Account Link and returns its <c>onboarding_url</c>.
    /// </summary>
    public async Task<HostConnectResponse> StartOnboardingAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _users.GetByIdAsync(userId, ct)
            ?? throw new ApiException(ApiErrorCode.NotFound, "The account no longer exists.");

        var (accountId, connectStatus) = await EnsureConnectAccountAsync(user, ct);

        var refreshUrl = RequireConfiguredUrl(_options.ConnectRefreshUrl, nameof(StripeOptions.ConnectRefreshUrl));
        var returnUrl = RequireConfiguredUrl(_options.ConnectReturnUrl, nameof(StripeOptions.ConnectReturnUrl));

        var onboardingUrl = await _stripe.CreateAccountLinkAsync(
            new AccountLinkRequest(accountId, refreshUrl, returnUrl), ct);

        _logger.LogInformation(
            "connect onboarding link minted for user {User} account {Account} (status {Status})",
            user.Id, accountId, connectStatus);
        return new HostConnectResponse(onboardingUrl, PgEnum.ToLabel(connectStatus));
    }

    /// <summary>
    /// The caller's Connect onboarding state (docs/PAYMENTS.md §5): reads the connected account's live
    /// capability snapshot, derives <c>connect_status</c>, reconciles the stored value opportunistically, and
    /// returns it with the outstanding requirements. A host with no account yet reports its stored status
    /// (<c>none</c>) and an empty requirement set.
    /// </summary>
    public async Task<HostConnectStatusResponse> GetStatusAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _users.GetByIdAsync(userId, ct)
            ?? throw new ApiException(ApiErrorCode.NotFound, "The account no longer exists.");

        if (string.IsNullOrWhiteSpace(user.ConnectAccountId))
        {
            // No Connect account has been created — there is nothing to query Stripe for yet.
            return new HostConnectStatusResponse(
                ConnectStatus: PgEnum.ToLabel(user.ConnectStatus),
                ChargesEnabled: false,
                PayoutsEnabled: false,
                DetailsSubmitted: false,
                CanGoOnline: ConnectGate.CanGoOnline(user.ConnectStatus),
                Requirements: HostConnectRequirements.Empty);
        }

        var snapshot = await _stripe.GetAccountAsync(user.ConnectAccountId, ct);
        var derived = ConnectStatusEvaluator.Evaluate(
            snapshot.ChargesEnabled, snapshot.PayoutsEnabled, snapshot.DetailsSubmitted);

        // Keep the stored status in sync with the live read. The account.updated webhook is the primary,
        // order-independent source of truth (§8); this is a best-effort reconcile, never load-bearing.
        if (derived != user.ConnectStatus)
        {
            try
            {
                await _users.UpdateAsync(
                    user with { ConnectStatus = derived, UpdatedAt = _time.GetUtcNow() }, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "failed to reconcile connect_status for user {User}", user.Id);
            }
        }

        return new HostConnectStatusResponse(
            ConnectStatus: PgEnum.ToLabel(derived),
            ChargesEnabled: snapshot.ChargesEnabled,
            PayoutsEnabled: snapshot.PayoutsEnabled,
            DetailsSubmitted: snapshot.DetailsSubmitted,
            CanGoOnline: ConnectGate.CanGoOnline(derived),
            Requirements: new HostConnectRequirements(
                snapshot.DisabledReason,
                snapshot.CurrentlyDue,
                snapshot.PastDue,
                snapshot.EventuallyDue,
                snapshot.PendingVerification));
    }

    /// <summary>
    /// Returns the caller's Connect account id, creating one on first onboarding and pinning it (with
    /// <c>connect_status = 'pending'</c>) to the <c>users</c> row. Mirrors the customer-link race handling in
    /// <see cref="Wisper.Api.Billing.BillingService"/>: a concurrent first onboarding that already linked an
    /// account wins, and we reuse it rather than orphan a second (idle) Connect account.
    /// </summary>
    private async Task<(string AccountId, ConnectStatus Status)> EnsureConnectAccountAsync(
        User user, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(user.ConnectAccountId))
        {
            return (user.ConnectAccountId, user.ConnectStatus);
        }

        var accountId = await _stripe.CreateExpressAccountAsync(
            new ConnectAccountRequest(user.Id, user.Email), ct);
        try
        {
            await _users.UpdateAsync(
                user with
                {
                    ConnectAccountId = accountId,
                    ConnectStatus = ConnectStatus.Pending,
                    UpdatedAt = _time.GetUtcNow(),
                },
                ct);
        }
        catch (Exception ex)
        {
            var current = await _users.GetByIdAsync(user.Id, ct);
            if (!string.IsNullOrWhiteSpace(current?.ConnectAccountId))
            {
                _logger.LogInformation(
                    "raced connect-account link for user {User}; using persisted {Account}",
                    user.Id, current!.ConnectAccountId);
                return (current.ConnectAccountId!, current.ConnectStatus);
            }

            _logger.LogError(ex, "failed to persist Connect account for user {User}", user.Id);
            throw;
        }

        return (accountId, ConnectStatus.Pending);
    }

    /// <summary>The onboarding-redirect URLs are deployment config; a blank one is a server misconfiguration.</summary>
    private static string RequireConfiguredUrl(string? value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ApiException(
                ApiErrorCode.Internal, $"Connect onboarding is not configured (Stripe:{name} is unset).")
            : value;
}
