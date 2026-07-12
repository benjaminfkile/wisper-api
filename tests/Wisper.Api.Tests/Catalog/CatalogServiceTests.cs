using Wisper.Api.Catalog;
using Wisper.Api.Domain;
using Wisper.Api.Persistence.HostImages;
using Wisper.Api.Persistence.Hosts;
using Wisper.Api.Tests.TestSupport;
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
        public CatalogService Service => new(Hosts, Images, Registry);

        public async Task<Host> AddHostAsync(
            string name, string region, DateTimeOffset createdAt,
            HostStatus status = HostStatus.Online, bool online = true, Guid? id = null)
        {
            var host = await Hosts.CreateAsync(new Host
            {
                Id = id ?? Guid.NewGuid(),
                OwnerUserId = Guid.NewGuid(),
                Name = name,
                Label = region,
                Status = status,
                AgentTokenHash = "hash",
                CreatedAt = createdAt,
                UpdatedAt = createdAt,
            });
            if (online)
            {
                Registry.SetOnline(host.Id);
            }

            return host;
        }

        public Task<HostImage> AddImageAsync(
            Guid hostId, string imageRef, long price, bool enabled = true,
            NetworkMode[]? networks = null) =>
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
}
