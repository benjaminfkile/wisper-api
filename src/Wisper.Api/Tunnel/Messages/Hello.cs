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

    /// <summary>
    /// The host's advertised GPU capability (snake_case wire block <c>gpu</c>, task #521). Opaque to this
    /// service — device classes and isolation strings inside it are mirrored, never interpreted. Null/absent
    /// for an older agent, which the manager treats as <c>supported=false</c> (no GPU).
    /// </summary>
    [JsonPropertyName("gpu")]
    public HelloGpu? Gpu { get; init; }

    /// <summary>
    /// The host's live contract capacity as wisp reports it (snake_case wire block <c>capacity</c>, task #571):
    /// the concurrent-contract ceiling the manager fast-fails against, plus the informational resource totals.
    /// Null/absent for an older agent that does not forward it, which the manager treats as <b>unlimited</b>
    /// (the pre-#571 behavior) — a per-host admission decision is made only when a positive ceiling is present.
    /// wisp remains the authoritative enforcer (its 409 surfaces as an <c>at_capacity</c> lease failure).
    /// </summary>
    [JsonPropertyName("capacity")]
    public HelloContractCapacity? Capacity { get; init; }

    [JsonPropertyName("limits")]
    public HelloLimits Limits { get; init; } = new();
}

/// <summary>
/// The <c>capacity</c> block wisp forwards inside a hello/heartbeat capability (task #571): the host's real
/// concurrent-contract ceiling and the live resource totals it is running against. Every field is snake_case,
/// mirroring wisp's own API. Only <see cref="MaxContracts"/> drives a manager-side decision — the per-host
/// fast-fail admission control — and only when it is positive; the resource totals are informational surfacing.
/// An absent block (older agent) ⇒ unlimited, so nothing here is enforced (docs/TUNNEL.md §5).
/// </summary>
public record HelloContractCapacity
{
    /// <summary>The most concurrent contracts (leases) this host will serve; <c>0</c> (or absent) ⇒ unlimited.</summary>
    [JsonPropertyName("max_contracts")]
    public int MaxContracts { get; init; }

    /// <summary>How many contracts wisp currently reports active — informational (the manager counts its own).</summary>
    [JsonPropertyName("active_contracts")]
    public int ActiveContracts { get; init; }

    /// <summary>The host's total advertised CPU capacity (cores) — informational surfacing.</summary>
    [JsonPropertyName("total_cpus")]
    public double TotalCpus { get; init; }

    /// <summary>How much CPU (cores) wisp reports in use — informational surfacing.</summary>
    [JsonPropertyName("used_cpus")]
    public double UsedCpus { get; init; }

    /// <summary>The host's total advertised memory (MiB) — informational surfacing.</summary>
    [JsonPropertyName("total_memory_mb")]
    public long TotalMemoryMb { get; init; }

    /// <summary>How much memory (MiB) wisp reports in use — informational surfacing.</summary>
    [JsonPropertyName("used_memory_mb")]
    public long UsedMemoryMb { get; init; }
}

/// <summary>
/// The <c>gpu</c> block of a hello/heartbeat capability (task #521): whether the host serves GPU, the devices
/// it exposes, its per-lease device ceiling, and the GPU isolation modes it offers. Every class and isolation
/// string inside is <b>opaque</b> to this service — stored/surfaced, never interpreted (like the sandbox
/// isolation levels). Absent for an older agent, which is treated as <c>supported=false</c>.
/// </summary>
public record HelloGpu
{
    /// <summary>Whether this host serves GPU at all. An absent <c>gpu</c> block is treated as <c>false</c>.</summary>
    [JsonPropertyName("supported")]
    public bool Supported { get; init; }

    /// <summary>The GPU devices the host exposes; empty when the host serves none.</summary>
    [JsonPropertyName("devices")]
    public IReadOnlyList<HelloGpuDevice> Devices { get; init; } = Array.Empty<HelloGpuDevice>();

    /// <summary>The most GPUs a single lease may request on this host (Wisper-enforced in a later task).</summary>
    [JsonPropertyName("max_gpus")]
    public int MaxGpus { get; init; }

    /// <summary>The GPU isolation modes this host offers — opaque strings, mirrored from the agent.</summary>
    [JsonPropertyName("isolations")]
    public IReadOnlyList<string> Isolations { get; init; } = Array.Empty<string>();

    /// <summary>
    /// The advertised device class strings in order (may contain duplicates across identical devices) — the
    /// input the manager de-dupes into the host's distinct <c>gpu_classes</c>. Never null.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<string> DeviceClasses => Devices.Select(d => d.Class).ToList();
}

/// <summary>One GPU device in a <see cref="HelloGpu"/> — its opaque id, opaque hardware class, and VRAM.</summary>
public record HelloGpuDevice
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("class")]
    public string Class { get; init; } = string.Empty;

    [JsonPropertyName("vram_mb")]
    public int VramMb { get; init; }
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
