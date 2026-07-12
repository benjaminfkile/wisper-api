using Wisper.Api.Domain;

namespace Wisper.Api.Metering;

/// <summary>
/// The outcome of flushing one metered interval for a lease (docs/DATA_MODEL.md §6, §14) — the
/// persisted <see cref="LeaseUsage"/> row, the id of the <c>lease_charge</c> ledger transaction that
/// debited the hold, and whether the flush was a de-duplicated replay (same <c>(lease_id, period_start)</c>,
/// so no new money moved). <see cref="MeteringService"/> returns this from a flush and <c>null</c> when
/// there was nothing billable to flush.
/// </summary>
public sealed record MeteringFlushResult(
    LeaseUsage Usage,
    Guid ChargeTxnId,
    bool WasDeduplicated)
{
    /// <summary>The lease this interval belongs to.</summary>
    public Guid LeaseId => Usage.LeaseId;

    /// <summary>Total cents charged for the interval (<c>= platform fee + host payout</c>).</summary>
    public long AmountCents => Usage.AmountCents;
}
