namespace Wisper.Api.Domain;

/// <summary>
/// One flushed metering interval for a lease (docs/DATA_MODEL.md §6, <c>lease_usage</c>). The in-memory
/// meter writes a row per tick and on lease end, each tied to the <c>lease_charge</c> ledger transaction
/// that debited the hold (<see cref="ChargeTxnId"/>). Rows are idempotent on
/// <c>(lease_id, period_start)</c> so a retried flush cannot double-charge (§14), and the money split is
/// checked: <see cref="AmountCents"/> equals <see cref="PlatformFeeCents"/> + <see cref="HostPayoutCents"/>.
/// </summary>
public sealed record LeaseUsage
{
    /// <summary>Row id (DB default <c>gen_random_uuid()</c>).</summary>
    public Guid Id { get; init; }

    /// <summary>The lease this interval belongs to.</summary>
    public required Guid LeaseId { get; init; }

    /// <summary>Start of the metered interval (UTC); part of the idempotency key.</summary>
    public required DateTimeOffset PeriodStart { get; init; }

    /// <summary>End of the metered interval (UTC).</summary>
    public required DateTimeOffset PeriodEnd { get; init; }

    /// <summary>Billable seconds in the interval (<c>&gt;= 0</c>).</summary>
    public int BillableSeconds { get; init; }

    /// <summary>Total charged this interval in cents (<c>= fee + payout</c>).</summary>
    public long AmountCents { get; init; }

    /// <summary>Platform cut in cents (docs/DATA_MODEL.md §8, §11).</summary>
    public long PlatformFeeCents { get; init; }

    /// <summary>Host earnings in cents.</summary>
    public long HostPayoutCents { get; init; }

    /// <summary>The <c>lease_charge</c> ledger transaction that debited the hold for this interval.</summary>
    public required Guid ChargeTxnId { get; init; }

    /// <summary>Row creation time (UTC).</summary>
    public DateTimeOffset CreatedAt { get; init; }
}
