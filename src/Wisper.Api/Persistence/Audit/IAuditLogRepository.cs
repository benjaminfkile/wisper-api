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
}
