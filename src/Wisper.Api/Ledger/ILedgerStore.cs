using Wisper.Api.Domain;
using Wisper.Api.Persistence;

namespace Wisper.Api.Ledger;

/// <summary>
/// The result of posting a transaction -- the committed <see cref="LedgerTransaction"/>, its persisted
/// <see cref="LedgerEntry"/> rows, and whether the post was a de-duplicated replay of an existing
/// idempotency key (in which case nothing new was written).
/// </summary>
public sealed record PostedTransaction(
    LedgerTransaction Transaction,
    IReadOnlyList<LedgerEntry> Entries,
    bool WasDeduplicated);

/// <summary>
/// One transaction as seen from a single account -- the transaction header plus the <b>signed</b> change it
/// made to that account's balance (by the account's normal side, §7c). This is the row shape behind the
/// caller's ledger view (docs/API.md §5, <c>GET /v1/billing/transactions</c>): for a consumer's
/// <c>user_wallet</c>, a <c>topup</c> shows a positive amount, a <c>lease_hold</c>/<c>refund</c> a negative
/// one. Newest-first order is the store's responsibility.
/// </summary>
public sealed record AccountTransaction(LedgerTransaction Transaction, long SignedAmountCents);

/// <summary>
/// Persistence + atomic-commit seam for the double-entry ledger (docs/DATA_MODEL.md §7). Two
/// implementations back it: a Dapper + explicit-SQL store over Postgres (whose triggers are the
/// defense-in-depth backstop) and an in-memory store that enforces the same invariants in C# for the
/// unit suite (Grunt has no Postgres). <see cref="PostAsync"/> is the atomic money movement -- it dedupes
/// on the idempotency key, maintains account balances by each account's normal side, and enforces the
/// non-negative guard on the earmarked liabilities (§7c, §7d) as one all-or-nothing operation.
/// </summary>
public interface ILedgerStore : IRepository
{
    /// <summary>
    /// Gets the singleton account for <paramref name="kind"/>/<paramref name="ownerUserId"/> in
    /// <paramref name="currency"/>, creating it if absent (docs/DATA_MODEL.md §3, §8) -- the lazy,
    /// unique-pinned wallet/earnings/platform accounts. Concurrent callers converge on the same row.
    /// </summary>
    Task<LedgerAccount> GetOrCreateAccountAsync(
        LedgerAccountKind kind, Guid? ownerUserId, string currency = "usd", CancellationToken ct = default);

    /// <summary>Gets an account by id, or <c>null</c> if none.</summary>
    Task<LedgerAccount?> GetAccountAsync(Guid id, CancellationToken ct = default);

    /// <summary>A snapshot of every ledger account (used by the reconciler, §7e).</summary>
    Task<IReadOnlyList<LedgerAccount>> ListAccountsAsync(CancellationToken ct = default);

    /// <summary>
    /// Every ledger account of <paramref name="kind"/> in <paramref name="currency"/> (docs/DATA_MODEL.md §7).
    /// The payout run enumerates the <c>host_earnings</c> singletons this way to find hosts whose accrued
    /// balance clears the payout minimum (docs/PAYMENTS.md §6).
    /// </summary>
    Task<IReadOnlyList<LedgerAccount>> ListAccountsByKindAsync(
        LedgerAccountKind kind, string currency = "usd", CancellationToken ct = default);

    /// <summary>
    /// A page of ledger accounts (docs/API.md §8, <c>GET /v1/admin/ledger/accounts</c>), narrowed by
    /// optional <paramref name="kind"/> and <paramref name="ownerUserId"/> filters (both null lists every
    /// account in <paramref name="currency"/>), ordered by <c>created_at</c> then id ascending so paging
    /// is stable, and paged with <paramref name="limit"/>/<paramref name="offset"/>. The admin listing
    /// uses this so an operator can find the platform singletons (owner NULL) or a user's wallet without
    /// SQL: the two account ids <c>POST /v1/admin/adjustments</c> needs.
    /// </summary>
    Task<IReadOnlyList<LedgerAccount>> SearchAccountsAsync(
        LedgerAccountKind? kind,
        Guid? ownerUserId,
        string currency,
        int limit,
        int offset,
        CancellationToken ct = default);

    /// <summary>The posted transaction carrying <paramref name="idempotencyKey"/>, or <c>null</c> if none.</summary>
    Task<LedgerTransaction?> FindTransactionByIdempotencyKeyAsync(
        string idempotencyKey, CancellationToken ct = default);

    /// <summary>Every entry posted against <paramref name="accountId"/> (used by the reconciler, §7e).</summary>
    Task<IReadOnlyList<LedgerEntry>> ListEntriesForAccountAsync(Guid accountId, CancellationToken ct = default);

    /// <summary>
    /// The transactions that touch <paramref name="accountId"/>, each with the signed net change it made to
    /// that account (by its normal side), newest-first (<c>created_at</c> then id, descending) -- the account
    /// statement behind the caller's ledger view (docs/API.md §5, §10). Returns an empty list when the
    /// account does not exist or has no entries.
    /// </summary>
    Task<IReadOnlyList<AccountTransaction>> ListAccountTransactionsAsync(
        Guid accountId, CancellationToken ct = default);

    /// <summary>
    /// Atomically posts <paramref name="draft"/>: if its idempotency key is already posted, returns that
    /// transaction unchanged (<see cref="PostedTransaction.WasDeduplicated"/> = <c>true</c>); otherwise
    /// appends the transaction and its entries, maintaining balances and enforcing the non-negative guard
    /// (docs/DATA_MODEL.md §7c, §7d). Throws <see cref="LedgerException"/> on a guard violation and rolls
    /// back -- nothing is persisted. Callers should validate the draft's shape/balance first
    /// (<see cref="LedgerInvariants.ValidateDraft"/>); the store re-checks as defense-in-depth.
    /// </summary>
    Task<PostedTransaction> PostAsync(TransactionDraft draft, CancellationToken ct = default);
}
