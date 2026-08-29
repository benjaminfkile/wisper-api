namespace Wisper.Api.Persistence.BillingIncidents;

/// <summary>
/// Data access for the persistent platform-policy fallback signal (task #210, docs/PAYMENTS.md §4,
/// docs/DATA_MODEL.md §11). The metering flush inserts one <c>billing_incidents</c> row per
/// observed fallback via <see cref="RecordAsync"/>; the admin overview reads the post-ack aggregate
/// via <see cref="GetAggregateAsync"/>; the admin ack endpoint writes the watermark via
/// <see cref="AckAsync"/>. Persistence is what makes the signal survive restarts and be visible from
/// every instance -- the previous process-local <c>PolicyFallbackMonitor</c> reset on every boot and
/// only the instance that metered the offending lease knew about the fallback.
///
/// Two implementations back it: the Dapper repository over Postgres and an in-memory double for
/// unit tests + the DB-less dev mode. History is append-only; an ack shifts the watermark but never
/// deletes rows.
/// </summary>
public interface IPolicyFallbackStore : IRepository
{
    /// <summary>
    /// Appends one fallback event to <c>billing_incidents</c>. <paramref name="leaseId"/> is the
    /// lease the flush was billing (or <c>null</c> if the caller can't provide one);
    /// <paramref name="policyId"/> is the row the flush fell back to on the stale branch, and
    /// <c>null</c> on the missing-at-flush branch where no row existed at all.
    /// </summary>
    Task RecordAsync(
        PolicyFallbackKind kind,
        Guid? leaseId,
        Guid? policyId,
        DateTimeOffset occurredAt,
        CancellationToken ct = default);

    /// <summary>
    /// The post-ack aggregate that <c>GET /v1/admin/overview</c> surfaces (task #210): the count of
    /// fallbacks recorded after the last <see cref="AckAsync"/>, the timestamp of the newest such
    /// row, and the policy id it referenced (<c>null</c> on a missing-at-flush event). When nothing
    /// has been recorded since the last ack, the aggregate is empty (count 0, both nullable fields
    /// <c>null</c>). The ack watermark itself rides on the aggregate so callers can display it.
    /// </summary>
    Task<PolicyFallbackAggregate> GetAggregateAsync(CancellationToken ct = default);

    /// <summary>
    /// Sets the ack watermark to <paramref name="ackAt"/>. The next call to
    /// <see cref="GetAggregateAsync"/> returns an empty aggregate until a fresh fallback is
    /// recorded with <c>occurred_at &gt; ackAt</c>. History (the <c>billing_incidents</c> rows) is
    /// left intact. Returns the previously observed aggregate at the moment of the ack, so the
    /// caller can record it on an audit row.
    /// </summary>
    Task<PolicyFallbackAggregate> AckAsync(DateTimeOffset ackAt, CancellationToken ct = default);
}

/// <summary>
/// Post-ack aggregate of the platform-policy fallback signal (task #210). Read by
/// <c>GET /v1/admin/overview</c>: <see cref="Count"/> is the number of fallbacks since the last
/// <see cref="AckAt"/> (or forever when no ack yet), <see cref="LastAt"/> is the newest such
/// event's timestamp, <see cref="LastPolicyId"/> is that event's policy id (<c>null</c> on the
/// missing-at-flush branch), and <see cref="AckAt"/> is the last ack watermark surfaced for
/// display. When no ack has ever run <see cref="AckAt"/> is <c>null</c>.
/// </summary>
public sealed record PolicyFallbackAggregate(
    long Count,
    DateTimeOffset? LastAt,
    Guid? LastPolicyId,
    DateTimeOffset? AckAt)
{
    /// <summary>Empty aggregate: no fallbacks recorded post-ack. Also the shape a fresh boot sees.</summary>
    public static PolicyFallbackAggregate Empty { get; } = new(0, null, null, null);
}
