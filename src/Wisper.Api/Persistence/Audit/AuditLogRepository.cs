using Dapper;
using Wisper.Api.Domain;

namespace Wisper.Api.Persistence.Audit;

/// <summary>
/// Dapper + explicit-SQL <see cref="IAuditLogRepository"/> over Postgres (docs/DATA_MODEL.md §12). <c>id</c>
/// is a DB identity (server-assigned on insert); <c>meta</c> is <c>jsonb</c>, written with a <c>::jsonb</c>
/// cast and read via <c>::text</c>. The table is append-only -- an immutability trigger blocks UPDATE/DELETE
/// -- so no mutator is offered. Not exercised by the unit suite (Grunt has no Postgres); the in-memory
/// double is.
/// </summary>
public sealed class AuditLogRepository : RepositoryBase, IAuditLogRepository
{
    private const string SelectColumns =
        "id, actor_user_id, action, target_type, target_id, meta::text AS meta, created_at";

    public AuditLogRepository(Db db) : base(db)
    {
    }

    public async Task<AuditLogEntry> AppendAsync(AuditLogEntry entry, CancellationToken ct = default)
    {
        const string sql = $"""
            INSERT INTO audit_log (actor_user_id, action, target_type, target_id, meta, created_at)
            VALUES (@ActorUserId, @Action, @TargetType, @TargetId, @Meta::jsonb, COALESCE(@CreatedAt, now()))
            RETURNING {SelectColumns}
            """;

        var parameters = new
        {
            entry.ActorUserId,
            entry.Action,
            entry.TargetType,
            entry.TargetId,
            entry.Meta,
            CreatedAt = entry.CreatedAt == default ? (DateTimeOffset?)null : entry.CreatedAt,
        };

        await using var conn = await OpenConnectionAsync(ct);
        var row = await conn.QuerySingleAsync<Row>(new CommandDefinition(sql, parameters, cancellationToken: ct));
        return row.ToEntity();
    }

    public async Task<IReadOnlyList<AuditLogEntry>> ListByTargetAsync(
        string targetType, Guid targetId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<Row>(new CommandDefinition(
            $"SELECT {SelectColumns} FROM audit_log WHERE target_type = @targetType AND target_id = @targetId " +
            "ORDER BY created_at DESC, id DESC", new { targetType, targetId }, cancellationToken: ct));
        return rows.Select(r => r.ToEntity()).ToList();
    }

    public async Task<IReadOnlyList<AuditLogEntry>> ListByActorAsync(Guid actorUserId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<Row>(new CommandDefinition(
            $"SELECT {SelectColumns} FROM audit_log WHERE actor_user_id = @actorUserId " +
            "ORDER BY created_at DESC, id DESC", new { actorUserId }, cancellationToken: ct));
        return rows.Select(r => r.ToEntity()).ToList();
    }

    public async Task<IReadOnlyList<AuditLogEntry>> ListAsync(AuditLogQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        // Every filter is optional (NULL matches all); the keyset cursor pages by descending id.
        const string sql = $"""
            SELECT {SelectColumns} FROM audit_log
             WHERE (@ActorUserId IS NULL OR actor_user_id = @ActorUserId)
               AND (@TargetType  IS NULL OR target_type   = @TargetType)
               AND (@TargetId    IS NULL OR target_id     = @TargetId)
               AND (@Action      IS NULL OR action        = @Action)
               AND (@BeforeId    IS NULL OR id            < @BeforeId)
             ORDER BY id DESC
             LIMIT @Limit
            """;

        var parameters = new
        {
            query.ActorUserId,
            query.TargetType,
            query.TargetId,
            query.Action,
            query.BeforeId,
            Limit = Math.Max(0, query.Limit),
        };

        await using var conn = await OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<Row>(new CommandDefinition(sql, parameters, cancellationToken: ct));
        return rows.Select(r => r.ToEntity()).ToList();
    }

    /// <summary>Dapper projection of an <c>audit_log</c> row (jsonb <c>meta</c> arrives as text).</summary>
    private sealed class Row
    {
        public long Id { get; init; }
        public Guid? ActorUserId { get; init; }
        public string Action { get; init; } = string.Empty;
        public string? TargetType { get; init; }
        public Guid? TargetId { get; init; }
        public string? Meta { get; init; }
        public DateTimeOffset CreatedAt { get; init; }

        public AuditLogEntry ToEntity() => new()
        {
            Id = Id,
            ActorUserId = ActorUserId,
            Action = Action,
            TargetType = TargetType,
            TargetId = TargetId,
            Meta = Meta,
            CreatedAt = CreatedAt,
        };
    }
}
