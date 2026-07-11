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
