namespace Wisper.Api.Payouts;

/// <summary>
/// Operational parameters for host payouts (docs/PAYMENTS.md §6), bound from the <c>Payouts</c> configuration
/// section. A scheduled run pays out every host whose accrued <c>host_earnings</c> clears
/// <see cref="PayoutMinCents"/> (and whose Connect account is enabled) on a fixed cadence; the on-demand
/// <c>POST /v1/payouts</c> uses the same threshold. The background loop is a no-op on a DB-less boot or when
/// disabled by config.
/// </summary>
public sealed class PayoutOptions
{
    /// <summary>Configuration section these options bind from.</summary>
    public const string SectionName = "Payouts";

    /// <summary>Whether the scheduled payout loop runs. Off automatically on a DB-less boot (see the host).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The minimum accrued <c>host_earnings</c>, in cents, a host must have before a payout is made
    /// (docs/PAYMENTS.md §6). Below this, earnings keep accruing and are paid on a later run. Applies to both
    /// the scheduled run and the on-demand path.
    /// </summary>
    public long PayoutMinCents { get; set; } = 100;

    /// <summary>The scheduled cadence in hours (docs/PAYMENTS.md §6 -- default <b>daily</b>).</summary>
    public int IntervalHours { get; set; } = 24;

    /// <summary>The scheduled cadence as a <see cref="TimeSpan"/> (clamped to at least one hour).</summary>
    public TimeSpan Interval => TimeSpan.FromHours(Math.Max(1, IntervalHours));
}
