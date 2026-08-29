namespace Wisper.Api.Metering;

/// <summary>
/// Operational parameters for the metering engine (docs/DATA_MODEL.md §14, docs/PAYMENTS.md §4), bound
/// from the <c>Metering</c> configuration section. The meter flushes accrued lease-minutes on a fixed
/// tick; a manager crash therefore loses at most one un-flushed tick, recoverable on restart from
/// <c>leases.last_metered_at</c>.
/// </summary>
public sealed class MeteringOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Metering";

    /// <summary>The flush cadence in seconds (docs/DATA_MODEL.md §14 -- default <b>60s</b>).</summary>
    public int TickSeconds { get; set; } = 60;

    /// <summary>Whether the background flush loop runs. Off by default in a DB-less boot (see the host).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The tick cadence as a <see cref="TimeSpan"/> (clamped to at least one second).</summary>
    public TimeSpan TickInterval => TimeSpan.FromSeconds(Math.Max(1, TickSeconds));
}
