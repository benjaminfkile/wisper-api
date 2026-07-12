namespace Wisper.Api.Domain;

/// <summary>
/// A single posting to one account (docs/DATA_MODEL.md §7, <c>ledger_entries</c>) — the atom of the
/// journal. Exactly one of <see cref="DebitCents"/> / <see cref="CreditCents"/> is non-zero (the other
/// is zero); which side moves the account's balance up or down depends on the account's normal side
/// (§7c). Entries are append-only and immutable.
/// </summary>
public sealed record LedgerEntry
{
    /// <summary>Entry id (DB <c>bigint GENERATED ALWAYS AS IDENTITY</c>); 0 until persisted.</summary>
    public long Id { get; init; }

    /// <summary>The transaction this entry belongs to.</summary>
    public required Guid TransactionId { get; init; }

    /// <summary>The account this entry posts to.</summary>
    public required Guid AccountId { get; init; }

    /// <summary>Debit amount in cents (<c>&gt;= 0</c>); zero when this is a credit entry.</summary>
    public long DebitCents { get; init; }

    /// <summary>Credit amount in cents (<c>&gt;= 0</c>); zero when this is a debit entry.</summary>
    public long CreditCents { get; init; }

    /// <summary>The lease this entry tracks, for per-lease hold accounting; <c>null</c> otherwise.</summary>
    public Guid? LeaseId { get; init; }

    /// <summary>Row creation time (UTC).</summary>
    public DateTimeOffset CreatedAt { get; init; }
}
