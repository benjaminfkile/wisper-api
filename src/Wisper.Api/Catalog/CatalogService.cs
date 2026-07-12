using Wisper.Api.Domain;
using Wisper.Api.Persistence.HostImages;
using Wisper.Api.Persistence.Hosts;
using Wisper.Api.Tunnel;
using Host = Wisper.Api.Domain.Host;

namespace Wisper.Api.Catalog;

/// <summary>
/// Default <see cref="ICatalogService"/>: joins the persisted <c>host_images</c> allow-list
/// (docs/DATA_MODEL.md §4) with the live tunnel registry (docs/TUNNEL.md §3) to produce the consumer
/// catalog (docs/API.md §5). A host is only catalogued while its agent tunnel is live in the
/// <see cref="IHostRegistry"/> — the registry is authoritative for presence, so the DB online subset is
/// re-confirmed against it. Hosts are ordered by the stable descending <c>(created_at, id)</c> key and
/// paginated with an opaque cursor (§10); only priced, <b>enabled</b> images that survive the
/// requested filters are emitted, and a host contributing no such image is dropped from the page.
/// </summary>
public sealed class CatalogService : ICatalogService
{
    private readonly IHostRepository _hosts;
    private readonly IHostImageRepository _images;
    private readonly IHostRegistry _registry;

    public CatalogService(IHostRepository hosts, IHostImageRepository images, IHostRegistry registry)
    {
        _hosts = hosts;
        _images = images;
        _registry = registry;
    }

    public async Task<CatalogPage> ListAsync(CatalogQuery query, CancellationToken ct = default)
    {
        // Candidate set = DB-online hosts, re-confirmed against the live tunnel registry so a stale
        // 'online' row with no live tunnel never appears. Ordered by the stable descending page key.
        var candidates = await _hosts.ListOnlineAsync(ct);
        var ordered = candidates
            .Where(IsLive)
            .Where(h => After(h, query.Cursor))
            .OrderBy(h => h, HostPageOrder)
            .ToList();

        // Collect up to limit+1 *qualifying* hosts (those with ≥1 matching image); the extra one only
        // tells us whether another page exists and never ships in the response.
        var page = new List<CatalogItem>(query.Limit);
        Host? lastIncluded = null;
        var more = false;
        foreach (var host in ordered)
        {
            var images = await MatchingImagesAsync(host.Id, query, ct);
            if (images.Count == 0)
            {
                continue;
            }

            if (page.Count == query.Limit)
            {
                more = true;
                break;
            }

            page.Add(CatalogItem.From(host, images, online: true));
            lastIncluded = host;
        }

        var nextCursor = more && lastIncluded is not null
            ? new CatalogCursor(lastIncluded.CreatedAt, lastIncluded.Id).Encode()
            : null;
        return new CatalogPage(page, nextCursor);
    }

    public async Task<HostDetail?> GetHostAsync(Guid hostId, CancellationToken ct = default)
    {
        // Suspended (and missing) hosts are not catalog-visible; a consumer never learns they exist
        // (docs/API.md §3 — ownership/visibility failures return 404, not 403).
        if (await _hosts.GetByIdAsync(hostId, ct) is not { } host || host.Status == HostStatus.Suspended)
        {
            return null;
        }

        var images = await _images.ListByHostAsync(hostId, enabledOnly: true, ct);
        var wire = images.Select(CatalogImage.From).ToList();
        return HostDetail.From(host, wire, online: IsLive(host));
    }

    /// <summary>The host's enabled priced images that pass the request's image/network/price filters.</summary>
    private async Task<IReadOnlyList<CatalogImage>> MatchingImagesAsync(
        Guid hostId, CatalogQuery query, CancellationToken ct)
    {
        var images = await _images.ListByHostAsync(hostId, enabledOnly: true, ct);
        return images
            .Where(i => query.ImageRef is null || string.Equals(i.ImageRef, query.ImageRef, StringComparison.Ordinal))
            .Where(i => query.Network is not { } n || i.Networks.Contains(n))
            .Where(i => query.MaxPriceCentsPerMin is not { } max || i.PriceCentsPerMin <= max)
            .Select(CatalogImage.From)
            .ToList();
    }

    /// <summary>True when the host has a live agent tunnel in the registry (authoritative presence).</summary>
    private bool IsLive(Host host) => _registry.TryGet(host.Id.ToString(), out _);

    /// <summary>True when <paramref name="host"/> sorts strictly after <paramref name="cursor"/> in page order.</summary>
    private static bool After(Host host, CatalogCursor? cursor) =>
        cursor is null || CatalogCursor.Compare(host.CreatedAt, host.Id, cursor.CreatedAt, cursor.Id) > 0;

    /// <summary>The stable descending page order — newest <c>created_at</c> first, ties broken by larger id.</summary>
    private static readonly IComparer<Host> HostPageOrder =
        Comparer<Host>.Create((a, b) => CatalogCursor.Compare(a.CreatedAt, a.Id, b.CreatedAt, b.Id));
}
