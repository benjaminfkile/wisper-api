using Dapper;
using Wisper.Api.Domain;

namespace Wisper.Api.Persistence.Payouts;

/// <summary>
/// Dapper + explicit-SQL <see cref="IPayoutRepository"/> over Postgres (docs/DATA_MODEL.md §9). The
/// <c>status</c> enum is written as a text parameter cast to <c>payout_status</c> and read back via
/// <c>::text</c> into a <see cref="Row"/>, then parsed (the multi-word <c>in_transit</c> uses the
/// snake_case pair on <see cref="PgEnum"/>). Not exercised by the unit suite (Grunt has no Postgres); the
/// in-memory double is.
/// </summary>
public sealed class PayoutRepository : RepositoryBase, IPayoutRepository
{
    private const string SelectColumns =
        "id, host_user_id, amount_cents, currency, period_start, period_end, status::text AS status, " +
        "stripe_transfer_id, payout_txn_id, error, created_at, updated_at";

    public PayoutRepository(Db db) : base(db)
    {
    }

    public async Task<Payout> CreateAsync(Payout payout, CancellationToken ct = default)
    {
        const string sql = $"""
            INSERT INTO payouts (id, host_user_id, amount_cents, currency, period_start, period_end, status,
                                 stripe_transfer_id, payout_txn_id, error, created_at, updated_at)
            VALUES (COALESCE(@Id, gen_random_uuid()), @HostUserId, @AmountCents, @Currency, @PeriodStart,
                    @PeriodEnd, @Status::payout_status, @StripeTransferId, @PayoutTxnId, @Error,
                    COALESCE(@CreatedAt, now()), COALESCE(@UpdatedAt, now()))
            RETURNING {SelectColumns}
            """;

        await using var conn = await OpenConnectionAsync(ct);
        var row = await conn.QuerySingleAsync<Row>(new CommandDefinition(sql, ToParameters(payout), cancellationToken: ct));
        return row.ToEntity();
    }

    public async Task<Payout?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            $"SELECT {SelectColumns} FROM payouts WHERE id = @id", new { id }, cancellationToken: ct));
        return row?.ToEntity();
    }

    public async Task<Payout> UpdateAsync(Payout payout, CancellationToken ct = default)
    {
        const string sql = $"""
            UPDATE payouts
               SET status             = @Status::payout_status,
                   stripe_transfer_id = @StripeTransferId,
                   payout_txn_id      = @PayoutTxnId,
                   error              = @Error,
                   updated_at         = now()
             WHERE id = @Id
            RETURNING {SelectColumns}
            """;

        await using var conn = await OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(sql, ToParameters(payout), cancellationToken: ct));
        return row?.ToEntity() ?? throw new InvalidOperationException($"Payout '{payout.Id}' does not exist.");
    }

    public async Task<IReadOnlyList<Payout>> ListByHostAsync(Guid hostUserId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<Row>(new CommandDefinition(
            $"SELECT {SelectColumns} FROM payouts WHERE host_user_id = @hostUserId ORDER BY created_at DESC",
            new { hostUserId }, cancellationToken: ct));
        return rows.Select(r => r.ToEntity()).ToList();
    }

    /// <summary>Projects a <see cref="Payout"/> to SQL parameters; the enum value as its DB label.</summary>
    private static object ToParameters(Payout payout) => new
    {
        Id = payout.Id == Guid.Empty ? (Guid?)null : payout.Id,
        payout.HostUserId,
        payout.AmountCents,
        payout.Currency,
        payout.PeriodStart,
        payout.PeriodEnd,
        Status = PgEnum.ToSnakeLabel(payout.Status),
        payout.StripeTransferId,
        payout.PayoutTxnId,
        payout.Error,
        CreatedAt = payout.CreatedAt == default ? (DateTimeOffset?)null : payout.CreatedAt,
        UpdatedAt = payout.UpdatedAt == default ? (DateTimeOffset?)null : payout.UpdatedAt,
    };

    /// <summary>Dapper projection of a <c>payouts</c> row; the enum column arrives as text.</summary>
    private sealed class Row
    {
        public Guid Id { get; init; }
        public Guid HostUserId { get; init; }
        public long AmountCents { get; init; }
        public string Currency { get; init; } = "usd";
        public DateTimeOffset? PeriodStart { get; init; }
        public DateTimeOffset? PeriodEnd { get; init; }
        public string Status { get; init; } = string.Empty;
        public string? StripeTransferId { get; init; }
        public Guid? PayoutTxnId { get; init; }
        public string? Error { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset UpdatedAt { get; init; }

        public Payout ToEntity() => new()
        {
            Id = Id,
            HostUserId = HostUserId,
            AmountCents = AmountCents,
            Currency = Currency,
            PeriodStart = PeriodStart,
            PeriodEnd = PeriodEnd,
            Status = PgEnum.ParseSnake<PayoutStatus>(Status),
            StripeTransferId = StripeTransferId,
            PayoutTxnId = PayoutTxnId,
            Error = Error,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
        };
    }
}
