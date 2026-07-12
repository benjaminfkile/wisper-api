using Dapper;
using Wisper.Api.Domain;

namespace Wisper.Api.Persistence.Stripe;

/// <summary>
/// Dapper + explicit-SQL <see cref="IStripeEventRepository"/> over Postgres (docs/DATA_MODEL.md §9). The
/// <c>status</c> enum is written as a text parameter cast to <c>stripe_event_status</c> and read back via
/// <c>::text</c>; <c>payload</c> is <c>jsonb</c>, written with a <c>::jsonb</c> cast and read via
/// <c>::text</c>. <see cref="TryInsertReceivedAsync"/> uses <c>ON CONFLICT (id) DO NOTHING</c> so a
/// re-delivered webhook is a no-op. Not exercised by the unit suite (Grunt has no Postgres); the in-memory
/// double is.
/// </summary>
public sealed class StripeEventRepository : RepositoryBase, IStripeEventRepository
{
    private const string SelectColumns =
        "id, type, payload::text AS payload, status::text AS status, received_at, processed_at, error";

    public StripeEventRepository(Db db) : base(db)
    {
    }

    public async Task<bool> TryInsertReceivedAsync(StripeEvent evt, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO stripe_events (id, type, payload, status, received_at, processed_at, error)
            VALUES (@Id, @Type, @Payload::jsonb, @Status::stripe_event_status,
                    COALESCE(@ReceivedAt, now()), @ProcessedAt, @Error)
            ON CONFLICT (id) DO NOTHING
            """;

        var parameters = new
        {
            evt.Id,
            evt.Type,
            evt.Payload,
            Status = PgEnum.ToLabel(StripeEventStatus.Received),
            ReceivedAt = evt.ReceivedAt == default ? (DateTimeOffset?)null : evt.ReceivedAt,
            evt.ProcessedAt,
            evt.Error,
        };

        await using var conn = await OpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition(sql, parameters, cancellationToken: ct));
        return rows == 1;
    }

    public async Task<StripeEvent?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            $"SELECT {SelectColumns} FROM stripe_events WHERE id = @id", new { id }, cancellationToken: ct));
        return row?.ToEntity();
    }

    public async Task<StripeEvent?> SetStatusAsync(
        string id, StripeEventStatus status, DateTimeOffset processedAt, string? error = null,
        CancellationToken ct = default)
    {
        const string sql = $"""
            UPDATE stripe_events
               SET status = @Status::stripe_event_status, processed_at = @ProcessedAt, error = @Error
             WHERE id = @Id
            RETURNING {SelectColumns}
            """;

        var parameters = new { Id = id, Status = PgEnum.ToLabel(status), ProcessedAt = processedAt, Error = error };

        await using var conn = await OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(sql, parameters, cancellationToken: ct));
        return row?.ToEntity();
    }

    /// <summary>Dapper projection of a <c>stripe_events</c> row (enum + jsonb columns arrive as text).</summary>
    private sealed class Row
    {
        public string Id { get; init; } = string.Empty;
        public string Type { get; init; } = string.Empty;
        public string Payload { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public DateTimeOffset ReceivedAt { get; init; }
        public DateTimeOffset? ProcessedAt { get; init; }
        public string? Error { get; init; }

        public StripeEvent ToEntity() => new()
        {
            Id = Id,
            Type = Type,
            Payload = Payload,
            Status = PgEnum.Parse<StripeEventStatus>(Status),
            ReceivedAt = ReceivedAt,
            ProcessedAt = ProcessedAt,
            Error = Error,
        };
    }
}
