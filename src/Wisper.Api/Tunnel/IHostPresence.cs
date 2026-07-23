using Wisper.Api.Domain;

namespace Wisper.Api.Tunnel;

/// <summary>
/// The tunnel-driven host presence hook (docs/TUNNEL.md §3, §8): flips a host's persisted
/// <c>host_status</c> as its agent tunnel comes up and goes durably down, so the consumer catalog (which
/// reads the DB-online subset) reflects the live tunnel. The gate that decides <i>whether</i> a ready host
/// may go online lives in <see cref="ConnectGate.CanHostGoOnline"/>; the implementation
/// (<see cref="HostPresenceService"/>) is the single place it is applied to presence.
/// </summary>
public interface IHostPresence
{
    /// <summary>
    /// On tunnel ready (registered + <c>hello.ack</c> sent): persist the host's advertised isolation
    /// capability (<paramref name="isolationLevels"/> / <paramref name="defaultIsolation"/> from the
    /// <c>hello</c>, normalized to <c>["shared"]</c>/<c>"shared"</c> when absent — task #417), then flip
    /// <paramref name="hostId"/> to <see cref="HostStatus.Online"/> when it clears the earning gate (owner
    /// Connect-enabled, or every enabled image is zero-priced). A suspended host, an earning-gated host, an
    /// unknown host, or a non-Guid dev host id all leave presence untouched. Passing <c>null</c> for both
    /// isolation arguments leaves the persisted capability as-is (the pre-#417 call shape).
    /// </summary>
    Task GoOnlineIfEligibleAsync(
        string hostId,
        IReadOnlyList<string>? isolationLevels = null,
        string? defaultIsolation = null,
        CancellationToken ct = default);

    /// <summary>
    /// Refreshes a host's persisted advertised isolation capability from a mid-session source (a heartbeat
    /// that re-advertises, task #417). Normalizes the report, skips the write when nothing changed, and
    /// never touches presence. A suspended, unknown, or non-Guid dev host id is a no-op.
    /// </summary>
    Task RefreshAdvertisedIsolationAsync(
        string hostId,
        IReadOnlyList<string>? isolationLevels,
        string? defaultIsolation,
        CancellationToken ct = default);

    /// <summary>
    /// On durable tunnel loss (grace expired, or a close with no leases to protect): flip
    /// <paramref name="hostId"/> to <see cref="HostStatus.Offline"/>, stamping last-seen at
    /// <paramref name="lastHealthyAt"/>. Only a currently-online host is flipped — a suspended or
    /// already-offline host is left as-is.
    /// </summary>
    Task GoOfflineAsync(Guid hostId, DateTimeOffset lastHealthyAt, CancellationToken ct = default);
}
