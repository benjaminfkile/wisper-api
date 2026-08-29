namespace Wisper.Api.Persistence.BillingIncidents;

/// <summary>
/// In-memory <see cref="IPolicyFallbackStore"/> double (task #210). Backs the unit suite and the
/// DB-less dev mode: semantics mirror the SQL side -- <see cref="RecordAsync"/> appends to an
/// in-memory list, <see cref="GetAggregateAsync"/> aggregates over rows after the ack watermark,
/// and <see cref="AckAsync"/> shifts the watermark without deleting rows. State lives for the
/// process lifetime only and resets on restart, matching every other in-memory repository double.
/// </summary>
public sealed class InMemoryPolicyFallbackStore : IPolicyFallbackStore
{
    private readonly object _gate = new();
    private readonly List<Incident> _incidents = new();
    private DateTimeOffset? _ackAt;

    public Task RecordAsync(
        PolicyFallbackKind kind,
        Guid? leaseId,
        Guid? policyId,
        DateTimeOffset occurredAt,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            _incidents.Add(new Incident(kind, leaseId, policyId, occurredAt));
        }
        return Task.CompletedTask;
    }

    public Task<PolicyFallbackAggregate> GetAggregateAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(SnapshotUnlocked());
        }
    }

    public Task<PolicyFallbackAggregate> AckAsync(DateTimeOffset ackAt, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var previous = SnapshotUnlocked();
            _ackAt = ackAt;
            return Task.FromResult(previous);
        }
    }

    private PolicyFallbackAggregate SnapshotUnlocked()
    {
        var after = _ackAt;
        var relevant = _incidents.Where(i => after is null || i.OccurredAt > after).ToList();
        if (relevant.Count == 0)
        {
            return new PolicyFallbackAggregate(0, null, null, _ackAt);
        }

        var newest = relevant.OrderByDescending(i => i.OccurredAt).First();
        return new PolicyFallbackAggregate(relevant.Count, newest.OccurredAt, newest.PolicyId, _ackAt);
    }

    private sealed record Incident(
        PolicyFallbackKind Kind,
        Guid? LeaseId,
        Guid? PolicyId,
        DateTimeOffset OccurredAt);
}
