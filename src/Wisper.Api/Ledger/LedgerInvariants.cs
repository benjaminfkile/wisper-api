namespace Wisper.Api.Ledger;

/// <summary>
/// State-independent validation of a <see cref="TransactionDraft"/> — the C# mirror of the SQL entry
/// checks and the deferred balanced-transaction trigger (docs/DATA_MODEL.md §7a, §7 <c>ledger_entries</c>
/// checks). "Balanced" and "exactly one side per entry" depend only on the draft, so they are checked
/// here up front; the balance-dependent non-negative guard (§7d) is enforced at commit time by the
/// in-memory ledger (in C#) and by the SQL trigger (defense-in-depth).
/// </summary>
public static class LedgerInvariants
{
    /// <summary>
    /// Validates that <paramref name="draft"/> is well-formed and balanced. Throws
    /// <see cref="LedgerException"/> with the offending <see cref="LedgerViolation"/> otherwise.
    /// </summary>
    public static void ValidateDraft(TransactionDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);

        if (draft.Entries.Count < 2)
        {
            throw new LedgerException(LedgerViolation.InvalidTransaction,
                "a ledger transaction needs at least two entries (a debit and a credit)");
        }

        long totalDebit = 0;
        long totalCredit = 0;
        foreach (var entry in draft.Entries)
        {
            if (entry.DebitCents < 0 || entry.CreditCents < 0)
            {
                throw new LedgerException(LedgerViolation.MalformedEntry,
                    $"ledger entry amounts must be non-negative (account {entry.AccountId})");
            }

            // Exactly one side is non-zero — the SQL CHECK ((debit = 0) <> (credit = 0)).
            if ((entry.DebitCents == 0) == (entry.CreditCents == 0))
            {
                throw new LedgerException(LedgerViolation.MalformedEntry,
                    $"ledger entry must have exactly one non-zero side (account {entry.AccountId})");
            }

            totalDebit += entry.DebitCents;
            totalCredit += entry.CreditCents;
        }

        if (totalDebit != totalCredit)
        {
            throw new LedgerException(LedgerViolation.Unbalanced,
                $"unbalanced ledger txn: debit {totalDebit} <> credit {totalCredit}");
        }

        if (totalDebit == 0)
        {
            throw new LedgerException(LedgerViolation.InvalidTransaction,
                "a ledger transaction must move a non-zero amount");
        }
    }
}
