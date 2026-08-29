using Stripe;
using Wisper.Api.Domain;
using Wisper.Api.Persistence.Payouts;
using Payout = Wisper.Api.Domain.Payout;

namespace Wisper.Api.Payments.Handlers;

/// <summary>
/// Handler for host-payout events (docs/PAYMENTS.md §6, §8.5). It advances the <c>payouts</c> row a Stripe
/// event describes, resolving it from the transfer's <see cref="TransferMetadata.PayoutId"/> metadata so the
/// handler is a pure function of the event with no ordering assumption (§8.3):
/// <list type="bullet">
/// <item><c>transfer.created</c> -- the platform → connected transfer exists; confirm the row is
/// <see cref="PayoutStatus.InTransit"/> (the payout path sets it synchronously, so this is normally a no-op).</item>
/// <item><c>transfer.failed</c> / <c>transfer.reversed</c> -- the transfer could not be completed; mark the row
/// <see cref="PayoutStatus.Failed"/> and post <b>no</b> ledger txn, so accrued <c>host_earnings</c> is retained
/// and a later run retries (§6). (A row that already committed its <c>payout</c> ledger txn -- the reversal
/// edge -- is flagged for reconciliation in P6.6 rather than silently mangled.)</item>
/// <item><c>payout.paid</c> / <c>payout.failed</c> (connected) -- the connected → bank leg; informational only
/// (Stripe owns that schedule), logged, no state change.</item>
/// </list>
/// Re-delivery is idempotent: a row already in the target state is left untouched. Backed by the payout
/// repository (Grunt has no Stripe/Postgres -- a fake + in-memory double back the tests).
/// </summary>
public sealed class TransferWebhookHandler : IStripeWebhookHandler
{
    private readonly IPayoutRepository _payouts;
    private readonly TimeProvider _time;
    private readonly ILogger<TransferWebhookHandler> _logger;

    public TransferWebhookHandler(
        IPayoutRepository payouts, TimeProvider time, ILogger<TransferWebhookHandler> logger)
    {
        _payouts = payouts;
        _time = time;
        _logger = logger;
    }

    // Stripe emits no `transfer.failed` for platform → connected transfers (they fail synchronously on the API
    // call, and asynchronous undo arrives as `transfer.reversed`). We still claim the `transfer.failed` type --
    // named by docs/PAYMENTS.md §8.5 and the task -- via a string literal (there is no SDK constant for it), so
    // the handler is complete should Stripe ever deliver one; the realistic failure paths are the API response
    // (handled in PayoutService) and `transfer.reversed`.
    public const string TransferFailed = "transfer.failed";

    public IReadOnlyCollection<string> EventTypes { get; } = new[]
    {
        Stripe.EventTypes.TransferCreated,
        TransferFailed,
        Stripe.EventTypes.TransferReversed,
        Stripe.EventTypes.PayoutPaid,
        Stripe.EventTypes.PayoutFailed,
    };

    public async Task HandleAsync(Event evt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(evt);

        // The connected `payout.*` events are the connected → bank leg -- Stripe's job, informational only.
        if (evt.Type is Stripe.EventTypes.PayoutPaid or Stripe.EventTypes.PayoutFailed)
        {
            _logger.LogInformation(
                "connected {Type} ({Event}) -- connected→bank status, no payout state change", evt.Type, evt.Id);
            return;
        }

        // Every remaining type carries a Transfer; without one there is nothing to update and no retry helps.
        if (evt.Data?.Object is not Transfer transfer)
        {
            _logger.LogWarning("stripe event {Id} ({Type}) carried no Transfer object -- no-op", evt.Id, evt.Type);
            return;
        }

        var payout = await ResolvePayoutAsync(transfer, evt, ct);
        if (payout is null)
        {
            return; // benign no-op -- logged in the resolver
        }

        if (evt.Type == Stripe.EventTypes.TransferCreated)
        {
            await ConfirmCreatedAsync(payout, transfer, evt, ct);
            return;
        }

        // transfer.failed / transfer.reversed → retain earnings.
        await MarkFailedAsync(payout, transfer, evt, ct);
    }

    /// <summary>Confirms a created transfer keeps the row <c>in_transit</c> (idempotent for a re-delivery).</summary>
    private async Task ConfirmCreatedAsync(Payout payout, Transfer transfer, Event evt, CancellationToken ct)
    {
        if (payout.Status is PayoutStatus.InTransit or PayoutStatus.Paid)
        {
            _logger.LogInformation(
                "transfer.created ({Event}) for payout {Payout} -- already {Status}, no-op",
                evt.Id, payout.Id, payout.Status);
            return;
        }

        await _payouts.UpdateAsync(
            payout with
            {
                Status = PayoutStatus.InTransit,
                StripeTransferId = payout.StripeTransferId ?? transfer.Id,
                UpdatedAt = _time.GetUtcNow(),
            },
            ct);
        _logger.LogInformation(
            "transfer.created ({Event}) -- payout {Payout} → in_transit (transfer {Transfer})",
            evt.Id, payout.Id, transfer.Id);
    }

    /// <summary>
    /// Marks a payout <c>failed</c> with no ledger effect (docs/PAYMENTS.md §6). If the row already committed a
    /// <c>payout</c> ledger txn (the async-reversal edge, which the synchronous payout path never produces), the
    /// earnings were already drained -- flag it for a compensating reversal (P6.6) rather than silently mangle.
    /// </summary>
    private async Task MarkFailedAsync(Payout payout, Transfer transfer, Event evt, CancellationToken ct)
    {
        if (payout.Status == PayoutStatus.Failed)
        {
            _logger.LogInformation(
                "{Type} ({Event}) for payout {Payout} -- already failed, no-op", evt.Type, evt.Id, payout.Id);
            return;
        }

        if (payout.PayoutTxnId is not null)
        {
            _logger.LogError(
                "{Type} ({Event}) for payout {Payout} which already committed ledger txn {Txn} -- marking " +
                "failed; a compensating reversal is required (P6.6)",
                evt.Type, evt.Id, payout.Id, payout.PayoutTxnId);
        }

        await _payouts.UpdateAsync(
            payout with
            {
                Status = PayoutStatus.Failed,
                Error = $"{evt.Type} (transfer {transfer.Id})",
                UpdatedAt = _time.GetUtcNow(),
            },
            ct);
        _logger.LogInformation(
            "{Type} ({Event}) -- payout {Payout} → failed; earnings retained in host_earnings",
            evt.Type, evt.Id, payout.Id);
    }

    /// <summary>Resolves the <c>payouts</c> row from the transfer's <see cref="TransferMetadata.PayoutId"/>.</summary>
    private async Task<Payout?> ResolvePayoutAsync(Transfer transfer, Event evt, CancellationToken ct)
    {
        if (transfer.Metadata is null ||
            !transfer.Metadata.TryGetValue(TransferMetadata.PayoutId, out var rawId) ||
            !Guid.TryParse(rawId, out var payoutId))
        {
            _logger.LogWarning(
                "{Type} ({Event}) transfer {Transfer} has no {Key} metadata -- no payout to update",
                evt.Type, evt.Id, transfer.Id, TransferMetadata.PayoutId);
            return null;
        }

        var payout = await _payouts.GetByIdAsync(payoutId, ct);
        if (payout is null)
        {
            _logger.LogWarning(
                "{Type} ({Event}) references unknown payout {Payout} -- no-op", evt.Type, evt.Id, payoutId);
        }

        return payout;
    }
}
