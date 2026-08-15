using Wisper.Api.Catalog;
using Wisper.Api.Domain;
using Wisper.Api.Persistence.HostImages;
using Wisper.Api.Persistence.Hosts;
using Wisper.Api.Persistence.Leases;
using Wisper.Api.Tests.TestSupport;
using Wisper.Api.Tunnel;
using Wisper.Api.Tunnel.Backplane;
using Xunit;
using Host = Wisper.Api.Domain.Host;

namespace Wisper.Api.Tests.Catalog;

/// <summary>
/// Unit tests for <see cref="CatalogService"/> (docs/API.md §5): the join of the persisted
/// <c>host_images</c> allow-list with the live tunnel registry, the documented
/// <c>image</c>/<c>network</c>/<c>max_price_cents_per_min</c> filters, cursor pagination (§10), and the
/// <c>GET /v1/hosts/:id</c> public detail. Everything runs against the in-memory repositories and a
/// fake registry (Grunt has no Postgres/tunnel).
/// </summary>
public class CatalogServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed class Harness
    {
        public InMemoryHostRepository Hosts { get; } = new();
        public InMemoryHostImageRepository Images { get; } = new();
        public FakeHostRegistry Registry { get; } = new();
        public FakeHostCapabilitySource Capabilities { get; } = new();
        public InMemoryLeaseRepository Leases { get; } = new();
        public InMemoryHostPresenceStore PresenceStore { get; } = new();
        public InMemoryHostDegradedStore DegradedStore { get; } = new();
        public CatalogService Service =>
            new(Hosts, Images, Registry, Capabilities, Leases, PresenceStore, DegradedStore);

        public async Task<Host> AddHostAsync(
            string name, string region, DateTimeOffset createdAt,
            HostStatus status = HostStatus.Online, bool online = true, Guid? id = null, string? os = null,
            IReadOnlyList<string>? isolationLevels = null, string? defaultIsolation = null,
            IReadOnlyList<string>? gpuClasses = null, int gpuCount = 0, int maxContracts = 0)
        {
            var host = await Hosts.CreateAsync(new Host
            {
                Id = id ?? Guid.NewGuid(),
                OwnerUserId = Guid.NewGuid(),
                Name = name,
                Label = region,
                Status = status,
                AgentTokenHash = "hash",
                IsolationLevels = isolationLevels ?? HostIsolation.SharedOnly,
                DefaultIsolation = defaultIsolation ?? HostIsolation.Shared,
                GpuClasses = gpuClasses ?? HostGpu.NoClasses,
                GpuCount = gpuCount,
                CreatedAt = createdAt,
                UpdatedAt = createdAt,
            });
            if (online)
            {
                Registry.SetOnline(host.Id);
            }

            // A live host advertises a wisp capability; declare it (optionally carrying the container OS and a
            // concurrent-contract ceiling) so the catalog surfaces `os`/`at_capacity` from the live snapshot the
            // way the real source does. maxContracts=0 = no advertised ceiling (unlimited).
            if (online)
            {
                Capabilities.Set(host.Id, new HostCapabilitySnapshot(
                    Array.Empty<string>(), Array.Empty<NetworkMode>(),
                    MaxTtlSeconds: 3600, MaxCpus: 4, MaxMemoryMb: 8192, MaxPids: 1024, Os: os,
                    MaxContracts: maxContracts));
            }

            return host;
        }

        /// <summary>Seeds <paramref name="count"/> live (active) leases on <paramref name="hostId"/>.</summary>
        public async Task SeedActiveLeasesAsync(Guid hostId, int count)
        {
            for (var i = 0; i < count; i++)
            {
                await Leases.CreateAsync(new Lease
                {
                    Id = Guid.NewGuid(),
                    ConsumerUserId = Guid.NewGuid(),
                    HostId = hostId,
                    HostImageId = Guid.NewGuid(),
                    ImageRef = "reg/wisp-base:latest",
                    Network = NetworkMode.Open,
                    TtlSeconds = 3600,
                    PriceCentsPerMin = 5,
                    Currency = "usd",
                    Status = LeaseStatus.Active,
                    WispContractId = $"wc-{i}",
                    CreatedAt = T0,
                    StartedAt = T0,
                    LastMeteredAt = T0,
                });
            }
        }

        public Task<HostImage> AddImageAsync(
            Guid hostId, string imageRef, long price, bool enabled = true,
            NetworkMode[]? networks = null, int maxGpus = 0, int? cpus = null, int? memoryMb = null) =>
            Images.CreateAsync(new HostImage
            {
                HostId = hostId,
                ImageRef = imageRef,
                PriceCentsPerMin = price,
                Networks = networks ?? new[] { NetworkMode.None, NetworkMode.Open },
                MaxTtlSeconds = 3600,
                MaxCpus = 4,
                MaxMemoryMb = 8192,
                MaxPids = 1024,
                Cpus = cpus,
                MemoryMb = memoryMb,
                Gpus = maxGpus,
                Enabled = enabled,
                CreatedAt = T0,
                UpdatedAt = T0,
            });
    }

    [Fact]
    public async Task Lists_online_hosts_with_their_enabled_priced_images()
    {
        var h = new Harness();
        var host = await h.AddHostAsync("home-server-1", "us", T0);
        await h.AddImageAsync(host.Id, "reg/wisp-base:latest", price: 5);

        var page = await h.Service.ListAsync(new CatalogQuery());

        var item = Assert.Single(page.Data);
        Assert.Equal(host.Id, item.HostId);
        Assert.Equal("home-server-1", item.Label);
        Assert.Equal("us", item.Region);
        Assert.True(item.Online);
        var image = Assert.Single(item.Images);
        Assert.Equal("reg/wisp-base:latest", image.ImageRef);
        Assert.Equal(5, image.PriceCentsPerMin);
        Assert.Equal("usd", image.Currency);
        Assert.Equal(new[] { "none", "open" }, image.Networks);
        Assert.Equal(3600, image.MaxTtlSeconds);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task Excludes_disabled_images_and_hosts_left_with_none()
    {
        var h = new Harness();
        var host = await h.AddHostAsync("h", "us", T0);
        await h.AddImageAsync(host.Id, "only:disabled", price: 5, enabled: false);

        var page = await h.Service.ListAsync(new CatalogQuery());

        Assert.Empty(page.Data);
    }

    [Fact]
    public async Task Excludes_a_host_whose_row_is_online_but_tunnel_is_dead()
    {
        var h = new Harness();
        // status='online' in the DB, but never registered a live tunnel — registry is authoritative.
        var host = await h.AddHostAsync("stale", "us", T0, online: false);
        await h.AddImageAsync(host.Id, "img:1", price: 5);

        var page = await h.Service.ListAsync(new CatalogQuery());

        Assert.Empty(page.Data);
    }

    [Fact]
    public async Task Excludes_offline_status_hosts_even_when_a_stray_tunnel_is_live()
    {
        var h = new Harness();
        // Not in the DB online subset (status=offline); it should never be catalogued.
        var host = await h.AddHostAsync("offline", "us", T0, status: HostStatus.Offline, online: true);
        await h.AddImageAsync(host.Id, "img:1", price: 5);

        var page = await h.Service.ListAsync(new CatalogQuery());

        Assert.Empty(page.Data);
    }

    [Fact]
    public async Task Filters_by_exact_image_ref()
    {
        var h = new Harness();
        var host = await h.AddHostAsync("h", "us", T0);
        await h.AddImageAsync(host.Id, "reg/a:latest", price: 5);
        await h.AddImageAsync(host.Id, "reg/b:latest", price: 5);

        var page = await h.Service.ListAsync(new CatalogQuery { ImageRef = "reg/b:latest" });

        var item = Assert.Single(page.Data);
        var image = Assert.Single(item.Images);
        Assert.Equal("reg/b:latest", image.ImageRef);
    }

    [Fact]
    public async Task Filters_by_network_mode()
    {
        var h = new Harness();
        var host = await h.AddHostAsync("h", "us", T0);
        await h.AddImageAsync(host.Id, "open:img", price: 5, networks: new[] { NetworkMode.Open });
        await h.AddImageAsync(host.Id, "none:img", price: 5, networks: new[] { NetworkMode.None });

        var page = await h.Service.ListAsync(new CatalogQuery { Network = NetworkMode.Open });

        var item = Assert.Single(page.Data);
        var image = Assert.Single(item.Images);
        Assert.Equal("open:img", image.ImageRef);
    }

    [Fact]
    public async Task Filters_by_max_price()
    {
        var h = new Harness();
        var host = await h.AddHostAsync("h", "us", T0);
        await h.AddImageAsync(host.Id, "cheap", price: 3);
        await h.AddImageAsync(host.Id, "pricey", price: 9);

        var page = await h.Service.ListAsync(new CatalogQuery { MaxPriceCentsPerMin = 5 });

        var item = Assert.Single(page.Data);
        var image = Assert.Single(item.Images);
        Assert.Equal("cheap", image.ImageRef);
    }

    [Fact]
    public async Task Filters_by_min_gpus_excluding_zero_ceiling_offers()
    {
        var h = new Harness();
        var host = await h.AddHostAsync("h", "us", T0);
        await h.AddImageAsync(host.Id, "cpu-only", price: 5, maxGpus: 0);
        await h.AddImageAsync(host.Id, "one-gpu", price: 5, maxGpus: 1);
        await h.AddImageAsync(host.Id, "four-gpu", price: 5, maxGpus: 4);

        var page = await h.Service.ListAsync(new CatalogQuery { MinGpus = 1 });

        var item = Assert.Single(page.Data);
        // The 0-ceiling offer is excluded; the two GPU offers survive (order-independent).
        Assert.Equal(
            new[] { "four-gpu", "one-gpu" },
            item.Images.Select(i => i.ImageRef).OrderBy(r => r));
    }

    [Fact]
    public async Task Min_gpus_includes_an_offer_whose_ceiling_equals_the_floor()
    {
        var h = new Harness();
        var host = await h.AddHostAsync("h", "us", T0);
        await h.AddImageAsync(host.Id, "two-gpu", price: 5, maxGpus: 2);
        await h.AddImageAsync(host.Id, "one-gpu", price: 5, maxGpus: 1);

        var page = await h.Service.ListAsync(new CatalogQuery { MinGpus = 2 });

        var item = Assert.Single(page.Data);
        // Boundary is inclusive: gpus == min_gpus qualifies; the smaller ceiling drops out.
        var image = Assert.Single(item.Images);
        Assert.Equal("two-gpu", image.ImageRef);
    }

    [Fact]
    public async Task Filters_by_exact_gpu_class_against_a_multi_class_host()
    {
        var h = new Harness();
        var match = await h.AddHostAsync("a100-box", "us", T0.AddMinutes(1),
            gpuClasses: new[] { "nvidia-a100", "nvidia-t4" }, gpuCount: 3);
        var other = await h.AddHostAsync("t4-box", "us", T0,
            gpuClasses: new[] { "nvidia-t4" }, gpuCount: 1);
        await h.AddImageAsync(match.Id, "img", price: 5, maxGpus: 2);
        await h.AddImageAsync(other.Id, "img", price: 5, maxGpus: 1);

        var page = await h.Service.ListAsync(new CatalogQuery { GpuClass = "nvidia-a100" });

        // Only the host advertising the exact class survives; a partial/substring never matches.
        var item = Assert.Single(page.Data);
        Assert.Equal(match.Id, item.HostId);
    }

    [Fact]
    public async Task Gpu_filters_compose_with_price_and_network()
    {
        var h = new Harness();
        var host = await h.AddHostAsync("gpu-host", "us", T0,
            gpuClasses: new[] { "nvidia-a100" }, gpuCount: 4);
        // Only this offer clears every filter: right class host, GPU ceiling ≥ 2, price ≤ 5, open network.
        await h.AddImageAsync(host.Id, "good", price: 5, networks: new[] { NetworkMode.Open }, maxGpus: 2);
        await h.AddImageAsync(host.Id, "too-pricey", price: 9, networks: new[] { NetworkMode.Open }, maxGpus: 2);
        await h.AddImageAsync(host.Id, "no-gpu", price: 5, networks: new[] { NetworkMode.Open }, maxGpus: 0);
        await h.AddImageAsync(host.Id, "wrong-net", price: 5, networks: new[] { NetworkMode.None }, maxGpus: 2);

        var page = await h.Service.ListAsync(new CatalogQuery
        {
            GpuClass = "nvidia-a100",
            MinGpus = 2,
            MaxPriceCentsPerMin = 5,
            Network = NetworkMode.Open,
        });

        var item = Assert.Single(page.Data);
        var image = Assert.Single(item.Images);
        Assert.Equal("good", image.ImageRef);
    }

    [Fact]
    public async Task Gpu_class_filter_drops_a_host_with_no_advertised_gpu()
    {
        var h = new Harness();
        var host = await h.AddHostAsync("legacy", "us", T0);
        await h.AddImageAsync(host.Id, "img", price: 5, maxGpus: 0);

        var page = await h.Service.ListAsync(new CatalogQuery { GpuClass = "nvidia-a100" });

        Assert.Empty(page.Data);
    }

    [Fact]
    public async Task Absent_gpu_filters_leave_results_unchanged()
    {
        var h = new Harness();
        var host = await h.AddHostAsync("h", "us", T0, gpuClasses: new[] { "nvidia-a100" }, gpuCount: 2);
        await h.AddImageAsync(host.Id, "cpu-only", price: 5, maxGpus: 0);
        await h.AddImageAsync(host.Id, "one-gpu", price: 5, maxGpus: 1);

        // No min_gpus / gpu_class set — every enabled priced image is returned, GPU ceilings ignored.
        var page = await h.Service.ListAsync(new CatalogQuery());

        var item = Assert.Single(page.Data);
        Assert.Equal(
            new[] { "cpu-only", "one-gpu" },
            item.Images.Select(i => i.ImageRef).OrderBy(r => r));
    }

    [Fact]
    public async Task List_surfaces_gpus_and_the_hosts_advertised_gpu()
    {
        var h = new Harness();
        var host = await h.AddHostAsync("gpu-host", "us", T0,
            gpuClasses: new[] { "nvidia-a100", "nvidia-t4" }, gpuCount: 3);
        await h.AddImageAsync(host.Id, "img", price: 5, maxGpus: 2);

        var item = Assert.Single((await h.Service.ListAsync(new CatalogQuery())).Data);

        Assert.Equal(new[] { "nvidia-a100", "nvidia-t4" }, item.GpuClasses);
        Assert.Equal(3, item.GpuCount);
        Assert.Equal(2, Assert.Single(item.Images).Gpus);
    }

    [Fact]
    public async Task List_surfaces_empty_gpu_for_a_host_that_advertises_none()
    {
        var h = new Harness();
        var host = await h.AddHostAsync("cpu-host", "us", T0);
        await h.AddImageAsync(host.Id, "img", price: 5);

        var item = Assert.Single((await h.Service.ListAsync(new CatalogQuery())).Data);

        Assert.Empty(item.GpuClasses);
        Assert.Equal(0, item.GpuCount);
        Assert.Equal(0, Assert.Single(item.Images).Gpus);
    }

    [Fact]
    public async Task List_carries_the_sized_cpu_and_memory_profile()
    {
        // The sized offer's fixed profile is surfaced on the catalog so a consumer can price the size (task #569).
        var h = new Harness();
        var host = await h.AddHostAsync("sized-host", "us", T0);
        await h.AddImageAsync(host.Id, "img", price: 5, cpus: 2, memoryMb: 4096);

        var item = Assert.Single((await h.Service.ListAsync(new CatalogQuery())).Data);

        var image = Assert.Single(item.Images);
        Assert.Equal(2, image.Cpus);
        Assert.Equal(4096, image.MemoryMb);
    }

    [Fact]
    public async Task List_resolves_a_sized_offer_to_its_own_values_source_offer()
    {
        // task #578: an offer that pins cpus/memory surfaces those as the effective profile — the offer beats
        // the host's per-lease cap (which the harness advertises at 4/8192), and the raw fields are kept as-is.
        var h = new Harness();
        var host = await h.AddHostAsync("sized-host", "us", T0);
        await h.AddImageAsync(host.Id, "img", price: 5, cpus: 2, memoryMb: 4096);

        var image = Assert.Single(Assert.Single((await h.Service.ListAsync(new CatalogQuery())).Data).Images);

        Assert.Equal(2, image.Cpus);            // raw, kept verbatim
        Assert.Equal(4096, image.MemoryMb);
        Assert.Equal(2m, image.EffectiveCpus);
        Assert.Equal(4096, image.EffectiveMemoryMb);
        Assert.Equal("offer", image.ResourcesSource);
    }

    [Fact]
    public async Task List_resolves_a_null_profile_offer_to_the_host_per_lease_cap_source_host_cap()
    {
        // task #578: the live UX bug — a NULL cpus/memory_mb offer used to render as "host default". It now
        // resolves the effective profile from the host's advertised per-lease cap (limits.max_cpus/memory_mb,
        // the harness's 4/8192) so the consumer never shops blind, while the raw fields stay null for the editor.
        var h = new Harness();
        var host = await h.AddHostAsync("cap-host", "us", T0);
        await h.AddImageAsync(host.Id, "img", price: 5, cpus: null, memoryMb: null);

        var image = Assert.Single(Assert.Single((await h.Service.ListAsync(new CatalogQuery())).Data).Images);

        Assert.Null(image.Cpus);                // raw NULL profile, unchanged
        Assert.Null(image.MemoryMb);
        Assert.Equal(4m, image.EffectiveCpus);  // resolved from the host per-lease cap
        Assert.Equal(8192, image.EffectiveMemoryMb);
        Assert.Equal("host_cap", image.ResourcesSource);
    }

    [Fact]
    public async Task List_leaves_a_null_profile_offer_unknown_when_the_host_advertises_no_cap()
    {
        // task #578: when neither the offer nor the host advertises a per-lease cap, the size is genuinely
        // unknown — effective values stay null and resources_source is "unknown" (an honest annotation).
        var h = new Harness();
        var host = await h.AddHostAsync("no-cap-host", "us", T0);
        // Overwrite the live capability with one that advertises no per-lease cpu/memory cap.
        h.Capabilities.Set(host.Id, new HostCapabilitySnapshot(
            Array.Empty<string>(), Array.Empty<NetworkMode>(),
            MaxTtlSeconds: 3600, MaxCpus: 0, MaxMemoryMb: 0, MaxPids: 0));
        await h.AddImageAsync(host.Id, "img", price: 5, cpus: null, memoryMb: null);

        var image = Assert.Single(Assert.Single((await h.Service.ListAsync(new CatalogQuery())).Data).Images);

        Assert.Null(image.EffectiveCpus);
        Assert.Null(image.EffectiveMemoryMb);
        Assert.Equal("unknown", image.ResourcesSource);
    }

    [Fact]
    public async Task Get_host_degrades_a_null_profile_offer_to_raw_values_when_offline()
    {
        // task #578: an offline host has no live capability, so there is no per-lease cap to fall back to. A
        // sized offer still surfaces its own values ("offer"); a NULL-profile offer degrades to unknown.
        var h = new Harness();
        var host = await h.AddHostAsync("offline-host", "us", T0, online: false);
        await h.AddImageAsync(host.Id, "sized", price: 5, cpus: 2, memoryMb: 4096);
        await h.AddImageAsync(host.Id, "unsized", price: 5, cpus: null, memoryMb: null);

        var detail = await h.Service.GetHostAsync(host.Id);

        Assert.NotNull(detail);
        Assert.False(detail!.Online);
        var sized = Assert.Single(detail.Images, i => i.ImageRef == "sized");
        Assert.Equal(2m, sized.EffectiveCpus);
        Assert.Equal(4096, sized.EffectiveMemoryMb);
        Assert.Equal("offer", sized.ResourcesSource);
        var unsized = Assert.Single(detail.Images, i => i.ImageRef == "unsized");
        Assert.Null(unsized.EffectiveCpus);
        Assert.Null(unsized.EffectiveMemoryMb);
        Assert.Equal("unknown", unsized.ResourcesSource);
    }

    [Fact]
    public async Task Get_host_surfaces_gpus_on_each_image()
    {
        var h = new Harness();
        var host = await h.AddHostAsync("gpu-detail", "eu", T0,
            gpuClasses: new[] { "nvidia-a100" }, gpuCount: 2);
        await h.AddImageAsync(host.Id, "img", price: 7, maxGpus: 2);

        var detail = await h.Service.GetHostAsync(host.Id);

        Assert.NotNull(detail);
        Assert.Equal(new[] { "nvidia-a100" }, detail!.GpuClasses);
        Assert.Equal(2, detail.GpuCount);
        Assert.Equal(2, Assert.Single(detail.Images).Gpus);
    }

    [Fact]
    public async Task List_surfaces_at_capacity_and_counts_for_a_limited_host_that_is_full()
    {
        // task #571: a host advertising max_contracts=2 that already runs 2 live leases badges at_capacity=true
        // and carries active_leases/max_leases so the frontend can render "full".
        var h = new Harness();
        var host = await h.AddHostAsync("full-host", "us", T0, maxContracts: 2);
        await h.AddImageAsync(host.Id, "reg/wisp-base:latest", price: 5);
        await h.SeedActiveLeasesAsync(host.Id, 2);

        var item = Assert.Single((await h.Service.ListAsync(new CatalogQuery())).Data);

        Assert.True(item.AtCapacity);
        Assert.Equal(2, item.ActiveLeases);
        Assert.Equal(2, item.MaxLeases);
    }

    [Fact]
    public async Task List_surfaces_below_capacity_counts_for_a_limited_host_with_headroom()
    {
        // A limited host below its ceiling carries the counts but is not at capacity.
        var h = new Harness();
        var host = await h.AddHostAsync("roomy-host", "us", T0, maxContracts: 4);
        await h.AddImageAsync(host.Id, "reg/wisp-base:latest", price: 5);
        await h.SeedActiveLeasesAsync(host.Id, 1);

        var item = Assert.Single((await h.Service.ListAsync(new CatalogQuery())).Data);

        Assert.False(item.AtCapacity);
        Assert.Equal(1, item.ActiveLeases);
        Assert.Equal(4, item.MaxLeases);
    }

    [Fact]
    public async Task List_omits_capacity_counts_for_an_unlimited_host()
    {
        // A host that advertises no ceiling (max_contracts=0) is never at capacity and omits the counts (null),
        // keeping the tolerant-optional-field discipline the frontend relies on — even with live leases running.
        var h = new Harness();
        var host = await h.AddHostAsync("unlimited-host", "us", T0, maxContracts: 0);
        await h.AddImageAsync(host.Id, "reg/wisp-base:latest", price: 5);
        await h.SeedActiveLeasesAsync(host.Id, 3);

        var item = Assert.Single((await h.Service.ListAsync(new CatalogQuery())).Data);

        Assert.False(item.AtCapacity);
        Assert.Null(item.ActiveLeases);
        Assert.Null(item.MaxLeases);
    }

    [Fact]
    public async Task Get_host_surfaces_at_capacity_and_counts_for_a_limited_host()
    {
        var h = new Harness();
        var host = await h.AddHostAsync("full-detail", "eu", T0, maxContracts: 1);
        await h.AddImageAsync(host.Id, "img", price: 7);
        await h.SeedActiveLeasesAsync(host.Id, 1);

        var detail = await h.Service.GetHostAsync(host.Id);

        Assert.NotNull(detail);
        Assert.True(detail!.AtCapacity);
        Assert.Equal(1, detail.ActiveLeases);
        Assert.Equal(1, detail.MaxLeases);
    }

    [Fact]
    public async Task Get_host_omits_capacity_counts_for_an_offline_host()
    {
        // An offline host has no live capability snapshot, so there is no advertised ceiling: never at capacity,
        // counts omitted (null) — the offline surfacing case.
        var h = new Harness();
        var host = await h.AddHostAsync("home", "eu", T0, status: HostStatus.Offline, online: false);
        await h.AddImageAsync(host.Id, "img", price: 7);
        await h.SeedActiveLeasesAsync(host.Id, 2);

        var detail = await h.Service.GetHostAsync(host.Id);

        Assert.NotNull(detail);
        Assert.False(detail!.Online);
        Assert.False(detail.AtCapacity);
        Assert.Null(detail.ActiveLeases);
        Assert.Null(detail.MaxLeases);
    }

    [Fact]
    public async Task Orders_hosts_newest_first_and_paginates_with_a_cursor()
    {
        var h = new Harness();
        // Created oldest→newest; the page should come back newest→oldest.
        var older = await h.AddHostAsync("older", "us", T0);
        var middle = await h.AddHostAsync("middle", "us", T0.AddMinutes(1));
        var newer = await h.AddHostAsync("newer", "us", T0.AddMinutes(2));
        foreach (var id in new[] { older.Id, middle.Id, newer.Id })
        {
            await h.AddImageAsync(id, "img", price: 5);
        }

        var first = await h.Service.ListAsync(new CatalogQuery { Limit = 2 });

        Assert.Equal(new[] { newer.Id, middle.Id }, first.Data.Select(d => d.HostId));
        Assert.NotNull(first.NextCursor);

        Assert.True(CatalogCursor.TryParse(first.NextCursor, out var cursor));
        var second = await h.Service.ListAsync(new CatalogQuery { Limit = 2, Cursor = cursor });

        var last = Assert.Single(second.Data);
        Assert.Equal(older.Id, last.HostId);
        Assert.Null(second.NextCursor);
    }

    [Fact]
    public async Task Next_cursor_is_null_when_remaining_hosts_have_no_matching_images()
    {
        var h = new Harness();
        var a = await h.AddHostAsync("a", "us", T0.AddMinutes(2));
        var b = await h.AddHostAsync("b", "us", T0.AddMinutes(1));
        var c = await h.AddHostAsync("c", "us", T0);
        await h.AddImageAsync(a.Id, "img", price: 5);
        await h.AddImageAsync(b.Id, "img", price: 5);
        // c has only a disabled image, so it never qualifies — the page of 2 is the whole listing.
        await h.AddImageAsync(c.Id, "img", price: 5, enabled: false);

        var page = await h.Service.ListAsync(new CatalogQuery { Limit = 2 });

        Assert.Equal(new[] { a.Id, b.Id }, page.Data.Select(d => d.HostId));
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task List_carries_the_hosts_advertised_isolation()
    {
        var h = new Harness();
        var host = await h.AddHostAsync("iso-host", "us", T0, isolationLevels: new[] { "shared", "vm" },
            defaultIsolation: "vm");
        await h.AddImageAsync(host.Id, "reg/wisp-base:latest", price: 5);

        var item = Assert.Single((await h.Service.ListAsync(new CatalogQuery())).Data);

        Assert.Equal(new[] { "shared", "vm" }, item.IsolationLevels);
        Assert.Equal("vm", item.DefaultIsolation);
    }

    [Fact]
    public async Task List_defaults_a_host_that_reported_nothing_to_shared()
    {
        var h = new Harness();
        // A host whose row carries no advertised isolation still surfaces ["shared"]/"shared".
        var host = await h.AddHostAsync("legacy-iso", "us", T0);
        await h.AddImageAsync(host.Id, "reg/wisp-base:latest", price: 5);

        var item = Assert.Single((await h.Service.ListAsync(new CatalogQuery())).Data);

        Assert.Equal(new[] { "shared" }, item.IsolationLevels);
        Assert.Equal("shared", item.DefaultIsolation);
    }

    [Fact]
    public async Task Get_host_carries_the_advertised_isolation()
    {
        var h = new Harness();
        var host = await h.AddHostAsync("iso-detail", "eu", T0, isolationLevels: new[] { "shared", "gvisor" },
            defaultIsolation: "gvisor");
        await h.AddImageAsync(host.Id, "enabled:img", price: 7);

        var detail = await h.Service.GetHostAsync(host.Id);

        Assert.NotNull(detail);
        Assert.Equal(new[] { "shared", "gvisor" }, detail!.IsolationLevels);
        Assert.Equal("gvisor", detail.DefaultIsolation);
    }

    [Fact]
    public async Task List_carries_the_hosts_container_os_from_the_live_capability()
    {
        var h = new Harness();
        var host = await h.AddHostAsync("win-server", "us", T0, os: "windows");
        await h.AddImageAsync(host.Id, "reg/wisp-base:latest", price: 5);

        var page = await h.Service.ListAsync(new CatalogQuery());

        var item = Assert.Single(page.Data);
        Assert.Equal("windows", item.Os);
    }

    [Fact]
    public async Task List_os_is_null_when_the_capability_advertises_none()
    {
        var h = new Harness();
        // Online host whose (older) agent advertised no os — surfaces as null, never an error.
        var host = await h.AddHostAsync("legacy", "us", T0, os: null);
        await h.AddImageAsync(host.Id, "reg/wisp-base:latest", price: 5);

        var page = await h.Service.ListAsync(new CatalogQuery());

        Assert.Null(Assert.Single(page.Data).Os);
    }

    [Fact]
    public async Task Get_host_carries_the_container_os_from_the_live_capability()
    {
        var h = new Harness();
        var host = await h.AddHostAsync("linux-server", "eu", T0, os: "linux");
        await h.AddImageAsync(host.Id, "enabled:img", price: 7);

        var detail = await h.Service.GetHostAsync(host.Id);

        Assert.NotNull(detail);
        Assert.Equal("linux", detail!.Os);
    }

    [Fact]
    public async Task Get_host_os_is_null_when_the_host_is_offline()
    {
        var h = new Harness();
        // Offline host has no live capability, so os is null (still returns detail).
        var host = await h.AddHostAsync("home", "eu", T0, status: HostStatus.Offline, online: false);
        await h.AddImageAsync(host.Id, "img", price: 7);

        var detail = await h.Service.GetHostAsync(host.Id);

        Assert.NotNull(detail);
        Assert.False(detail!.Online);
        Assert.Null(detail.Os);
    }

    [Fact]
    public async Task Get_host_returns_public_detail_with_enabled_images_only()
    {
        var h = new Harness();
        var host = await h.AddHostAsync("home", "eu", T0);
        await h.AddImageAsync(host.Id, "enabled:img", price: 7);
        await h.AddImageAsync(host.Id, "disabled:img", price: 7, enabled: false);

        var detail = await h.Service.GetHostAsync(host.Id);

        Assert.NotNull(detail);
        Assert.Equal(host.Id, detail!.HostId);
        Assert.Equal("home", detail.Label);
        Assert.Equal("eu", detail.Region);
        Assert.True(detail.Online);
        var image = Assert.Single(detail.Images);
        Assert.Equal("enabled:img", image.ImageRef);
        Assert.Equal(1024, image.MaxPids);
    }

    [Fact]
    public async Task Get_host_reflects_offline_state_but_still_returns_detail()
    {
        var h = new Harness();
        var host = await h.AddHostAsync("home", "eu", T0, status: HostStatus.Offline, online: false);
        await h.AddImageAsync(host.Id, "img", price: 7);

        var detail = await h.Service.GetHostAsync(host.Id);

        Assert.NotNull(detail);
        Assert.False(detail!.Online);
    }

    [Fact]
    public async Task Get_host_is_null_for_unknown_or_suspended_hosts()
    {
        var h = new Harness();
        var suspended = await h.AddHostAsync("bad", "us", T0, status: HostStatus.Suspended, online: false);

        Assert.Null(await h.Service.GetHostAsync(Guid.NewGuid()));
        Assert.Null(await h.Service.GetHostAsync(suspended.Id));
    }

    // ---- Distributed liveness (cross-instance) tests ------------------------------------------------

    [Fact]
    public async Task List_includes_a_host_whose_tunnel_is_on_another_instance()
    {
        // Acceptance criterion #45: catalog on "instance B" (local registry empty) lists a host whose tunnel
        // is owned by "instance A" (presence store has an owner).
        var h = new Harness();
        // online: false → local registry is empty (simulates instance B which does not own the socket).
        var host = await h.AddHostAsync("remote", "us", T0, online: false);
        // Presence store records the owner that instance A wrote — the distributed source of truth.
        await h.PresenceStore.SetOwnerAsync(host.Id.ToString(), "instance-a");
        // Provide capability so the catalog entry is complete (mirrors what instance A's registry holds).
        h.Capabilities.Set(host.Id, new HostCapabilitySnapshot(
            Array.Empty<string>(), Array.Empty<NetworkMode>(),
            MaxTtlSeconds: 3600, MaxCpus: 4, MaxMemoryMb: 8192, MaxPids: 1024));
        await h.AddImageAsync(host.Id, "img:1", price: 5);

        var page = await h.Service.ListAsync(new CatalogQuery());

        var item = Assert.Single(page.Data);
        Assert.Equal(host.Id, item.HostId);
        Assert.True(item.Online);
    }

    [Fact]
    public async Task Get_host_reports_online_when_tunnel_is_on_another_instance()
    {
        // Acceptance criterion #46: host detail online=true when presence store has an owner but local registry empty.
        var h = new Harness();
        var host = await h.AddHostAsync("remote", "us", T0, online: false);
        await h.PresenceStore.SetOwnerAsync(host.Id.ToString(), "instance-a");
        await h.AddImageAsync(host.Id, "img:1", price: 5);

        var detail = await h.Service.GetHostAsync(host.Id);

        Assert.NotNull(detail);
        Assert.True(detail!.Online);
    }

    [Fact]
    public async Task List_excludes_a_host_absent_from_both_local_registry_and_presence_store()
    {
        // Acceptance criterion #47: stale DB-online row with no live tunnel on any instance is still filtered out.
        var h = new Harness();
        // online: false → not in local registry; nothing written to presence store.
        var host = await h.AddHostAsync("stale-remote", "us", T0, online: false);
        await h.AddImageAsync(host.Id, "img:1", price: 5);

        var page = await h.Service.ListAsync(new CatalogQuery());

        Assert.Empty(page.Data);
    }

    [Fact]
    public async Task Get_host_reports_offline_when_absent_from_both_local_registry_and_presence_store()
    {
        // Acceptance criterion #47: stale DB-online row with no owner anywhere → online=false on the detail page.
        var h = new Harness();
        var host = await h.AddHostAsync("stale-remote", "us", T0, status: HostStatus.Offline, online: false);
        await h.AddImageAsync(host.Id, "img:1", price: 5);

        var detail = await h.Service.GetHostAsync(host.Id);

        Assert.NotNull(detail);
        Assert.False(detail!.Online);
    }

    // ---- Degraded-host exclusion (task #62) --------------------------------------------------------

    [Fact]
    public async Task List_excludes_a_host_marked_degraded_even_when_the_tunnel_is_live()
    {
        // Task #62 AC #211: a degraded heartbeat marks the host degraded (its agent cannot reach its
        // local wisp) — the catalog must exclude it from new lease placement even though the tunnel
        // is still up and presence still reports it online.
        var h = new Harness();
        var host = await h.AddHostAsync("wisp-down", "us", T0, online: true);
        await h.AddImageAsync(host.Id, "img:1", price: 5);
        await h.DegradedStore.SetDegradedAsync(host.Id.ToString());

        var page = await h.Service.ListAsync(new CatalogQuery());

        Assert.Empty(page.Data);
    }

    [Fact]
    public async Task List_reincludes_a_host_after_its_degraded_flag_is_cleared()
    {
        // Task #62 AC #212: the next non-degraded heartbeat clears the flag and placement resumes
        // automatically — no manual admin action, no reconnect required.
        var h = new Harness();
        var host = await h.AddHostAsync("recovers", "us", T0, online: true);
        await h.AddImageAsync(host.Id, "img:1", price: 5);
        await h.DegradedStore.SetDegradedAsync(host.Id.ToString());
        Assert.Empty((await h.Service.ListAsync(new CatalogQuery())).Data);

        await h.DegradedStore.ClearDegradedAsync(host.Id.ToString());

        var item = Assert.Single((await h.Service.ListAsync(new CatalogQuery())).Data);
        Assert.Equal(host.Id, item.HostId);
    }

    [Fact]
    public async Task List_degraded_flag_is_visible_across_instances_via_the_shared_store()
    {
        // Multi-instance safety (AC #214): a host marked degraded by its owning instance ("A") must
        // be invisible to placement on a different instance ("B"). Simulated by writing to the shared
        // store while the local registry is empty (this harness stands in for instance B).
        var h = new Harness();
        var host = await h.AddHostAsync("remote", "us", T0, online: false); // no local tunnel = instance B
        await h.PresenceStore.SetOwnerAsync(host.Id.ToString(), "instance-a");
        h.Capabilities.Set(host.Id, new HostCapabilitySnapshot(
            Array.Empty<string>(), Array.Empty<NetworkMode>(),
            MaxTtlSeconds: 3600, MaxCpus: 4, MaxMemoryMb: 8192, MaxPids: 1024));
        await h.AddImageAsync(host.Id, "img:1", price: 5);
        // Instance A observed the degraded heartbeat and wrote it to the shared store.
        await h.DegradedStore.SetDegradedAsync(host.Id.ToString());

        var page = await h.Service.ListAsync(new CatalogQuery());

        Assert.Empty(page.Data);
    }

    [Fact]
    public async Task Get_host_reports_online_false_for_a_degraded_host()
    {
        // On the detail page a degraded host surfaces as online=false — the "book this host" view
        // must be honest about placement being blocked, mirroring the offline path (AC #211).
        var h = new Harness();
        var host = await h.AddHostAsync("wisp-down", "us", T0, online: true);
        await h.AddImageAsync(host.Id, "img:1", price: 5);
        await h.DegradedStore.SetDegradedAsync(host.Id.ToString());

        var detail = await h.Service.GetHostAsync(host.Id);

        Assert.NotNull(detail);
        Assert.False(detail!.Online);
    }

    [Fact]
    public async Task List_local_tunnel_bypasses_presence_store_fast_path()
    {
        // Acceptance criterion #49: when the tunnel is local the fast path hits (presence store returns empty
        // but the host still appears). This is the existing single-instance behaviour unchanged.
        var h = new Harness();
        var host = await h.AddHostAsync("local", "us", T0, online: true);
        // Presence store is empty — no SetOwnerAsync called. Local registry is the only source.
        await h.AddImageAsync(host.Id, "img:1", price: 5);

        var page = await h.Service.ListAsync(new CatalogQuery());

        Assert.Single(page.Data);
    }
}
