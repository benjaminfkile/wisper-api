using Microsoft.Extensions.Logging.Abstractions;
using Wisper.Api.Catalog;
using Wisper.Api.Domain;
using Wisper.Api.Persistence.HostImages;
using Wisper.Api.Persistence.Hosts;
using Wisper.Api.Persistence.Leases;
using Wisper.Api.Persistence.Users;
using Wisper.Api.Tests.TestSupport;
using Wisper.Api.Tunnel;
using Xunit;
using Host = Wisper.Api.Domain.Host;

namespace Wisper.Api.Tests.Tunnel;

/// <summary>
/// Unit tests for <see cref="HostPresenceService"/> — the tunnel-driven flip of a host's persisted
/// <c>host_status</c> (docs/TUNNEL.md §3, §8, task #392). Ready flips a host online only when it clears the
/// earning gate (owner Connect-enabled, or every enabled image zero-priced); a suspended host never comes
/// back online; a durable loss flips it offline; a non-Guid dev host id is a no-op.
/// </summary>
public class HostPresenceServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 12, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset TReady = new(2026, 7, 12, 1, 0, 0, TimeSpan.Zero);

    private sealed class Fixture
    {
        public InMemoryHostRepository Hosts { get; } = new();
        public InMemoryHostImageRepository Images { get; } = new();
        public InMemoryUserRepository Users { get; } = new();
        public FakeHostRegistry Registry { get; } = new();
        public FakeHostCapabilitySource Capabilities { get; } = new();
        public InMemoryLeaseRepository Leases { get; } = new();
        public FakeTimeProvider Clock { get; } = new(TReady);
        public HostPresenceService Service { get; }

        // The catalog joins the DB-online subset with the live registry, so it proves the presence flip is
        // what makes a host visible (docs/API.md §5).
        public CatalogService Catalog => new(Hosts, Images, Registry, Capabilities, Leases);

        public Fixture()
        {
            Service = new HostPresenceService(
                Hosts, Images, Users, Clock, NullLogger<HostPresenceService>.Instance);
        }

        public async Task<Host> SeedHostAsync(ConnectStatus connect, HostStatus status = HostStatus.Offline)
        {
            var owner = await Users.CreateAsync(new User
            {
                CognitoSub = $"sub-{Guid.NewGuid()}",
                Email = $"{Guid.NewGuid()}@hosts.test",
                ConnectStatus = connect,
                CreatedAt = T0,
                UpdatedAt = T0,
            });
            return await Hosts.CreateAsync(new Host
            {
                OwnerUserId = owner.Id,
                Status = status,
                AgentTokenHash = "hash",
                CreatedAt = T0,
                UpdatedAt = T0,
            });
        }

        public Task AddImageAsync(Guid hostId, long price, bool enabled = true) =>
            Images.CreateAsync(new HostImage
            {
                HostId = hostId,
                ImageRef = "reg/wisp-base:latest",
                PriceCentsPerMin = price,
                Networks = new[] { NetworkMode.None },
                MaxTtlSeconds = 3600,
                Enabled = enabled,
                CreatedAt = T0,
                UpdatedAt = T0,
            });

        public async Task<HostStatus> StatusOf(Guid hostId) => (await Hosts.GetByIdAsync(hostId))!.Status;
    }

    [Fact]
    public async Task Ready_flips_online_for_a_zero_priced_host_without_connect()
    {
        var fx = new Fixture();
        var host = await fx.SeedHostAsync(ConnectStatus.None);
        await fx.AddImageAsync(host.Id, price: 0);

        await fx.Service.GoOnlineIfEligibleAsync(host.Id.ToString());

        var reloaded = (await fx.Hosts.GetByIdAsync(host.Id))!;
        Assert.Equal(HostStatus.Online, reloaded.Status);
        Assert.Equal(TReady, reloaded.LastSeenAt); // last-seen stamped at the ready instant
    }

    [Fact]
    public async Task Ready_persists_the_advertised_isolation_capability()
    {
        var fx = new Fixture();
        var host = await fx.SeedHostAsync(ConnectStatus.None);
        await fx.AddImageAsync(host.Id, price: 0);

        await fx.Service.GoOnlineIfEligibleAsync(
            host.Id.ToString(), new[] { "shared", "vm" }, "vm");

        var reloaded = (await fx.Hosts.GetByIdAsync(host.Id))!;
        Assert.Equal(HostStatus.Online, reloaded.Status);
        Assert.Equal(new[] { "shared", "vm" }, reloaded.IsolationLevels);
        Assert.Equal("vm", reloaded.DefaultIsolation);
    }

    [Fact]
    public async Task Ready_defaults_an_older_agent_to_shared()
    {
        // An older agent advertises nothing (empty list, no default) → persisted as ["shared"]/"shared".
        var fx = new Fixture();
        var host = await fx.SeedHostAsync(ConnectStatus.None);
        await fx.AddImageAsync(host.Id, price: 0);

        await fx.Service.GoOnlineIfEligibleAsync(host.Id.ToString(), Array.Empty<string>(), null);

        var reloaded = (await fx.Hosts.GetByIdAsync(host.Id))!;
        Assert.Equal(new[] { "shared" }, reloaded.IsolationLevels);
        Assert.Equal("shared", reloaded.DefaultIsolation);
    }

    [Fact]
    public async Task Refresh_updates_a_hosts_advertised_isolation()
    {
        var fx = new Fixture();
        var host = await fx.SeedHostAsync(ConnectStatus.None, status: HostStatus.Online);

        await fx.Service.RefreshAdvertisedIsolationAsync(
            host.Id.ToString(), new[] { "shared", "gvisor" }, "gvisor");

        var reloaded = (await fx.Hosts.GetByIdAsync(host.Id))!;
        Assert.Equal(new[] { "shared", "gvisor" }, reloaded.IsolationLevels);
        Assert.Equal("gvisor", reloaded.DefaultIsolation);
    }

    [Fact]
    public async Task Refresh_never_touches_a_suspended_host()
    {
        var fx = new Fixture();
        var host = await fx.SeedHostAsync(ConnectStatus.None, status: HostStatus.Suspended);

        await fx.Service.RefreshAdvertisedIsolationAsync(
            host.Id.ToString(), new[] { "shared", "vm" }, "vm");

        var reloaded = (await fx.Hosts.GetByIdAsync(host.Id))!;
        Assert.Equal(new[] { "shared" }, reloaded.IsolationLevels); // the seed default, untouched
    }

    [Fact]
    public async Task Ready_persists_the_advertised_gpu_capability()
    {
        var fx = new Fixture();
        var host = await fx.SeedHostAsync(ConnectStatus.None);
        await fx.AddImageAsync(host.Id, price: 0);

        await fx.Service.GoOnlineIfEligibleAsync(
            host.Id.ToString(), gpuClasses: new[] { "nvidia-a100", "nvidia-a100", "nvidia-h100" }, gpuCount: 3);

        var reloaded = (await fx.Hosts.GetByIdAsync(host.Id))!;
        Assert.Equal(HostStatus.Online, reloaded.Status);
        Assert.Equal(new[] { "nvidia-a100", "nvidia-h100" }, reloaded.GpuClasses); // distinct, order-preserving
        Assert.Equal(3, reloaded.GpuCount); // total advertised devices, not distinct classes
    }

    [Fact]
    public async Task Ready_leaves_gpu_empty_for_an_older_agent_without_a_gpu_block()
    {
        // An older agent advertises no gpu block (null classes) → the fresh host keeps its zero/empty defaults.
        var fx = new Fixture();
        var host = await fx.SeedHostAsync(ConnectStatus.None);
        await fx.AddImageAsync(host.Id, price: 0);

        await fx.Service.GoOnlineIfEligibleAsync(host.Id.ToString(), gpuClasses: null, gpuCount: 0);

        var reloaded = (await fx.Hosts.GetByIdAsync(host.Id))!;
        Assert.Empty(reloaded.GpuClasses);
        Assert.Equal(0, reloaded.GpuCount);
    }

    [Fact]
    public async Task Ready_without_a_gpu_block_does_not_null_a_previously_advertised_host()
    {
        // A host that previously advertised GPU then sends a hello with no gpu block (null classes) keeps its
        // persisted GPU — matching the isolation refresh, an absent block leaves the capability as-is (#521).
        var fx = new Fixture();
        var host = await fx.SeedHostAsync(ConnectStatus.None);
        await fx.AddImageAsync(host.Id, price: 0);
        await fx.Hosts.SetAdvertisedGpuAsync(host.Id, new[] { "nvidia-a100" }, 2, T0);

        await fx.Service.GoOnlineIfEligibleAsync(host.Id.ToString(), gpuClasses: null, gpuCount: 0);

        var reloaded = (await fx.Hosts.GetByIdAsync(host.Id))!;
        Assert.Equal(new[] { "nvidia-a100" }, reloaded.GpuClasses); // untouched
        Assert.Equal(2, reloaded.GpuCount);
    }

    [Fact]
    public async Task Refresh_updates_a_hosts_advertised_gpu()
    {
        var fx = new Fixture();
        var host = await fx.SeedHostAsync(ConnectStatus.None, status: HostStatus.Online);

        await fx.Service.RefreshAdvertisedGpuAsync(
            host.Id.ToString(), new[] { "nvidia-h100" }, 1);

        var reloaded = (await fx.Hosts.GetByIdAsync(host.Id))!;
        Assert.Equal(new[] { "nvidia-h100" }, reloaded.GpuClasses);
        Assert.Equal(1, reloaded.GpuCount);
    }

    [Fact]
    public async Task Refresh_gpu_never_touches_a_suspended_host()
    {
        var fx = new Fixture();
        var host = await fx.SeedHostAsync(ConnectStatus.None, status: HostStatus.Suspended);

        await fx.Service.RefreshAdvertisedGpuAsync(
            host.Id.ToString(), new[] { "nvidia-a100" }, 4);

        var reloaded = (await fx.Hosts.GetByIdAsync(host.Id))!;
        Assert.Empty(reloaded.GpuClasses); // the seed default, untouched
        Assert.Equal(0, reloaded.GpuCount);
    }

    [Fact]
    public async Task Ready_surfaces_the_isolation_capability_in_the_catalog()
    {
        var fx = new Fixture();
        var host = await fx.SeedHostAsync(ConnectStatus.None);
        await fx.AddImageAsync(host.Id, price: 0);
        fx.Registry.SetOnline(host.Id);

        await fx.Service.GoOnlineIfEligibleAsync(
            host.Id.ToString(), new[] { "shared", "vm" }, "vm");

        var item = Assert.Single((await fx.Catalog.ListAsync(new CatalogQuery())).Data);
        Assert.Equal(new[] { "shared", "vm" }, item.IsolationLevels);
        Assert.Equal("vm", item.DefaultIsolation);
    }

    [Fact]
    public async Task Ready_flips_online_for_a_connect_enabled_host_even_when_priced()
    {
        var fx = new Fixture();
        var host = await fx.SeedHostAsync(ConnectStatus.Enabled);
        await fx.AddImageAsync(host.Id, price: 5);

        await fx.Service.GoOnlineIfEligibleAsync(host.Id.ToString());

        Assert.Equal(HostStatus.Online, await fx.StatusOf(host.Id));
    }

    [Fact]
    public async Task Ready_leaves_a_priced_host_offline_without_connect()
    {
        var fx = new Fixture();
        var host = await fx.SeedHostAsync(ConnectStatus.Pending);
        await fx.AddImageAsync(host.Id, price: 5);

        await fx.Service.GoOnlineIfEligibleAsync(host.Id.ToString());

        Assert.Equal(HostStatus.Offline, await fx.StatusOf(host.Id));
    }

    [Fact]
    public async Task Ready_ignores_a_disabled_priced_image_and_flips_online()
    {
        // The single priced image is disabled → the host earns nothing → it may go online un-onboarded.
        var fx = new Fixture();
        var host = await fx.SeedHostAsync(ConnectStatus.None);
        await fx.AddImageAsync(host.Id, price: 9, enabled: false);

        await fx.Service.GoOnlineIfEligibleAsync(host.Id.ToString());

        Assert.Equal(HostStatus.Online, await fx.StatusOf(host.Id));
    }

    [Fact]
    public async Task Ready_never_flips_a_suspended_host_online()
    {
        var fx = new Fixture();
        var host = await fx.SeedHostAsync(ConnectStatus.Enabled, status: HostStatus.Suspended);
        await fx.AddImageAsync(host.Id, price: 0);

        await fx.Service.GoOnlineIfEligibleAsync(host.Id.ToString());

        Assert.Equal(HostStatus.Suspended, await fx.StatusOf(host.Id));
    }

    [Fact]
    public async Task Ready_is_a_no_op_for_a_non_guid_host_id()
    {
        var fx = new Fixture();
        // No throw, nothing to assert beyond graceful completion (the dev/no-DB tunnel harness).
        await fx.Service.GoOnlineIfEligibleAsync("dev-host-alpha");
    }

    [Fact]
    public async Task Offline_flips_an_online_host_and_stamps_last_healthy()
    {
        var fx = new Fixture();
        var host = await fx.SeedHostAsync(ConnectStatus.None, status: HostStatus.Online);
        var lastHealthy = new DateTimeOffset(2026, 7, 12, 0, 30, 0, TimeSpan.Zero);

        await fx.Service.GoOfflineAsync(host.Id, lastHealthy);

        var reloaded = (await fx.Hosts.GetByIdAsync(host.Id))!;
        Assert.Equal(HostStatus.Offline, reloaded.Status);
        Assert.Equal(lastHealthy, reloaded.LastSeenAt);
    }

    [Fact]
    public async Task Offline_never_clears_a_suspended_host()
    {
        var fx = new Fixture();
        var host = await fx.SeedHostAsync(ConnectStatus.None, status: HostStatus.Suspended);

        await fx.Service.GoOfflineAsync(host.Id, T0);

        Assert.Equal(HostStatus.Suspended, await fx.StatusOf(host.Id));
    }

    [Fact]
    public async Task A_zero_priced_host_appears_in_the_catalog_after_the_ready_flip()
    {
        var fx = new Fixture();
        var host = await fx.SeedHostAsync(ConnectStatus.None);
        await fx.AddImageAsync(host.Id, price: 0);
        fx.Registry.SetOnline(host.Id); // the live tunnel

        // Before the flip the row is still offline → nothing catalogued despite the live tunnel.
        Assert.Empty((await fx.Catalog.ListAsync(new CatalogQuery())).Data);

        await fx.Service.GoOnlineIfEligibleAsync(host.Id.ToString());

        var item = Assert.Single((await fx.Catalog.ListAsync(new CatalogQuery())).Data);
        Assert.Equal(host.Id, item.HostId);
    }

    [Fact]
    public async Task A_priced_host_without_connect_stays_absent_from_the_catalog()
    {
        var fx = new Fixture();
        var host = await fx.SeedHostAsync(ConnectStatus.None);
        await fx.AddImageAsync(host.Id, price: 5);
        fx.Registry.SetOnline(host.Id); // a live tunnel, but earning-gated

        await fx.Service.GoOnlineIfEligibleAsync(host.Id.ToString());

        Assert.Equal(HostStatus.Offline, await fx.StatusOf(host.Id));
        Assert.Empty((await fx.Catalog.ListAsync(new CatalogQuery())).Data);
    }
}
