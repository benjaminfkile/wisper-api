using Wisper.Api.Domain;
using Wisper.Api.Ledger;
using Xunit;

namespace Wisper.Api.Tests.Ledger;

/// <summary>
/// The pure accounting rules (docs/DATA_MODEL.md §7) — normal side, earmarked-liability set, and the
/// signed-delta a posting makes — that the in-memory ledger and the SQL triggers both rely on. Getting
/// the normal side wrong is a money bug, so it is pinned per kind.
/// </summary>
public class LedgerAccountKindsTests
{
    [Theory]
    [InlineData(LedgerAccountKind.UserWallet, NormalSide.Credit)]
    [InlineData(LedgerAccountKind.HostEarnings, NormalSide.Credit)]
    [InlineData(LedgerAccountKind.LeaseHolds, NormalSide.Credit)]
    [InlineData(LedgerAccountKind.PlatformRevenue, NormalSide.Credit)]
    [InlineData(LedgerAccountKind.PlatformCash, NormalSide.Debit)]
    [InlineData(LedgerAccountKind.StripeFees, NormalSide.Debit)]
    public void SideOf_pins_each_accounts_normal_side(LedgerAccountKind kind, NormalSide expected) =>
        Assert.Equal(expected, LedgerAccountKinds.SideOf(kind));

    [Theory]
    [InlineData(LedgerAccountKind.UserWallet, true)]
    [InlineData(LedgerAccountKind.LeaseHolds, true)]
    [InlineData(LedgerAccountKind.HostEarnings, false)]
    [InlineData(LedgerAccountKind.PlatformRevenue, false)]
    [InlineData(LedgerAccountKind.PlatformCash, false)]
    [InlineData(LedgerAccountKind.StripeFees, false)]
    public void Only_wallet_and_holds_are_earmarked_liabilities(LedgerAccountKind kind, bool earmarked) =>
        Assert.Equal(earmarked, LedgerAccountKinds.IsEarmarkedLiability(kind));

    [Fact]
    public void SignedDelta_of_a_credit_normal_account_grows_with_credits()
    {
        // A wallet (credit-normal): a credit adds, a debit subtracts.
        Assert.Equal(100, LedgerAccountKinds.SignedDelta(LedgerAccountKind.UserWallet, debitCents: 0, creditCents: 100));
        Assert.Equal(-100, LedgerAccountKinds.SignedDelta(LedgerAccountKind.UserWallet, debitCents: 100, creditCents: 0));
    }

    [Fact]
    public void SignedDelta_of_a_debit_normal_account_grows_with_debits()
    {
        // platform_cash (debit-normal): a debit adds, a credit subtracts.
        Assert.Equal(100, LedgerAccountKinds.SignedDelta(LedgerAccountKind.PlatformCash, debitCents: 100, creditCents: 0));
        Assert.Equal(-100, LedgerAccountKinds.SignedDelta(LedgerAccountKind.PlatformCash, debitCents: 0, creditCents: 100));
    }
}
