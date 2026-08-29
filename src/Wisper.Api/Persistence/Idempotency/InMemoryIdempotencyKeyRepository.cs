using Wisper.Api.Domain;

namespace Wisper.Api.Persistence.Idempotency;

/// <summary>
/// In-memory <see cref="IIdempotencyKeyRepository"/> double for unit tests (Grunt has no Postgres).
/// Semantics mirror the SQL side: <see cref="TryBeginAsync"/> is a first-writer-wins insert on the key
/// (like <c>ON CONFLICT (key) DO NOTHING</c>) -- the winner gets <c>null</c> back and holds the
/// in-progress lock; a loser gets the existing row. The insert is guarded by a lock so two concurrent
/// begins can't both win (the atomicity the DB gives for free).
/// </summary>
public sealed class InMemoryIdempotencyKeyRepository
    : InMemoryRepositoryBase<string, IdempotencyKey>, IIdempotencyKeyRepository
{
    private readonly object _beginGate = new();

    protected override string KeyOf(IdempotencyKey entity) => entity.Key;

    public Task<IdempotencyKey?> TryBeginAsync(IdempotencyKey record, CancellationToken ct = default)
    {
        lock (_beginGate)
        {
            if (Find(record.Key) is { } existing)
            {
                return Task.FromResult<IdempotencyKey?>(existing);
            }

            var stored = record with
            {
                Status = IdempotencyStatus.InProgress,
                ResponseStatus = null,
                ResponseBody = null,
                CreatedAt = record.CreatedAt == default ? DateTimeOffset.UtcNow : record.CreatedAt,
            };
            Insert(stored);
            return Task.FromResult<IdempotencyKey?>(null);
        }
    }

    public Task<IdempotencyKey?> GetAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(Find(key));

    public Task<IdempotencyKey?> CompleteAsync(
        string key, int responseStatus, string responseBody, CancellationToken ct = default)
    {
        if (Find(key) is not { } existing)
        {
            return Task.FromResult<IdempotencyKey?>(null);
        }

        var updated = existing with
        {
            ResponseStatus = responseStatus,
            ResponseBody = responseBody,
            Status = IdempotencyStatus.Done,
        };
        Upsert(updated);
        return Task.FromResult<IdempotencyKey?>(updated);
    }

    public Task<bool> DeleteAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(Remove(key));

    public Task<int> DeleteExpiredAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var expired = Where(r => r.ExpiresAt <= now);
        foreach (var record in expired)
        {
            Remove(record.Key);
        }

        return Task.FromResult(expired.Count);
    }
}
