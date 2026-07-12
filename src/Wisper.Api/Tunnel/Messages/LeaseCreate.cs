using System.Text.Json.Serialization;

namespace Wisper.Api.Tunnel.Messages;

/// <summary>
/// <c>lease.create</c> (W→A) — Wisper asks the agent to provision a lease
/// (docs/TUNNEL.md §5, §10). Wisper has already authorized + billing-gated and owns
/// the id space, so it carries the server-assigned <c>rid</c> (inherited) and
/// <c>leaseId</c>. The agent forwards <c>image/network/resources/ttl_seconds/userdata/env</c>
/// to wisp's <c>POST /contracts</c> unchanged — hence the snake_case
/// <c>ttl_seconds</c>/<c>memory_mb</c> (mirrors wisp's API), even though tunnel ids stay
/// camelCase.
/// </summary>
public record LeaseCreate : ControlEnvelope
{
    public LeaseCreate() => T = FrameTypes.LeaseCreate;

    [JsonPropertyName("leaseId")]
    public string LeaseId { get; init; } = string.Empty;

    [JsonPropertyName("image")]
    public string Image { get; init; } = string.Empty;

    [JsonPropertyName("network")]
    public string Network { get; init; } = string.Empty;

    [JsonPropertyName("resources")]
    public LeaseResources Resources { get; init; } = new();

    [JsonPropertyName("ttl_seconds")]
    public int TtlSeconds { get; init; }

    [JsonPropertyName("userdata")]
    public string? Userdata { get; init; }

    /// <summary>
    /// Optional, opaque create-time environment variables forwarded down the tunnel with the
    /// lease (generic key→value; never provider-specific). Omitted from the wire when null
    /// (<see cref="ControlJson.Options"/> ignores null). Values are secrets-in-transit — never log
    /// them (docs/TUNNEL.md §13).
    /// </summary>
    [JsonPropertyName("env")]
    public Dictionary<string, string>? Env { get; init; }
}

/// <summary>
/// Per-lease resource request, forwarded to wisp verbatim — so the field names are
/// snake_case to match wisp's <c>POST /contracts</c> body (docs/TUNNEL.md §10).
/// </summary>
public record LeaseResources
{
    [JsonPropertyName("cpus")]
    public double Cpus { get; init; }

    [JsonPropertyName("memory_mb")]
    public int MemoryMb { get; init; }

    [JsonPropertyName("pids")]
    public int Pids { get; init; }
}
