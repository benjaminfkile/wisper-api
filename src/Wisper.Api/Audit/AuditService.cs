using System.Text.Json;
using Wisper.Api.Domain;
using Wisper.Api.Persistence.Audit;

namespace Wisper.Api.Audit;

/// <summary>
/// Records admin/policy/money-sensitive actions to the append-only audit log (docs/DATA_MODEL.md §12). The
/// admin and billing surfaces (P6/P7) call <see cref="RecordAsync"/> whenever they suspend a host, publish
/// a policy, trigger a payout, or post a ledger adjustment -- the trail every such action leaves. The
/// optional <c>meta</c> (before/after, amounts, reason) is serialized to the <c>jsonb</c> column.
/// </summary>
public sealed class AuditService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IAuditLogRepository _repo;
    private readonly TimeProvider _clock;

    public AuditService(IAuditLogRepository repo, TimeProvider? clock = null)
    {
        _repo = repo;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Appends an audit record for <paramref name="action"/> (e.g. <c>host.suspend</c>, <c>policy.update</c>,
    /// <c>payout.trigger</c>, <c>ledger.adjustment</c>). <paramref name="actorUserId"/> is the admin, or
    /// <c>null</c> for a system action; <paramref name="meta"/> is any object serialized to the <c>jsonb</c>
    /// meta column. Returns the stored row (with its assigned id and timestamp).
    /// </summary>
    public Task<AuditLogEntry> RecordAsync(
        string action,
        Guid? actorUserId = null,
        string? targetType = null,
        Guid? targetId = null,
        object? meta = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);

        var entry = new AuditLogEntry
        {
            Action = action,
            ActorUserId = actorUserId,
            TargetType = targetType,
            TargetId = targetId,
            Meta = meta is null ? null : JsonSerializer.Serialize(meta, Json),
            CreatedAt = _clock.GetUtcNow(),
        };

        return _repo.AppendAsync(entry, ct);
    }

    /// <summary>
    /// Reads a page of the audit trail for the admin audit view (docs/API.md §8) -- entries matching
    /// <paramref name="query"/>'s optional actor/target/action filters, newest first, keyset-paginated.
    /// </summary>
    public Task<IReadOnlyList<AuditLogEntry>> QueryAsync(
        AuditLogQuery query, CancellationToken ct = default) =>
        _repo.ListAsync(query, ct);
}
