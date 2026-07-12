using Wisper.Api.Domain;

namespace Wisper.Api.Persistence.Audit;

/// <summary>
/// In-memory <see cref="IAuditLogRepository"/> double for unit tests (Grunt has no Postgres). Semantics
/// mirror the SQL side: <see cref="AppendAsync"/> assigns a monotonically increasing id (like the DB
/// identity) and a creation timestamp when unset; there is no mutator, matching the append-only table.
/// </summary>
public sealed class InMemoryAuditLogRepository
    : InMemoryRepositoryBase<long, AuditLogEntry>, IAuditLogRepository
{
    private long _nextId;

    protected override long KeyOf(AuditLogEntry entity) => entity.Id;

    public Task<AuditLogEntry> AppendAsync(AuditLogEntry entry, CancellationToken ct = default)
    {
        var stored = entry with
        {
            Id = Interlocked.Increment(ref _nextId),
            CreatedAt = entry.CreatedAt == default ? DateTimeOffset.UtcNow : entry.CreatedAt,
        };
        Insert(stored);
        return Task.FromResult(stored);
    }

    public Task<IReadOnlyList<AuditLogEntry>> ListByTargetAsync(
        string targetType, Guid targetId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AuditLogEntry>>(
            Where(e => e.TargetType == targetType && e.TargetId == targetId)
                .OrderByDescending(e => e.Id).ToList());

    public Task<IReadOnlyList<AuditLogEntry>> ListByActorAsync(Guid actorUserId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AuditLogEntry>>(
            Where(e => e.ActorUserId == actorUserId).OrderByDescending(e => e.Id).ToList());

    public Task<IReadOnlyList<AuditLogEntry>> ListAsync(AuditLogQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var matches = Where(e =>
                (query.ActorUserId is not { } actor || e.ActorUserId == actor)
                && (query.TargetType is null || e.TargetType == query.TargetType)
                && (query.TargetId is not { } target || e.TargetId == target)
                && (query.Action is null || e.Action == query.Action)
                && (query.BeforeId is not { } before || e.Id < before))
            .OrderByDescending(e => e.Id)
            .Take(Math.Max(0, query.Limit))
            .ToList();
        return Task.FromResult<IReadOnlyList<AuditLogEntry>>(matches);
    }
}
