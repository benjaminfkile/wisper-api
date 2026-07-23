using Dapper;
using Wisper.Api.Domain;

namespace Wisper.Api.Persistence.Policy;

/// <summary>
/// Dapper + explicit-SQL <see cref="IPlatformPolicyRepository"/> over Postgres (docs/DATA_MODEL.md §11).
/// All-scalar columns map by name; the active-row lookup is a <c>ORDER BY effective_from DESC LIMIT 1</c>
/// over the versions that have taken effect. Not exercised by the unit suite (Grunt has no Postgres); the
/// in-memory double is.
/// </summary>
public sealed class PlatformPolicyRepository : RepositoryBase, IPlatformPolicyRepository
{
    private const string SelectColumns =
        "id, fee_bps, min_topup_cents, max_concurrent_leases_per_user, max_ttl_seconds_cap, " +
        "min_isolation, first_topup_max_cents, new_account_window_hours, new_account_max_topup_cents_per_day, " +
        "max_spend_cents_per_day, effective_from, created_by";

    public PlatformPolicyRepository(Db db) : base(db)
    {
    }

    public async Task<PlatformPolicy> AppendAsync(PlatformPolicy policy, CancellationToken ct = default)
    {
        const string sql = $"""
            INSERT INTO platform_policy (id, fee_bps, min_topup_cents, max_concurrent_leases_per_user,
                                         max_ttl_seconds_cap, min_isolation, first_topup_max_cents,
                                         new_account_window_hours, new_account_max_topup_cents_per_day,
                                         max_spend_cents_per_day, effective_from, created_by)
            VALUES (COALESCE(@Id, gen_random_uuid()), @FeeBps, @MinTopupCents, @MaxConcurrentLeasesPerUser,
                    @MaxTtlSecondsCap, @MinIsolation, @FirstTopupMaxCents, @NewAccountWindowHours,
                    @NewAccountMaxTopupCentsPerDay, @MaxSpendCentsPerDay,
                    COALESCE(@EffectiveFrom, now()), @CreatedBy)
            RETURNING {SelectColumns}
            """;

        var parameters = new
        {
            Id = policy.Id == Guid.Empty ? (Guid?)null : policy.Id,
            policy.FeeBps,
            policy.MinTopupCents,
            policy.MaxConcurrentLeasesPerUser,
            policy.MaxTtlSecondsCap,
            policy.MinIsolation,
            policy.FirstTopupMaxCents,
            policy.NewAccountWindowHours,
            policy.NewAccountMaxTopupCentsPerDay,
            policy.MaxSpendCentsPerDay,
            EffectiveFrom = policy.EffectiveFrom == default ? (DateTimeOffset?)null : policy.EffectiveFrom,
            policy.CreatedBy,
        };

        await using var conn = await OpenConnectionAsync(ct);
        return await conn.QuerySingleAsync<PlatformPolicy>(new CommandDefinition(sql, parameters, cancellationToken: ct));
    }

    public async Task<PlatformPolicy?> GetActiveAsync(DateTimeOffset asOf, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<PlatformPolicy>(new CommandDefinition(
            $"SELECT {SelectColumns} FROM platform_policy WHERE effective_from <= @asOf " +
            "ORDER BY effective_from DESC LIMIT 1", new { asOf }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<PlatformPolicy>> ListAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<PlatformPolicy>(new CommandDefinition(
            $"SELECT {SelectColumns} FROM platform_policy ORDER BY effective_from DESC", cancellationToken: ct));
        return rows.ToList();
    }
}
