using Wisper.Api.Domain;

namespace Wisper.Api.Payments;

/// <summary>The outcome of ingesting one webhook delivery (docs/PAYMENTS.md §8), independent of HTTP.</summary>
public enum StripeWebhookOutcome
{
    /// <summary>The event id was already stored in a terminal state — a true duplicate; acked, no-op.</summary>
    Deduplicated,

    /// <summary>A handler ran to completion (row is <c>processed</c>).</summary>
    Processed,

    /// <summary>No handler claims the type — stored but no effect (row is <c>ignored</c>).</summary>
    Ignored,

    /// <summary>A handler threw — the row is <c>failed</c> and the delivery should be retried.</summary>
    Failed,
}

/// <summary>
/// The result of <see cref="StripeWebhookService.IngestAsync"/> — the persisted event's id/type, the
/// pipeline <see cref="Outcome"/>, and the terminal <see cref="StripeEventStatus"/> now on the row. The
/// endpoint turns this into an HTTP status: everything but <see cref="StripeWebhookOutcome.Failed"/> acks
/// <c>200</c>; a failure answers <c>500</c> so Stripe re-delivers (docs/PAYMENTS.md §8.4).
/// </summary>
public sealed record StripeWebhookResult(
    string EventId,
    string EventType,
    StripeWebhookOutcome Outcome,
    StripeEventStatus Status)
{
    /// <summary>True when the delivery should be acked with <c>200</c> (dedupe, processed, or ignored).</summary>
    public bool IsAck => Outcome != StripeWebhookOutcome.Failed;
}
