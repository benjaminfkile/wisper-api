namespace Wisper.Api.Domain;

/// <summary>
/// A host payout (docs/DATA_MODEL.md §9, <c>payouts</c>) — a Stripe Connect transfer that drains a host's
/// accrued <see cref="LedgerAccountKind.HostEarnings"/> to their bank. The Stripe transfer's idempotency
/// key is the row's <see cref="Id"/> and <see cref="StripeTransferId"/> is unique, so a retried run can't
/// double-pay (docs/PAYMENTS.md §6). <see cref="PayoutTxnId"/> ties the row to its <c>payout</c> ledger
/// transaction (the money leaving the platform).
/// </summary>
public sealed record Payout
{
    /// <summary>Row id (DB default <c>gen_random_uuid()</c>); also the Stripe transfer idempotency key.</summary>
    public Guid Id { get; init; }

    /// <summary>The host owner being paid.</summary>
    public required Guid HostUserId { get; init; }

    /// <summary>Amount transferred, in cents (<c>&gt; 0</c>).</summary>
    public required long AmountCents { get; init; }

    /// <summary>Settlement currency (single-currency for now — <c>usd</c>).</summary>
    public string Currency { get; init; } = "usd";

    /// <summary>Start of the earnings window paid (UTC), if scoped to a window.</summary>
    public DateTimeOffset? PeriodStart { get; init; }

    /// <summary>End of the earnings window paid (UTC), if scoped to a window.</summary>
    public DateTimeOffset? PeriodEnd { get; init; }

    /// <summary>Transfer state (<see cref="PayoutStatus.Pending"/> on creation).</summary>
    public PayoutStatus Status { get; init; } = PayoutStatus.Pending;

    /// <summary>The Stripe Connect transfer id once created (unique), or <c>null</c> before.</summary>
    public string? StripeTransferId { get; init; }

    /// <summary>The <c>payout</c> ledger transaction that posted this transfer, or <c>null</c> before posting.</summary>
    public Guid? PayoutTxnId { get; init; }

    /// <summary>Failure detail when <see cref="Status"/> is <see cref="PayoutStatus.Failed"/>, else <c>null</c>.</summary>
    public string? Error { get; init; }

    /// <summary>Row creation time (UTC).</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Last update time (UTC).</summary>
    public DateTimeOffset UpdatedAt { get; init; }
}
