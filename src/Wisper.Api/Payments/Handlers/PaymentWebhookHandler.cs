using Stripe;
using Wisper.Api.Audit;
using Wisper.Api.Domain;
using Wisper.Api.Ledger;
using Wisper.Api.Persistence.Users;

namespace Wisper.Api.Payments.Handlers;

/// <summary>
/// The refund + dispute handler (docs/PAYMENTS.md §7, §8.5) — the adversarial money events, made idempotent
/// and order-independent by keying every ledger effect so re-delivery is a no-op (§8.3):
/// <list type="bullet">
/// <item><c>charge.refunded</c> — posts a <c>refund</c> ledger txn (<c>user_wallet → platform_cash</c>) for the
/// refunded amount, keyed by the Stripe <b>refund id</b>. This is the same key
/// <see cref="Wisper.Api.Billing.BillingService.RefundAsync"/> uses, so an API-initiated refund and its webhook dedupe to one
/// posting, while a dashboard-initiated refund posts here. A refund that would drive the wallet negative (the
/// consumer already spent the credits) is <b>blocked</b> and left for an admin clawback/adjustment — logged,
/// not wedged (docs/PAYMENTS.md §7, §12).</item>
/// <item><c>charge.dispute.created</c> — the hard case (a consumer disputes a top-up <i>after</i> spending):
/// posts a <c>chargeback</c> ledger txn (the one place a wallet may go <b>negative</b> = a genuine debt),
/// <b>suspends the user</b> (blocking new leases), and records an <c>audit_log</c> entry. Paid host earnings are
/// absorbed by the platform — no host clawback (docs/PAYMENTS.md §7).</item>
/// <item><c>charge.dispute.closed</c> — informational: the dispute resolved. A reversal on a <i>won</i> dispute
/// is a deliberate, audited admin <c>adjustment</c> (docs/PAYMENTS.md §7), not an automatic mutation here.</item>
/// </list>
/// Backed by the ledger, user repository, and audit service (Grunt has no Stripe/Postgres — fakes + in-memory
/// doubles back the tests).
/// </summary>
public sealed class PaymentWebhookHandler : IStripeWebhookHandler
{
    /// <summary>The single currency v0 supports (docs/PAYMENTS.md §13, docs/DATA_MODEL.md §16).</summary>
    private const string Currency = "usd";

    private readonly LedgerService _ledger;
    private readonly IUserRepository _users;
    private readonly AuditService _audit;
    private readonly TimeProvider _time;
    private readonly ILogger<PaymentWebhookHandler> _logger;

    public PaymentWebhookHandler(
        LedgerService ledger,
        IUserRepository users,
        AuditService audit,
        TimeProvider time,
        ILogger<PaymentWebhookHandler> logger)
    {
        _ledger = ledger;
        _users = users;
        _audit = audit;
        _time = time;
        _logger = logger;
    }

    public IReadOnlyCollection<string> EventTypes { get; } = new[]
    {
        Stripe.EventTypes.ChargeRefunded,
        Stripe.EventTypes.ChargeDisputeCreated,
        Stripe.EventTypes.ChargeDisputeClosed,
    };

    public async Task HandleAsync(Event evt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(evt);

        switch (evt.Type)
        {
            case Stripe.EventTypes.ChargeRefunded:
                await HandleRefundedAsync(evt, ct);
                return;
            case Stripe.EventTypes.ChargeDisputeCreated:
                await HandleDisputeCreatedAsync(evt, ct);
                return;
            case Stripe.EventTypes.ChargeDisputeClosed:
                HandleDisputeClosed(evt);
                return;
        }
    }

    /// <summary>
    /// <c>charge.refunded</c> → post one <c>refund</c> ledger txn per refund in <c>charge.refunds.data</c>,
    /// each keyed by its Stripe <b>refund id</b> (<see cref="Wisper.Api.Billing.BillingService.RefundIdempotencyKey"/>) —
    /// the same key the API path uses, so an API-initiated refund and its webhook dedupe to one posting, while
    /// a dashboard-initiated refund posts here (docs/PAYMENTS.md §7, §8). Every refund on the charge is
    /// enumerated so a second refund on the same charge gets its own distinct key rather than colliding with
    /// (and being wrongly deduped against) the first. A refund that would overdraw the wallet is blocked and
    /// left for an admin adjustment (logged, not thrown, so the pipeline is not wedged).
    /// </summary>
    private async Task HandleRefundedAsync(Event evt, CancellationToken ct)
    {
        if (evt.Data?.Object is not Charge charge)
        {
            _logger.LogWarning("stripe event {Id} ({Type}) carried no Charge object — no-op", evt.Id, evt.Type);
            return;
        }

        // We MUST have refund objects to key posts by refund id — the only key that dedupes with the API path
        // (BillingService.RefundIdempotencyKey). Without it, posting under charge id or event id would either
        // double-debit (different key than the API path) or drop a subsequent refund on the same charge (same
        // key as an earlier one). Skip and warn — Stripe expands data.object.refunds on charge.refunded by
        // default; a missing list is a payload/config issue to flag, not something to paper over.
        var refunds = charge.Refunds?.Data;
        if (refunds is null || refunds.Count == 0)
        {
            _logger.LogWarning(
                "charge.refunded ({Event}) charge {Charge} carried no expanded refunds — no-op (cannot key posts " +
                "by refund id to dedupe with the API path)", evt.Id, charge.Id);
            return;
        }

        var user = await ResolveUserAsync(charge.Metadata, charge.CustomerId, charge.PaymentIntent, evt, "charge.refunded", ct);
        if (user is null)
        {
            return; // benign no-op — logged in the resolver
        }

        var currency = string.IsNullOrWhiteSpace(charge.Currency) ? Currency : charge.Currency;
        var wallet = await _ledger.GetOrCreateAccountAsync(LedgerAccountKind.UserWallet, user.Id, currency, ct);
        var platformCash = await _ledger.GetOrCreateAccountAsync(LedgerAccountKind.PlatformCash, null, currency, ct);
        var stripeFees = await _ledger.GetOrCreateAccountAsync(LedgerAccountKind.StripeFees, null, currency, ct);

        foreach (var refund in refunds)
        {
            if (string.IsNullOrWhiteSpace(refund.Id))
            {
                _logger.LogWarning(
                    "charge.refunded ({Event}) charge {Charge} contained a refund with no id — skipped",
                    evt.Id, charge.Id);
                continue;
            }

            if (refund.Amount <= 0)
            {
                _logger.LogWarning(
                    "charge.refunded ({Event}) refund {Refund} has non-positive amount — skipped",
                    evt.Id, refund.Id);
                continue;
            }

            try
            {
                var posted = await _ledger.PostAsync(
                    LedgerFlows.Refund(
                        wallet.Id, platformCash.Id, stripeFees.Id,
                        grossAmountCents: refund.Amount,
                        stripeFeeCents: 0,
                        idempotencyKey: Wisper.Api.Billing.BillingService.RefundIdempotencyKey(refund.Id),
                        externalRef: refund.Id,
                        memo: $"refund {refund.Id} for user {user.Id} (charge {charge.Id})"),
                    ct);
                _logger.LogInformation(
                    "charge.refunded ({Event}) posted refund {Refund} of {Amount}¢ for user {User}{Replay}",
                    evt.Id, refund.Id, refund.Amount, user.Id, posted.WasDeduplicated ? " [replay]" : string.Empty);
            }
            catch (LedgerException ex) when (ex.Reason == LedgerViolation.InsufficientFunds)
            {
                // Refunding spent credits would drive the wallet negative — blocked (docs/PAYMENTS.md §7, §12).
                // A recovery is an audited admin adjustment, not an automatic negative here. Not thrown, so the
                // event is Processed and the remaining refunds in the batch still get posted.
                _logger.LogError(
                    "charge.refunded ({Event}) refund {Refund} for user {User} exceeds unspent wallet balance — " +
                    "blocked; an admin adjustment is required (docs/PAYMENTS.md §7)", evt.Id, refund.Id, user.Id);
            }
        }
    }

    /// <summary>
    /// <c>charge.dispute.created</c> → post a <c>chargeback</c> (wallet may go negative = debt), suspend the
    /// user, and audit (docs/PAYMENTS.md §7, §8). The ledger post (keyed by the event id) is the idempotency
    /// anchor: the suspend + audit run only on the first, non-deduplicated posting, so a re-delivery is a no-op.
    /// </summary>
    private async Task HandleDisputeCreatedAsync(Event evt, CancellationToken ct)
    {
        if (evt.Data?.Object is not Dispute dispute)
        {
            _logger.LogWarning("stripe event {Id} ({Type}) carried no Dispute object — no-op", evt.Id, evt.Type);
            return;
        }

        var user = await ResolveUserAsync(
            dispute.Metadata, dispute.Charge?.CustomerId, dispute.PaymentIntent, evt, "charge.dispute.created", ct);
        if (user is null)
        {
            return; // benign no-op — logged in the resolver
        }

        if (dispute.Amount <= 0)
        {
            _logger.LogWarning("charge.dispute.created {Dispute} ({Event}) has non-positive amount — no-op",
                dispute.Id, evt.Id);
            return;
        }

        var currency = string.IsNullOrWhiteSpace(dispute.Currency) ? Currency : dispute.Currency;
        var wallet = await _ledger.GetOrCreateAccountAsync(LedgerAccountKind.UserWallet, user.Id, currency, ct);
        var platformCash = await _ledger.GetOrCreateAccountAsync(LedgerAccountKind.PlatformCash, null, currency, ct);

        // Keyed by the STRIPE EVENT ID — a re-delivered dispute dedupes at the ledger and posts nothing. The
        // wallet may go negative (a debt): the one documented exception to the non-negative guard (§7).
        var posted = await _ledger.PostAsync(
            LedgerFlows.Chargeback(
                wallet.Id, platformCash.Id, dispute.Amount,
                idempotencyKey: evt.Id,
                externalRef: dispute.Id,
                memo: $"chargeback dispute {dispute.Id} for user {user.Id}"),
            ct);

        if (posted.WasDeduplicated)
        {
            // The chargeback (and thus the suspend + audit) already ran for this event — a re-delivery no-op.
            _logger.LogInformation(
                "charge.dispute.created ({Event}) already processed for user {User} — no-op", evt.Id, user.Id);
            return;
        }

        _logger.LogWarning(
            "chargeback posted for user {User}: dispute {Dispute} clawed back {Amount}¢ (wallet balance now {Balance}¢, may be a debt)",
            user.Id, dispute.Id, dispute.Amount, await _ledger.GetWalletBalanceCentsAsync(user.Id, currency, ct));

        // Suspend the user — blocks new leases (docs/PAYMENTS.md §7). Idempotent: skip if already suspended.
        if (user.Status != UserStatus.Suspended)
        {
            await _users.UpdateAsync(
                user with { Status = UserStatus.Suspended, UpdatedAt = _time.GetUtcNow() }, ct);
        }

        // Audit the money-sensitive action (system actor) with before/after status + amounts (docs/PAYMENTS.md §10, §12).
        await _audit.RecordAsync(
            "user.chargeback_suspend",
            actorUserId: null,
            targetType: "user",
            targetId: user.Id,
            meta: new
            {
                dispute_id = dispute.Id,
                amount_cents = dispute.Amount,
                status_before = PgEnum.ToLabel(user.Status),
                status_after = PgEnum.ToLabel(UserStatus.Suspended),
                stripe_event = evt.Id,
            },
            ct);
    }

    /// <summary><c>charge.dispute.closed</c> — informational; a reversal is a deliberate admin adjustment (§7).</summary>
    private void HandleDisputeClosed(Event evt)
    {
        var status = evt.Data?.Object is Dispute dispute ? dispute.Status : null;
        _logger.LogInformation(
            "charge.dispute.closed ({Event}) status={Status} — informational; any reversal is an admin adjustment (§7)",
            evt.Id, status ?? "unknown");
    }

    /// <summary>
    /// Resolves the Wisper user a charge/dispute belongs to (docs/PAYMENTS.md §7, §8): first the
    /// <c>wisper_user_id</c> metadata we stamp on top-ups/refunds (on the object or its PaymentIntent), then the
    /// Stripe customer id. A benign no-op (logged) when none resolves, so a foreign event can't wedge the pipeline.
    /// </summary>
    private async Task<User?> ResolveUserAsync(
        IDictionary<string, string>? metadata,
        string? customerId,
        PaymentIntent? intent,
        Event evt,
        string label,
        CancellationToken ct)
    {
        if (TryUserId(metadata, out var userId) || TryUserId(intent?.Metadata, out userId))
        {
            var byId = await _users.GetByIdAsync(userId, ct);
            if (byId is not null)
            {
                return byId;
            }
        }

        if (!string.IsNullOrWhiteSpace(customerId))
        {
            var byCustomer = await _users.GetByStripeCustomerIdAsync(customerId, ct);
            if (byCustomer is not null)
            {
                return byCustomer;
            }
        }

        _logger.LogWarning("{Label} ({Event}) could not resolve a Wisper user — no ledger effect", label, evt.Id);
        return null;
    }

    private static bool TryUserId(IDictionary<string, string>? metadata, out Guid userId)
    {
        userId = default;
        return metadata is not null
            && metadata.TryGetValue(TopupMetadata.UserId, out var raw)
            && Guid.TryParse(raw, out userId);
    }
}
