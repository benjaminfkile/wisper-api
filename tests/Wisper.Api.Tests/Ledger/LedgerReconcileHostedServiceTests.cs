using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wisper.Api.Domain;
using Wisper.Api.Ledger;
using Wisper.Api.Persistence;
using Wisper.Api.Tests.TestSupport;
using Xunit;

namespace Wisper.Api.Tests.Ledger;

/// <summary>
/// Unit tests for the scheduled ledger reconciliation loop (task #183, docs/DATA_MODEL.md §7e, §14): the
/// pass re-derives every account's balance from the journal, compares it to the maintained balance cache,
/// records the outcome on <see cref="LedgerReconcileMonitor"/>, and logs drift. The loop is off in the
/// in-memory persistence mode (nothing to reconcile without a database) and off when disabled by config.
/// The Postgres advisory lock that makes it multi-instance safe is verified against a real Postgres
/// separately; these tests exercise the reconcile pass through <see cref="LedgerReconcileHostedService.ReconcileOnceAsync"/>.
/// </summary>
public class LedgerReconcileHostedServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 29, 0, 0, 0, TimeSpan.Zero);

    private static LedgerReconcileHostedService NewService(
        ILedgerStore store, LedgerReconcileMonitor monitor, FakeTimeProvider clock,
        LedgerReconcileOptions? options = null, Db? db = null) =>
        new(
            new LedgerService(store),
            monitor,
            Options.Create(options ?? new LedgerReconcileOptions()),
            db ?? Db.Unconfigured,
            clock,
            NullLogger<LedgerReconcileHostedService>.Instance);

    [Fact]
    public async Task Balanced_ledger_records_a_no_drift_summary_and_health_reads_ok()
    {
        // A ledger whose maintained balances agree with the journal (the norm) should report zero drift
        // and mark the admin overview signal as balanced.
        var store = new InMemoryLedgerStore();
        var ledger = new LedgerService(store);
        var consumer = Guid.NewGuid();
        var wallet = await ledger.GetOrCreateAccountAsync(LedgerAccountKind.UserWallet, consumer);
        var cash = await ledger.GetOrCreateAccountAsync(LedgerAccountKind.PlatformCash, null);
        var fees = await ledger.GetOrCreateAccountAsync(LedgerAccountKind.StripeFees, null);
        await ledger.PostAsync(LedgerFlows.Topup(
            wallet.Id, cash.Id, fees.Id, grossAmountCents: 1000, stripeFeeCents: 0,
            idempotencyKey: "topup:1"));

        var clock = new FakeTimeProvider(T0);
        var monitor = new LedgerReconcileMonitor();
        var svc = NewService(store, monitor, clock);

        var summary = await svc.ReconcileOnceAsync();

        Assert.NotNull(summary);
        Assert.Equal(T0, summary!.RanAt);
        Assert.Equal(3, summary.AccountsChecked);
        Assert.Equal(0, summary.DriftAccountCount);
        Assert.Equal(0, summary.TotalAbsoluteDriftCents);
        Assert.False(summary.HasDrift);
        Assert.Same(summary, monitor.Last);
    }

    [Fact]
    public async Task Drift_between_maintained_and_derived_balance_is_recorded_and_surfaced()
    {
        // The core value of §7e: when the maintained balance cache diverges from the journal, the pass
        // MUST detect and surface it; that is the operator page.
        var store = new DriftInjectingLedgerStore();
        var accountId = store.SeedAccount(LedgerAccountKind.UserWallet, ownerUserId: Guid.NewGuid(),
            maintainedCents: 1000);
        // Journal shows +900, not +1000; 100c of drift.
        store.SeedEntry(accountId, debit: 0, credit: 900);

        var clock = new FakeTimeProvider(T0);
        var monitor = new LedgerReconcileMonitor();
        var svc = NewService(store, monitor, clock);

        var summary = await svc.ReconcileOnceAsync();

        Assert.NotNull(summary);
        Assert.Equal(1, summary!.AccountsChecked);
        Assert.Equal(1, summary.DriftAccountCount);
        Assert.Equal(100, summary.TotalAbsoluteDriftCents);
        Assert.True(summary.HasDrift);
        Assert.Equal(summary, monitor.Last);
    }

    [Fact]
    public async Task Drift_summary_sums_absolute_values_across_multiple_accounts()
    {
        // Positive and negative drift on different accounts must not cancel; the operator cares about
        // absolute divergence.
        var store = new DriftInjectingLedgerStore();
        var overStated = store.SeedAccount(
            LedgerAccountKind.UserWallet, Guid.NewGuid(), maintainedCents: 500);
        store.SeedEntry(overStated, debit: 0, credit: 400); // maintained > derived by 100

        var underStated = store.SeedAccount(
            LedgerAccountKind.HostEarnings, Guid.NewGuid(), maintainedCents: 200);
        store.SeedEntry(underStated, debit: 0, credit: 250); // maintained < derived by 50

        var balanced = store.SeedAccount(
            LedgerAccountKind.PlatformRevenue, null, maintainedCents: 100);
        store.SeedEntry(balanced, debit: 0, credit: 100);

        var monitor = new LedgerReconcileMonitor();
        var svc = NewService(store, monitor, new FakeTimeProvider(T0));

        var summary = await svc.ReconcileOnceAsync();

        Assert.Equal(3, summary.AccountsChecked);
        Assert.Equal(2, summary.DriftAccountCount);
        Assert.Equal(150, summary.TotalAbsoluteDriftCents);
    }

    [Fact]
    public async Task RunOnceAsync_is_a_skip_when_no_database_is_configured()
    {
        // The advisory lock is a Postgres construct; with no DB there is nothing to coordinate and the
        // hosted service ExecuteAsync short-circuits at startup. RunOnceAsync mirrors that: it treats an
        // absent lock as "another instance owns this pass" and skips (returns null).
        var store = new InMemoryLedgerStore();
        var monitor = new LedgerReconcileMonitor();
        var svc = NewService(store, monitor, new FakeTimeProvider(T0), db: Db.Unconfigured);

        var summary = await svc.RunOnceAsync();

        Assert.Null(summary);
        Assert.Null(monitor.Last); // and no accidental write to the admin overview signal
    }

    [Fact]
    public async Task ExecuteAsync_returns_immediately_when_disabled_by_config()
    {
        // Enabled=false is the operator kill-switch. ExecuteAsync must return without starting the timer.
        var store = new InMemoryLedgerStore();
        var monitor = new LedgerReconcileMonitor();
        var svc = NewService(store, monitor, new FakeTimeProvider(T0),
            options: new LedgerReconcileOptions { Enabled = false });

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromSeconds(2));
        await svc.StartAsync(cts.Token);
        await svc.ExecuteTask!; // completes immediately because the loop never armed the timer
        await svc.StopAsync(CancellationToken.None);

        Assert.True(svc.ExecuteTask.IsCompletedSuccessfully);
        Assert.Null(monitor.Last);
    }

    [Fact]
    public async Task ExecuteAsync_returns_immediately_in_the_in_memory_persistence_mode()
    {
        // The DB-less boot must not start the loop, same shape as the metering / payout / sweep loops.
        var store = new InMemoryLedgerStore();
        var monitor = new LedgerReconcileMonitor();
        var svc = NewService(store, monitor, new FakeTimeProvider(T0), db: Db.Unconfigured);

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromSeconds(2));
        await svc.StartAsync(cts.Token);
        await svc.ExecuteTask!;
        await svc.StopAsync(CancellationToken.None);

        Assert.True(svc.ExecuteTask.IsCompletedSuccessfully);
    }

    // ----- test-only ILedgerStore that lets a test wedge drift between derived and maintained ------

    private sealed class DriftInjectingLedgerStore : ILedgerStore
    {
        private readonly List<LedgerAccount> _accounts = new();
        private readonly List<LedgerEntry> _entries = new();
        private long _nextEntryId = 1;

        public Guid SeedAccount(LedgerAccountKind kind, Guid? ownerUserId, long maintainedCents)
        {
            var account = new LedgerAccount
            {
                Id = Guid.NewGuid(),
                Kind = kind,
                OwnerUserId = ownerUserId,
                Currency = "usd",
                BalanceCents = maintainedCents,
                CreatedAt = T0,
            };
            _accounts.Add(account);
            return account.Id;
        }

        public void SeedEntry(Guid accountId, long debit, long credit)
        {
            _entries.Add(new LedgerEntry
            {
                Id = _nextEntryId++,
                TransactionId = Guid.NewGuid(),
                AccountId = accountId,
                DebitCents = debit,
                CreditCents = credit,
                CreatedAt = T0,
            });
        }

        public Task<IReadOnlyList<LedgerAccount>> ListAccountsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<LedgerAccount>>(_accounts.ToList());

        public Task<IReadOnlyList<LedgerEntry>> ListEntriesForAccountAsync(
            Guid accountId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<LedgerEntry>>(
                _entries.Where(e => e.AccountId == accountId).ToList());

        // The reconcile pass only calls the two methods above; the rest are unused.
        public Task<LedgerAccount> GetOrCreateAccountAsync(
            LedgerAccountKind kind, Guid? ownerUserId, string currency = "usd", CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<LedgerAccount?> GetAccountAsync(Guid id, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<LedgerAccount>> ListAccountsByKindAsync(
            LedgerAccountKind kind, string currency = "usd", CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<LedgerTransaction?> FindTransactionByIdempotencyKeyAsync(
            string idempotencyKey, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<AccountTransaction>> ListAccountTransactionsAsync(
            Guid accountId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<PostedTransaction> PostAsync(TransactionDraft draft, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
