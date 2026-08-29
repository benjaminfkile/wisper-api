using System.Net.WebSockets;
using Microsoft.Extensions.Logging.Abstractions;
using Wisper.Api.Domain;
using Wisper.Api.Tunnel;
using Wisper.Api.Tunnel.Backplane;
using Wisper.Api.Tunnel.Messages;
using Xunit;

namespace Wisper.Api.Tests.Tunnel.Backplane;

/// <summary>
/// Simulates two instances sharing one <see cref="InMemoryHostCapabilityStore"/> to verify the
/// full distributed capability lifecycle (task #17): capability published by instance A is readable
/// on instance B; the images-validation path works remotely; capability disappears when presence clears;
/// the local fast path wins when the tunnel is local and pays no store I/O.
/// </summary>
public class DistributedHostCapabilitySourceTests
{
    private static readonly Guid HostId = new("11111111-1111-1111-1111-111111111111");

    private static readonly HelloCapability SampleCapability = new()
    {
        Images = ["ubuntu:22.04", "debian:12"],
        Os = "linux",
        Limits = new HelloLimits
        {
            MaxTtlSeconds = 3600,
            MaxCpus = 4.0,
            MaxMemoryMb = 8192,
            PidsLimit = 1024,
            Networks = ["none", "open"],
        },
    };

    private static TunnelConnection ConnectionWith(HelloCapability capability) =>
        new(WebSocket.CreateFromStream(Stream.Null, isServer: true, subProtocol: null, TimeSpan.FromSeconds(30)),
            HostId.ToString(),
            sessionId: "sess-A",
            maxReceiveBytes: 65536,
            NullLogger.Instance)
        {
            Capability = capability,
        };

    private static (DistributedHostRegistry Registry, RegistryHostCapabilitySource LocalSource)
        BuildInstance(string instanceId, InMemoryHostCapabilityStore store, InMemoryHostPresenceStore presence)
    {
        var localRegistry = new InMemoryHostRegistry(NullLogger<InMemoryHostRegistry>.Instance);
        var localSource = new RegistryHostCapabilitySource(localRegistry);
        var registry = new DistributedHostRegistry(
            localRegistry,
            presence,
            store,
            new WisperInstanceIdentity(instanceId),
            NullLogger<DistributedHostRegistry>.Instance);
        return (registry, localSource);
    }

    [Fact]
    public async Task RemoteRead_capability_written_by_instance_A_is_visible_on_instance_B()
    {
        var store = new InMemoryHostCapabilityStore();
        var presence = new InMemoryHostPresenceStore();

        // Instance A registers the host (writes capability to shared store).
        var (registryA, _) = BuildInstance("inst-A", store, presence);
        await registryA.RegisterAsync(ConnectionWith(SampleCapability));

        // Instance B has an empty local registry -- host tunnel is not local.
        var (_, localSrcB) = BuildInstance("inst-B", new InMemoryHostCapabilityStore(), new InMemoryHostPresenceStore());
        var sourceB = new DistributedHostCapabilitySource(localSrcB, store);

        var snap = sourceB.GetCapability(HostId);

        Assert.NotNull(snap);
        Assert.Equal("linux", snap.Os);
        Assert.Equal(4.0, snap.MaxCpus);
        Assert.Equal(8192, snap.MaxMemoryMb);
        Assert.Equal(3600, snap.MaxTtlSeconds);
        Assert.Contains("ubuntu:22.04", snap.Images);
        Assert.Contains("debian:12", snap.Images);
        Assert.Contains(NetworkMode.None, snap.Networks);
        Assert.Contains(NetworkMode.Open, snap.Networks);
    }

    [Fact]
    public async Task RemoteRead_AllowsImage_works_against_remote_capability()
    {
        var store = new InMemoryHostCapabilityStore();
        var presence = new InMemoryHostPresenceStore();

        var (registryA, _) = BuildInstance("inst-A", store, presence);
        await registryA.RegisterAsync(ConnectionWith(SampleCapability));

        var (_, localSrcB) = BuildInstance("inst-B", new InMemoryHostCapabilityStore(), new InMemoryHostPresenceStore());
        var sourceB = new DistributedHostCapabilitySource(localSrcB, store);

        var snap = sourceB.GetCapability(HostId);

        Assert.NotNull(snap);
        Assert.True(snap.AllowsImage("ubuntu:22.04"));
        Assert.False(snap.AllowsImage("alpine:3.19"));
    }

    [Fact]
    public async Task Disconnect_clears_capability_from_store()
    {
        var store = new InMemoryHostCapabilityStore();
        var presence = new InMemoryHostPresenceStore();

        var (registryA, _) = BuildInstance("inst-A", store, presence);
        var conn = ConnectionWith(SampleCapability);
        await registryA.RegisterAsync(conn);

        // Sanity: capability is visible before disconnect.
        Assert.NotNull(store.Get(HostId.ToString()));

        // Disconnect clears capability alongside presence.
        registryA.Unregister(conn);

        await WaitUntilAsync(() => Task.FromResult(store.Get(HostId.ToString()) is null));

        var (_, localSrcB) = BuildInstance("inst-B", new InMemoryHostCapabilityStore(), new InMemoryHostPresenceStore());
        var sourceB = new DistributedHostCapabilitySource(localSrcB, store);
        Assert.Null(sourceB.GetCapability(HostId));
    }

    [Fact]
    public async Task LocalFastPath_wins_when_tunnel_is_local_and_store_is_stale()
    {
        var store = new InMemoryHostCapabilityStore();
        var presence = new InMemoryHostPresenceStore();

        var (registryA, localSrcA) = BuildInstance("inst-A", store, presence);
        await registryA.RegisterAsync(ConnectionWith(SampleCapability));

        // Overwrite the store with different data to prove the local registry wins.
        var staleSnapshot = new HostCapabilitySnapshot(
            Images: ["stale:image"],
            Networks: [],
            MaxTtlSeconds: 1,
            MaxCpus: 0.1,
            MaxMemoryMb: 1,
            MaxPids: 1,
            Os: "windows");
        await store.SetAsync(HostId.ToString(), staleSnapshot);

        // Instance A's distributed source: local registry is populated, so fast path wins.
        var sourceA = new DistributedHostCapabilitySource(localSrcA, store);
        var snap = sourceA.GetCapability(HostId);

        Assert.NotNull(snap);
        Assert.Equal("linux", snap.Os);
        Assert.Contains("ubuntu:22.04", snap.Images);
    }

    [Fact]
    public void RemoteRead_returns_null_when_store_is_empty()
    {
        var store = new InMemoryHostCapabilityStore();
        var (_, localSrcB) = BuildInstance("inst-B", new InMemoryHostCapabilityStore(), new InMemoryHostPresenceStore());
        var sourceB = new DistributedHostCapabilitySource(localSrcB, store);

        Assert.Null(sourceB.GetCapability(HostId));
    }

    [Fact]
    public async Task ReRegister_replaces_capability_in_store()
    {
        var store = new InMemoryHostCapabilityStore();
        var presence = new InMemoryHostPresenceStore();

        var (registryA, _) = BuildInstance("inst-A", store, presence);
        await registryA.RegisterAsync(ConnectionWith(SampleCapability));

        // Re-advertise with an updated capability (e.g. fewer images).
        var updatedCapability = new HelloCapability
        {
            Images = ["alpine:3.19"],
            Os = "linux",
            Limits = new HelloLimits
            {
                MaxTtlSeconds = 1800,
                MaxCpus = 2.0,
                MaxMemoryMb = 4096,
                PidsLimit = 512,
                Networks = ["none"],
            },
        };
        // Re-register replaces the previous connection (and its capability).
        await registryA.RegisterAsync(ConnectionWith(updatedCapability));

        var snap = store.Get(HostId.ToString());
        Assert.NotNull(snap);
        Assert.Equal(2.0, snap.MaxCpus);
        Assert.Contains("alpine:3.19", snap.Images);
        Assert.DoesNotContain("ubuntu:22.04", snap.Images);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.Fail("condition not met before the deadline");
    }
}
