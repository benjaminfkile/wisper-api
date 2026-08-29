using Microsoft.Extensions.Logging;

namespace Wisper.Api.Billing;

/// <summary>
/// Distinct <see cref="EventId"/>s for billing-integrity log events so filters can match on the id
/// rather than a message prefix (task #206). Grouped in the 2000 range to leave room for the framework
/// defaults. Add new ids monotonically; never re-use an id for a different event.
/// </summary>
public static class BillingEventIds
{
    /// <summary>
    /// <c>MeteringService.ResolvePolicyForFlushAsync</c> found no active <c>platform_policy</c> row at
    /// flush time but did find at least one row and fell back to the newest version regardless of
    /// <c>effective_from</c>. Money accounting stays correct against a real fee basis; the operator
    /// has a clear signal to publish an active policy (docs/PAYMENTS.md §4).
    /// </summary>
    public static readonly EventId PolicyStaleFallback = new(2001, "billing.policy.stale_fallback");

    /// <summary>
    /// <c>MeteringService.ResolvePolicyForFlushAsync</c> found no <c>platform_policy</c> row at all
    /// (impossible after migration 0017 but kept as a guard). The flush is skipped and the caller's
    /// end path releases the full hold to the wallet so a lease is never stranded with a parked hold.
    /// A critical billing-integrity alert; an operator must publish a <c>platform_policy</c> row
    /// (docs/PAYMENTS.md §4).
    /// </summary>
    public static readonly EventId PolicyMissingAtFlush = new(2002, "billing.policy.missing_at_flush");
}
