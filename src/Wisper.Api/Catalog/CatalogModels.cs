using System.Text.Json.Serialization;
using Wisper.Api.Domain;
using Host = Wisper.Api.Domain.Host;

namespace Wisper.Api.Catalog;

/// <summary>
/// One priced, enabled image as it appears in the consumer catalog (docs/API.md §5). It flattens a
/// <see cref="HostImage"/> to its public, lease-relevant fields: the id a lease references, the image
/// ref, the price snapshot the lease will take, and the ceilings/limits the host offers.
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
    [property: JsonPropertyName("max_pids")] int? MaxPids)
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
        MaxPids: image.MaxPids);
}

/// <summary>
/// One catalog entry (docs/API.md §5): an online host and its priced, enabled images. The wire
/// <c>label</c> carries the host's display name and <c>region</c> its region/label
/// (docs/DATA_MODEL.md §4 — <c>hosts.name</c> is the display, <c>hosts.label</c> the region). Only
/// hosts confirmed online by the live tunnel registry are emitted, so <c>online</c> is always true.
/// <c>os</c> carries the host's advertised container OS (<c>"linux"</c> | <c>"windows"</c>, mirroring
/// the wisp <c>/images</c> document), or <c>null</c> when the live capability has none — a client can
/// adapt to a Windows host without a separate fetch, and an older agent that omits it never errors.
/// </summary>
public sealed record CatalogItem(
    [property: JsonPropertyName("host_id")] Guid HostId,
    [property: JsonPropertyName("label")] string? Label,
    [property: JsonPropertyName("region")] string? Region,
    [property: JsonPropertyName("images")] IReadOnlyList<CatalogImage> Images,
    [property: JsonPropertyName("online")] bool Online,
    [property: JsonPropertyName("os")] string? Os = null)
{
    /// <summary>Projects a host plus its already-filtered priced images into the catalog wire shape.</summary>
    public static CatalogItem From(Host host, IReadOnlyList<CatalogImage> images, bool online, string? os = null) => new(
        HostId: host.Id,
        Label: host.Name,
        Region: host.Label,
        Images: images,
        Online: online,
        Os: os);
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
/// </summary>
public sealed record HostDetail(
    [property: JsonPropertyName("host_id")] Guid HostId,
    [property: JsonPropertyName("label")] string? Label,
    [property: JsonPropertyName("region")] string? Region,
    [property: JsonPropertyName("online")] bool Online,
    [property: JsonPropertyName("images")] IReadOnlyList<CatalogImage> Images,
    [property: JsonPropertyName("os")] string? Os = null)
{
    /// <summary>Projects a host plus its enabled priced images into the host-detail wire shape.</summary>
    public static HostDetail From(Host host, IReadOnlyList<CatalogImage> images, bool online, string? os = null) => new(
        HostId: host.Id,
        Label: host.Name,
        Region: host.Label,
        Online: online,
        Images: images,
        Os: os);
}
