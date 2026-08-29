namespace Wisper.Api.Ledger;

/// <summary>
/// A ledger invariant was violated (docs/DATA_MODEL.md §7): an unbalanced transaction, a malformed entry
/// (not exactly one side), a missing account, an over-drawn hold, or an insufficient-funds wallet debit.
/// The C# ledger raises this before anything is persisted; the SQL triggers raise the equivalent as the
/// defense-in-depth backstop. Higher layers (billing) translate <see cref="Reason"/> into the API error
/// envelope -- e.g. <see cref="LedgerViolation.InsufficientFunds"/> → <c>insufficient_funds</c>.
/// </summary>
public sealed class LedgerException : Exception
{
    public LedgerException(LedgerViolation reason, string message) : base(message) => Reason = reason;

    /// <summary>Which invariant was violated.</summary>
    public LedgerViolation Reason { get; }
}

/// <summary>The specific ledger invariant a <see cref="LedgerException"/> reports (docs/DATA_MODEL.md §7).</summary>
public enum LedgerViolation
{
    /// <summary><c>Σ debit ≠ Σ credit</c> across the transaction's entries (§7a).</summary>
    Unbalanced,

    /// <summary>An entry did not have exactly one non-zero side, or a negative amount (§7 <c>ledger_entries</c> checks).</summary>
    MalformedEntry,

    /// <summary>A transaction had no entries, or referenced an account that does not exist.</summary>
    InvalidTransaction,

    /// <summary>A <c>user_wallet</c> debit would overdraw the wallet (§7d) -- the consumer can't afford it.</summary>
    InsufficientFunds,

    /// <summary>A <c>lease_holds</c> debit would over-draw the hold (§7d).</summary>
    HoldOverdrawn,
}
