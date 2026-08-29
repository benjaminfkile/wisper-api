using System.Threading.Tasks;
using Wisper.Api.Domain;
using Wisper.Api.Ledger;
using Xunit;

namespace Wisper.Api.Tests.Ledger;

/// <summary>
/// The ledger invariants enforced in C# (docs/DATA_MODEL.md §7), exercised through
/// <see cref="LedgerService"/> over the in-memory store (Grunt has no Postgres): balanced-only posting
/// (§7a), maintained balances by normal side (§7c), the non-negative guard on the earmarked liabilities
/// (§7d) with the documented chargeback exception, idempotent replay (§8), and journal reconciliation
/// (§7e). Every persisted transaction is atomic -- a rejected post leaves balances untouched.
/// </summary>
public class LedgerServiceTests
{
    private static LedgerService NewService() => new(new InMemoryLedgerStore());

    private sealed record Accounts(Guid Wallet, Guid Holds, Guid HostEarnings, Guid PlatformRevenue, Guid PlatformCash, Guid StripeFees);

    private static async Task<Accounts> SeedAccountsAsync(LedgerService svc)
    {
        var consumer = Guid.NewGuid();
        var host = Guid.NewGuid();
        var wallet = await svc.GetOrCreateAccountAsync(LedgerAccountKind.UserWallet, consumer);
        var holds = await svc.GetOrCreateAccountAsync(LedgerAccountKind.LeaseHolds, null);
        var earnings = await svc.GetOrCreateAccountAsync(LedgerAccountKind.HostEarnings, host);
        var revenue = await svc.GetOrCreateAccountAsync(LedgerAccountKind.PlatformRevenue, null);
        var cash = await svc.GetOrCreateAccountAsync(LedgerAccountKind.PlatformCash, null);
        var fees = await svc.GetOrCreateAccountAsync(LedgerAccountKind.StripeFees, null);
        return new Accounts(wallet.Id, holds.Id, earnings.Id, revenue.Id, cash.Id, fees.Id);
    }

    [Fact]
    public async Task GetOrCreateAccount_pins_one_account_per_kind_and_owner()
    {
        var svc = NewService();
        var owner = Guid.NewGuid();

        var first = await svc.GetOrCreateAccountAsync(LedgerAccountKind.UserWallet, owner);
        var again = await svc.GetOrCreateAccountAsync(LedgerAccountKind.UserWallet, owner);
        var singletonA = await svc.GetOrCreateAccountAsync(LedgerAccountKind.LeaseHolds, null);
        var singletonB = await svc.GetOrCreateAccountAsync(LedgerAccountKind.LeaseHolds, null);

        Assert.Equal(first.Id, again.Id);
        Assert.Equal(0, first.BalanceCents);
        Assert.Equal(singletonA.Id, singletonB.Id);
    }

    [Fact]
    public async Task Post_a_balanced_transaction_maintains_balances_by_normal_side()
    {
        var svc = NewService();
        var a = await SeedAccountsAsync(svc);

        var posted = await svc.PostAsync(LedgerFlows.Topup(
            a.Wallet, a.PlatformCash, a.StripeFees, grossAmountCents: 1000, stripeFeeCents: 59, idempotencyKey: "evt_1"));

        Assert.False(posted.WasDeduplicated);
        Assert.Equal(LedgerTxnKind.Topup, posted.Transaction.Kind);
        Assert.All(posted.Entries, e => Assert.True(e.Id > 0));       // persisted, identity assigned
        Assert.Equal(1000, await svc.GetBalanceAsync(a.Wallet));      // credit-normal grows with credit
        Assert.Equal(941, await svc.GetBalanceAsync(a.PlatformCash)); // debit-normal grows with debit
        Assert.Equal(59, await svc.GetBalanceAsync(a.StripeFees));
    }

    [Fact]
    public async Task Post_rejects_an_unbalanced_transaction_and_persists_nothing()
    {
        var svc = NewService();
        var a = await SeedAccountsAsync(svc);

        var unbalanced = new TransactionDraft
        {
            Kind = LedgerTxnKind.Adjustment,
            Entries = new[]
            {
                EntryDraft.Debit(a.PlatformCash, 100),
                EntryDraft.Credit(a.Wallet, 50),   // 100 debit ≠ 50 credit
            },
        };

        var ex = await Assert.ThrowsAsync<LedgerException>(() => svc.PostAsync(unbalanced));
        Assert.Equal(LedgerViolation.Unbalanced, ex.Reason);
        Assert.Equal(0, await svc.GetBalanceAsync(a.Wallet));
        Assert.Equal(0, await svc.GetBalanceAsync(a.PlatformCash));
    }

    [Fact]
    public async Task Post_rejects_an_entry_that_is_not_exactly_one_side()
    {
        var svc = NewService();
        var a = await SeedAccountsAsync(svc);

        var bothZero = new TransactionDraft
        {
            Kind = LedgerTxnKind.Adjustment,
            Entries = new[]
            {
                new EntryDraft { AccountId = a.Wallet, DebitCents = 0, CreditCents = 0 },
                EntryDraft.Credit(a.PlatformCash, 100),
            },
        };
        var bothSides = new TransactionDraft
        {
            Kind = LedgerTxnKind.Adjustment,
            Entries = new[]
            {
                new EntryDraft { AccountId = a.Wallet, DebitCents = 100, CreditCents = 100 },
                EntryDraft.Credit(a.PlatformCash, 100),
            },
        };

        Assert.Equal(LedgerViolation.MalformedEntry,
            (await Assert.ThrowsAsync<LedgerException>(() => svc.PostAsync(bothZero))).Reason);
        Assert.Equal(LedgerViolation.MalformedEntry,
            (await Assert.ThrowsAsync<LedgerException>(() => svc.PostAsync(bothSides))).Reason);
    }

    [Fact]
    public async Task A_wallet_debit_that_would_overdraw_is_rejected_as_insufficient_funds()
    {
        var svc = NewService();
        var a = await SeedAccountsAsync(svc);
        var lease = Guid.NewGuid();
        await svc.PostAsync(LedgerFlows.Topup(a.Wallet, a.PlatformCash, a.StripeFees, 500, 0, "evt_topup"));

        // A hold for more than the wallet holds must fail -- the hard gate (§7d).
        var ex = await Assert.ThrowsAsync<LedgerException>(
            () => svc.PostAsync(LedgerFlows.LeaseHold(a.Wallet, a.Holds, lease, amountCents: 600)));

        Assert.Equal(LedgerViolation.InsufficientFunds, ex.Reason);
        Assert.Equal(500, await svc.GetBalanceAsync(a.Wallet));  // untouched -- atomic rollback
        Assert.Equal(0, await svc.GetBalanceAsync(a.Holds));
    }

    [Fact]
    public async Task A_hold_cannot_be_over_drawn()
    {
        var svc = NewService();
        var a = await SeedAccountsAsync(svc);
        var lease = Guid.NewGuid();
        await svc.PostAsync(LedgerFlows.Topup(a.Wallet, a.PlatformCash, a.StripeFees, 1000, 0, "evt_topup"));
        await svc.PostAsync(LedgerFlows.LeaseHold(a.Wallet, a.Holds, lease, amountCents: 300));

        // Charging more than is held over-draws the hold.
        var ex = await Assert.ThrowsAsync<LedgerException>(() => svc.PostAsync(
            LedgerFlows.LeaseCharge(a.Holds, a.HostEarnings, a.PlatformRevenue, lease, amountCents: 400, platformFeeCents: 0)));

        Assert.Equal(LedgerViolation.HoldOverdrawn, ex.Reason);
        Assert.Equal(300, await svc.GetBalanceAsync(a.Holds));       // untouched
        Assert.Equal(0, await svc.GetBalanceAsync(a.HostEarnings));
    }

    [Fact]
    public async Task A_chargeback_may_drive_the_wallet_negative()
    {
        var svc = NewService();
        var a = await SeedAccountsAsync(svc);
        await svc.PostAsync(LedgerFlows.Topup(a.Wallet, a.PlatformCash, a.StripeFees, 200, 0, "evt_topup"));

        // The one documented case a wallet may go below zero -- a debt (docs/PAYMENTS.md §7).
        var chargeback = new TransactionDraft
        {
            Kind = LedgerTxnKind.Chargeback,
            IdempotencyKey = "dp_1",
            Entries = new[]
            {
                EntryDraft.Debit(a.Wallet, 1000),
                EntryDraft.Credit(a.PlatformCash, 1000),
            },
        };

        var posted = await svc.PostAsync(chargeback);

        Assert.False(posted.WasDeduplicated);
        Assert.Equal(-800, await svc.GetBalanceAsync(a.Wallet));
    }

    [Fact]
    public async Task A_duplicate_idempotency_key_posts_exactly_once()
    {
        var svc = NewService();
        var a = await SeedAccountsAsync(svc);

        var first = await svc.PostAsync(LedgerFlows.Topup(a.Wallet, a.PlatformCash, a.StripeFees, 1000, 59, "evt_dupe"));
        var replay = await svc.PostAsync(LedgerFlows.Topup(a.Wallet, a.PlatformCash, a.StripeFees, 1000, 59, "evt_dupe"));

        Assert.False(first.WasDeduplicated);
        Assert.True(replay.WasDeduplicated);
        Assert.Equal(first.Transaction.Id, replay.Transaction.Id);
        Assert.Equal(1000, await svc.GetBalanceAsync(a.Wallet));   // credited once, not twice
    }

    [Fact]
    public async Task Reconcile_re_derives_every_balance_from_the_journal()
    {
        var svc = NewService();
        var a = await SeedAccountsAsync(svc);
        var lease = Guid.NewGuid();
        await svc.PostAsync(LedgerFlows.Topup(a.Wallet, a.PlatformCash, a.StripeFees, 1000, 59, "evt_topup"));
        await svc.PostAsync(LedgerFlows.LeaseHold(a.Wallet, a.Holds, lease, 500));
        await svc.PostAsync(LedgerFlows.LeaseCharge(a.Holds, a.HostEarnings, a.PlatformRevenue, lease, 120, 18));
        await svc.PostAsync(LedgerFlows.HoldRelease(a.Holds, a.Wallet, lease, 380));

        var report = await svc.ReconcileAsync();

        Assert.NotEmpty(report);
        Assert.All(report, r => Assert.True(r.IsBalanced, $"{r.Kind} drifted by {r.DriftCents}"));
        // Spot-check a derived balance: wallet = 1000 − 500 hold + 380 release = 880.
        var wallet = report.Single(r => r.AccountId == a.Wallet);
        Assert.Equal(880, wallet.DerivedBalanceCents);
    }

    [Fact]
    public async Task GetBalance_of_a_missing_account_throws()
    {
        var svc = NewService();
        await Assert.ThrowsAsync<LedgerException>(() => svc.GetBalanceAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Post_to_a_missing_account_throws()
    {
        var svc = NewService();
        var a = await SeedAccountsAsync(svc);
        var draft = new TransactionDraft
        {
            Kind = LedgerTxnKind.Adjustment,
            Entries = new[]
            {
                EntryDraft.Debit(Guid.NewGuid(), 100),   // account never created
                EntryDraft.Credit(a.Wallet, 100),
            },
        };

        var ex = await Assert.ThrowsAsync<LedgerException>(() => svc.PostAsync(draft));
        Assert.Equal(LedgerViolation.InvalidTransaction, ex.Reason);
    }
}
