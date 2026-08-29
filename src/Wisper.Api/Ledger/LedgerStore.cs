using Dapper;
using Npgsql;
using Wisper.Api.Domain;
using Wisper.Api.Persistence;

namespace Wisper.Api.Ledger;

/// <summary>
/// Dapper + explicit-SQL <see cref="ILedgerStore"/> over Postgres (docs/DATA_MODEL.md §7). A money
/// movement is one DB transaction that inserts the <c>ledger_transaction</c> and all its balanced
/// <c>ledger_entries</c>; the schema's triggers then maintain balances and enforce the balanced and
/// non-negative invariants atomically (§7a, §7c, §7d) -- this store leans on them as the source of truth
/// and translates their <c>RAISE</c>s into <see cref="LedgerException"/>. The C# <see cref="LedgerService"/>
/// pre-validates shape/balance and the in-memory store mirrors the same rules, so the logic is unit-tested
/// without Postgres; this type is not exercised by the unit suite (Grunt has no database), matching the
/// other Dapper repositories.
/// </summary>
public sealed class LedgerStore : RepositoryBase, ILedgerStore
{
    // The trigger RAISE messages we translate back into typed ledger violations (docs/DATA_MODEL.md §7).
    private const string UnbalancedMarker = "unbalanced ledger txn";
    private const string OverdrawnMarker = "over-drawn";
    private const string InsufficientMarker = "insufficient funds";

    public LedgerStore(Db db) : base(db)
    {
    }

    public async Task<LedgerAccount> GetOrCreateAccountAsync(
        LedgerAccountKind kind, Guid? ownerUserId, string currency = "usd", CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);

        var existing = await FindAccountAsync(conn, kind, ownerUserId, ct);
        if (existing is not null)
        {
            return existing;
        }

        const string insert = $"""
            INSERT INTO ledger_accounts (kind, owner_user_id, currency)
            VALUES (@Kind::ledger_account_kind, @OwnerUserId, @Currency)
            RETURNING {AccountColumns}
            """;
        try
        {
            var row = await conn.QuerySingleAsync<AccountRow>(new CommandDefinition(
                insert,
                new { Kind = PgEnum.ToSnakeLabel(kind), OwnerUserId = ownerUserId, Currency = currency },
                cancellationToken: ct));
            return row.ToEntity();
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // Lost a race to create the singleton -- read the winner back.
            return await FindAccountAsync(conn, kind, ownerUserId, ct)
                ?? throw new LedgerException(LedgerViolation.InvalidTransaction,
                    $"ledger account ({kind}, {ownerUserId}) could not be created or found");
        }
    }

    public async Task<LedgerAccount?> GetAccountAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<AccountRow>(new CommandDefinition(
            $"SELECT {AccountColumns} FROM ledger_accounts WHERE id = @id", new { id }, cancellationToken: ct));
        return row?.ToEntity();
    }

    public async Task<IReadOnlyList<LedgerAccount>> ListAccountsAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<AccountRow>(new CommandDefinition(
            $"SELECT {AccountColumns} FROM ledger_accounts", cancellationToken: ct));
        return rows.Select(r => r.ToEntity()).ToList();
    }

    public async Task<IReadOnlyList<LedgerAccount>> ListAccountsByKindAsync(
        LedgerAccountKind kind, string currency = "usd", CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<AccountRow>(new CommandDefinition(
            $"SELECT {AccountColumns} FROM ledger_accounts " +
            "WHERE kind = @Kind::ledger_account_kind AND currency = @Currency",
            new { Kind = PgEnum.ToSnakeLabel(kind), Currency = currency }, cancellationToken: ct));
        return rows.Select(r => r.ToEntity()).ToList();
    }

    public async Task<IReadOnlyList<LedgerAccount>> SearchAccountsAsync(
        LedgerAccountKind? kind,
        Guid? ownerUserId,
        string currency,
        int limit,
        int offset,
        CancellationToken ct = default)
    {
        // Optional filters coalesce with NULL sentinels; the ORDER BY (created_at, id) matches the
        // in-memory store so paging is stable across implementations (task #194).
        const string sql = $"""
            SELECT {AccountColumns} FROM ledger_accounts
             WHERE currency = @Currency
               AND (@Kind IS NULL OR kind = @Kind::ledger_account_kind)
               AND (@OwnerUserId IS NULL OR owner_user_id = @OwnerUserId)
             ORDER BY created_at, id
             LIMIT @limit OFFSET @offset
            """;

        await using var conn = await OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<AccountRow>(new CommandDefinition(
            sql,
            new
            {
                Currency = currency,
                Kind = kind is { } k ? PgEnum.ToSnakeLabel(k) : null,
                OwnerUserId = ownerUserId,
                limit = Math.Max(0, limit),
                offset = Math.Max(0, offset),
            },
            cancellationToken: ct));
        return rows.Select(r => r.ToEntity()).ToList();
    }

    public async Task<LedgerTransaction?> FindTransactionByIdempotencyKeyAsync(
        string idempotencyKey, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        return (await FindTransactionByKeyAsync(conn, idempotencyKey, ct))?.ToEntity();
    }

    public async Task<IReadOnlyList<LedgerEntry>> ListEntriesForAccountAsync(
        Guid accountId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<EntryRow>(new CommandDefinition(
            $"SELECT {EntryColumns} FROM ledger_entries WHERE account_id = @accountId ORDER BY id",
            new { accountId }, cancellationToken: ct));
        return rows.Select(r => r.ToEntity()).ToList();
    }

    public async Task<IReadOnlyList<AccountTransaction>> ListAccountTransactionsAsync(
        Guid accountId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);

        // The account's normal side decides how a (debit, credit) sum signs into a balance delta (§7c);
        // resolve the kind first, then aggregate its entries per transaction in the DB.
        var accountRow = await conn.QuerySingleOrDefaultAsync<AccountRow>(new CommandDefinition(
            $"SELECT {AccountColumns} FROM ledger_accounts WHERE id = @accountId",
            new { accountId }, cancellationToken: ct));
        if (accountRow is null)
        {
            return Array.Empty<AccountTransaction>();
        }

        var kind = PgEnum.ParseSnake<LedgerAccountKind>(accountRow.Kind);
        const string sql = """
            SELECT t.id, t.kind::text AS kind, t.lease_id, t.external_ref, t.idempotency_key, t.memo,
                   t.created_at,
                   COALESCE(SUM(e.debit_cents), 0) AS debit_cents,
                   COALESCE(SUM(e.credit_cents), 0) AS credit_cents
            FROM ledger_transactions t
            JOIN ledger_entries e ON e.transaction_id = t.id
            WHERE e.account_id = @accountId
            GROUP BY t.id
            ORDER BY t.created_at DESC, t.id DESC
            """;
        var rows = await conn.QueryAsync<TransactionAggRow>(new CommandDefinition(
            sql, new { accountId }, cancellationToken: ct));
        return rows
            .Select(r => new AccountTransaction(
                r.ToEntity(), LedgerAccountKinds.SignedDelta(kind, r.DebitCents, r.CreditCents)))
            .ToList();
    }

    public async Task<PostedTransaction> PostAsync(TransactionDraft draft, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        await using var conn = await OpenConnectionAsync(ct);

        // Idempotent replay -- a duplicate key returns the existing txn, posts nothing (docs/DATA_MODEL.md §8).
        if (draft.IdempotencyKey is { } key)
        {
            var found = await FindTransactionByKeyAsync(conn, key, ct);
            if (found is not null)
            {
                return await LoadPostedAsync(conn, found.ToEntity(), deduplicated: true, ct);
            }
        }

        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            const string insertTxn = """
                INSERT INTO ledger_transactions (kind, lease_id, external_ref, idempotency_key, memo)
                VALUES (@Kind::ledger_txn_kind, @LeaseId, @ExternalRef, @IdempotencyKey, @Memo)
                RETURNING id, kind::text AS kind, lease_id, external_ref, idempotency_key, memo, created_at
                """;
            var txnRow = await conn.QuerySingleAsync<TransactionRow>(new CommandDefinition(
                insertTxn,
                new
                {
                    Kind = PgEnum.ToSnakeLabel(draft.Kind),
                    draft.LeaseId,
                    draft.ExternalRef,
                    draft.IdempotencyKey,
                    draft.Memo,
                },
                transaction: tx,
                cancellationToken: ct));

            var entries = new List<LedgerEntry>(draft.Entries.Count);
            const string insertEntry = $"""
                INSERT INTO ledger_entries (transaction_id, account_id, debit_cents, credit_cents, lease_id)
                VALUES (@TransactionId, @AccountId, @DebitCents, @CreditCents, @LeaseId)
                RETURNING {EntryColumns}
                """;
            foreach (var entry in draft.Entries)
            {
                var entryRow = await conn.QuerySingleAsync<EntryRow>(new CommandDefinition(
                    insertEntry,
                    new
                    {
                        TransactionId = txnRow.Id,
                        entry.AccountId,
                        entry.DebitCents,
                        entry.CreditCents,
                        entry.LeaseId,
                    },
                    transaction: tx,
                    cancellationToken: ct));
                entries.Add(entryRow.ToEntity());
            }

            // The deferred balanced-transaction trigger (§7a) is evaluated here, at commit.
            await tx.CommitAsync(ct);
            return new PostedTransaction(txnRow.ToEntity(), entries, WasDeduplicated: false);
        }
        catch (PostgresException ex)
        {
            await tx.RollbackAsync(ct);

            // A concurrent poster committed the same idempotency key first -- return theirs.
            if (ex.SqlState == PostgresErrorCodes.UniqueViolation && draft.IdempotencyKey is { } racedKey)
            {
                var found = await FindTransactionByKeyAsync(conn, racedKey, ct);
                if (found is not null)
                {
                    return await LoadPostedAsync(conn, found.ToEntity(), deduplicated: true, ct);
                }
            }

            throw Translate(ex);
        }
    }

    /// <summary>Maps a trigger/constraint failure to the typed ledger violation, else rethrows.</summary>
    private static Exception Translate(PostgresException ex)
    {
        var message = ex.MessageText;
        if (message.Contains(UnbalancedMarker, StringComparison.Ordinal))
        {
            return new LedgerException(LedgerViolation.Unbalanced, message);
        }

        if (message.Contains(OverdrawnMarker, StringComparison.Ordinal))
        {
            return new LedgerException(LedgerViolation.HoldOverdrawn, message);
        }

        if (message.Contains(InsufficientMarker, StringComparison.Ordinal))
        {
            return new LedgerException(LedgerViolation.InsufficientFunds, message);
        }

        return ex;
    }

    private static async Task<LedgerAccount?> FindAccountAsync(
        NpgsqlConnection conn, LedgerAccountKind kind, Guid? ownerUserId, CancellationToken ct)
    {
        const string sql = $"""
            SELECT {AccountColumns} FROM ledger_accounts
            WHERE kind = @Kind::ledger_account_kind AND owner_user_id IS NOT DISTINCT FROM @OwnerUserId
            """;
        var row = await conn.QuerySingleOrDefaultAsync<AccountRow>(new CommandDefinition(
            sql, new { Kind = PgEnum.ToSnakeLabel(kind), OwnerUserId = ownerUserId }, cancellationToken: ct));
        return row?.ToEntity();
    }

    private static async Task<TransactionRow?> FindTransactionByKeyAsync(
        NpgsqlConnection conn, string idempotencyKey, CancellationToken ct)
    {
        const string sql = """
            SELECT id, kind::text AS kind, lease_id, external_ref, idempotency_key, memo, created_at
            FROM ledger_transactions WHERE idempotency_key = @idempotencyKey
            """;
        return await conn.QuerySingleOrDefaultAsync<TransactionRow>(new CommandDefinition(
            sql, new { idempotencyKey }, cancellationToken: ct));
    }

    private static async Task<PostedTransaction> LoadPostedAsync(
        NpgsqlConnection conn, LedgerTransaction transaction, bool deduplicated, CancellationToken ct)
    {
        var rows = await conn.QueryAsync<EntryRow>(new CommandDefinition(
            $"SELECT {EntryColumns} FROM ledger_entries WHERE transaction_id = @id ORDER BY id",
            new { id = transaction.Id }, cancellationToken: ct));
        return new PostedTransaction(transaction, rows.Select(r => r.ToEntity()).ToList(), deduplicated);
    }

    private const string AccountColumns =
        "id, kind::text AS kind, owner_user_id, currency, balance_cents, created_at";

    private const string EntryColumns =
        "id, transaction_id, account_id, debit_cents, credit_cents, lease_id, created_at";

    private sealed class AccountRow
    {
        public Guid Id { get; init; }
        public string Kind { get; init; } = string.Empty;
        public Guid? OwnerUserId { get; init; }
        public string Currency { get; init; } = "usd";
        public long BalanceCents { get; init; }
        public DateTimeOffset CreatedAt { get; init; }

        public LedgerAccount ToEntity() => new()
        {
            Id = Id,
            Kind = PgEnum.ParseSnake<LedgerAccountKind>(Kind),
            OwnerUserId = OwnerUserId,
            Currency = Currency,
            BalanceCents = BalanceCents,
            CreatedAt = CreatedAt,
        };
    }

    private sealed class TransactionRow
    {
        public Guid Id { get; init; }
        public string Kind { get; init; } = string.Empty;
        public Guid? LeaseId { get; init; }
        public string? ExternalRef { get; init; }
        public string? IdempotencyKey { get; init; }
        public string? Memo { get; init; }
        public DateTimeOffset CreatedAt { get; init; }

        public LedgerTransaction ToEntity() => new()
        {
            Id = Id,
            Kind = PgEnum.ParseSnake<LedgerTxnKind>(Kind),
            LeaseId = LeaseId,
            ExternalRef = ExternalRef,
            IdempotencyKey = IdempotencyKey,
            Memo = Memo,
            CreatedAt = CreatedAt,
        };
    }

    private sealed class TransactionAggRow
    {
        public Guid Id { get; init; }
        public string Kind { get; init; } = string.Empty;
        public Guid? LeaseId { get; init; }
        public string? ExternalRef { get; init; }
        public string? IdempotencyKey { get; init; }
        public string? Memo { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public long DebitCents { get; init; }
        public long CreditCents { get; init; }

        public LedgerTransaction ToEntity() => new()
        {
            Id = Id,
            Kind = PgEnum.ParseSnake<LedgerTxnKind>(Kind),
            LeaseId = LeaseId,
            ExternalRef = ExternalRef,
            IdempotencyKey = IdempotencyKey,
            Memo = Memo,
            CreatedAt = CreatedAt,
        };
    }

    private sealed class EntryRow
    {
        public long Id { get; init; }
        public Guid TransactionId { get; init; }
        public Guid AccountId { get; init; }
        public long DebitCents { get; init; }
        public long CreditCents { get; init; }
        public Guid? LeaseId { get; init; }
        public DateTimeOffset CreatedAt { get; init; }

        public LedgerEntry ToEntity() => new()
        {
            Id = Id,
            TransactionId = TransactionId,
            AccountId = AccountId,
            DebitCents = DebitCents,
            CreditCents = CreditCents,
            LeaseId = LeaseId,
            CreatedAt = CreatedAt,
        };
    }
}
