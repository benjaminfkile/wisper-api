using Stripe;
using Wisper.Api.Domain;
using Wisper.Api.Persistence.Users;

namespace Wisper.Api.Payments.Handlers;

/// <summary>
/// Handler for Connect account + saved-method events (docs/PAYMENTS.md §5, §8.5).
/// <para>
/// <c>account.updated</c> — the source of truth for a host's onboarding state: it recomputes
/// <c>connect_status</c> from the account's <c>charges_enabled</c>/<c>payouts_enabled</c>/<c>details_submitted</c>
/// (<see cref="ConnectStatusEvaluator"/>) and pins it on the owning <c>users</c> row. Only
/// <c>connect_status = 'enabled'</c> lets a host go online and earn; a <c>restricted</c> account keeps
/// accruing earnings but has payouts held (<see cref="ConnectGate"/>). The handler is a pure function of the
/// event's current account state and writes only when the derived status differs, so a duplicate or
/// out-of-order re-delivery is a no-op (§8.3).
/// </para>
/// <para>
/// <c>setup_intent.succeeded</c> — saved payment methods / auto-recharge (docs/PAYMENTS.md §3) — stays a
/// logged no-op until P6.2's saved-method work fills it in.
/// </para>
/// </summary>
public sealed class AccountWebhookHandler : IStripeWebhookHandler
{
    private readonly IUserRepository _users;
    private readonly TimeProvider _time;
    private readonly ILogger<AccountWebhookHandler> _logger;

    public AccountWebhookHandler(
        IUserRepository users, TimeProvider time, ILogger<AccountWebhookHandler> logger)
    {
        _users = users;
        _time = time;
        _logger = logger;
    }

    public IReadOnlyCollection<string> EventTypes { get; } = new[]
    {
        Stripe.EventTypes.AccountUpdated,
        Stripe.EventTypes.SetupIntentSucceeded,
    };

    public async Task HandleAsync(Event evt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(evt);

        if (evt.Type == Stripe.EventTypes.SetupIntentSucceeded)
        {
            // Saved payment methods / auto-recharge land in P6.2 — recognised, no state change yet.
            _logger.LogInformation(
                "setup_intent.succeeded ({Event}) received — saved-method handling is a stub (P6.2)", evt.Id);
            return;
        }

        // Without an Account object there is nothing to recompute and no retry would help — a logged no-op is
        // correct (keeps a synthetic/id-only event a benign Processed rather than a wedged failure).
        if (evt.Data?.Object is not Account account)
        {
            _logger.LogWarning("stripe event {Id} ({Type}) carried no Account object — no-op", evt.Id, evt.Type);
            return;
        }

        var user = await _users.GetByConnectAccountIdAsync(account.Id, ct);
        if (user is null)
        {
            // Not a host we track (or the link hasn't been persisted yet). Benign — nothing to gate.
            _logger.LogWarning(
                "account.updated for {Account} ({Event}) matched no user — not gating", account.Id, evt.Id);
            return;
        }

        var derived = ConnectStatusEvaluator.Evaluate(
            account.ChargesEnabled, account.PayoutsEnabled, account.DetailsSubmitted);
        if (derived == user.ConnectStatus)
        {
            // Idempotent: re-delivery / no capability change ⇒ nothing to write (docs/PAYMENTS.md §8.3).
            _logger.LogInformation(
                "account.updated for {Account} ({Event}) — connect_status unchanged ({Status})",
                account.Id, evt.Id, derived);
            return;
        }

        await _users.UpdateAsync(user with { ConnectStatus = derived, UpdatedAt = _time.GetUtcNow() }, ct);

        _logger.LogInformation(
            "account.updated for {Account} ({Event}) — connect_status {From} → {To} for user {User}",
            account.Id, evt.Id, user.ConnectStatus, derived, user.Id);
    }
}
