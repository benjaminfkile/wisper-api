namespace Wisper.Api.Infrastructure.Idempotency;

/// <summary>
/// The four ways <see cref="IdempotencyService.BeginAsync"/> can resolve a request carrying an
/// <c>Idempotency-Key</c> (docs/API.md §9, docs/DATA_MODEL.md §10).
/// </summary>
public enum IdempotencyOutcome
{
    /// <summary>The key was free — the caller holds the lock and must run the operation, then complete it.</summary>
    Began,

    /// <summary>A completed record with a matching body exists — replay its stored response verbatim.</summary>
    Replay,

    /// <summary>The key was reused with a <b>different</b> body — a <c>409 conflict</c>.</summary>
    Conflict,

    /// <summary>A first request under this key is still running — a <c>409</c> in-progress lock.</summary>
    InProgress,
}

/// <summary>
/// The result of beginning an idempotent request (docs/API.md §9). On <see cref="IdempotencyOutcome.Replay"/>
/// the caller returns <see cref="ResponseStatus"/>/<see cref="ResponseBody"/>; on
/// <see cref="IdempotencyOutcome.Conflict"/> / <see cref="IdempotencyOutcome.InProgress"/> it maps to a
/// <c>409</c> (see <see cref="Infrastructure.ApiErrorCode.Conflict"/>) using <see cref="Message"/>; on
/// <see cref="IdempotencyOutcome.Began"/> it runs the operation and then calls
/// <see cref="IdempotencyService.CompleteAsync"/>.
/// </summary>
public sealed record IdempotencyResult
{
    /// <summary>Which of the four cases occurred.</summary>
    public required IdempotencyOutcome Outcome { get; init; }

    /// <summary>The stored HTTP status to replay (only meaningful for <see cref="IdempotencyOutcome.Replay"/>).</summary>
    public int ResponseStatus { get; init; }

    /// <summary>The stored response body to replay (only for <see cref="IdempotencyOutcome.Replay"/>).</summary>
    public string? ResponseBody { get; init; }

    /// <summary>A human-readable reason for a conflict / in-progress outcome, for the error envelope.</summary>
    public string? Message { get; init; }

    /// <summary>The caller holds the lock and must run the operation.</summary>
    public static IdempotencyResult Began() => new() { Outcome = IdempotencyOutcome.Began };

    /// <summary>Replay the stored response for a same-key+same-body retry.</summary>
    public static IdempotencyResult Replay(int status, string? body) =>
        new() { Outcome = IdempotencyOutcome.Replay, ResponseStatus = status, ResponseBody = body };

    /// <summary>The key was reused with a different body.</summary>
    public static IdempotencyResult Conflict(string message) =>
        new() { Outcome = IdempotencyOutcome.Conflict, Message = message };

    /// <summary>A first request under this key is still in flight.</summary>
    public static IdempotencyResult InProgress() => new()
    {
        Outcome = IdempotencyOutcome.InProgress,
        Message = "A request with this Idempotency-Key is already in progress.",
    };
}
