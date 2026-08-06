using Dapper;
using Wisper.Api.Domain;

namespace Wisper.Api.Persistence.Leases;

/// <summary>
/// Dapper + explicit-SQL <see cref="ILeaseRepository"/> over Postgres (docs/DATA_MODEL.md §5). The enum
/// columns (<c>network</c>, <c>status</c>, <c>lease_end_reason</c>) are written as text parameters cast
/// to their enum type and read back via <c>::text</c> into a <see cref="Row"/>, then parsed — the
/// multi-word <c>end_reason</c> uses the snake_case pair on <see cref="PgEnum"/> since Dapper's built-in
/// enum parse can't bridge <c>host_disconnect</c> ↔ <c>HostDisconnect</c>. The <c>hold_txn_id</c> FK to
/// <c>ledger_transactions</c> is added in P2.4 (circular dependency). Not exercised by the unit suite
/// (Grunt has no Postgres); the in-memory double is.
/// </summary>
public sealed class LeaseRepository : RepositoryBase, ILeaseRepository
{
    private const string Columns =
        "id, consumer_user_id, host_id, host_image_id, image_ref, network::text AS network, isolation, cpus, " +
        "memory_mb, pids, gpus, ttl_seconds, price_cents_per_min, currency, status::text AS status, " +
        "end_reason::text AS end_reason, wisp_contract_id, hold_txn_id, created_at, started_at, " +
        "last_metered_at, billable_seconds, ended_at";

    public LeaseRepository(Db db) : base(db)
    {
    }

    public async Task<Lease?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM leases WHERE id = @id", new { id }, cancellationToken: ct));
        return row?.ToEntity();
    }

    public async Task<IReadOnlyList<Lease>> ListByConsumerAsync(Guid consumerUserId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM leases WHERE consumer_user_id = @consumerUserId ORDER BY created_at DESC",
            new { consumerUserId }, cancellationToken: ct));
        return rows.Select(r => r.ToEntity()).ToList();
    }

    public async Task<IReadOnlyList<Lease>> ListActiveByHostAsync(Guid hostId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM leases WHERE host_id = @hostId AND status IN ('active', 'suspended') " +
            "ORDER BY created_at DESC", new { hostId }, cancellationToken: ct));
        return rows.Select(r => r.ToEntity()).ToList();
    }

    public async Task<IReadOnlyList<Lease>> ListActiveAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM leases WHERE status = 'active' ORDER BY created_at",
            cancellationToken: ct));
        return rows.Select(r => r.ToEntity()).ToList();
    }

    public async Task<Lease> CreateAsync(Lease lease, CancellationToken ct = default)
    {
        const string sql = $"""
            INSERT INTO leases (id, consumer_user_id, host_id, host_image_id, image_ref, network, isolation,
                                cpus, memory_mb, pids, gpus, ttl_seconds, price_cents_per_min, currency, status,
                                end_reason, wisp_contract_id, hold_txn_id, created_at, started_at,
                                last_metered_at, billable_seconds, ended_at)
            VALUES (COALESCE(@Id, gen_random_uuid()), @ConsumerUserId, @HostId, @HostImageId, @ImageRef,
                    @Network::network_mode, @Isolation, @Cpus, @MemoryMb, @Pids, @Gpus, @TtlSeconds,
                    @PriceCentsPerMin, @Currency, @Status::lease_status, @EndReason::lease_end_reason,
                    @WispContractId, @HoldTxnId, COALESCE(@CreatedAt, now()), @StartedAt, @LastMeteredAt,
                    @BillableSeconds, @EndedAt)
            RETURNING {Columns}
            """;

        await using var conn = await OpenConnectionAsync(ct);
        var row = await conn.QuerySingleAsync<Row>(new CommandDefinition(sql, ToParameters(lease), cancellationToken: ct));
        return row.ToEntity();
    }

    public async Task<Lease> UpdateAsync(Lease lease, CancellationToken ct = default)
    {
        const string sql = $"""
            UPDATE leases
               SET status           = @Status::lease_status,
                   end_reason       = @EndReason::lease_end_reason,
                   wisp_contract_id = @WispContractId,
                   hold_txn_id      = @HoldTxnId,
                   started_at       = @StartedAt,
                   last_metered_at  = @LastMeteredAt,
                   billable_seconds = @BillableSeconds,
                   ended_at         = @EndedAt
             WHERE id = @Id
            RETURNING {Columns}
            """;

        await using var conn = await OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<Row>(
            new CommandDefinition(sql, ToParameters(lease), cancellationToken: ct));
        return row?.ToEntity() ?? throw new InvalidOperationException($"Lease '{lease.Id}' does not exist.");
    }

    public async Task<Lease?> TransitionStateAsync(
        Guid id,
        LeaseStatus status,
        LeaseEndReason? endReason = null,
        DateTimeOffset? startedAt = null,
        DateTimeOffset? lastMeteredAt = null,
        long? billableSeconds = null,
        DateTimeOffset? endedAt = null,
        CancellationToken ct = default)
    {
        const string sql = $"""
            UPDATE leases
               SET status           = @Status::lease_status,
                   end_reason       = COALESCE(@EndReason::lease_end_reason, end_reason),
                   started_at       = COALESCE(@StartedAt, started_at),
                   last_metered_at  = COALESCE(@LastMeteredAt, last_metered_at),
                   billable_seconds = COALESCE(@BillableSeconds, billable_seconds),
                   ended_at         = COALESCE(@EndedAt, ended_at)
             WHERE id = @Id
            RETURNING {Columns}
            """;

        var parameters = new
        {
            Id = id,
            Status = PgEnum.ToLabel(status),
            EndReason = endReason is { } r ? PgEnum.ToSnakeLabel(r) : null,
            StartedAt = startedAt,
            LastMeteredAt = lastMeteredAt,
            BillableSeconds = billableSeconds,
            EndedAt = endedAt,
        };

        await using var conn = await OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(sql, parameters, cancellationToken: ct));
        return row?.ToEntity();
    }

    /// <summary>Projects a <see cref="Lease"/> to SQL parameters; enum values as their DB labels.</summary>
    private static object ToParameters(Lease lease) => new
    {
        Id = lease.Id == Guid.Empty ? (Guid?)null : lease.Id,
        lease.ConsumerUserId,
        lease.HostId,
        lease.HostImageId,
        lease.ImageRef,
        Network = PgEnum.ToLabel(lease.Network),
        lease.Isolation,
        lease.Cpus,
        lease.MemoryMb,
        lease.Pids,
        lease.Gpus,
        lease.TtlSeconds,
        lease.PriceCentsPerMin,
        lease.Currency,
        Status = PgEnum.ToLabel(lease.Status),
        EndReason = lease.EndReason is { } r ? PgEnum.ToSnakeLabel(r) : null,
        lease.WispContractId,
        lease.HoldTxnId,
        CreatedAt = lease.CreatedAt == default ? (DateTimeOffset?)null : lease.CreatedAt,
        lease.StartedAt,
        lease.LastMeteredAt,
        lease.BillableSeconds,
        lease.EndedAt,
    };

    /// <summary>
    /// Dapper projection of a <c>leases</c> row: the enum columns arrive as text (read via <c>::text</c>)
    /// and are parsed to their domain enums in <see cref="ToEntity"/>. Snake_case columns map to these
    /// PascalCase properties via <c>MatchNamesWithUnderscores</c>.
    /// </summary>
    private sealed class Row
    {
        public Guid Id { get; init; }
        public Guid ConsumerUserId { get; init; }
        public Guid HostId { get; init; }
        public Guid HostImageId { get; init; }
        public string ImageRef { get; init; } = string.Empty;
        public string Network { get; init; } = string.Empty;
        public string Isolation { get; init; } = HostIsolation.Shared;
        public decimal? Cpus { get; init; }
        public int? MemoryMb { get; init; }
        public int? Pids { get; init; }
        public int Gpus { get; init; }
        public int TtlSeconds { get; init; }
        public long PriceCentsPerMin { get; init; }
        public string Currency { get; init; } = "usd";
        public string Status { get; init; } = string.Empty;
        public string? EndReason { get; init; }
        public string? WispContractId { get; init; }
        public Guid? HoldTxnId { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset? StartedAt { get; init; }
        public DateTimeOffset? LastMeteredAt { get; init; }
        public long BillableSeconds { get; init; }
        public DateTimeOffset? EndedAt { get; init; }

        public Lease ToEntity() => new()
        {
            Id = Id,
            ConsumerUserId = ConsumerUserId,
            HostId = HostId,
            HostImageId = HostImageId,
            ImageRef = ImageRef,
            Network = PgEnum.Parse<NetworkMode>(Network),
            Isolation = Isolation,
            Cpus = Cpus,
            MemoryMb = MemoryMb,
            Pids = Pids,
            Gpus = Gpus,
            TtlSeconds = TtlSeconds,
            PriceCentsPerMin = PriceCentsPerMin,
            Currency = Currency,
            Status = PgEnum.Parse<LeaseStatus>(Status),
            EndReason = EndReason is null ? null : PgEnum.ParseSnake<LeaseEndReason>(EndReason),
            WispContractId = WispContractId,
            HoldTxnId = HoldTxnId,
            CreatedAt = CreatedAt,
            StartedAt = StartedAt,
            LastMeteredAt = LastMeteredAt,
            BillableSeconds = BillableSeconds,
            EndedAt = EndedAt,
        };
    }
}
