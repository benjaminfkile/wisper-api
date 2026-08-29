using Wisper.Api.Domain;
using Wisper.Api.Persistence.Stripe;

namespace Wisper.Api.Payments;

/// <summary>
/// The webhook ingest pipeline (docs/PAYMENTS.md §8) -- signature-verify, persist-then-process, dedupe, and
/// dispatch to idempotent handlers with retry-safe failure recording. Kept HTTP-free so it is unit-testable
/// with a fake verifier + in-memory event store (Grunt has no Stripe/Postgres); the endpoint is a thin
/// shell that reads the raw body and header and calls <see cref="IngestAsync"/>.
///
/// <para>Ordering vs. dedupe: the <c>stripe_events</c> PK makes the store exactly-once. A first sight is
/// inserted <c>received</c> and dispatched. A re-delivery whose row is already terminal-<b>successful</b>
/// (<c>processed</c>/<c>ignored</c>) is a true duplicate → acked, no-op (§8.2, edge matrix). A re-delivery
/// whose row is <c>failed</c> or a stuck <c>received</c> is a genuine <i>retry</i> → re-dispatched to the
/// idempotent handler (§8.4); because handlers are keyed by the event id, replaying one is safe. A handler
/// throw records <c>failed</c> and the endpoint answers non-2xx so Stripe re-delivers.</para>
/// </summary>
public sealed class StripeWebhookService
{
    private readonly IStripeSignatureVerifier _verifier;
    private readonly IStripeEventRepository _events;
    private readonly StripeEventDispatcher _dispatcher;
    private readonly TimeProvider _clock;
    private readonly ILogger<StripeWebhookService> _logger;

    public StripeWebhookService(
        IStripeSignatureVerifier verifier,
        IStripeEventRepository events,
        StripeEventDispatcher dispatcher,
        TimeProvider clock,
        ILogger<StripeWebhookService> logger)
    {
        _verifier = verifier;
        _events = events;
        _dispatcher = dispatcher;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Verifies, stores, and dispatches one raw webhook delivery. Throws
    /// <see cref="StripeSignatureException"/> when the signature does not verify (the endpoint maps that to
    /// <c>400</c>, no processing). Otherwise returns the <see cref="StripeWebhookResult"/> describing the
    /// dedupe/processed/ignored/failed outcome.
    /// </summary>
    public async Task<StripeWebhookResult> IngestAsync(
        string payload, string? signatureHeader, CancellationToken ct = default)
    {
        // 1. Signature is the trust boundary -- verify before we touch the store or a handler (§8.1).
        var evt = _verifier.Verify(payload, signatureHeader);

        // 2. Persist-then-process: first sight inserts `received`; a conflict is a re-delivery (§8.2).
        var stored = new StripeEvent
        {
            Id = evt.Id,
            Type = evt.Type,
            Payload = payload,
            Status = StripeEventStatus.Received,
            ReceivedAt = _clock.GetUtcNow(),
        };

        var inserted = await _events.TryInsertReceivedAsync(stored, ct);
        if (!inserted)
        {
            var existing = await _events.GetAsync(evt.Id, ct);
            if (existing is null)
            {
                // Vanishingly rare: another delivery deleted it between insert and read. Treat as fresh.
                _logger.LogWarning("stripe event {Id} missing right after an insert conflict -- re-dispatching", evt.Id);
            }
            else if (existing.Status is StripeEventStatus.Processed or StripeEventStatus.Ignored)
            {
                // True duplicate of an already-settled event → ack, no-op (§8.2, edge matrix).
                _logger.LogInformation("stripe event {Id} ({Type}) is a duplicate ({Status}) -- dedupe no-op",
                    evt.Id, evt.Type, existing.Status);
                return new StripeWebhookResult(evt.Id, evt.Type, StripeWebhookOutcome.Deduplicated, existing.Status);
            }
            else
            {
                // `failed` / stuck `received` → this delivery is a retry; re-dispatch the idempotent handler (§8.4).
                _logger.LogInformation("stripe event {Id} ({Type}) re-delivered in state {Status} -- retrying",
                    evt.Id, evt.Type, existing.Status);
            }
        }

        // 3. Dispatch to the idempotent handler and record the terminal status (§8.3, §8.4).
        return await DispatchAndRecordAsync(evt, ct);
    }

    private async Task<StripeWebhookResult> DispatchAndRecordAsync(Stripe.Event evt, CancellationToken ct)
    {
        try
        {
            var status = await _dispatcher.DispatchAsync(evt, ct);
            await _events.SetStatusAsync(evt.Id, status, _clock.GetUtcNow(), error: null, ct);

            var outcome = status == StripeEventStatus.Ignored
                ? StripeWebhookOutcome.Ignored
                : StripeWebhookOutcome.Processed;
            return new StripeWebhookResult(evt.Id, evt.Type, outcome, status);
        }
        catch (Exception ex)
        {
            // Retry-safe failure: durably record the error and let Stripe re-deliver (§8.4). Never rethrow --
            // the endpoint decides the HTTP status from the result, and the row is the dead-letter record.
            _logger.LogError(ex, "stripe webhook handler failed for {Id} ({Type})", evt.Id, evt.Type);
            await _events.SetStatusAsync(
                evt.Id, StripeEventStatus.Failed, _clock.GetUtcNow(), error: ex.Message, ct);
            return new StripeWebhookResult(evt.Id, evt.Type, StripeWebhookOutcome.Failed, StripeEventStatus.Failed);
        }
    }
}
