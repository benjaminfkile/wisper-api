using Wisper.Api.Domain;

namespace Wisper.Api.Ledger;

/// <summary>
/// The money service (docs/DATA_MODEL.md §7, §8) -- the single entry point for posting balanced
/// double-entry transactions. It <b>enforces the ledger invariants in C#</b> so the logic is unit-testable
/// without Postgres (Grunt has no database): every post is validated balanced and well-formed here (§7a),
/// then committed atomically through the <see cref="ILedgerStore"/>, which maintains balances by each
/// account's normal side (§7c) and enforces the non-negative guard on the earmarked liabilities (§7d).
/// The SQL triggers are the equivalent defense-in-depth backstop. The §8 money flows are built with
/// <see cref="LedgerFlows"/> and posted through <see cref="PostAsync"/>; <see cref="ReconcileAsync"/>
/// re-derives balances from the journal (§7e).
/// </summary>
public sealed class LedgerService
{
    private readonly ILedgerStore _store;

    public LedgerService(ILedgerStore store) => _store = store;

    /// <summary>
    /// Validates <paramref name="draft"/> is balanced and well-formed (§7a), then posts it atomically.
    /// A duplicate idempotency key is a no-op replay (returns the existing transaction). Throws
    /// <see cref="LedgerException"/> if unbalanced/malformed, or if the commit would overdraw an
    /// earmarked liability (§7d) -- in which case nothing is persisted.
    /// </summary>
    public async Task<PostedTransaction> PostAsync(TransactionDraft draft, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        LedgerInvariants.ValidateDraft(draft);
        return await _store.PostAsync(draft, ct);
    }

    /// <summary>
    /// Gets (or lazily creates) the singleton ledger account for <paramref name="kind"/> /
    /// <paramref name="ownerUserId"/> (docs/DATA_MODEL.md §3, §8). Platform singletons pass a
    /// <c>null</c> owner.
    /// </summary>
    public Task<LedgerAccount> GetOrCreateAccountAsync(
        LedgerAccountKind kind, Guid? ownerUserId, string currency = "usd", CancellationToken ct = default) =>
        _store.GetOrCreateAccountAsync(kind, ownerUserId, currency, ct);

    /// <summary>
    /// Gets a ledger account by id, or <c>null</c> if none (docs/DATA_MODEL.md §7) -- the read behind the
    /// admin forensics view (<c>GET /v1/admin/ledger/accounts/:id</c>) and the adjustment account checks.
    /// </summary>
    public Task<LedgerAccount?> GetAccountAsync(Guid accountId, CancellationToken ct = default) =>
        _store.GetAccountAsync(accountId, ct);

    /// <summary>
    /// Every entry posted against <paramref name="accountId"/>, the immutable journal behind the admin
    /// forensics view (docs/API.md §8, docs/DATA_MODEL.md §7). Empty when the account has no postings.
    /// </summary>
    public Task<IReadOnlyList<LedgerEntry>> ListEntriesForAccountAsync(
        Guid accountId, CancellationToken ct = default) =>
        _store.ListEntriesForAccountAsync(accountId, ct);

    /// <summary>
    /// The transactions that touch <paramref name="accountId"/>, each with the signed net change it made to
    /// that account, newest-first (docs/DATA_MODEL.md §7) -- the statement view for admin forensics.
    /// </summary>
    public Task<IReadOnlyList<AccountTransaction>> ListAccountTransactionsAsync(
        Guid accountId, CancellationToken ct = default) =>
        _store.ListAccountTransactionsAsync(accountId, ct);

    /// <summary>The maintained balance of an account. Throws if the account does not exist.</summary>
    public async Task<long> GetBalanceAsync(Guid accountId, CancellationToken ct = default)
    {
        var account = await _store.GetAccountAsync(accountId, ct)
            ?? throw new LedgerException(LedgerViolation.InvalidTransaction,
                $"ledger account {accountId} does not exist");
        return account.BalanceCents;
    }

    /// <summary>
    /// The consumer's spendable wallet balance in cents (docs/DATA_MODEL.md §7, §8) -- the maintained
    /// balance of their singleton <c>user_wallet</c> account, get-or-created so a never-funded wallet
    /// reads a clean <c>0</c>. Backs <c>GET /v1/billing</c>.
    /// </summary>
    public async Task<long> GetWalletBalanceCentsAsync(
        Guid userId, string currency = "usd", CancellationToken ct = default)
    {
        var wallet = await _store.GetOrCreateAccountAsync(LedgerAccountKind.UserWallet, userId, currency, ct);
        return wallet.BalanceCents;
    }

    /// <summary>
    /// A host's accrued, not-yet-paid earnings in cents (docs/DATA_MODEL.md §7, docs/PAYMENTS.md §6) -- the
    /// maintained balance of their singleton <c>host_earnings</c> account, get-or-created so a host who has
    /// never earned reads a clean <c>0</c>. This is what a payout drains (<c>host_earnings → platform_cash</c>).
    /// Backs <c>GET /v1/earnings</c> and the payout guard.
    /// </summary>
    public async Task<long> GetHostEarningsCentsAsync(
        Guid hostUserId, string currency = "usd", CancellationToken ct = default)
    {
        var earnings = await _store.GetOrCreateAccountAsync(
            LedgerAccountKind.HostEarnings, hostUserId, currency, ct);
        return earnings.BalanceCents;
    }

    /// <summary>
    /// Every <c>host_earnings</c> account in <paramref name="currency"/> (docs/PAYMENTS.md §6) -- the payout run
    /// walks these to find hosts whose accrued balance clears the payout minimum. Each account's
    /// <see cref="LedgerAccount.OwnerUserId"/> is the host being paid and its
    /// <see cref="LedgerAccount.BalanceCents"/> the amount waiting.
    /// </summary>
    public Task<IReadOnlyList<LedgerAccount>> ListHostEarningsAccountsAsync(
        string currency = "usd", CancellationToken ct = default) =>
        _store.ListAccountsByKindAsync(LedgerAccountKind.HostEarnings, currency, ct);

    /// <summary>
    /// Every ledger account of <paramref name="kind"/> in <paramref name="currency"/> (docs/DATA_MODEL.md §7).
    /// The admin overview sums these to report the outstanding wallet liability and unpaid host earnings.
    /// </summary>
    public Task<IReadOnlyList<LedgerAccount>> ListAccountsByKindAsync(
        LedgerAccountKind kind, string currency = "usd", CancellationToken ct = default) =>
        _store.ListAccountsByKindAsync(kind, currency, ct);

    /// <summary>
    /// A page of ledger accounts (docs/API.md §8, <c>GET /v1/admin/ledger/accounts</c>, task #194),
    /// narrowed by optional <paramref name="kind"/> / <paramref name="ownerUserId"/> filters. Backs the
    /// admin listing an operator uses to find the two account ids <c>POST /v1/admin/adjustments</c> needs.
    /// </summary>
    public Task<IReadOnlyList<LedgerAccount>> SearchAccountsAsync(
        LedgerAccountKind? kind,
        Guid? ownerUserId,
        string currency,
        int limit,
        int offset,
        CancellationToken ct = default) =>
        _store.SearchAccountsAsync(kind, ownerUserId, currency, limit, offset, ct);

    /// <summary>
    /// The consumer's wallet statement (docs/API.md §5) -- every transaction that touched their
    /// <c>user_wallet</c> (top-ups, holds, releases, refunds, chargebacks), each with the signed amount it
    /// moved, newest-first. Backs <c>GET /v1/billing/transactions</c>; the endpoint paginates it.
    /// </summary>
    public async Task<IReadOnlyList<AccountTransaction>> ListWalletTransactionsAsync(
        Guid userId, string currency = "usd", CancellationToken ct = default)
    {
        var wallet = await _store.GetOrCreateAccountAsync(LedgerAccountKind.UserWallet, userId, currency, ct);
        return await _store.ListAccountTransactionsAsync(wallet.Id, ct);
    }

    /// <summary>
    /// Reconciliation (docs/DATA_MODEL.md §7e): for every account, re-derive its balance from the
    /// immutable journal (<c>Σ</c> of each entry's signed delta) and compare it to the maintained
    /// <see cref="LedgerAccount.BalanceCents"/>. Any non-zero <see cref="AccountReconciliation.DriftCents"/>
    /// is drift that should page an operator -- balances are a cache, the journal is truth.
    /// </summary>
    public async Task<IReadOnlyList<AccountReconciliation>> ReconcileAsync(CancellationToken ct = default)
    {
        var accounts = await _store.ListAccountsAsync(ct);
        var results = new List<AccountReconciliation>(accounts.Count);
        foreach (var account in accounts)
        {
            var entries = await _store.ListEntriesForAccountAsync(account.Id, ct);
            long derived = 0;
            foreach (var entry in entries)
            {
                derived += LedgerAccountKinds.SignedDelta(account.Kind, entry.DebitCents, entry.CreditCents);
            }

            results.Add(new AccountReconciliation(
                account.Id, account.Kind, account.OwnerUserId, account.BalanceCents, derived));
        }

        return results;
    }
}

/// <summary>
/// One account's reconciliation result (docs/DATA_MODEL.md §7e) -- the maintained balance versus the
/// balance re-derived from the journal. <see cref="IsBalanced"/> is the invariant; a non-zero
/// <see cref="DriftCents"/> is a discrepancy to alert on.
/// </summary>
public sealed record AccountReconciliation(
    Guid AccountId,
    LedgerAccountKind Kind,
    Guid? OwnerUserId,
    long MaintainedBalanceCents,
    long DerivedBalanceCents)
{
    /// <summary>Maintained minus derived; zero when the cache agrees with the journal.</summary>
    public long DriftCents => MaintainedBalanceCents - DerivedBalanceCents;

    /// <summary>Whether the maintained balance equals the journal-derived balance.</summary>
    public bool IsBalanced => DriftCents == 0;
}
