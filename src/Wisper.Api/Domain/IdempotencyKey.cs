namespace Wisper.Api.Domain;

/// <summary>
/// The lifecycle of an <see cref="IdempotencyKey"/> row (docs/DATA_MODEL.md §10). Stored as plain text
/// (not a native enum): <see cref="InProgress"/> is the lock taken while the first request runs;
/// <see cref="Done"/> means the response is stored and can be replayed. The labels are <c>in_progress</c>
/// and <c>done</c>.
/// </summary>
public enum IdempotencyStatus
{
    InProgress,
    Done,
}

/// <summary>
/// An idempotency record guarding a money-mutating POST (docs/DATA_MODEL.md §10, docs/API.md §9). The
/// first request inserts an <see cref="IdempotencyStatus.InProgress"/> row keyed by the client-supplied
/// <see cref="Key"/> (the in-progress lock); on completion the response is stored and the row flips to
/// <see cref="IdempotencyStatus.Done"/>. A same-key+same-body retry replays
/// <see cref="ResponseStatus"/>/<see cref="ResponseBody"/> verbatim; a same-key+different-body retry is a
/// conflict, detected by <see cref="RequestHash"/>. Rows are user-scoped and TTL'd via <see cref="ExpiresAt"/>.
/// </summary>
public sealed record IdempotencyKey
{
    /// <summary>The idempotency key (PK) -- a client-generated UUID on the <c>Idempotency-Key</c> header.</summary>
    public required string Key { get; init; }

    /// <summary>The user the key is scoped to.</summary>
    public required Guid UserId { get; init; }

    /// <summary>Hash of the request body -- a retry with a different body under the same key is a conflict.</summary>
    public required string RequestHash { get; init; }

    /// <summary>The stored HTTP status to replay, or <c>null</c> while <see cref="IdempotencyStatus.InProgress"/>.</summary>
    public int? ResponseStatus { get; init; }

    /// <summary>The stored response body (<c>jsonb</c> text) to replay, or <c>null</c> while in progress.</summary>
    public string? ResponseBody { get; init; }

    /// <summary>The record's state (<see cref="IdempotencyStatus.InProgress"/> on insert).</summary>
    public IdempotencyStatus Status { get; init; } = IdempotencyStatus.InProgress;

    /// <summary>When the record was created (UTC).</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the record expires and may be swept (UTC) -- the TTL.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }
}
