using Wisper.Api.Domain;

namespace Wisper.Api.Persistence.Stripe;

/// <summary>
/// Data access for <see cref="StripeEvent"/> rows — the webhook dedupe store that makes Stripe processing
/// exactly-once (docs/DATA_MODEL.md §9, docs/PAYMENTS.md §9). The ingest path
/// <see cref="TryInsertReceivedAsync"/>s the event keyed by its Stripe id: a first sight is stored and
/// handed to a handler; a re-delivery is a no-op. Handlers then move the row to a terminal state with
/// <see cref="SetStatusAsync"/>. A Dapper + explicit-SQL implementation backs Postgres; an in-memory
/// double backs the unit suite (Grunt has no Postgres).
/// </summary>
public interface IStripeEventRepository : IRepository
{
    /// <summary>
    /// Inserts <paramref name="evt"/> as <see cref="StripeEventStatus.Received"/> if its id is new
    /// (mirroring <c>INSERT … ON CONFLICT (id) DO NOTHING</c>). Returns <c>true</c> when the row was
    /// newly inserted (the caller should process it), or <c>false</c> when the id already existed (a
    /// re-delivery to ack and drop — the dedupe hit).
    /// </summary>
    Task<bool> TryInsertReceivedAsync(StripeEvent evt, CancellationToken ct = default);

    /// <summary>Gets the stored event by id, or <c>null</c> if none.</summary>
    Task<StripeEvent?> GetAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Moves the event to a terminal <paramref name="status"/> (<see cref="StripeEventStatus.Processed"/>/
    /// <see cref="StripeEventStatus.Ignored"/>/<see cref="StripeEventStatus.Failed"/>), stamping
    /// <c>processed_at</c> and recording <paramref name="error"/> on failure. Returns the updated row,
    /// or <c>null</c> if the id is unknown.
    /// </summary>
    Task<StripeEvent?> SetStatusAsync(
        string id, StripeEventStatus status, DateTimeOffset processedAt, string? error = null,
        CancellationToken ct = default);
}
