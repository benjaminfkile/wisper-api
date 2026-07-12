namespace Wisper.Api.Metering;

/// <summary>
/// Supplies the last time a host's tunnel was known healthy — the last received frame / pong bracketed by
/// liveness (docs/TUNNEL.md §7). The metering tick consults it so accrual is capped at the last-healthy
/// point and a blind (silent-but-not-yet-detected) window is <b>structurally un-billable</b>, not merely
/// un-billed (docs/TUNNEL.md §8): a lease whose host has no live tunnel is skipped this tick — the
/// disconnect path flushes it to last-healthy and suspends it. The tunnel registry backs the real
/// implementation; the meter treats a <c>null</c> source as "no liveness gating" (unit tests).
/// </summary>
public interface IMeterLivenessSource
{
    /// <summary>
    /// The last-healthy liveness instant for <paramref name="hostId"/>, or <c>null</c> when the host has
    /// no live tunnel (so the meter must not accrue past a point it cannot vouch for).
    /// </summary>
    DateTimeOffset? LastHealthyAt(Guid hostId);
}
