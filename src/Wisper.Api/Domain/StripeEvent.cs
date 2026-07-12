namespace Wisper.Api.Domain;

/// <summary>
/// A persisted Stripe webhook event (docs/DATA_MODEL.md §9, <c>stripe_events</c>) — the store that makes
/// webhook processing exactly-once. The ingest path upserts on <see cref="Id"/> (the Stripe event id, PK):
/// a first sight is stored <see cref="StripeEventStatus.Received"/> and handed to an idempotent handler;
/// a re-delivery of the same id is a dedupe hit and acked without re-processing (docs/PAYMENTS.md §9).
/// </summary>
public sealed record StripeEvent
{
    /// <summary>Stripe event id — the PK and dedupe key.</summary>
    public required string Id { get; init; }

    /// <summary>Event type (<c>payment_intent.succeeded</c>, <c>account.updated</c>, <c>transfer.*</c>, …).</summary>
    public required string Type { get; init; }

    /// <summary>The raw event body, stored as <c>jsonb</c> (carried here as its JSON text).</summary>
    public required string Payload { get; init; }

    /// <summary>Processing state (<see cref="StripeEventStatus.Received"/> on insert).</summary>
    public StripeEventStatus Status { get; init; } = StripeEventStatus.Received;

    /// <summary>When the event was first stored (UTC).</summary>
    public DateTimeOffset ReceivedAt { get; init; }

    /// <summary>When handling completed (UTC), or <c>null</c> while still <see cref="StripeEventStatus.Received"/>.</summary>
    public DateTimeOffset? ProcessedAt { get; init; }

    /// <summary>Failure detail when <see cref="Status"/> is <see cref="StripeEventStatus.Failed"/>, else <c>null</c>.</summary>
    public string? Error { get; init; }
}
