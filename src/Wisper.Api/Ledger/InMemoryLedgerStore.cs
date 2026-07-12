using Wisper.Api.Domain;

namespace Wisper.Api.Ledger;

/// <summary>
/// In-memory <see cref="ILedgerStore"/> that enforces the double-entry invariants in C# (docs/DATA_MODEL.md
/// §7) so the money logic is unit-testable without Postgres (Grunt has no database). It is the faithful
/// mirror of the SQL side: <see cref="PostAsync"/> is atomic under a single lock (mirroring "one DB
/// transaction", §14), dedupes on the idempotency key (the <c>ledger_transactions.idempotency_key</c>
/// unique constraint), maintains each account's <see cref="LedgerAccount.BalanceCents"/> by its normal
/// side (§7c), and enforces the non-negative guard on the earmarked liabilities (§7d) — with the one
/// documented exception that a <c>chargeback</c> may drive a wallet negative (docs/PAYMENTS.md §7).
/// </summary>
public sealed class InMemoryLedgerStore : ILedgerStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, LedgerAccount> _accounts = new();
    private readonly Dictionary<Guid, LedgerTransaction> _transactions = new();
    private readonly Dictionary<string, Guid> _byIdempotencyKey = new(StringComparer.Ordinal);
    private readonly List<LedgerEntry> _entries = new();
    private long _nextEntryId = 1;

    public Task<LedgerAccount> GetOrCreateAccountAsync(
        LedgerAccountKind kind, Guid? ownerUserId, string currency = "usd", CancellationToken ct = default)
    {
        lock (_gate)
        {
            // Pinned by (kind, owner_user_id) — the wallet/earnings/platform singletons (docs/DATA_MODEL.md §8).
            var existing = _accounts.Values.FirstOrDefault(a => a.Kind == kind && a.OwnerUserId == ownerUserId);
            if (existing is not null)
            {
                return Task.FromResult(existing);
            }

            var account = new LedgerAccount
            {
                Id = Guid.NewGuid(),
                Kind = kind,
                OwnerUserId = ownerUserId,
                Currency = currency,
                BalanceCents = 0,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            _accounts[account.Id] = account;
            return Task.FromResult(account);
        }
    }

    public Task<LedgerAccount?> GetAccountAsync(Guid id, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_accounts.TryGetValue(id, out var account) ? account : null);
        }
    }

    public Task<IReadOnlyList<LedgerAccount>> ListAccountsAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<LedgerAccount>>(_accounts.Values.ToList());
        }
    }

    public Task<IReadOnlyList<LedgerAccount>> ListAccountsByKindAsync(
        LedgerAccountKind kind, string currency = "usd", CancellationToken ct = default)
    {
        lock (_gate)
        {
            var list = _accounts.Values
                .Where(a => a.Kind == kind && a.Currency == currency)
                .ToList();
            return Task.FromResult<IReadOnlyList<LedgerAccount>>(list);
        }
    }

    public Task<LedgerTransaction?> FindTransactionByIdempotencyKeyAsync(
        string idempotencyKey, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(
                _byIdempotencyKey.TryGetValue(idempotencyKey, out var id) ? _transactions[id] : null);
        }
    }

    public Task<IReadOnlyList<LedgerEntry>> ListEntriesForAccountAsync(Guid accountId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<LedgerEntry>>(
                _entries.Where(e => e.AccountId == accountId).OrderBy(e => e.Id).ToList());
        }
    }

    public Task<IReadOnlyList<AccountTransaction>> ListAccountTransactionsAsync(
        Guid accountId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_accounts.TryGetValue(accountId, out var account))
            {
                return Task.FromResult<IReadOnlyList<AccountTransaction>>(Array.Empty<AccountTransaction>());
            }

            // Sum each transaction's signed delta against this account (by its normal side, §7c).
            var byTxn = new Dictionary<Guid, long>();
            foreach (var entry in _entries.Where(e => e.AccountId == accountId))
            {
                var delta = LedgerAccountKinds.SignedDelta(account.Kind, entry.DebitCents, entry.CreditCents);
                byTxn[entry.TransactionId] = byTxn.GetValueOrDefault(entry.TransactionId) + delta;
            }

            var list = byTxn
                .Select(kv => new AccountTransaction(_transactions[kv.Key], kv.Value))
                .OrderByDescending(t => t.Transaction.CreatedAt)
                .ThenByDescending(t => t.Transaction.Id)
                .ToList();
            return Task.FromResult<IReadOnlyList<AccountTransaction>>(list);
        }
    }

    public Task<PostedTransaction> PostAsync(TransactionDraft draft, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        lock (_gate)
        {
            // Idempotent replay — a duplicate webhook/retry returns the existing txn, posts nothing (§8).
            if (draft.IdempotencyKey is { } key && _byIdempotencyKey.TryGetValue(key, out var postedId))
            {
                var existingTxn = _transactions[postedId];
                var existingEntries = _entries.Where(e => e.TransactionId == postedId).OrderBy(e => e.Id).ToList();
                return Task.FromResult(
                    new PostedTransaction(existingTxn, existingEntries, WasDeduplicated: true));
            }

            // Defense-in-depth: re-check shape/balance even though the service validated (mirrors the SQL
            // entry checks + deferred balanced trigger running independently of app code).
            LedgerInvariants.ValidateDraft(draft);

            // Net signed change per account, judged atomically (mirrors "one DB transaction", §14).
            var deltas = new Dictionary<Guid, long>();
            foreach (var entry in draft.Entries)
            {
                if (!_accounts.TryGetValue(entry.AccountId, out var account))
                {
                    throw new LedgerException(LedgerViolation.InvalidTransaction,
                        $"ledger account {entry.AccountId} does not exist");
                }

                var delta = LedgerAccountKinds.SignedDelta(account.Kind, entry.DebitCents, entry.CreditCents);
                deltas[entry.AccountId] = deltas.GetValueOrDefault(entry.AccountId) + delta;
            }

            // Non-negative guard on the earmarked liabilities (§7d) before anything is written.
            foreach (var (accountId, delta) in deltas)
            {
                var account = _accounts[accountId];
                var newBalance = account.BalanceCents + delta;
                if (newBalance >= 0 || !LedgerAccountKinds.IsEarmarkedLiability(account.Kind))
                {
                    continue;
                }

                if (account.Kind == LedgerAccountKind.LeaseHolds)
                {
                    throw new LedgerException(LedgerViolation.HoldOverdrawn,
                        $"lease_holds account {accountId} would be over-drawn: {newBalance}");
                }

                // user_wallet: a chargeback is the one documented case a wallet may go negative (a debt).
                if (draft.Kind != LedgerTxnKind.Chargeback)
                {
                    throw new LedgerException(LedgerViolation.InsufficientFunds,
                        $"user_wallet account {accountId} has insufficient funds: {newBalance}");
                }
            }

            // Commit: append the transaction + its entries and roll the maintained balances forward.
            var now = DateTimeOffset.UtcNow;
            var transaction = new LedgerTransaction
            {
                Id = Guid.NewGuid(),
                Kind = draft.Kind,
                LeaseId = draft.LeaseId,
                ExternalRef = draft.ExternalRef,
                IdempotencyKey = draft.IdempotencyKey,
                Memo = draft.Memo,
                CreatedAt = now,
            };
            _transactions[transaction.Id] = transaction;
            if (transaction.IdempotencyKey is { } committedKey)
            {
                _byIdempotencyKey[committedKey] = transaction.Id;
            }

            var entries = new List<LedgerEntry>(draft.Entries.Count);
            foreach (var entry in draft.Entries)
            {
                var stored = new LedgerEntry
                {
                    Id = _nextEntryId++,
                    TransactionId = transaction.Id,
                    AccountId = entry.AccountId,
                    DebitCents = entry.DebitCents,
                    CreditCents = entry.CreditCents,
                    LeaseId = entry.LeaseId,
                    CreatedAt = now,
                };
                _entries.Add(stored);
                entries.Add(stored);
            }

            foreach (var (accountId, delta) in deltas)
            {
                var account = _accounts[accountId];
                _accounts[accountId] = account with { BalanceCents = account.BalanceCents + delta };
            }

            return Task.FromResult(new PostedTransaction(transaction, entries, WasDeduplicated: false));
        }
    }
}
