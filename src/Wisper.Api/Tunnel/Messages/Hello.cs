using System.Text.Json.Serialization;

namespace Wisper.Api.Tunnel.Messages;

/// <summary>
/// <c>hello</c> (A→W) — the first control frame the agent sends after the WebSocket
/// upgrade (docs/TUNNEL.md §3, §5). It advertises the host's capability (the wisp
/// <c>GET /images</c> document) plus versions and the concurrency the host will serve.
/// </summary>
public record Hello : ControlEnvelope
{
    public Hello() => T = FrameTypes.Hello;

    /// <summary>Protocol version the agent speaks (see <see cref="TunnelProtocol.ProtocolVersion"/>).</summary>
    [JsonPropertyName("proto")]
    public int Proto { get; init; }

    /// <summary>The wisp-agent build version.</summary>
    [JsonPropertyName("agentVersion")]
    public string AgentVersion { get; init; } = string.Empty;

    /// <summary>The local wisp (<c>wispd</c>) version the agent is bridging to.</summary>
    [JsonPropertyName("wispVersion")]
    public string WispVersion { get; init; } = string.Empty;

    /// <summary>What images/limits this host will serve (mirrors wisp's <c>GET /images</c>).</summary>
    [JsonPropertyName("capability")]
    public HelloCapability Capability { get; init; } = new();

    /// <summary>How much this host will serve concurrently (Wisper-enforced, docs/TUNNEL.md §5).</summary>
    [JsonPropertyName("capacity")]
    public HelloCapacity Capacity { get; init; } = new();
}

/// <summary>The <c>capability</c> block of a <see cref="Hello"/> — the wisp image allow-list.</summary>
public record HelloCapability
{
    [JsonPropertyName("images")]
    public IReadOnlyList<string> Images { get; init; } = Array.Empty<string>();

    [JsonPropertyName("default")]
    public string Default { get; init; } = string.Empty;

    /// <summary>
    /// The host's container OS (<c>"linux"</c> | <c>"windows"</c>), mirroring wisp's <c>GET /images</c>
    /// (snake_case wire field <c>os</c>). Optional and back-compatible: an older agent that omits it leaves
    /// this <c>null</c> (unknown). Surfacing only — it drives no lease-routing decision (docs/TUNNEL.md §5).
    /// </summary>
    [JsonPropertyName("os")]
    public string? Os { get; init; }

    /// <summary>
    /// The sandbox isolation levels this host offers (snake_case wire field <c>isolation_levels</c>,
    /// task #417). Opaque strings mirrored from the agent's capability report; empty for an older agent
    /// that does not advertise them, which the manager treats as <c>["shared"]</c>.
    /// </summary>
    [JsonPropertyName("isolation_levels")]
    public IReadOnlyList<string> IsolationLevels { get; init; } = Array.Empty<string>();

    /// <summary>
    /// The isolation level this host uses when a lease requests none (snake_case wire field
    /// <c>default_isolation</c>). Null/absent for an older agent, normalized to <c>"shared"</c>.
    /// </summary>
    [JsonPropertyName("default_isolation")]
    public string? DefaultIsolation { get; init; }

    [JsonPropertyName("limits")]
    public HelloLimits Limits { get; init; } = new();
}

/// <summary>Per-lease limits wisp enforces on the host (snake_case, mirroring wisp's API).</summary>
public record HelloLimits
{
    [JsonPropertyName("max_ttl_seconds")]
    public long MaxTtlSeconds { get; init; }

    [JsonPropertyName("max_cpus")]
    public double MaxCpus { get; init; }

    [JsonPropertyName("max_memory_mb")]
    public long MaxMemoryMb { get; init; }

    [JsonPropertyName("pids_limit")]
    public long PidsLimit { get; init; }

    [JsonPropertyName("networks")]
    public IReadOnlyList<string> Networks { get; init; } = Array.Empty<string>();
}

/// <summary>The <c>capacity</c> block of a <see cref="Hello"/>.</summary>
public record HelloCapacity
{
    [JsonPropertyName("maxLeases")]
    public int MaxLeases { get; init; }

    [JsonPropertyName("maxStreams")]
    public int MaxStreams { get; init; }
}
