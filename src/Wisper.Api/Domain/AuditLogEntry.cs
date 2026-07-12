namespace Wisper.Api.Domain;

/// <summary>
/// One append-only audit record (docs/DATA_MODEL.md §12, <c>audit_log</c>) — every admin/policy/
/// money-sensitive action, written for a permanent, tamper-evident trail (the table forbids
/// UPDATE/DELETE). <see cref="Meta"/> carries the before/after, amounts, and reason as <c>jsonb</c>.
/// </summary>
public sealed record AuditLogEntry
{
    /// <summary>Row id (DB identity; unset until persisted).</summary>
    public long Id { get; init; }

    /// <summary>The acting admin, or <c>null</c> for a system action.</summary>
    public Guid? ActorUserId { get; init; }

    /// <summary>The action taken (<c>host.suspend</c>, <c>policy.update</c>, <c>payout.trigger</c>, …).</summary>
    public required string Action { get; init; }

    /// <summary>The kind of entity acted on (<c>host</c>, <c>user</c>, <c>lease</c>, …), if any.</summary>
    public string? TargetType { get; init; }

    /// <summary>The id of the entity acted on, if any.</summary>
    public Guid? TargetId { get; init; }

    /// <summary>Structured context (before/after, amounts, reason) as <c>jsonb</c> text, or <c>null</c>.</summary>
    public string? Meta { get; init; }

    /// <summary>When the action was recorded (UTC).</summary>
    public DateTimeOffset CreatedAt { get; init; }
}
