using Wisper.Api.Domain;

namespace Wisper.Api.Persistence.Audit;

/// <summary>
/// Data access for <see cref="AuditLogEntry"/> rows (docs/DATA_MODEL.md §12) — the append-only trail of
/// admin/policy/money-sensitive actions. Only appends and reads exist; the table forbids UPDATE/DELETE. A
/// Dapper + explicit-SQL implementation backs Postgres; an in-memory double backs the unit suite (Grunt
/// has no Postgres).
/// </summary>
public interface IAuditLogRepository : IRepository
{
    /// <summary>Appends an audit entry and returns the stored row (with its DB-assigned id and timestamp).</summary>
    Task<AuditLogEntry> AppendAsync(AuditLogEntry entry, CancellationToken ct = default);

    /// <summary>Entries for a target (<paramref name="targetType"/>, <paramref name="targetId"/>), newest first.</summary>
    Task<IReadOnlyList<AuditLogEntry>> ListByTargetAsync(
        string targetType, Guid targetId, CancellationToken ct = default);

    /// <summary>Entries recorded by an actor, newest first.</summary>
    Task<IReadOnlyList<AuditLogEntry>> ListByActorAsync(Guid actorUserId, CancellationToken ct = default);

    /// <summary>
    /// The admin audit view (docs/API.md §8, <c>GET /v1/admin/audit</c>): entries matching
    /// <paramref name="query"/>'s optional actor/target/action filters, newest first (by the monotonic id),
    /// paginated by <see cref="AuditLogQuery.BeforeId"/> + <see cref="AuditLogQuery.Limit"/>.
    /// </summary>
    Task<IReadOnlyList<AuditLogEntry>> ListAsync(AuditLogQuery query, CancellationToken ct = default);
}

/// <summary>
/// Filters + a page bound for the admin audit view (docs/API.md §8, docs/DATA_MODEL.md §12). Every filter
/// is optional (a <c>null</c> filter matches all); <see cref="BeforeId"/> is the keyset cursor — only rows
/// with a strictly smaller id (i.e. older, since ids are monotonic) are returned — and <see cref="Limit"/>
/// bounds the page.
/// </summary>
public sealed record AuditLogQuery
{
    /// <summary>Restrict to entries recorded by this actor, or <c>null</c> for any.</summary>
    public Guid? ActorUserId { get; init; }

    /// <summary>Restrict to this target type (<c>host</c>, <c>user</c>, …), or <c>null</c> for any.</summary>
    public string? TargetType { get; init; }

    /// <summary>Restrict to this target id, or <c>null</c> for any.</summary>
    public Guid? TargetId { get; init; }

    /// <summary>Restrict to this exact action (<c>host.suspend</c>, <c>policy.update</c>, …), or <c>null</c> for any.</summary>
    public string? Action { get; init; }

    /// <summary>Keyset cursor: return only rows with a smaller id (older), or <c>null</c> for the first page.</summary>
    public long? BeforeId { get; init; }

    /// <summary>Maximum rows to return.</summary>
    public int Limit { get; init; } = 50;
}
