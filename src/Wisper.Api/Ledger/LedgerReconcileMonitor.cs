namespace Wisper.Api.Ledger;

/// <summary>
/// A minimal, thread-safe snapshot of the most recent ledger reconciliation pass (docs/DATA_MODEL.md §7e).
/// The scheduled loop (<see cref="LedgerReconcileHostedService"/>) writes into it on every completed pass;
/// the admin overview reads it so an operator can see drift without waiting for the next log line
/// (docs/API.md §8). Values are process-local (each instance reports its own last pass), but the
/// underlying invariant it observes (the journal is truth) is global, so a cross-instance loop taking the
/// advisory lock still moves this dial on the instance that ran the pass.
/// </summary>
public sealed class LedgerReconcileMonitor
{
    private readonly object _gate = new();
    private LedgerReconcileSummary? _last;

    /// <summary>The most recent completed pass, or <c>null</c> if none has run yet on this instance.</summary>
    public LedgerReconcileSummary? Last
    {
        get { lock (_gate) { return _last; } }
    }

    /// <summary>
    /// Records the outcome of a pass. Called by the hosted service after each successful reconciliation;
    /// the entry replaces the previous snapshot atomically so a reader never sees a torn write.
    /// </summary>
    public void Record(LedgerReconcileSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        lock (_gate) { _last = summary; }
    }
}

/// <summary>
/// One reconciliation pass's aggregate result (docs/DATA_MODEL.md §7e): when it ran, how many accounts it
/// visited, how many showed drift, and the total absolute drift in cents. <see cref="HasDrift"/> is the
/// operator flag: the balance cache disagrees with the journal.
/// </summary>
/// <param name="RanAt">Wall clock when the pass completed.</param>
/// <param name="AccountsChecked">Total number of accounts visited on the pass.</param>
/// <param name="DriftAccountCount">Number of accounts whose derived balance differed from their maintained one.</param>
/// <param name="TotalAbsoluteDriftCents">Sum of the absolute drift across every drifted account, in cents.</param>
public sealed record LedgerReconcileSummary(
    DateTimeOffset RanAt,
    int AccountsChecked,
    int DriftAccountCount,
    long TotalAbsoluteDriftCents)
{
    /// <summary>Whether the maintained balance cache diverged from the journal on the recorded pass.</summary>
    public bool HasDrift => DriftAccountCount > 0;
}
