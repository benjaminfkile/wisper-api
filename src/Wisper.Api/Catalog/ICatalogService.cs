using Wisper.Api.Domain;

namespace Wisper.Api.Catalog;

/// <summary>
/// The validated query for a <c>GET /v1/catalog</c> listing (docs/API.md §5, §10): the documented
/// filters (all optional) plus the page size and decoded cursor. Filter/cursor parsing and
/// validation happen at the endpoint edge; the service consumes this already-typed shape.
/// </summary>
public sealed record CatalogQuery
{
    /// <summary>Exact <c>image_ref</c> to match, or null for no image filter.</summary>
    public string? ImageRef { get; init; }

    /// <summary>Network mode the image must permit, or null for no network filter.</summary>
    public NetworkMode? Network { get; init; }

    /// <summary>Inclusive ceiling on <c>price_cents_per_min</c>, or null for no price filter.</summary>
    public long? MaxPriceCentsPerMin { get; init; }

    /// <summary>Page size, already clamped to <c>[1, 100]</c> (default 25).</summary>
    public int Limit { get; init; } = 25;

    /// <summary>Decoded pagination cursor, or null for the first page.</summary>
    public CatalogCursor? Cursor { get; init; }
}

/// <summary>
/// Reads the consumer catalog (docs/API.md §5): online hosts and their priced, enabled images. The
/// online set is the join of the persisted <c>host_images</c> allow-list with the <b>live</b> tunnel
/// registry (docs/TUNNEL.md §3) — a host whose row still reads online but whose agent tunnel has
/// dropped is excluded, because the registry is authoritative for presence.
/// </summary>
public interface ICatalogService
{
    /// <summary>Lists one page of online hosts and their filtered priced images (§5, §10).</summary>
    Task<CatalogPage> ListAsync(CatalogQuery query, CancellationToken ct = default);

    /// <summary>
    /// The public detail of one host — identity, live online state, and enabled priced images — or
    /// <c>null</c> when there is no such catalog-visible host (surfaced as <c>404 not_found</c>).
    /// </summary>
    Task<HostDetail?> GetHostAsync(Guid hostId, CancellationToken ct = default);
}
