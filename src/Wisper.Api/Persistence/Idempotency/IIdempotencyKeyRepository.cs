using Wisper.Api.Domain;

namespace Wisper.Api.Persistence.Idempotency;

/// <summary>
/// Data access for <see cref="IdempotencyKey"/> rows — the replay/conflict/in-progress store behind the
/// <c>Idempotency-Key</c> header (docs/DATA_MODEL.md §10, docs/API.md §9). The atomic
/// <see cref="TryBeginAsync"/> is the lock: the first request inserts an
/// <see cref="IdempotencyStatus.InProgress"/> row and wins; a concurrent or later request sees the
/// existing row instead. A Dapper + explicit-SQL implementation backs Postgres; an in-memory double backs
/// the unit suite (Grunt has no Postgres).
/// </summary>
public interface IIdempotencyKeyRepository : IRepository
{
    /// <summary>
    /// Atomically inserts <paramref name="record"/> (an <see cref="IdempotencyStatus.InProgress"/> row) if
    /// its key is free, mirroring <c>INSERT … ON CONFLICT (key) DO NOTHING</c>. Returns <c>null</c> when
    /// the row was inserted (the caller holds the lock and should run the operation), or the <b>existing</b>
    /// row when the key is already taken (the caller must replay/conflict off it).
    /// </summary>
    Task<IdempotencyKey?> TryBeginAsync(IdempotencyKey record, CancellationToken ct = default);

    /// <summary>Gets the record for <paramref name="key"/>, or <c>null</c> if none.</summary>
    Task<IdempotencyKey?> GetAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// Completes an in-progress record: stores <paramref name="responseStatus"/>/
    /// <paramref name="responseBody"/> and flips it to <see cref="IdempotencyStatus.Done"/>. Returns the
    /// updated row, or <c>null</c> if the key is unknown.
    /// </summary>
    Task<IdempotencyKey?> CompleteAsync(
        string key, int responseStatus, string responseBody, CancellationToken ct = default);

    /// <summary>Removes the record for <paramref name="key"/>; <c>true</c> if one was removed.</summary>
    Task<bool> DeleteAsync(string key, CancellationToken ct = default);

    /// <summary>Deletes every record whose <c>expires_at</c> is at or before <paramref name="now"/> (TTL sweep). Returns the count removed.</summary>
    Task<int> DeleteExpiredAsync(DateTimeOffset now, CancellationToken ct = default);
}
