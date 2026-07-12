using Wisper.Api.Domain;

namespace Wisper.Api.Persistence.Stripe;

/// <summary>
/// In-memory <see cref="IStripeEventRepository"/> double for unit tests (Grunt has no Postgres). Semantics
/// mirror the SQL side: <see cref="TryInsertReceivedAsync"/> is a first-writer-wins insert on the event id
/// (like <c>ON CONFLICT (id) DO NOTHING</c>) so a re-delivery reports <c>false</c> and changes nothing.
/// </summary>
public sealed class InMemoryStripeEventRepository
    : InMemoryRepositoryBase<string, StripeEvent>, IStripeEventRepository
{
    protected override string KeyOf(StripeEvent entity) => entity.Id;

    public Task<bool> TryInsertReceivedAsync(StripeEvent evt, CancellationToken ct = default)
    {
        if (Find(evt.Id) is not null)
        {
            return Task.FromResult(false);
        }

        var stored = evt with
        {
            Status = StripeEventStatus.Received,
            ReceivedAt = evt.ReceivedAt == default ? DateTimeOffset.UtcNow : evt.ReceivedAt,
        };
        Insert(stored);
        return Task.FromResult(true);
    }

    public Task<StripeEvent?> GetAsync(string id, CancellationToken ct = default) =>
        Task.FromResult(Find(id));

    public Task<StripeEvent?> SetStatusAsync(
        string id, StripeEventStatus status, DateTimeOffset processedAt, string? error = null,
        CancellationToken ct = default)
    {
        if (Find(id) is not { } existing)
        {
            return Task.FromResult<StripeEvent?>(null);
        }

        var updated = existing with { Status = status, ProcessedAt = processedAt, Error = error };
        Upsert(updated);
        return Task.FromResult<StripeEvent?>(updated);
    }
}
