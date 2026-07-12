using Wisper.Api.Domain;
using Wisper.Api.Persistence;

namespace Wisper.Api.Ledger;

/// <summary>
/// The result of posting a transaction — the committed <see cref="LedgerTransaction"/>, its persisted
/// <see cref="LedgerEntry"/> rows, and whether the post was a de-duplicated replay of an existing
/// idempotency key (in which case nothing new was written).
/// </summary>
public sealed record PostedTransaction(
    LedgerTransaction Transaction,
    IReadOnlyList<LedgerEntry> Entries,
    bool WasDeduplicated);

/// <summary>
/// Persistence + atomic-commit seam for the double-entry ledger (docs/DATA_MODEL.md §7). Two
/// implementations back it: a Dapper + explicit-SQL store over Postgres (whose triggers are the
/// defense-in-depth backstop) and an in-memory store that enforces the same invariants in C# for the
/// unit suite (Grunt has no Postgres). <see cref="PostAsync"/> is the atomic money movement — it dedupes
/// on the idempotency key, maintains account balances by each account's normal side, and enforces the
/// non-negative guard on the earmarked liabilities (§7c, §7d) as one all-or-nothing operation.
/// </summary>
public interface ILedgerStore : IRepository
{
    /// <summary>
    /// Gets the singleton account for <paramref name="kind"/>/<paramref name="ownerUserId"/> in
    /// <paramref name="currency"/>, creating it if absent (docs/DATA_MODEL.md §3, §8) — the lazy,
    /// unique-pinned wallet/earnings/platform accounts. Concurrent callers converge on the same row.
    /// </summary>
    Task<LedgerAccount> GetOrCreateAccountAsync(
        LedgerAccountKind kind, Guid? ownerUserId, string currency = "usd", CancellationToken ct = default);

    /// <summary>Gets an account by id, or <c>null</c> if none.</summary>
    Task<LedgerAccount?> GetAccountAsync(Guid id, CancellationToken ct = default);

    /// <summary>A snapshot of every ledger account (used by the reconciler, §7e).</summary>
    Task<IReadOnlyList<LedgerAccount>> ListAccountsAsync(CancellationToken ct = default);

    /// <summary>The posted transaction carrying <paramref name="idempotencyKey"/>, or <c>null</c> if none.</summary>
    Task<LedgerTransaction?> FindTransactionByIdempotencyKeyAsync(
        string idempotencyKey, CancellationToken ct = default);

    /// <summary>Every entry posted against <paramref name="accountId"/> (used by the reconciler, §7e).</summary>
    Task<IReadOnlyList<LedgerEntry>> ListEntriesForAccountAsync(Guid accountId, CancellationToken ct = default);

    /// <summary>
    /// Atomically posts <paramref name="draft"/>: if its idempotency key is already posted, returns that
    /// transaction unchanged (<see cref="PostedTransaction.WasDeduplicated"/> = <c>true</c>); otherwise
    /// appends the transaction and its entries, maintaining balances and enforcing the non-negative guard
    /// (docs/DATA_MODEL.md §7c, §7d). Throws <see cref="LedgerException"/> on a guard violation and rolls
    /// back — nothing is persisted. Callers should validate the draft's shape/balance first
    /// (<see cref="LedgerInvariants.ValidateDraft"/>); the store re-checks as defense-in-depth.
    /// </summary>
    Task<PostedTransaction> PostAsync(TransactionDraft draft, CancellationToken ct = default);
}
