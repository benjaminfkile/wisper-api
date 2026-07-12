using Dapper;
using Wisper.Api.Domain;

namespace Wisper.Api.Persistence.Idempotency;

/// <summary>
/// Dapper + explicit-SQL <see cref="IIdempotencyKeyRepository"/> over Postgres (docs/DATA_MODEL.md §10).
/// The <c>status</c> column is plain text (<c>in_progress</c>/<c>done</c>, not a native enum); the stored
/// <c>response_body</c> is <c>jsonb</c>, written with a <c>::jsonb</c> cast and read via <c>::text</c>.
/// <see cref="TryBeginAsync"/> uses <c>ON CONFLICT (key) DO NOTHING</c> as the in-progress lock. Not
/// exercised by the unit suite (Grunt has no Postgres); the in-memory double is.
/// </summary>
public sealed class IdempotencyKeyRepository : RepositoryBase, IIdempotencyKeyRepository
{
    private const string SelectColumns =
        "key, user_id, request_hash, response_status, response_body::text AS response_body, status, " +
        "created_at, expires_at";

    public IdempotencyKeyRepository(Db db) : base(db)
    {
    }

    public async Task<IdempotencyKey?> TryBeginAsync(IdempotencyKey record, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO idempotency_keys (key, user_id, request_hash, status, created_at, expires_at)
            VALUES (@Key, @UserId, @RequestHash, 'in_progress', COALESCE(@CreatedAt, now()), @ExpiresAt)
            ON CONFLICT (key) DO NOTHING
            """;

        var parameters = new
        {
            record.Key,
            record.UserId,
            record.RequestHash,
            CreatedAt = record.CreatedAt == default ? (DateTimeOffset?)null : record.CreatedAt,
            record.ExpiresAt,
        };

        await using var conn = await OpenConnectionAsync(ct);
        var inserted = await conn.ExecuteAsync(new CommandDefinition(sql, parameters, cancellationToken: ct));
        // Inserted → we hold the lock (return null). On conflict → hand back the existing row to replay/conflict off.
        return inserted == 1 ? null : await GetAsync(record.Key, ct);
    }

    public async Task<IdempotencyKey?> GetAsync(string key, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            $"SELECT {SelectColumns} FROM idempotency_keys WHERE key = @key", new { key }, cancellationToken: ct));
        return row?.ToEntity();
    }

    public async Task<IdempotencyKey?> CompleteAsync(
        string key, int responseStatus, string responseBody, CancellationToken ct = default)
    {
        const string sql = $"""
            UPDATE idempotency_keys
               SET response_status = @ResponseStatus, response_body = @ResponseBody::jsonb, status = 'done'
             WHERE key = @Key
            RETURNING {SelectColumns}
            """;

        var parameters = new { Key = key, ResponseStatus = responseStatus, ResponseBody = responseBody };

        await using var conn = await OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(sql, parameters, cancellationToken: ct));
        return row?.ToEntity();
    }

    public async Task<bool> DeleteAsync(string key, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM idempotency_keys WHERE key = @key", new { key }, cancellationToken: ct));
        return rows > 0;
    }

    public async Task<int> DeleteExpiredAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM idempotency_keys WHERE expires_at <= @now", new { now }, cancellationToken: ct));
    }

    /// <summary>Dapper projection of an <c>idempotency_keys</c> row (jsonb + status columns arrive as text).</summary>
    private sealed class Row
    {
        public string Key { get; init; } = string.Empty;
        public Guid UserId { get; init; }
        public string RequestHash { get; init; } = string.Empty;
        public int? ResponseStatus { get; init; }
        public string? ResponseBody { get; init; }
        public string Status { get; init; } = string.Empty;
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset ExpiresAt { get; init; }

        public IdempotencyKey ToEntity() => new()
        {
            Key = Key,
            UserId = UserId,
            RequestHash = RequestHash,
            ResponseStatus = ResponseStatus,
            ResponseBody = ResponseBody,
            Status = PgEnum.ParseSnake<IdempotencyStatus>(Status),
            CreatedAt = CreatedAt,
            ExpiresAt = ExpiresAt,
        };
    }
}
