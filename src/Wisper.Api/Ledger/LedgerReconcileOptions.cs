namespace Wisper.Api.Ledger;

/// <summary>
/// Operational parameters for the scheduled ledger reconciliation loop (docs/DATA_MODEL.md §7e, §14), bound
/// from the <c>LedgerReconcile</c> configuration section. On every tick the loop re-derives each account's
/// balance from the immutable journal and compares it to the maintained balance; any drift is logged and
/// surfaced on the admin overview.
/// </summary>
public sealed class LedgerReconcileOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "LedgerReconcile";

    /// <summary>
    /// Whether the background loop runs. The loop is <b>additionally</b> skipped in the in-memory
    /// persistence mode (no configured database, see the hosted service), so leaving this at the default
    /// safely does the right thing on a DB-less boot.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The cadence in minutes (default <b>15</b>).</summary>
    public int IntervalMinutes { get; set; } = 15;

    /// <summary>The cadence as a <see cref="TimeSpan"/> (clamped to at least one minute).</summary>
    public TimeSpan Interval => TimeSpan.FromMinutes(Math.Max(1, IntervalMinutes));
}
