using Wisper.Api.Domain;

namespace Wisper.Api.Ledger;

/// <summary>Which side of an account its natural (positive) balance accrues on (docs/DATA_MODEL.md §7).</summary>
public enum NormalSide
{
    /// <summary>Balance grows with credits: <c>balance += credit − debit</c>.</summary>
    Credit,

    /// <summary>Balance grows with debits: <c>balance += debit − credit</c>.</summary>
    Debit,
}

/// <summary>
/// Pure accounting rules for <see cref="LedgerAccountKind"/> -- the C# mirror of the SQL balance and
/// non-negative triggers (docs/DATA_MODEL.md §7c, §7d). Shared by the in-memory ledger and the flow
/// builders so the app-side and DB-side interpretations of "normal side" cannot drift.
/// </summary>
public static class LedgerAccountKinds
{
    /// <summary>
    /// The account's normal side. <c>user_wallet</c>, <c>host_earnings</c>, <c>lease_holds</c> and
    /// <c>platform_revenue</c> are credit-normal; <c>platform_cash</c> and <c>stripe_fees</c> are
    /// debit-normal (docs/DATA_MODEL.md §7).
    /// </summary>
    public static NormalSide SideOf(LedgerAccountKind kind) => kind switch
    {
        LedgerAccountKind.UserWallet => NormalSide.Credit,
        LedgerAccountKind.HostEarnings => NormalSide.Credit,
        LedgerAccountKind.LeaseHolds => NormalSide.Credit,
        LedgerAccountKind.PlatformRevenue => NormalSide.Credit,
        LedgerAccountKind.PlatformCash => NormalSide.Debit,
        LedgerAccountKind.StripeFees => NormalSide.Debit,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown ledger account kind"),
    };

    /// <summary>
    /// Whether the account is an <b>earmarked liability</b> guarded non-negative -- the <c>user_wallet</c>
    /// and <c>lease_holds</c> accounts (docs/DATA_MODEL.md §7d). These are the hard guarantee a consumer
    /// can never outspend their wallet and a hold can't be over-drawn.
    /// </summary>
    public static bool IsEarmarkedLiability(LedgerAccountKind kind) =>
        kind is LedgerAccountKind.UserWallet or LedgerAccountKind.LeaseHolds;

    /// <summary>
    /// The signed change this posting makes to the account's natural balance -- credit-normal:
    /// <c>credit − debit</c>; debit-normal: <c>debit − credit</c> (docs/DATA_MODEL.md §7c).
    /// </summary>
    public static long SignedDelta(LedgerAccountKind kind, long debitCents, long creditCents) =>
        SideOf(kind) == NormalSide.Credit ? creditCents - debitCents : debitCents - creditCents;
}
