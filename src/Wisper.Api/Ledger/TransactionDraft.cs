using Wisper.Api.Domain;

namespace Wisper.Api.Ledger;

/// <summary>
/// An unposted double-entry transaction — a <see cref="LedgerTxnKind"/> plus the balanced set of
/// <see cref="EntryDraft"/> legs to post atomically (docs/DATA_MODEL.md §7, §8). The §8 money flows are
/// produced as these by <see cref="LedgerFlows"/>; <see cref="LedgerService.PostAsync"/> validates and
/// commits one.
/// </summary>
public sealed record TransactionDraft
{
    /// <summary>The money flow this transaction represents.</summary>
    public required LedgerTxnKind Kind { get; init; }

    /// <summary>The balanced legs (<c>Σ debit = Σ credit</c>); at least two, each a single side.</summary>
    public required IReadOnlyList<EntryDraft> Entries { get; init; }

    /// <summary>The lease this movement is scoped to, when lease-scoped.</summary>
    public Guid? LeaseId { get; init; }

    /// <summary>An external reference — a Stripe id, etc.</summary>
    public string? ExternalRef { get; init; }

    /// <summary>Unique idempotency key that dedupes retries/webhooks; <c>null</c> for un-keyed internal posts.</summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>Human-readable note for audit/debugging.</summary>
    public string? Memo { get; init; }
}
