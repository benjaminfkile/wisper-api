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
    /// The host's advertised capability, if the heartbeat carries it (task #417). The <c>hello</c> is the
    /// primary source; a heartbeat that re-advertises lets a host update its offered isolation without a
    /// reconnect. Null when the heartbeat carries no capability (the common case).
    /// </summary>
    [JsonPropertyName("capability")]
    public HeartbeatCapability? Capability { get; init; }
}

/// <summary>
/// The optional capability block on a <see cref="HostHeartbeat"/> (task #417) — currently the advertised
/// isolation levels and default, so a host can refresh them mid-session. Mirrors the snake_case wire
/// fields the <c>hello.capability</c> uses.
/// </summary>
public record HeartbeatCapability
{
    [JsonPropertyName("isolation_levels")]
    public IReadOnlyList<string> IsolationLevels { get; init; } = Array.Empty<string>();

    [JsonPropertyName("default_isolation")]
    public string? DefaultIsolation { get; init; }

    /// <summary>
    /// The host's advertised GPU (snake_case wire block <c>gpu</c>, task #521), so a host can refresh its
    /// offered classes/count mid-session the same way it refreshes isolation. Null when the heartbeat carries
    /// no GPU block (the common case, and every older agent).
    /// </summary>
    [JsonPropertyName("gpu")]
    public HelloGpu? Gpu { get; init; }
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
