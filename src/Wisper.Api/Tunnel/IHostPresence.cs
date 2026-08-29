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
    /// <c>hello</c>) when the agent actually advertised any, then flip <paramref name="hostId"/> to
    /// <see cref="HostStatus.Online"/> when it clears the earning gate (owner Connect-enabled, or every
    /// enabled image is zero-priced). A suspended host, an earning-gated host, an unknown host, or a
    /// non-Guid dev host id all leave presence untouched. A null or empty <paramref name="isolationLevels"/>
    /// (an absent capability block, or an agent whose local wisp is unreachable at handshake) leaves the
    /// persisted isolation as-is rather than normalizing it back to <c>["shared"]</c> and overwriting a
    /// kata/gVisor host's advertisement (task #191); only a first-ever hello for a fresh row falls back to
    /// the DB default of <c>["shared"]</c>. Likewise passing <c>null</c> for <paramref name="gpuClasses"/>
    /// (an absent <c>gpu</c> block, an older agent) leaves the persisted GPU capability as-is rather than
    /// nulling it (task #521).
    /// </summary>
    Task GoOnlineIfEligibleAsync(
        string hostId,
        IReadOnlyList<string>? isolationLevels = null,
        string? defaultIsolation = null,
        IReadOnlyList<string>? gpuClasses = null,
        int gpuCount = 0,
        CancellationToken ct = default);

    /// <summary>
    /// Persists the host's <c>hello</c>-reported versions and top-level capacity (<c>wisp_version</c>,
    /// <c>agent_version</c>, <c>max_leases</c>, <c>max_streams</c>) from the handshake (task #182), so admin
    /// reads (<c>GET /v1/hosts/mine</c>, <c>GET /v1/admin/hosts</c>) see what the connected agent actually
    /// advertised. Blank/whitespace strings and non-positive capacity values normalize to <c>null</c> on the
    /// row. The columns are advisory surfacing only; per-host admission is enforced against the live
    /// <c>capability.capacity.max_contracts</c> snapshot (task #571, refreshed via heartbeat, task #61), not
    /// against these persisted fields, so a heartbeat capability refresh never rewrites them. A suspended
    /// host is left untouched (suspension is authoritative, so a suspended host must not have its row
    /// mutated as if it were live); an unknown host or a non-Guid dev host id is a no-op.
    /// </summary>
    Task RefreshAdvertisedVersionsAndCapacityAsync(
        string hostId,
        string? wispVersion,
        string? agentVersion,
        int maxLeases,
        int maxStreams,
        CancellationToken ct = default);

    /// <summary>
    /// Refreshes a host's persisted advertised isolation capability from a mid-session source (a heartbeat
    /// that re-advertises, task #417). Normalizes the report, skips the write when nothing changed, and
    /// never touches presence. A null or empty <paramref name="isolationLevels"/> is treated as absent
    /// (no update, keep last known, task #191): a heartbeat that omits the field cannot overwrite the
    /// host's persisted advertisement. A suspended, unknown, or non-Guid dev host id is a no-op.
    /// </summary>
    Task RefreshAdvertisedIsolationAsync(
        string hostId,
        IReadOnlyList<string>? isolationLevels,
        string? defaultIsolation,
        CancellationToken ct = default);

    /// <summary>
    /// Refreshes a host's persisted advertised GPU capability (<c>gpu_classes</c> + <c>gpu_count</c>) from a
    /// mid-session source (a heartbeat that re-advertises its <c>gpu</c> block, task #521) -- the GPU sibling
    /// of <see cref="RefreshAdvertisedIsolationAsync"/>. Normalizes the classes (distinct), skips the write
    /// when nothing changed, and never touches presence. A suspended, unknown, or non-Guid dev host id is a
    /// no-op.
    /// </summary>
    Task RefreshAdvertisedGpuAsync(
        string hostId,
        IReadOnlyList<string>? gpuClasses,
        int gpuCount,
        CancellationToken ct = default);

    /// <summary>
    /// On durable tunnel loss (grace expired, or a close with no leases to protect): flip
    /// <paramref name="hostId"/> to <see cref="HostStatus.Offline"/>, stamping last-seen at
    /// <paramref name="lastHealthyAt"/>. Only a currently-online host is flipped -- a suspended or
    /// already-offline host is left as-is.
    /// </summary>
    Task GoOfflineAsync(Guid hostId, DateTimeOffset lastHealthyAt, CancellationToken ct = default);
}
