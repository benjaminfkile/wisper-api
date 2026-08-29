namespace Wisper.Api.Metering;

/// <summary>
/// A minimal, thread-safe process-local counter of platform-policy fallbacks the metering flush observed
/// (task #206, docs/PAYMENTS.md §4). Every time <c>MeteringService.ResolvePolicyForFlushAsync</c> falls
/// back (stale: no active row but a version exists; missing: no row at all) it calls <see cref="Record"/>
/// with the policy id it used (or <c>null</c> when nothing was available). The admin overview reads the
/// last-snapshot so an operator sees the incident without tailing logs; the boot-count discipline is
/// process-local (each instance reports its own), so an operator wiring a fleet alert should aggregate
/// across instances rather than one.
/// </summary>
public sealed class PolicyFallbackMonitor
{
    private readonly object _gate = new();
    private long _count;
    private PolicyFallbackSnapshot? _last;

    /// <summary>The total fallbacks recorded on this instance since boot.</summary>
    public long Count
    {
        get { lock (_gate) { return _count; } }
    }

    /// <summary>The most recent fallback recorded on this instance, or <c>null</c> if none has occurred.</summary>
    public PolicyFallbackSnapshot? Last
    {
        get { lock (_gate) { return _last; } }
    }

    /// <summary>
    /// Records one fallback occurrence at <paramref name="at"/>. <paramref name="policyId"/> is the
    /// row the flush actually used (the newest version for the stale-fallback branch), or <c>null</c>
    /// on the missing-at-flush branch where no row existed at all.
    /// </summary>
    public void Record(DateTimeOffset at, Guid? policyId)
    {
        lock (_gate)
        {
            _count++;
            _last = new PolicyFallbackSnapshot(at, policyId, _count);
        }
    }
}

/// <summary>
/// One snapshot of the last platform-policy fallback recorded on this instance (task #206): when it
/// happened, the policy id the flush fell back to (or <c>null</c> on the missing-at-flush branch),
/// and the total count observed since boot at the moment of the snapshot.
/// </summary>
/// <param name="At">Wall clock when the fallback was recorded.</param>
/// <param name="PolicyId">The row the flush fell back to; <c>null</c> when no row existed at all.</param>
/// <param name="Count">The total fallback count observed since boot at snapshot time.</param>
public sealed record PolicyFallbackSnapshot(DateTimeOffset At, Guid? PolicyId, long Count);
