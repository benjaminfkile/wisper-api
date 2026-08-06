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
    private readonly IHostCapabilitySource _capabilities;

    public CatalogService(
        IHostRepository hosts,
        IHostImageRepository images,
        IHostRegistry registry,
        IHostCapabilitySource capabilities)
    {
        _hosts = hosts;
        _images = images;
        _registry = registry;
        _capabilities = capabilities;
    }

    public async Task<CatalogPage> ListAsync(CatalogQuery query, CancellationToken ct = default)
    {
        // Candidate set = DB-online hosts, re-confirmed against the live tunnel registry so a stale
        // 'online' row with no live tunnel never appears. Ordered by the stable descending page key.
        var candidates = await _hosts.ListOnlineAsync(ct);
        var ordered = candidates
            .Where(IsLive)
            .Where(h => MatchesGpuClass(h, query.GpuClass))
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

            page.Add(CatalogItem.From(host, images, online: true, os: OsOf(host.Id)));
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
        return HostDetail.From(host, wire, online: IsLive(host), os: OsOf(host.Id));
    }

    /// <summary>
    /// The host's advertised container OS from its live capability snapshot (docs/TUNNEL.md §5), or null
    /// when it has no live tunnel or its agent advertised none — surfacing only, always null-safe (task #316).
    /// </summary>
    private string? OsOf(Guid hostId) => _capabilities.GetCapability(hostId)?.Os;

    /// <summary>
    /// The host's enabled priced images that pass the request's image/network/price/min_gpus filters.
    /// The <c>min_gpus</c> floor is per-offer (an offer's <see cref="HostImage.MaxGpus"/> ceiling), so an
    /// offer with a <c>0</c> ceiling is excluded whenever any GPU is required (task #523).
    /// </summary>
    private async Task<IReadOnlyList<CatalogImage>> MatchingImagesAsync(
        Guid hostId, CatalogQuery query, CancellationToken ct)
    {
        var images = await _images.ListByHostAsync(hostId, enabledOnly: true, ct);
        return images
            .Where(i => query.ImageRef is null || string.Equals(i.ImageRef, query.ImageRef, StringComparison.Ordinal))
            .Where(i => query.Network is not { } n || i.Networks.Contains(n))
            .Where(i => query.MaxPriceCentsPerMin is not { } max || i.PriceCentsPerMin <= max)
            .Where(i => query.MinGpus is not { } min || i.MaxGpus >= min)
            .Select(CatalogImage.From)
            .ToList();
    }

    /// <summary>
    /// True when the host advertises the requested opaque GPU class (exact ordinal match against its
    /// persisted <c>gpu_classes</c>), or when no class filter is set — a host-level filter, mirroring the
    /// stored capability without interpreting it (task #523).
    /// </summary>
    private static bool MatchesGpuClass(Host host, string? gpuClass) =>
        gpuClass is null || host.GpuClasses.Contains(gpuClass, StringComparer.Ordinal);

    /// <summary>True when the host has a live agent tunnel in the registry (authoritative presence).</summary>
    private bool IsLive(Host host) => _registry.TryGet(host.Id.ToString(), out _);

    /// <summary>True when <paramref name="host"/> sorts strictly after <paramref name="cursor"/> in page order.</summary>
    private static bool After(Host host, CatalogCursor? cursor) =>
        cursor is null || CatalogCursor.Compare(host.CreatedAt, host.Id, cursor.CreatedAt, cursor.Id) > 0;

    /// <summary>The stable descending page order — newest <c>created_at</c> first, ties broken by larger id.</summary>
    private static readonly IComparer<Host> HostPageOrder =
        Comparer<Host>.Create((a, b) => CatalogCursor.Compare(a.CreatedAt, a.Id, b.CreatedAt, b.Id));
}
