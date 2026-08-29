using Wisper.Api.Domain;
using Wisper.Api.Persistence.Idempotency;

namespace Wisper.Api.Infrastructure.Idempotency;

/// <summary>
/// The reusable helper behind the <c>Idempotency-Key</c> header (docs/API.md §9, docs/DATA_MODEL.md §10) --
/// the replay / conflict / in-progress-lock logic a money-mutating endpoint (top-up, <c>POST /leases</c>,
/// payout trigger) wraps its work in, so a client retry is always safe. It is deliberately transport-free
/// (no <see cref="HttpContext"/>) so it is unit-testable without Postgres or auth; the endpoint layer
/// (P3+) computes the body hash with <see cref="RequestHash"/>, calls <see cref="BeginAsync"/>, and either
/// replays, 409s, or runs the operation and calls <see cref="CompleteAsync"/>.
/// </summary>
/// <remarks>
/// Semantics: the first request atomically inserts an <see cref="IdempotencyStatus.InProgress"/> row (the
/// lock) via <see cref="IIdempotencyKeyRepository.TryBeginAsync"/> and gets <see cref="IdempotencyOutcome.Began"/>;
/// a retry with the <b>same</b> body replays the stored response once the first completed, or blocks with an
/// in-progress <c>409</c> while it is still running; a retry with a <b>different</b> body under the same key
/// is a <c>409</c> conflict. Rows are TTL'd -- an expired record is swept and the lock retaken.
/// </remarks>
public sealed class IdempotencyService
{
    /// <summary>How long a stored idempotency record is honoured before it may be swept.</summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(24);

    private readonly IIdempotencyKeyRepository _repo;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _ttl;

    public IdempotencyService(IIdempotencyKeyRepository repo, TimeProvider? clock = null, TimeSpan? ttl = null)
    {
        _repo = repo;
        _clock = clock ?? TimeProvider.System;
        _ttl = ttl ?? DefaultTtl;
    }

    /// <summary>
    /// Takes the idempotency lock for (<paramref name="userId"/>, <paramref name="key"/>) and classifies the
    /// request. On <see cref="IdempotencyOutcome.Began"/> the caller runs the operation and then calls
    /// <see cref="CompleteAsync"/>; every other outcome is a short-circuit (replay or 409).
    /// </summary>
    public async Task<IdempotencyResult> BeginAsync(
        Guid userId, string key, string requestHash, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestHash);

        var now = _clock.GetUtcNow();
        var candidate = new IdempotencyKey
        {
            Key = key,
            UserId = userId,
            RequestHash = requestHash,
            CreatedAt = now,
            ExpiresAt = now + _ttl,
        };

        var existing = await _repo.TryBeginAsync(candidate, ct);
        if (existing is null)
        {
            return IdempotencyResult.Began();
        }

        // A stale (expired) record is swept and the lock retaken once, so a long-dead key doesn't wedge.
        if (existing.ExpiresAt <= now)
        {
            await _repo.DeleteAsync(existing.Key, ct);
            var retaken = await _repo.TryBeginAsync(candidate, ct);
            return retaken is null ? IdempotencyResult.Began() : Evaluate(retaken, userId, requestHash);
        }

        return Evaluate(existing, userId, requestHash);
    }

    /// <summary>
    /// Completes the in-progress record for <paramref name="key"/>, storing the response to replay on a
    /// later retry. Returns the stored record, or <c>null</c> if the key is unknown (e.g. already swept).
    /// </summary>
    public Task<IdempotencyKey?> CompleteAsync(
        string key, int responseStatus, string responseBody, CancellationToken ct = default) =>
        _repo.CompleteAsync(key, responseStatus, responseBody, ct);

    /// <summary>
    /// Releases the in-progress lock for <paramref name="key"/> without storing a response, so a request
    /// that <b>failed</b> after <see cref="BeginAsync"/> doesn't wedge the key until its TTL -- the client
    /// can retry the same key. Used only on the error path; a successful op calls <see cref="CompleteAsync"/>.
    /// </summary>
    public Task<bool> AbandonAsync(string key, CancellationToken ct = default) =>
        _repo.DeleteAsync(key, ct);

    /// <summary>Sweeps every expired idempotency record (the TTL cleanup, §10). Returns the count removed.</summary>
    public Task<int> SweepExpiredAsync(CancellationToken ct = default) =>
        _repo.DeleteExpiredAsync(_clock.GetUtcNow(), ct);

    /// <summary>Classifies an existing record against the current request's user and body hash.</summary>
    private static IdempotencyResult Evaluate(IdempotencyKey existing, Guid userId, string requestHash)
    {
        // A different body (or, defensively, a different user) under the same key is a conflict.
        if (existing.UserId != userId ||
            !string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal))
        {
            return IdempotencyResult.Conflict(
                "The Idempotency-Key was already used with a different request.");
        }

        // Same key + same body: replay once done, otherwise the first request is still in flight.
        return existing.Status == IdempotencyStatus.Done
            ? IdempotencyResult.Replay(existing.ResponseStatus ?? 200, existing.ResponseBody)
            : IdempotencyResult.InProgress();
    }
}
