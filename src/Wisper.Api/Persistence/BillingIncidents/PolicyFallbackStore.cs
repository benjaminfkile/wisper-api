using Dapper;

namespace Wisper.Api.Persistence.BillingIncidents;

/// <summary>
/// Dapper + explicit-SQL <see cref="IPolicyFallbackStore"/> over Postgres (task #210, migration
/// <c>0018_BillingIncidents</c>). Two rows per invocation at most: a <c>billing_incidents</c> insert
/// on record, an aggregate over the same table on read, and a single-row update on
/// <c>operational_state</c> on ack. Not exercised by the unit suite (Grunt has no Postgres); the
/// in-memory double is.
/// </summary>
public sealed class PolicyFallbackStore : RepositoryBase, IPolicyFallbackStore
{
    public PolicyFallbackStore(Db db) : base(db)
    {
    }

    public async Task RecordAsync(
        PolicyFallbackKind kind,
        Guid? leaseId,
        Guid? policyId,
        DateTimeOffset occurredAt,
        CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO billing_incidents (kind, lease_id, policy_id, occurred_at)
            VALUES (@Kind, @LeaseId, @PolicyId, @OccurredAt)
            """;

        await using var conn = await OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            Kind = PolicyFallbackKindLabels.ToLabel(kind),
            LeaseId = leaseId,
            PolicyId = policyId,
            OccurredAt = occurredAt,
        }, cancellationToken: ct));
    }

    public async Task<PolicyFallbackAggregate> GetAggregateAsync(CancellationToken ct = default)
    {
        // Two round-trips: (1) read the ack watermark from operational_state (single row, id = 1);
        // (2) aggregate billing_incidents over rows after the watermark. The '-infinity' fallback
        // means "no ack yet, count every row"; COALESCE keeps the WHERE clause a single expression.
        const string ackSql = "SELECT policy_fallback_ack_at FROM operational_state WHERE id = 1";
        const string aggSql = """
            SELECT COUNT(*)                                              AS count,
                   MAX(occurred_at)                                      AS last_at,
                   (
                       SELECT policy_id
                         FROM billing_incidents
                        WHERE kind IN ('policy_stale_fallback', 'policy_missing_at_flush')
                          AND occurred_at > COALESCE(@AckAt, 'epoch'::timestamptz)
                        ORDER BY occurred_at DESC
                        LIMIT 1
                   )                                                     AS last_policy_id
              FROM billing_incidents
             WHERE kind IN ('policy_stale_fallback', 'policy_missing_at_flush')
               AND occurred_at > COALESCE(@AckAt, 'epoch'::timestamptz)
            """;

        await using var conn = await OpenConnectionAsync(ct);
        var ackAt = await conn.QuerySingleOrDefaultAsync<DateTimeOffset?>(
            new CommandDefinition(ackSql, cancellationToken: ct));
        var row = await conn.QuerySingleAsync<AggregateRow>(new CommandDefinition(
            aggSql, new { AckAt = ackAt }, cancellationToken: ct));

        return new PolicyFallbackAggregate(row.Count, row.LastAt, row.LastPolicyId, ackAt);
    }

    public async Task<PolicyFallbackAggregate> AckAsync(DateTimeOffset ackAt, CancellationToken ct = default)
    {
        var previous = await GetAggregateAsync(ct);

        const string sql = """
            UPDATE operational_state
               SET policy_fallback_ack_at = @AckAt
             WHERE id = 1
            """;

        await using var conn = await OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new { AckAt = ackAt }, cancellationToken: ct));

        return previous;
    }

    /// <summary>Dapper projection of the aggregate query row.</summary>
    private sealed class AggregateRow
    {
        public long Count { get; init; }
        public DateTimeOffset? LastAt { get; init; }
        public Guid? LastPolicyId { get; init; }
    }
}
