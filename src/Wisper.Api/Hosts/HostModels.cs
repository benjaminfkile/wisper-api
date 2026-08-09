using System.Text.Json.Serialization;
using Wisper.Api.Domain;
using Wisper.Api.Payouts;
using Host = Wisper.Api.Domain.Host;

namespace Wisper.Api.Hosts;

/// <summary>Body of <c>POST /v1/hosts</c> (docs/API.md §6): the host's display name + region/label (both optional).</summary>
public sealed record RegisterHostRequest(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("label")] string? Label);

/// <summary>
/// Response of <c>POST /v1/hosts</c> (docs/API.md §6): the freshly issued <c>agent_token</c> — shown
/// <b>once</b>, never retrievable again — plus its non-secret prefix, the <c>manager_ws</c> the agent dials,
/// and the host's initial (offline) status.
/// </summary>
public sealed record HostRegisteredResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("agent_token")] string AgentToken,
    [property: JsonPropertyName("agent_token_prefix")] string AgentTokenPrefix,
    [property: JsonPropertyName("manager_ws")] string ManagerWs,
    [property: JsonPropertyName("status")] string Status);

/// <summary>
/// Response of <c>POST /v1/hosts/:id/agent-token</c> (docs/API.md §6): the rotated token (shown once) and
/// prefix, the <c>manager_ws</c> to reconnect to, and whether the old token's live tunnel was closed
/// (<c>4402</c>). The previous token stops resolving the instant this returns.
/// </summary>
public sealed record AgentTokenRotatedResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("agent_token")] string AgentToken,
    [property: JsonPropertyName("agent_token_prefix")] string AgentTokenPrefix,
    [property: JsonPropertyName("manager_ws")] string ManagerWs,
    [property: JsonPropertyName("tunnel_closed")] bool TunnelClosed);

/// <summary>
/// One row of <c>GET /v1/hosts/mine</c> (docs/API.md §6): the owner's view of a host — identity, presence
/// (<c>online</c> from the live tunnel registry, authoritative over the stored status), advertised capacity,
/// versions, and the non-secret token prefix. The clear token is never included (it exists only at issuance).
/// </summary>
public sealed record HostSummary(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("label")] string? Label,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("online")] bool Online,
    [property: JsonPropertyName("wisp_version")] string? WispVersion,
    [property: JsonPropertyName("agent_version")] string? AgentVersion,
    [property: JsonPropertyName("max_leases")] int? MaxLeases,
    [property: JsonPropertyName("max_streams")] int? MaxStreams,
    [property: JsonPropertyName("gpu_classes")] IReadOnlyList<string> GpuClasses,
    [property: JsonPropertyName("gpu_count")] int GpuCount,
    [property: JsonPropertyName("agent_token_prefix")] string? AgentTokenPrefix,
    [property: JsonPropertyName("last_seen_at")] DateTimeOffset? LastSeenAt,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt)
{
    public static HostSummary From(Host host, bool online) => new(
        Id: host.Id,
        Name: host.Name,
        Label: host.Label,
        Status: PgEnum.ToLabel(host.Status),
        Online: online,
        WispVersion: host.WispVersion,
        AgentVersion: host.AgentVersion,
        MaxLeases: host.MaxLeases,
        MaxStreams: host.MaxStreams,
        // The advertised GPU, surfaced read-only (task #521) — opaque classes + total device count.
        GpuClasses: host.GpuClasses,
        GpuCount: host.GpuCount,
        AgentTokenPrefix: host.AgentTokenPrefix,
        LastSeenAt: host.LastSeenAt,
        CreatedAt: host.CreatedAt);
}

/// <summary>
/// Response of <c>GET /v1/hosts/mine</c> (docs/API.md §6): the caller's hosts with online state + capacity,
/// plus the owner-scoped earnings summary (<c>host_earnings</c> is per owner, not per host).
/// </summary>
public sealed record HostsMineResponse(
    [property: JsonPropertyName("data")] IReadOnlyList<HostSummary> Data,
    [property: JsonPropertyName("earnings")] EarningsResponse Earnings);

/// <summary>
/// The owner-facing view of one priced allow-list entry (docs/API.md §6, docs/DATA_MODEL.md §4). Unlike the
/// consumer catalog shape it also carries <c>enabled</c> and the audit timestamps.
/// </summary>
public sealed record HostImageView(
    [property: JsonPropertyName("host_image_id")] Guid HostImageId,
    [property: JsonPropertyName("image_ref")] string ImageRef,
    [property: JsonPropertyName("price_cents_per_min")] long PriceCentsPerMin,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("networks")] IReadOnlyList<string> Networks,
    [property: JsonPropertyName("max_ttl_seconds")] int MaxTtlSeconds,
    [property: JsonPropertyName("max_cpus")] decimal? MaxCpus,
    [property: JsonPropertyName("max_memory_mb")] int? MaxMemoryMb,
    [property: JsonPropertyName("max_pids")] int? MaxPids,
    [property: JsonPropertyName("cpus")] int? Cpus,
    [property: JsonPropertyName("memory_mb")] int? MemoryMb,
    [property: JsonPropertyName("gpus")] int Gpus,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt)
{
    /// <summary>Currency is USD-only in v0 (docs/API.md §1).</summary>
    private const string Usd = "usd";

    public static HostImageView From(HostImage image) => new(
        HostImageId: image.Id,
        ImageRef: image.ImageRef,
        PriceCentsPerMin: image.PriceCentsPerMin,
        Currency: Usd,
        Networks: image.Networks.Select(PgEnum.ToLabel).ToList(),
        MaxTtlSeconds: image.MaxTtlSeconds,
        MaxCpus: image.MaxCpus,
        MaxMemoryMb: image.MaxMemoryMb,
        MaxPids: image.MaxPids,
        Cpus: image.Cpus,
        MemoryMb: image.MemoryMb,
        Gpus: image.Gpus,
        Enabled: image.Enabled,
        CreatedAt: image.CreatedAt,
        UpdatedAt: image.UpdatedAt);
}

/// <summary>The priced allow-list (docs/API.md §6, <c>GET/PUT /v1/hosts/:id/images</c>), ordered by image ref.</summary>
public sealed record HostImagesResponse(
    [property: JsonPropertyName("data")] IReadOnlyList<HostImageView> Data);

/// <summary>Body of <c>PUT /v1/hosts/:id/images</c> (docs/API.md §6): the full replacement allow-list.</summary>
public sealed record ReplaceImagesRequest(
    [property: JsonPropertyName("images")] IReadOnlyList<ImageUpsert>? Images);

/// <summary>
/// One entry in a <see cref="ReplaceImagesRequest"/> — a priced image and the FIXED resource profile it sells
/// (task #569). <see cref="Cpus"/>/<see cref="MemoryMb"/> are the exact per-lease provisioning (omitted/null =
/// the host's own per-lease policy default applies downstream); each must be positive when present.
/// <see cref="Gpus"/> is the exact whole-device GPU count this offer provisions (0 = no GPU access, the
/// default); it is validated live against the host's advertised GPU capability (task #522). GPU access is
/// priced into this offer, not a separate rate table. The legacy <c>max_*</c> ceilings remain until the
/// lease-side free-form knobs are removed (task #570).
/// </summary>
public sealed record ImageUpsert(
    [property: JsonPropertyName("image_ref")] string? ImageRef,
    [property: JsonPropertyName("price_cents_per_min")] long PriceCentsPerMin,
    [property: JsonPropertyName("networks")] IReadOnlyList<string>? Networks,
    [property: JsonPropertyName("max_ttl_seconds")] int MaxTtlSeconds,
    [property: JsonPropertyName("max_cpus")] decimal? MaxCpus,
    [property: JsonPropertyName("max_memory_mb")] int? MaxMemoryMb,
    [property: JsonPropertyName("max_pids")] int? MaxPids,
    [property: JsonPropertyName("enabled")] bool? Enabled,
    [property: JsonPropertyName("cpus")] int? Cpus = null,
    [property: JsonPropertyName("memory_mb")] int? MemoryMb = null,
    [property: JsonPropertyName("gpus")] int Gpus = 0);

/// <summary>
/// Body of <c>PATCH /v1/hosts/:id/images/:imageId</c> (docs/API.md §6): price/enable/profile/networks for one
/// image. Every field is optional; an omitted field keeps its stored value. The image ref itself is immutable
/// (a re-ref is a replace via PUT). <see cref="Cpus"/>/<see cref="MemoryMb"/>/<see cref="Gpus"/> patch the
/// sized offer's resource profile (task #569).
/// </summary>
public sealed record PatchImageRequest(
    [property: JsonPropertyName("price_cents_per_min")] long? PriceCentsPerMin,
    [property: JsonPropertyName("networks")] IReadOnlyList<string>? Networks,
    [property: JsonPropertyName("max_ttl_seconds")] int? MaxTtlSeconds,
    [property: JsonPropertyName("max_cpus")] decimal? MaxCpus,
    [property: JsonPropertyName("max_memory_mb")] int? MaxMemoryMb,
    [property: JsonPropertyName("max_pids")] int? MaxPids,
    [property: JsonPropertyName("enabled")] bool? Enabled,
    [property: JsonPropertyName("cpus")] int? Cpus = null,
    [property: JsonPropertyName("memory_mb")] int? MemoryMb = null,
    [property: JsonPropertyName("gpus")] int? Gpus = null);
