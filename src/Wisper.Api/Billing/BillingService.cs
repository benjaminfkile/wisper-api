using Wisper.Api.Audit;
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
/// derived from the double-entry ledger -- the source of truth (docs/DATA_MODEL.md §7). Crucially it
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
    private readonly FraudGuardService _fraud;
    private readonly AuditService _audit;
    private readonly IStripeBillingGateway _stripe;
    private readonly TimeProvider _time;
    private readonly ILogger<BillingService> _logger;

    public BillingService(
        LedgerService ledger,
        ILeaseRepository leases,
        IUserRepository users,
        PlatformPolicyService policy,
        FraudGuardService fraud,
        AuditService audit,
        IStripeBillingGateway stripe,
        TimeProvider time,
        ILogger<BillingService> logger)
    {
        _ledger = ledger;
        _leases = leases;
        _users = users;
        _policy = policy;
        _fraud = fraud;
        _audit = audit;
        _stripe = stripe;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Starts a wallet top-up (docs/PAYMENTS.md §3): validates the amount against
    /// <c>platform_policy.min_topup_cents</c>, ensures the caller has a Stripe customer (creating one on
    /// first use), then creates a PaymentIntent stamped with the caller's user id and keyed by
    /// <paramref name="idempotencyKey"/>. Returns the <c>client_secret</c>. No ledger effect here -- the
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

        // Enforce the minimum top-up (docs/PAYMENTS.md §3). Below the minimum is a 402 (docs/API.md §3 --
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

        // Fraud guards (docs/PAYMENTS.md §7): the first-top-up hold + new-account top-up velocity, checked
        // BEFORE Stripe so a limited request never creates a PaymentIntent. Throws limit_exceeded (429).
        await _fraud.EnforceTopupAllowedAsync(user, amountCents, ct);

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
    /// Refunds unspent wallet credits (docs/API.md §5, docs/PAYMENTS.md §3, §7): validates the amount, checks
    /// the wallet currently holds at least that much (so only <b>unspent</b> credits are refundable -- refunding
    /// spent credits is a clawback question), issues a Stripe <b>Refund</b> against a top-up PaymentIntent
    /// (the named one, else the most recent), then posts the <c>refund</c> ledger txn (<c>user_wallet →
    /// platform_cash</c>) keyed by the Stripe <b>refund id</b>, so the <c>charge.refunded</c> webhook dedupes
    /// against it (exactly-once). The refund is audited (docs/PAYMENTS.md §10, §12). The API idempotency key is
    /// forwarded to Stripe so a retried request cannot refund twice.
    /// </summary>
    public Task<RefundResponse> RefundAsync(
        Guid userId, RefundRequest request, string idempotencyKey, CancellationToken ct = default) =>
        RefundInternalAsync(
            actorUserId: userId, targetUserId: userId, request, idempotencyKey, "billing.refund", ct);

    /// <summary>
    /// The admin manual refund (docs/API.md §8, <c>POST /v1/admin/refunds</c>): refunds unspent wallet credits
    /// of <paramref name="targetUserId"/> exactly as the self-serve path does -- same Stripe refund + same
    /// <c>refund</c> ledger txn (<c>user_wallet → platform_cash</c>) keyed by the refund id -- but recorded with
    /// the acting <paramref name="actorUserId"/> (admin) as the audit actor under <c>admin.refund</c>.
    /// </summary>
    public Task<RefundResponse> AdminRefundAsync(
        Guid actorUserId,
        Guid targetUserId,
        RefundRequest request,
        string idempotencyKey,
        CancellationToken ct = default) =>
        RefundInternalAsync(actorUserId, targetUserId, request, idempotencyKey, "admin.refund", ct);

    private async Task<RefundResponse> RefundInternalAsync(
        Guid actorUserId,
        Guid targetUserId,
        RefundRequest request,
        string idempotencyKey,
        string auditAction,
        CancellationToken ct)
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

        var userId = targetUserId;
        var user = await _users.GetByIdAsync(userId, ct)
            ?? throw new ApiException(ApiErrorCode.NotFound, "The account no longer exists.");

        // Only UNSPENT credits are refundable: the wallet must currently hold at least the refund amount
        // (docs/PAYMENTS.md §3, §7). Refunding spent credits would drive the wallet negative -- that is a
        // clawback/admin-adjustment question, not a self-serve refund. Blocked with 402.
        var balance = await _ledger.GetWalletBalanceCentsAsync(userId, Currency, ct);
        if (balance < amountCents)
        {
            throw new ApiException(
                ApiErrorCode.InsufficientFunds,
                "The refund exceeds the unspent wallet balance.",
                new { available_cents = balance, requested_cents = amountCents });
        }

        // Which top-up to refund against: the caller may name a PaymentIntent, else the most recent top-up.
        var paymentIntentId = await ResolveRefundTargetAsync(userId, request.PaymentIntent, ct);

        // Issue the Stripe refund (idempotency key = the API key), then post the ledger effect keyed by the
        // returned refund id -- the same key the charge.refunded webhook uses, so they dedupe (docs/PAYMENTS.md §8).
        var refund = await _stripe.CreateRefundAsync(
            new StripeRefundRequest(userId, paymentIntentId, amountCents, idempotencyKey), ct);

        var wallet = await _ledger.GetOrCreateAccountAsync(LedgerAccountKind.UserWallet, userId, Currency, ct);
        var platformCash = await _ledger.GetOrCreateAccountAsync(LedgerAccountKind.PlatformCash, null, Currency, ct);
        var stripeFees = await _ledger.GetOrCreateAccountAsync(LedgerAccountKind.StripeFees, null, Currency, ct);

        try
        {
            var posted = await _ledger.PostAsync(
                LedgerFlows.Refund(
                    wallet.Id, platformCash.Id, stripeFees.Id,
                    grossAmountCents: refund.AmountCents,
                    stripeFeeCents: 0, // platform returns the full credit; the original processing fee is absorbed
                    idempotencyKey: RefundIdempotencyKey(refund.Id),
                    externalRef: refund.Id,
                    memo: $"refund {refund.Id} for user {userId} (pi {paymentIntentId})"),
                ct);
            _logger.LogInformation(
                "refunded {Amount}¢ to user {User} via refund {Refund} (pi {Intent}){Replay}",
                refund.AmountCents, userId, refund.Id, paymentIntentId,
                posted.WasDeduplicated ? " [ledger replay]" : string.Empty);
        }
        catch (LedgerException ex) when (ex.Reason == LedgerViolation.InsufficientFunds)
        {
            // Wallet drained between the balance check and the post (a rare race). The Stripe refund was
            // already issued and is idempotent by refund id; the charge.refunded webhook is the backstop --
            // flag for reconciliation rather than silently drive the wallet negative.
            _logger.LogError(
                ex, "refund {Refund} for user {User} could not post (wallet raced below balance); reconciliation required",
                refund.Id, userId);
            throw new ApiException(
                ApiErrorCode.InsufficientFunds,
                "The refund exceeds the unspent wallet balance.",
                new { requested_cents = amountCents });
        }

        await _audit.RecordAsync(
            auditAction,
            actorUserId: actorUserId,
            targetType: "user",
            targetId: userId,
            meta: new { refund_id = refund.Id, amount_cents = refund.AmountCents, payment_intent = paymentIntentId },
            ct);

        var newBalance = await _ledger.GetWalletBalanceCentsAsync(userId, Currency, ct);
        return new RefundResponse(refund.Id, refund.AmountCents, Currency, newBalance);
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

    /// <summary>The <c>refund</c> ledger idempotency key -- stable per Stripe refund id, shared with the webhook.</summary>
    public static string RefundIdempotencyKey(string refundId) => $"refund:{refundId}";

    /// <summary>
    /// Resolves the top-up PaymentIntent (<c>pi_…</c>) a refund is issued against: the caller-named one when
    /// present (validated to be one of the user's own top-ups), else the caller's most recent top-up. A
    /// top-up's <c>pi_</c> id is carried on its <c>topup</c> ledger transaction's external ref. Throws a
    /// validation error when the user has no top-up to refund, or names one that isn't theirs.
    /// </summary>
    private async Task<string> ResolveRefundTargetAsync(
        Guid userId, string? requestedPaymentIntent, CancellationToken ct)
    {
        var txns = await _ledger.ListWalletTransactionsAsync(userId, Currency, ct);
        // Newest-first already; a top-up's external ref is its PaymentIntent id.
        var topupIntents = txns
            .Where(t => t.Transaction.Kind == LedgerTxnKind.Topup && !string.IsNullOrWhiteSpace(t.Transaction.ExternalRef))
            .Select(t => t.Transaction.ExternalRef!)
            .ToList();

        if (!string.IsNullOrWhiteSpace(requestedPaymentIntent))
        {
            if (!topupIntents.Contains(requestedPaymentIntent))
            {
                throw new ApiException(
                    ApiErrorCode.ValidationError,
                    "The named payment_intent is not a top-up on this account.",
                    new { field = "payment_intent" });
            }

            return requestedPaymentIntent;
        }

        return topupIntents.FirstOrDefault()
            ?? throw new ApiException(
                ApiErrorCode.Conflict, "There is no top-up to refund against.");
    }

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
            // A concurrent first top-up already linked a customer -- reuse whichever is now persisted so we
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
