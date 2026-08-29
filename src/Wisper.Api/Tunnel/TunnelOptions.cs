namespace Wisper.Api.Tunnel;

/// <summary>
/// Operational parameters for the agent tunnel, bound from configuration
/// (section <see cref="SectionName"/>). Defaults match docs/TUNNEL.md §5/§7/§8/§9.
/// </summary>
public sealed class TunnelOptions
{
    /// <summary>Configuration section these options bind from.</summary>
    public const string SectionName = "Tunnel";

    /// <summary>
    /// The public agent-tunnel WebSocket URL handed to a host at registration/rotation as
    /// <c>manager_ws</c> (docs/API.md §6 -- the <c>wss://&lt;wisper-host&gt;/agent</c> the agent dials).
    /// Deployment config; the default is a sensible local value.
    /// </summary>
    public string ManagerWebSocketUrl { get; set; } = "wss://localhost/agent";

    /// <summary>WebSocket ping cadence in milliseconds (docs/TUNNEL.md §7). Announced in <c>hello.ack</c>.</summary>
    public int PingIntervalMs { get; set; } = 30000;

    /// <summary>Maximum binary payload per data frame in bytes (docs/TUNNEL.md §2).</summary>
    public int MaxFrameBytes { get; set; } = 32768;

    /// <summary>Initial per-stream send window in bytes (docs/TUNNEL.md §9).</summary>
    public int InitialWindowBytes { get; set; } = 262144;

    /// <summary>Disconnect grace window in seconds (docs/TUNNEL.md §8).</summary>
    public int GraceSeconds { get; set; } = 90;

    /// <summary>
    /// Application liveness timeout in milliseconds: if no frame arrives within this window
    /// the peer is treated as dead and closed with <see cref="CloseCodes.LivenessTimeout"/>.
    /// When 0 (the default) it is derived as 2.5× <see cref="PingIntervalMs"/> (~75s at 30s),
    /// which is roughly two missed pongs (docs/TUNNEL.md §7).
    /// </summary>
    public int LivenessTimeoutMs { get; set; }

    /// <summary>
    /// Per-request relay deadline in milliseconds (docs/TUNNEL.md §12): how long the relay
    /// waits for the agent's correlated response (<c>lease.accepted</c>+<c>ready</c>,
    /// <c>exec.result</c>, <c>lease.released</c>) before failing with an upstream timeout.
    /// </summary>
    public int RelayRequestTimeoutMs { get; set; } = 120000;

    /// <summary>
    /// How long a relay lookup waits for a host's tunnel to become <b>ready</b> (registered +
    /// <c>hello.ack</c> sent, docs/TUNNEL.md §3) before failing with <c>host_offline</c>. This closes
    /// the connection-readiness race: a create arriving in the brief window while a freshly-connected
    /// agent is still completing its hello handshake waits this long for readiness instead of racing to
    /// a spurious <c>host_offline</c>. Kept short -- the handshake is milliseconds -- so a genuinely
    /// offline host still fails fast with a retryable 409.
    /// </summary>
    public int HostReadinessTimeoutMs { get; set; } = 2000;

    /// <summary>
    /// When <c>true</c>, maps the money-free, DEV-ONLY lease drive endpoints
    /// (<c>POST /dev/leases</c>, <c>POST /dev/leases/{id}/exec</c>, <c>DELETE /dev/leases/{id}</c>)
    /// used as a Phase-1 test harness. Off by default -- these have no auth/accounts/billing
    /// and are replaced by the real <c>/v1/leases</c> surface once accounts land.
    /// </summary>
    public bool EnableDevEndpoints { get; set; }

    /// <summary>
    /// Phase-1 host-token allow-list: maps an opaque Bearer host token to a stable host id.
    /// If empty, the tunnel <b>fails closed</b> (rejects every connection). A later DB-backed
    /// validator replaces this (docs/TUNNEL.md §13).
    /// </summary>
    public Dictionary<string, string> HostTokens { get; set; } = new();

    /// <summary>The effective liveness timeout, resolving the derive-from-ping default.</summary>
    public int EffectiveLivenessTimeoutMs =>
        LivenessTimeoutMs > 0 ? LivenessTimeoutMs : (int)(PingIntervalMs * 2.5);
}
