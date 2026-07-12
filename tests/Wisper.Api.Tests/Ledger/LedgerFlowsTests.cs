using System.Threading.Tasks;
using Wisper.Api.Domain;
using Wisper.Api.Ledger;
using Xunit;

namespace Wisper.Api.Tests.Ledger;

/// <summary>
/// The §8 money flows as balanced-transaction builders (docs/DATA_MODEL.md §8). Each builder must emit
/// <c>Σ debit = Σ credit</c> with the right side per account, and posting the whole lease lifecycle
/// through the ledger must leave every account where §8 says it should — the hold fully drained, host
/// earnings and platform revenue split by the fee, and the unused remainder back in the wallet.
/// </summary>
public class LedgerFlowsTests
{
    private static void AssertBalanced(TransactionDraft draft)
    {
        long debit = 0;
        long credit = 0;
        foreach (var entry in draft.Entries)
        {
            Assert.True((entry.DebitCents == 0) != (entry.CreditCents == 0), "entry must have exactly one side");
            debit += entry.DebitCents;
            credit += entry.CreditCents;
        }

        Assert.Equal(debit, credit);
        Assert.True(debit > 0, "a flow must move a non-zero amount");
    }

    private static long SideTotal(TransactionDraft draft, Guid accountId, bool debit) =>
        draft.Entries.Where(e => e.AccountId == accountId).Sum(e => debit ? e.DebitCents : e.CreditCents);

    [Theory]
    [InlineData(1000, 0, 0, 1000)]      // 0 bps → all to host
    [InlineData(1000, 1500, 150, 850)]  // 15% fee
    [InlineData(120, 1500, 18, 102)]    // the §8 example
    [InlineData(7, 3333, 2, 5)]         // floor: 7*3333/10000 = 2.3 → 2
    public void SplitFee_floors_the_fee_and_sums_to_the_amount(long amount, int bps, long expectedFee, long expectedHost)
    {
        var (fee, host) = LedgerFlows.SplitFee(amount, bps);

        Assert.Equal(expectedFee, fee);
        Assert.Equal(expectedHost, host);
        Assert.Equal(amount, fee + host);   // no rounding drift (matches lease_usage check, §6)
    }

    [Fact]
    public void Topup_debits_cash_and_fees_and_credits_the_wallet()
    {
        var wallet = Guid.NewGuid();
        var cash = Guid.NewGuid();
        var fees = Guid.NewGuid();

        var draft = LedgerFlows.Topup(wallet, cash, fees, grossAmountCents: 1000, stripeFeeCents: 59, idempotencyKey: "evt_1");

        AssertBalanced(draft);
        Assert.Equal(LedgerTxnKind.Topup, draft.Kind);
        Assert.Equal("evt_1", draft.IdempotencyKey);
        Assert.Equal(941, SideTotal(draft, cash, debit: true));
        Assert.Equal(59, SideTotal(draft, fees, debit: true));
        Assert.Equal(1000, SideTotal(draft, wallet, debit: false));
    }

    [Fact]
    public void Topup_with_no_fee_omits_the_stripe_fees_leg()
    {
        var draft = LedgerFlows.Topup(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1000, 0, "evt_2");

        AssertBalanced(draft);
        Assert.Equal(2, draft.Entries.Count);   // just cash-debit + wallet-credit
    }

    [Fact]
    public void LeaseHold_debits_wallet_and_credits_holds_tagged_with_the_lease()
    {
        var wallet = Guid.NewGuid();
        var holds = Guid.NewGuid();
        var lease = Guid.NewGuid();

        var draft = LedgerFlows.LeaseHold(wallet, holds, lease, amountCents: 500);

        AssertBalanced(draft);
        Assert.Equal(LedgerTxnKind.LeaseHold, draft.Kind);
        Assert.Equal(lease, draft.LeaseId);
        Assert.All(draft.Entries, e => Assert.Equal(lease, e.LeaseId));
        Assert.Equal(500, SideTotal(draft, wallet, debit: true));
        Assert.Equal(500, SideTotal(draft, holds, debit: false));
    }

    [Fact]
    public void LeaseCharge_debits_holds_and_splits_between_host_and_platform()
    {
        var holds = Guid.NewGuid();
        var earnings = Guid.NewGuid();
        var revenue = Guid.NewGuid();
        var lease = Guid.NewGuid();

        var draft = LedgerFlows.LeaseCharge(holds, earnings, revenue, lease, amountCents: 120, platformFeeCents: 18);

        AssertBalanced(draft);
        Assert.Equal(120, SideTotal(draft, holds, debit: true));
        Assert.Equal(102, SideTotal(draft, earnings, debit: false));
        Assert.Equal(18, SideTotal(draft, revenue, debit: false));
    }

    [Fact]
    public void HoldRelease_debits_holds_and_credits_the_wallet()
    {
        var holds = Guid.NewGuid();
        var wallet = Guid.NewGuid();
        var lease = Guid.NewGuid();

        var draft = LedgerFlows.HoldRelease(holds, wallet, lease, amountCents: 380);

        AssertBalanced(draft);
        Assert.Equal(380, SideTotal(draft, holds, debit: true));
        Assert.Equal(380, SideTotal(draft, wallet, debit: false));
    }

    [Fact]
    public void Payout_debits_host_earnings_and_credits_platform_cash()
    {
        var earnings = Guid.NewGuid();
        var cash = Guid.NewGuid();

        var draft = LedgerFlows.Payout(earnings, cash, amountCents: 102, idempotencyKey: "payout_1");

        AssertBalanced(draft);
        Assert.Equal(102, SideTotal(draft, earnings, debit: true));
        Assert.Equal(102, SideTotal(draft, cash, debit: false));
    }

    [Fact]
    public void Refund_reverses_a_topup_debiting_wallet_and_crediting_cash_and_fees()
    {
        var wallet = Guid.NewGuid();
        var cash = Guid.NewGuid();
        var fees = Guid.NewGuid();

        var draft = LedgerFlows.Refund(wallet, cash, fees, grossAmountCents: 1000, stripeFeeCents: 59, idempotencyKey: "re_1");

        AssertBalanced(draft);
        Assert.Equal(1000, SideTotal(draft, wallet, debit: true));
        Assert.Equal(941, SideTotal(draft, cash, debit: false));
        Assert.Equal(59, SideTotal(draft, fees, debit: false));
    }

    [Fact]
    public void Chargeback_debits_wallet_and_credits_cash()
    {
        var wallet = Guid.NewGuid();
        var cash = Guid.NewGuid();

        var draft = LedgerFlows.Chargeback(wallet, cash, amountCents: 1000, idempotencyKey: "evt_dispute");

        AssertBalanced(draft);
        Assert.Equal(LedgerTxnKind.Chargeback, draft.Kind);
        Assert.Equal("evt_dispute", draft.IdempotencyKey);
        Assert.Equal(1000, SideTotal(draft, wallet, debit: true));   // consumer loses the credits
        Assert.Equal(1000, SideTotal(draft, cash, debit: false));    // money left the platform to the network
    }

    [Fact]
    public async Task Chargeback_may_drive_the_wallet_negative_a_debt()
    {
        var svc = new LedgerService(new InMemoryLedgerStore());
        var consumer = Guid.NewGuid();

        var wallet = (await svc.GetOrCreateAccountAsync(LedgerAccountKind.UserWallet, consumer)).Id;
        var cash = (await svc.GetOrCreateAccountAsync(LedgerAccountKind.PlatformCash, null)).Id;
        var fees = (await svc.GetOrCreateAccountAsync(LedgerAccountKind.StripeFees, null)).Id;

        // Fund 1000, spend it all (a lease charge draining the wallet via a hold), then dispute the top-up.
        await svc.PostAsync(LedgerFlows.Topup(wallet, cash, fees, 1000, 0, "evt_topup"));
        var holds = (await svc.GetOrCreateAccountAsync(LedgerAccountKind.LeaseHolds, null)).Id;
        var earnings = (await svc.GetOrCreateAccountAsync(LedgerAccountKind.HostEarnings, consumer)).Id;
        var revenue = (await svc.GetOrCreateAccountAsync(LedgerAccountKind.PlatformRevenue, null)).Id;
        var lease = Guid.NewGuid();
        await svc.PostAsync(LedgerFlows.LeaseHold(wallet, holds, lease, 1000));
        await svc.PostAsync(LedgerFlows.LeaseCharge(holds, earnings, revenue, lease, 1000, 150));
        Assert.Equal(0, await svc.GetBalanceAsync(wallet));

        // The disputed top-up is clawed back — the wallet goes negative (a genuine debt, docs/PAYMENTS.md §7).
        await svc.PostAsync(LedgerFlows.Chargeback(wallet, cash, 1000, "evt_dispute"));

        Assert.Equal(-1000, await svc.GetBalanceAsync(wallet));
        var report = await svc.ReconcileAsync();
        Assert.All(report, r => Assert.True(r.IsBalanced));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Builders_reject_a_non_positive_amount(long amount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LedgerFlows.LeaseHold(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), amount));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LedgerFlows.Topup(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), amount, 0, "k"));
    }

    [Fact]
    public void Builders_reject_a_fee_greater_than_the_amount()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LedgerFlows.Topup(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 100, 200, "k"));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LedgerFlows.LeaseCharge(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 100, 200));
    }

    [Fact]
    public async Task The_full_lease_lifecycle_settles_every_account_per_section_8()
    {
        var svc = new LedgerService(new InMemoryLedgerStore());
        var consumer = Guid.NewGuid();
        var host = Guid.NewGuid();
        var lease = Guid.NewGuid();

        var wallet = (await svc.GetOrCreateAccountAsync(LedgerAccountKind.UserWallet, consumer)).Id;
        var holds = (await svc.GetOrCreateAccountAsync(LedgerAccountKind.LeaseHolds, null)).Id;
        var earnings = (await svc.GetOrCreateAccountAsync(LedgerAccountKind.HostEarnings, host)).Id;
        var revenue = (await svc.GetOrCreateAccountAsync(LedgerAccountKind.PlatformRevenue, null)).Id;
        var cash = (await svc.GetOrCreateAccountAsync(LedgerAccountKind.PlatformCash, null)).Id;
        var fees = (await svc.GetOrCreateAccountAsync(LedgerAccountKind.StripeFees, null)).Id;

        // top-up 1000 (fee 59) → hold 500 → charge one tick 120 (fee 18) → release the unused 380 → payout 102.
        await svc.PostAsync(LedgerFlows.Topup(wallet, cash, fees, 1000, 59, "evt_topup"));
        await svc.PostAsync(LedgerFlows.LeaseHold(wallet, holds, lease, 500));
        await svc.PostAsync(LedgerFlows.LeaseCharge(holds, earnings, revenue, lease, 120, 18));
        await svc.PostAsync(LedgerFlows.HoldRelease(holds, wallet, lease, 380));
        await svc.PostAsync(LedgerFlows.Payout(earnings, cash, 102, "payout_1"));

        Assert.Equal(880, await svc.GetBalanceAsync(wallet));   // 1000 − 500 + 380
        Assert.Equal(0, await svc.GetBalanceAsync(holds));      // fully drained (charge 120 + release 380)
        Assert.Equal(0, await svc.GetBalanceAsync(earnings));   // 102 earned, 102 paid out
        Assert.Equal(18, await svc.GetBalanceAsync(revenue));
        Assert.Equal(839, await svc.GetBalanceAsync(cash));     // 941 in − 102 paid out
        Assert.Equal(59, await svc.GetBalanceAsync(fees));

        var report = await svc.ReconcileAsync();
        Assert.All(report, r => Assert.True(r.IsBalanced));
    }
}
