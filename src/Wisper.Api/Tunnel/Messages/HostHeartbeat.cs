using System.Text.Json.Serialization;

namespace Wisper.Api.Tunnel.Messages;

/// <summary>
/// <c>host.heartbeat</c> (A→W) — the application heartbeat the agent sends every ~15s
/// (docs/TUNNEL.md §5, §7). It is <b>not</b> the liveness signal (ping/pong is); it is
/// the truth source Wisper reconciles against after a reconnect and the signal for host
/// load/health. Carries the host's live lease list and optional load.
/// </summary>
public record HostHeartbeat : ControlEnvelope
{
    public HostHeartbeat() => T = FrameTypes.HostHeartbeat;

    [JsonPropertyName("leases")]
    public IReadOnlyList<HeartbeatLease> Leases { get; init; } = Array.Empty<HeartbeatLease>();

    [JsonPropertyName("load")]
    public HostLoad? Load { get; init; }

    /// <summary>
    /// The host's advertised capability, if the heartbeat carries it. The <c>hello</c> is the primary
    /// source; a heartbeat that re-advertises lets a host refresh its offered isolation, GPU, and
    /// contract-capacity ceiling mid-session without reconnecting (tasks #417, #521, #61). Reuses the
    /// exact <see cref="HelloCapability"/> shape so parsing / snapshot projection is shared between
    /// hello and heartbeat — no fields are silently dropped. Null when the heartbeat carries no
    /// capability (the common case, and when the agent's local wisp is unreachable so it deliberately
    /// omits the block): "no update — keep last known".
    /// </summary>
    [JsonPropertyName("capability")]
    public HelloCapability? Capability { get; init; }

    /// <summary>
    /// The agent's self-reported health at this beat (task #62). Populated as <c>"degraded"</c> when
    /// the agent cannot reach its local wisp daemon — the tunnel is up but every <c>lease.create</c>
    /// would fail downstream, so the manager must stop placing new leases on this host until a later
    /// beat drops the field. Absent (null / empty) means "normal" — the agent is healthy and the host
    /// is placement-eligible again. Any unknown value is treated as normal (forward-compatible).
    /// Consumed by the heartbeat handler in <see cref="Wisper.Api.Tunnel.TunnelEndpoints"/>; the
    /// cross-instance degraded set lives in <see cref="Wisper.Api.Tunnel.Backplane.IHostDegradedStore"/>.
    /// </summary>
    [JsonPropertyName("status")]
    public string? Status { get; init; }
}

/// <summary>A single live lease entry in a <see cref="HostHeartbeat"/>.</summary>
public record HeartbeatLease
{
    [JsonPropertyName("leaseId")]
    public string LeaseId { get; init; } = string.Empty;

    [JsonPropertyName("wispContractId")]
    public string WispContractId { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;
}

/// <summary>Optional host load snapshot in a <see cref="HostHeartbeat"/>.</summary>
public record HostLoad
{
    [JsonPropertyName("cpu")]
    public double Cpu { get; init; }

    [JsonPropertyName("mem")]
    public double Mem { get; init; }

    [JsonPropertyName("running")]
    public int Running { get; init; }
}
