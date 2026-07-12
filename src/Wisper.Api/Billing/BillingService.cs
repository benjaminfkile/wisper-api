using Wisper.Api.Domain;
using Wisper.Api.Infrastructure;
using Wisper.Api.Ledger;
using Wisper.Api.Metering;
using Wisper.Api.Payments;
using Wisper.Api.Persistence.Leases;
using Wisper.Api.Persistence.Users;
using Wisper.Api.Policy;

namespace Wisper.Api.Billing;

/// <summary>
/// The consumer billing surface (docs/API.md §5, docs/PAYMENTS.md §3). It orchestrates the top-up
/// <b>create</b> path (ensure a Stripe customer → create a PaymentIntent → return its <c>client_secret</c>)
/// and the read paths (<b>wallet balance + usage summary</b> and the <b>paginated ledger view</b>), both
/// derived from the double-entry ledger — the source of truth (docs/DATA_MODEL.md §7). Crucially it
/// <b>never credits the wallet</b>: the credit is webhook-driven (<see cref="Payments.Handlers.TopupWebhookHandler"/>),
/// so a client that confirms but never returns still gets its credit and a duplicate cannot double-credit
/// (docs/PAYMENTS.md §3, §8). Everything depends on interfaces so the unit suite runs without Stripe/Postgres.
/// </summary>
public sealed class BillingService
{
    /// <summary>The single currency v0 supports (docs/PAYMENTS.md §13, docs/DATA_MODEL.md §16).</summary>
    public const string Currency = "usd";

    private const int MaxPageLimit = 100;
    private const int DefaultPageLimit = 25;

    private readonly LedgerService _ledger;
    private readonly ILeaseRepository _leases;
    private readonly IUserRepository _users;
    private readonly PlatformPolicyService _policy;
    private readonly IStripeBillingGateway _stripe;
    private readonly TimeProvider _time;
    private readonly ILogger<BillingService> _logger;

    public BillingService(
        LedgerService ledger,
        ILeaseRepository leases,
        IUserRepository users,
        PlatformPolicyService policy,
        IStripeBillingGateway stripe,
        TimeProvider time,
        ILogger<BillingService> logger)
    {
        _ledger = ledger;
        _leases = leases;
        _users = users;
        _policy = policy;
        _stripe = stripe;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Starts a wallet top-up (docs/PAYMENTS.md §3): validates the amount against
    /// <c>platform_policy.min_topup_cents</c>, ensures the caller has a Stripe customer (creating one on
    /// first use), then creates a PaymentIntent stamped with the caller's user id and keyed by
    /// <paramref name="idempotencyKey"/>. Returns the <c>client_secret</c>. No ledger effect here — the
    /// wallet is credited only on the webhook.
    /// </summary>
    public async Task<TopupResponse> TopupAsync(
        Guid userId, TopupRequest request, string idempotencyKey, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        if (request.AmountCents is not { } amountCents || amountCents <= 0)
        {
            throw new ApiException(
                ApiErrorCode.ValidationError,
                "'amount_cents' must be a positive integer.",
                new { field = "amount_cents" });
        }

        // Enforce the minimum top-up (docs/PAYMENTS.md §3). Below the minimum is a 402 (docs/API.md §3 —
        // "wallet can't cover a … top-up minimum"). No policy configured ⇒ no minimum.
        var minTopup = (await _policy.GetActiveAsync(ct))?.MinTopupCents ?? 0;
        if (amountCents < minTopup)
        {
            throw new ApiException(
                ApiErrorCode.PaymentRequired,
                $"The minimum top-up is {minTopup} cents.",
                new { field = "amount_cents", min_topup_cents = minTopup });
        }

        var user = await _users.GetByIdAsync(userId, ct)
            ?? throw new ApiException(ApiErrorCode.NotFound, "The account no longer exists.");

        var customerId = await EnsureCustomerAsync(user, ct);

        var intent = await _stripe.CreatePaymentIntentAsync(
            new StripePaymentIntentRequest(user.Id, customerId, amountCents, Currency, idempotencyKey), ct);

        _logger.LogInformation(
            "created top-up PaymentIntent {Intent} for user {User}: {Amount}¢ (credit on webhook only)",
            intent.Id, user.Id, amountCents);
        return new TopupResponse(intent.ClientSecret);
    }

    /// <summary>
    /// Starts saving a payment method for future top-ups (docs/PAYMENTS.md §3): ensures a Stripe customer and
    /// creates a SetupIntent, returning its <c>client_secret</c>.
    /// </summary>
    public async Task<SetupIntentResponse> CreatePaymentMethodSetupAsync(
        Guid userId, CancellationToken ct = default)
    {
        var user = await _users.GetByIdAsync(userId, ct)
            ?? throw new ApiException(ApiErrorCode.NotFound, "The account no longer exists.");

        var customerId = await EnsureCustomerAsync(user, ct);
        var intent = await _stripe.CreateSetupIntentAsync(new StripeSetupIntentRequest(user.Id, customerId), ct);
        return new SetupIntentResponse(intent.ClientSecret);
    }

    /// <summary>
    /// The caller's wallet balance + usage summary (docs/API.md §5). The balance is the ledger-derived
    /// <c>user_wallet</c> balance; the usage summary aggregates the caller's leases (counts, metered seconds,
    /// and actual metered spend).
    /// </summary>
    public async Task<BillingResponse> GetBillingAsync(Guid userId, CancellationToken ct = default)
    {
        var balance = await _ledger.GetWalletBalanceCentsAsync(userId, Currency, ct);

        var leases = await _leases.ListByConsumerAsync(userId, ct);
        var active = 0;
        long billableSeconds = 0;
        long spentCents = 0;
        foreach (var lease in leases)
        {
            if (lease.Status == LeaseStatus.Active)
            {
                active++;
            }

            billableSeconds += lease.BillableSeconds;
            // Actual metered spend so far, per the same integer-floor charge math the meter posts (§14).
            spentCents += MeteringService.ChargeCentsFor(lease.BillableSeconds, lease.PriceCentsPerMin);
        }

        var usage = new BillingUsageSummary(leases.Count, active, billableSeconds, spentCents);
        return new BillingResponse(balance, Currency, usage);
    }

    /// <summary>
    /// A page of the caller's ledger view (docs/API.md §5, §10): the wallet's transactions newest-first,
    /// cursor-paginated. Each row carries the signed amount the transaction moved on the wallet.
    /// </summary>
    public async Task<BillingTransactionPage> ListTransactionsAsync(
        Guid userId, int limit, BillingCursor? cursor, CancellationToken ct = default)
    {
        var pageSize = Math.Clamp(limit, 1, MaxPageLimit);
        var all = await _ledger.ListWalletTransactionsAsync(userId, Currency, ct);

        var page = new List<BillingTransactionView>(pageSize);
        AccountTransaction? lastIncluded = null;
        var more = false;
        foreach (var txn in all.Where(t => After(t, cursor)))
        {
            if (page.Count == pageSize)
            {
                more = true;
                break;
            }

            page.Add(ToView(txn));
            lastIncluded = txn;
        }

        var nextCursor = more && lastIncluded is not null
            ? new BillingCursor(lastIncluded.Transaction.CreatedAt, lastIncluded.Transaction.Id).Encode()
            : null;
        return new BillingTransactionPage(page, nextCursor);
    }

    /// <summary>The default page size when the caller does not specify one (docs/API.md §10).</summary>
    public static int DefaultLimit => DefaultPageLimit;

    /// <summary>The maximum page size a caller may request (docs/API.md §10).</summary>
    public static int MaxLimit => MaxPageLimit;

    private async Task<string> EnsureCustomerAsync(User user, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(user.StripeCustomerId))
        {
            return user.StripeCustomerId;
        }

        // First top-up: create the Stripe customer and persist the id so later calls reuse it.
        var customerId = await _stripe.CreateCustomerAsync(new StripeCustomerRequest(user.Id, user.Email), ct);
        try
        {
            await _users.UpdateAsync(
                user with { StripeCustomerId = customerId, UpdatedAt = _time.GetUtcNow() }, ct);
        }
        catch (Exception ex)
        {
            // A concurrent first top-up already linked a customer — reuse whichever is now persisted so we
            // never orphan the wallet from its customer. (The extra Stripe customer is harmless/idle.)
            var current = await _users.GetByIdAsync(user.Id, ct);
            if (!string.IsNullOrWhiteSpace(current?.StripeCustomerId))
            {
                _logger.LogInformation(
                    "raced customer link for user {User}; using persisted {Customer}", user.Id, current!.StripeCustomerId);
                return current.StripeCustomerId!;
            }

            _logger.LogError(ex, "failed to persist Stripe customer for user {User}", user.Id);
            throw;
        }

        return customerId;
    }

    private static bool After(AccountTransaction txn, BillingCursor? cursor) =>
        cursor is null || BillingCursor.Compare(
            txn.Transaction.CreatedAt, txn.Transaction.Id, cursor.CreatedAt, cursor.Id) > 0;

    private static BillingTransactionView ToView(AccountTransaction txn) => new(
        Id: txn.Transaction.Id,
        Kind: PgEnum.ToSnakeLabel(txn.Transaction.Kind),
        AmountCents: txn.SignedAmountCents,
        Currency: Currency,
        LeaseId: txn.Transaction.LeaseId,
        ExternalRef: txn.Transaction.ExternalRef,
        Memo: txn.Transaction.Memo,
        CreatedAt: txn.Transaction.CreatedAt);
}
