using Wisper.Api.Domain;
using Wisper.Api.Persistence.HostImages;
using Wisper.Api.Persistence.Hosts;
using Wisper.Api.Persistence.Leases;
using Wisper.Api.Tunnel;
using Wisper.Api.Tunnel.Backplane;
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
    private readonly ILeaseRepository _leases;
    private readonly IHostPresenceStore _presenceStore;

    public CatalogService(
        IHostRepository hosts,
        IHostImageRepository images,
        IHostRegistry registry,
        IHostCapabilitySource capabilities,
        ILeaseRepository leases,
        IHostPresenceStore presenceStore)
    {
        _hosts = hosts;
        _images = images;
        _registry = registry;
        _capabilities = capabilities;
        _leases = leases;
        _presenceStore = presenceStore;
    }

    public async Task<CatalogPage> ListAsync(CatalogQuery query, CancellationToken ct = default)
    {
        // Candidate set = DB-online hosts, re-confirmed against the live tunnel for a stale-DB-row guard.
        // One presence-store snapshot covers all candidates (one Redis round-trip for N hosts); the local
        // registry is the fast path so a same-instance tunnel costs no I/O at all.
        var candidates = await _hosts.ListOnlineAsync(ct);
        var presenceSnapshot = await PresenceSnapshotAsync(ct);
        var ordered = candidates
            .Where(h => IsLive(h, presenceSnapshot))
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
            // One live capability read per host: its per-lease caps resolve each offer's effective profile
            // (task #578), its container OS surfaces on the entry (task #316), and its contract ceiling drives
            // the capacity badge (task #571).
            var capability = _capabilities.GetCapability(host.Id);
            var images = await MatchingImagesAsync(host.Id, query, capability, ct);
            if (images.Count == 0)
            {
                continue;
            }

            if (page.Count == query.Limit)
            {
                more = true;
                break;
            }

            var (active, max) = await CapacityOf(capability, host.Id, ct);
            page.Add(CatalogItem.From(host, images, online: true, os: capability?.Os, active, max));
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

        var capability = _capabilities.GetCapability(hostId);
        var images = await _images.ListByHostAsync(hostId, enabledOnly: true, ct);
        var wire = images.Select(i => CatalogImage.From(i, capability?.MaxCpus ?? 0, capability?.MaxMemoryMb ?? 0))
            .ToList();
        var (active, max) = await CapacityOf(capability, hostId, ct);
        var live = await IsLiveAsync(hostId, ct);
        return HostDetail.From(host, wire, online: live, os: capability?.Os, active, max);
    }

    /// <summary>
    /// The host's live admission state for the catalog badge (task #571): when its live capability advertises a
    /// positive concurrent-contract ceiling, returns (its non-terminal lease count, that ceiling); otherwise
    /// (offline or unlimited) returns (null, null) so the surfaces omit the counts and never badge it full. The
    /// per-host lease count is only queried when there is a ceiling to compare against — an unlimited host costs
    /// no extra read.
    /// </summary>
    private async Task<(int? Active, int? Max)> CapacityOf(
        HostCapabilitySnapshot? capability, Guid hostId, CancellationToken ct)
    {
        if (capability is not { HasContractLimit: true })
        {
            return (null, null);
        }

        var active = await _leases.CountActiveByHostAsync(hostId, ct);
        return (active, capability.MaxContracts);
    }

    /// <summary>
    /// The host's enabled priced images that pass the request's image/network/price/min_gpus filters, each
    /// projected with the host's live per-lease caps so its effective resource profile resolves (task #578).
    /// The <c>min_gpus</c> floor is per-offer (an offer's exact <see cref="HostImage.Gpus"/> count, task #569),
    /// so an offer that provisions <c>0</c> GPUs is excluded whenever any GPU is required (task #523).
    /// </summary>
    private async Task<IReadOnlyList<CatalogImage>> MatchingImagesAsync(
        Guid hostId, CatalogQuery query, HostCapabilitySnapshot? capability, CancellationToken ct)
    {
        var images = await _images.ListByHostAsync(hostId, enabledOnly: true, ct);
        return images
            .Where(i => query.ImageRef is null || string.Equals(i.ImageRef, query.ImageRef, StringComparison.Ordinal))
            .Where(i => query.Network is not { } n || i.Networks.Contains(n))
            .Where(i => query.MaxPriceCentsPerMin is not { } max || i.PriceCentsPerMin <= max)
            .Where(i => query.MinGpus is not { } min || i.Gpus >= min)
            .Select(i => CatalogImage.From(i, capability?.MaxCpus ?? 0, capability?.MaxMemoryMb ?? 0))
            .ToList();
    }

    /// <summary>
    /// True when the host advertises the requested opaque GPU class (exact ordinal match against its
    /// persisted <c>gpu_classes</c>), or when no class filter is set — a host-level filter, mirroring the
    /// stored capability without interpreting it (task #523).
    /// </summary>
    private static bool MatchesGpuClass(Host host, string? gpuClass) =>
        gpuClass is null || host.GpuClasses.Contains(gpuClass, StringComparer.Ordinal);

    /// <summary>
    /// True when the host has a live tunnel: fast path is the local registry (no I/O); fallback is the
    /// distributed presence snapshot so a tunnel owned by another instance is still considered live.
    /// </summary>
    private bool IsLive(Host host, HashSet<string> presenceSnapshot)
    {
        var id = host.Id.ToString();
        return _registry.TryGet(id, out _) || presenceSnapshot.Contains(id);
    }

    /// <summary>Single-host async liveness for GetHostAsync: local registry fast path, then presence store.</summary>
    private async Task<bool> IsLiveAsync(Guid hostId, CancellationToken ct)
    {
        var id = hostId.ToString();
        return _registry.TryGet(id, out _) || await _presenceStore.GetOwnerAsync(id, ct) is not null;
    }

    /// <summary>One presence-store snapshot so ListAsync needs only one round-trip for all candidates.</summary>
    private async Task<HashSet<string>> PresenceSnapshotAsync(CancellationToken ct)
    {
        var snapshot = await _presenceStore.SnapshotAsync(ct);
        return snapshot.Select(p => p.HostId).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>True when <paramref name="host"/> sorts strictly after <paramref name="cursor"/> in page order.</summary>
    private static bool After(Host host, CatalogCursor? cursor) =>
        cursor is null || CatalogCursor.Compare(host.CreatedAt, host.Id, cursor.CreatedAt, cursor.Id) > 0;

    /// <summary>The stable descending page order — newest <c>created_at</c> first, ties broken by larger id.</summary>
    private static readonly IComparer<Host> HostPageOrder =
        Comparer<Host>.Create((a, b) => CatalogCursor.Compare(a.CreatedAt, a.Id, b.CreatedAt, b.Id));
}
