namespace Wisper.Api.Ledger;

/// <summary>
/// A single unposted leg of a transaction -- one account and a debit <b>or</b> a credit (docs/DATA_MODEL.md
/// §7). It becomes a <see cref="Wisper.Api.Domain.LedgerEntry"/> once the transaction is posted. Build one
/// with <see cref="Debit"/> or <see cref="Credit"/> so the "exactly one side" invariant holds by
/// construction.
/// </summary>
public sealed record EntryDraft
{
    /// <summary>The account this leg posts to.</summary>
    public required Guid AccountId { get; init; }

    /// <summary>Debit amount in cents (<c>&gt;= 0</c>); zero for a credit leg.</summary>
    public long DebitCents { get; init; }

    /// <summary>Credit amount in cents (<c>&gt;= 0</c>); zero for a debit leg.</summary>
    public long CreditCents { get; init; }

    /// <summary>The lease this leg tracks, for per-lease hold accounting; <c>null</c> otherwise.</summary>
    public Guid? LeaseId { get; init; }

    /// <summary>A debit leg of <paramref name="cents"/> against <paramref name="accountId"/>.</summary>
    public static EntryDraft Debit(Guid accountId, long cents, Guid? leaseId = null) =>
        new() { AccountId = accountId, DebitCents = cents, CreditCents = 0, LeaseId = leaseId };

    /// <summary>A credit leg of <paramref name="cents"/> against <paramref name="accountId"/>.</summary>
    public static EntryDraft Credit(Guid accountId, long cents, Guid? leaseId = null) =>
        new() { AccountId = accountId, DebitCents = 0, CreditCents = cents, LeaseId = leaseId };
}
