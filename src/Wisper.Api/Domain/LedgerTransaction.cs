namespace Wisper.Api.Domain;

/// <summary>
/// One money movement (docs/DATA_MODEL.md §7, <c>ledger_transactions</c>) -- the header over a balanced
/// set of <see cref="LedgerEntry"/> rows whose <c>Σ debit = Σ credit</c>. Append-only and immutable; a
/// correction is a new reversing transaction, never an edit. <see cref="IdempotencyKey"/> is the
/// unique dedupe key (a Stripe event id, an API idempotency key, …) so a retry or duplicate webhook can
/// never double-post (§8, docs/PAYMENTS.md §8).
/// </summary>
public sealed record LedgerTransaction
{
    /// <summary>Transaction id (DB default <c>gen_random_uuid()</c>).</summary>
    public Guid Id { get; init; }

    /// <summary>The money flow this transaction represents (docs/DATA_MODEL.md §8).</summary>
    public required LedgerTxnKind Kind { get; init; }

    /// <summary>The lease this movement is scoped to, when lease-scoped (hold/charge/release).</summary>
    public Guid? LeaseId { get; init; }

    /// <summary>An external reference -- a Stripe <c>pi_</c>/<c>tr_</c>/<c>ch_</c> id, etc.</summary>
    public string? ExternalRef { get; init; }

    /// <summary>Unique idempotency key that dedupes retries/webhooks; <c>null</c> for un-keyed internal posts.</summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>Human-readable note for audit/debugging.</summary>
    public string? Memo { get; init; }

    /// <summary>Row creation time (UTC).</summary>
    public DateTimeOffset CreatedAt { get; init; }
}
