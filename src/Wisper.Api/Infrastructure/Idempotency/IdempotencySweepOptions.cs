namespace Wisper.Api.Infrastructure.Idempotency;

/// <summary>
/// Operational parameters for the scheduled idempotency-key TTL sweep
/// (docs/DATA_MODEL.md §10, §14), bound from the <c>IdempotencySweep</c> configuration section.
/// Expired records are otherwise only swept lazily (on retry); the loop reaps them proactively so
/// <c>idempotency_keys</c> does not bloat between low-traffic windows.
/// </summary>
public sealed class IdempotencySweepOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "IdempotencySweep";

    /// <summary>
    /// Whether the background sweep loop runs. The loop is <b>additionally</b> skipped in the in-memory
    /// persistence mode (no configured database, see the hosted service), so leaving this at the default
    /// safely does the right thing on a DB-less boot.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The cadence in minutes (default <b>60</b>).</summary>
    public int IntervalMinutes { get; set; } = 60;

    /// <summary>The cadence as a <see cref="TimeSpan"/> (clamped to at least one minute).</summary>
    public TimeSpan Interval => TimeSpan.FromMinutes(Math.Max(1, IntervalMinutes));
}
