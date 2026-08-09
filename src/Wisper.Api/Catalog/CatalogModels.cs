using System.Text.Json.Serialization;
using Wisper.Api.Domain;
using Host = Wisper.Api.Domain.Host;

namespace Wisper.Api.Catalog;

/// <summary>
/// One priced, enabled image as it appears in the consumer catalog (docs/API.md §5). It flattens a
/// <see cref="HostImage"/> to its public, lease-relevant fields: the id a lease references, the image
/// ref, the price snapshot the lease will take, the fixed resource profile the sized offer provisions
/// (<c>cpus</c>/<c>memory_mb</c>/<c>gpus</c>, task #569), and the legacy ceilings/limits the host offers.
/// </summary>
public sealed record CatalogImage(
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
    [property: JsonPropertyName("gpus")] int Gpus)
{
    /// <summary>Currency is USD-only in v0 (docs/API.md §1 — integer cents + <c>"usd"</c>).</summary>
    private const string Usd = "usd";

    /// <summary>Projects a stored <see cref="HostImage"/> into its catalog wire shape.</summary>
    public static CatalogImage From(HostImage image) => new(
        HostImageId: image.Id,
        ImageRef: image.ImageRef,
        PriceCentsPerMin: image.PriceCentsPerMin,
        Currency: Usd,
        Networks: image.Networks.Select(PgEnum.ToLabel).ToList(),
        MaxTtlSeconds: image.MaxTtlSeconds,
        MaxCpus: image.MaxCpus,
        MaxMemoryMb: image.MaxMemoryMb,
        MaxPids: image.MaxPids,
        // The sized offer's fixed profile (task #569): the exact cpu/memory this offer provisions per lease
        // (null = the host's per-lease policy default applies downstream), and the exact GPU device count
        // (0 = no GPU access on this offer). Surfaced so a consumer can price the size before leasing.
        Cpus: image.Cpus,
        MemoryMb: image.MemoryMb,
        Gpus: image.Gpus);
}

/// <summary>
/// One catalog entry (docs/API.md §5): an online host and its priced, enabled images. The wire
/// <c>label</c> carries the host's display name and <c>region</c> its region/label
/// (docs/DATA_MODEL.md §4 — <c>hosts.name</c> is the display, <c>hosts.label</c> the region). Only
/// hosts confirmed online by the live tunnel registry are emitted, so <c>online</c> is always true.
/// <c>os</c> carries the host's advertised container OS (<c>"linux"</c> | <c>"windows"</c>, mirroring
/// the wisp <c>/images</c> document), or <c>null</c> when the live capability has none — a client can
/// adapt to a Windows host without a separate fetch, and an older agent that omits it never errors.
/// <c>gpu_classes</c>/<c>gpu_count</c> mirror the host's advertised GPU (task #523) so a consumer can
/// filter/size without a per-host fetch; empty/0 for a host that advertises none. <c>at_capacity</c> (task
/// #571) tells the frontend to badge a full host; when the host advertises a concurrent-contract ceiling it
/// also carries <c>active_leases</c>/<c>max_leases</c>. A host with no advertised ceiling (or offline) is never
/// at capacity and omits the counts (null) — the tolerant-optional-field discipline the frontend relies on.
/// </summary>
public sealed record CatalogItem(
    [property: JsonPropertyName("host_id")] Guid HostId,
    [property: JsonPropertyName("label")] string? Label,
    [property: JsonPropertyName("region")] string? Region,
    [property: JsonPropertyName("images")] IReadOnlyList<CatalogImage> Images,
    [property: JsonPropertyName("online")] bool Online,
    [property: JsonPropertyName("isolation_levels")] IReadOnlyList<string> IsolationLevels,
    [property: JsonPropertyName("default_isolation")] string DefaultIsolation,
    [property: JsonPropertyName("gpu_classes")] IReadOnlyList<string> GpuClasses,
    [property: JsonPropertyName("gpu_count")] int GpuCount,
    [property: JsonPropertyName("os")] string? Os = null,
    [property: JsonPropertyName("at_capacity")] bool AtCapacity = false,
    [property: JsonPropertyName("active_leases")] int? ActiveLeases = null,
    [property: JsonPropertyName("max_leases")] int? MaxLeases = null)
{
    /// <summary>
    /// Projects a host plus its already-filtered priced images into the catalog wire shape. <paramref
    /// name="activeLeases"/>/<paramref name="maxLeases"/> carry the host's live admission state (task #571):
    /// pass both when the host advertises a positive contract ceiling, or leave them null for an unlimited/offline
    /// host — <see cref="CatalogCapacity.Badge"/> folds them into the wire fields with the tolerant-optional rule.
    /// </summary>
    public static CatalogItem From(
        Host host, IReadOnlyList<CatalogImage> images, bool online, string? os = null,
        int? activeLeases = null, int? maxLeases = null)
    {
        // Surface the persisted advertised isolation, normalized so a legacy row that stored nothing still
        // reads as ["shared"]/"shared" — a consumer can filter on the level without a separate fetch (task #417).
        var (levels, defaultIsolation) = HostIsolation.Normalize(host.IsolationLevels, host.DefaultIsolation);
        var (atCapacity, active, max) = CatalogCapacity.Badge(activeLeases, maxLeases);
        return new(
            HostId: host.Id,
            Label: host.Name,
            Region: host.Label,
            Images: images,
            Online: online,
            IsolationLevels: levels,
            DefaultIsolation: defaultIsolation,
            // Mirror the host's advertised GPU onto the catalog entry (task #523); normalized so a legacy row
            // still reads as an empty class list / 0 count, matching the host-detail surfacing.
            GpuClasses: HostGpu.NormalizeClasses(host.GpuClasses),
            GpuCount: Math.Max(0, host.GpuCount),
            Os: os,
            AtCapacity: atCapacity,
            ActiveLeases: active,
            MaxLeases: max);
    }
}

/// <summary>Shared capacity-badge helpers for the catalog surfaces (task #571).</summary>
public static class CatalogCapacity
{
    /// <summary>
    /// Folds a host's live admission state into the tolerant wire fields (task #571). Only a positive
    /// <paramref name="maxLeases"/> ceiling produces counts and can flip <c>at_capacity</c>; a host with no
    /// advertised ceiling (or offline, both passing a null/non-positive max) is never at capacity and omits the
    /// counts (null), matching the discipline the frontend relies on. A null <paramref name="activeLeases"/>
    /// alongside a positive ceiling reads as 0 active.
    /// </summary>
    public static (bool AtCapacity, int? ActiveLeases, int? MaxLeases) Badge(int? activeLeases, int? maxLeases)
    {
        if (maxLeases is not { } max || max <= 0)
        {
            return (false, null, null);
        }

        var active = Math.Max(0, activeLeases ?? 0);
        return (active >= max, active, max);
    }
}

/// <summary>
/// A page of catalog entries (docs/API.md §10): the <c>data</c> array plus the opaque
/// <c>next_cursor</c> (null at the end of the listing).
/// </summary>
public sealed record CatalogPage(
    [property: JsonPropertyName("data")] IReadOnlyList<CatalogItem> Data,
    [property: JsonPropertyName("next_cursor")] string? NextCursor);

/// <summary>
/// The public detail of one host (docs/API.md §5, <c>GET /v1/hosts/:id</c>): the same public
/// identity as a catalog entry plus its full priced, enabled image list and per-image limits.
/// <c>os</c> is the host's advertised container OS (<c>"linux"</c> | <c>"windows"</c>), or <c>null</c>
/// when the host is offline or its (older) agent advertised none — surfacing only, back-compatible.
/// <c>at_capacity</c>/<c>active_leases</c>/<c>max_leases</c> mirror the catalog entry's admission badge
/// (task #571): the counts appear only when the host advertises a concurrent-contract ceiling, and an
/// unlimited/offline host is never at capacity and omits them (null).
/// </summary>
public sealed record HostDetail(
    [property: JsonPropertyName("host_id")] Guid HostId,
    [property: JsonPropertyName("label")] string? Label,
    [property: JsonPropertyName("region")] string? Region,
    [property: JsonPropertyName("online")] bool Online,
    [property: JsonPropertyName("images")] IReadOnlyList<CatalogImage> Images,
    [property: JsonPropertyName("isolation_levels")] IReadOnlyList<string> IsolationLevels,
    [property: JsonPropertyName("default_isolation")] string DefaultIsolation,
    [property: JsonPropertyName("gpu_classes")] IReadOnlyList<string> GpuClasses,
    [property: JsonPropertyName("gpu_count")] int GpuCount,
    [property: JsonPropertyName("os")] string? Os = null,
    [property: JsonPropertyName("at_capacity")] bool AtCapacity = false,
    [property: JsonPropertyName("active_leases")] int? ActiveLeases = null,
    [property: JsonPropertyName("max_leases")] int? MaxLeases = null)
{
    /// <summary>
    /// Projects a host plus its enabled priced images into the host-detail wire shape. <paramref
    /// name="activeLeases"/>/<paramref name="maxLeases"/> carry the host's live admission state (task #571),
    /// folded into the tolerant wire fields by <see cref="CatalogCapacity.Badge"/> exactly as the catalog entry.
    /// </summary>
    public static HostDetail From(
        Host host, IReadOnlyList<CatalogImage> images, bool online, string? os = null,
        int? activeLeases = null, int? maxLeases = null)
    {
        var (levels, defaultIsolation) = HostIsolation.Normalize(host.IsolationLevels, host.DefaultIsolation);
        var (atCapacity, active, max) = CatalogCapacity.Badge(activeLeases, maxLeases);
        return new(
            HostId: host.Id,
            Label: host.Name,
            Region: host.Label,
            Online: online,
            Images: images,
            IsolationLevels: levels,
            DefaultIsolation: defaultIsolation,
            // The advertised GPU, surfaced read-only (task #521); catalog-list surfacing + filters land in
            // task #523. Normalized so a legacy row still reads as an empty class list.
            GpuClasses: HostGpu.NormalizeClasses(host.GpuClasses),
            GpuCount: Math.Max(0, host.GpuCount),
            Os: os,
            AtCapacity: atCapacity,
            ActiveLeases: active,
            MaxLeases: max);
    }
}
