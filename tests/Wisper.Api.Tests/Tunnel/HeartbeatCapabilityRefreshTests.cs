using System.Net.WebSockets;
using Microsoft.Extensions.Logging.Abstractions;
using Wisper.Api.Domain;
using Wisper.Api.Persistence.HostImages;
using Wisper.Api.Persistence.Hosts;
using Wisper.Api.Persistence.Users;
using Wisper.Api.Tests.TestSupport;
using Wisper.Api.Tunnel;
using Wisper.Api.Tunnel.Backplane;
using Wisper.Api.Tunnel.Messages;
using Xunit;
using Host = Wisper.Api.Domain.Host;

namespace Wisper.Api.Tests.Tunnel;

/// <summary>
/// Task #61: a <c>host.heartbeat</c> that carries a full <c>capability</c> block replaces the manager's
/// live view of the host — not just isolation/GPU (tasks #417, #521) but capacity/limits/os too — so the
/// next lease admission sees fresh values within one heartbeat with no reconnect. Verifies both the wire
/// contract (the block deserializes into the same <see cref="HelloCapability"/> the hello uses) and the
/// runtime contract (the local snapshot flips, the shared capability store is republished, and an omitted
/// block is "no update — keep last known").
/// </summary>
public class HeartbeatCapabilityRefreshTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 15, 0, 0, 0, TimeSpan.Zero);

    // ----- wire ----------------------------------------------------------------------------------

    [Fact]
    public void Heartbeat_carrying_the_full_capability_block_is_parsed()
    {
        // The full hello.capability shape — images/default/limits/os/isolation_levels/default_isolation/
        // gpu/capacity — must all round-trip on host.heartbeat too (the agent sends this every ~15s).
        const string json =
            "{\"t\":\"host.heartbeat\",\"leases\":[]," +
            "\"capability\":{\"images\":[\"alpine\",\"ubuntu\"],\"default\":\"alpine\",\"os\":\"linux\"," +
            "\"isolation_levels\":[\"shared\",\"gvisor\"],\"default_isolation\":\"gvisor\"," +
            "\"gpu\":{\"supported\":true,\"max_gpus\":2,\"isolations\":[\"vgpu\"]," +
            "\"devices\":[{\"id\":\"gpu-0\",\"class\":\"nvidia-h100\",\"vram_mb\":81920}]}," +
            "\"capacity\":{\"max_contracts\":7,\"active_contracts\":3," +
            "\"total_cpus\":32,\"used_cpus\":12.5,\"total_memory_mb\":131072,\"used_memory_mb\":40960}," +
            "\"limits\":{\"max_ttl_seconds\":7200,\"max_cpus\":8,\"max_memory_mb\":16384,\"pids_limit\":2048,\"networks\":[\"open\",\"none\"]}}}";

        var heartbeat = ControlJson.Deserialize<HostHeartbeat>(json);

        Assert.NotNull(heartbeat);
        var cap = heartbeat!.Capability;
        Assert.NotNull(cap);
        Assert.Equal(new[] { "alpine", "ubuntu" }, cap!.Images);
        Assert.Equal("alpine", cap.Default);
        Assert.Equal("linux", cap.Os);
        Assert.Equal(new[] { "shared", "gvisor" }, cap.IsolationLevels);
        Assert.Equal("gvisor", cap.DefaultIsolation);
        Assert.NotNull(cap.Gpu);
        Assert.Equal(2, cap.Gpu!.MaxGpus);
        Assert.NotNull(cap.Capacity);
        Assert.Equal(7, cap.Capacity!.MaxContracts);
        Assert.Equal(3, cap.Capacity.ActiveContracts);
        Assert.Equal(12.5d, cap.Capacity.UsedCpus);
        Assert.Equal(40960, cap.Capacity.UsedMemoryMb);
        Assert.Equal(7200, cap.Limits.MaxTtlSeconds);
        Assert.Equal(8, cap.Limits.MaxCpus);
    }

    // ----- runtime (task #61 AC 207 / 208 / 209) -------------------------------------------------

    [Fact]
    public async Task Heartbeat_refresh_updates_the_live_capacity_ceiling_seen_by_admission()
    {
        // AC 207: a heartbeat carrying a changed capacity block MUST change admission decisions within
        // one heartbeat. The registry-backed source reads connection.Capability; the refresh must swap
        // that reference so the very next capability read reflects the new max_contracts.
        var fx = await Fixture.CreateAsync();
        fx.ConnectHost(WithCapacity(maxContracts: 5));

        // Pre-heartbeat: the source sees the 5-contract ceiling from the hello.
        var before = fx.Source.GetCapability(fx.HostId)!;
        Assert.Equal(5, before.MaxContracts);
        Assert.True(before.HasContractLimit);

        // A heartbeat lowers the ceiling to 2 (e.g. wisp cut its concurrent-contract limit).
        await HeartbeatCapabilityRefresh.ApplyAsync(
            fx.Connection, WithCapacity(maxContracts: 2),
            fx.CapabilityStore, fx.Presence, NullLogger.Instance, ct: default);

        var after = fx.Source.GetCapability(fx.HostId)!;
        Assert.Equal(2, after.MaxContracts);
        // And the shared capability store is republished — non-owner instances see the new ceiling too.
        Assert.Equal(2, fx.CapabilityStore.Get(fx.HostId.ToString())!.MaxContracts);
    }

    [Fact]
    public async Task Heartbeat_with_no_capability_leaves_the_stored_snapshot_untouched()
    {
        // AC 208: a heartbeat that omits `capability` (the agent's local wisp is unreachable, so it
        // deliberately drops the block) MUST leave the last-known snapshot exactly as-is. The router
        // does not invoke the helper at all, so the connection's Capability and the shared store keep
        // the hello-time values — the manager keeps admitting against the last-known truth.
        var fx = await Fixture.CreateAsync();
        fx.ConnectHost(WithCapacity(maxContracts: 4));

        // Seed the shared store the way DistributedHostRegistry.RegisterAsync would.
        var seeded = RegistryHostCapabilitySource.BuildSnapshot(fx.Connection.Capability)!;
        await fx.CapabilityStore.SetAsync(fx.HostId.ToString(), seeded);

        var heartbeat = ControlJson.Deserialize<HostHeartbeat>(
            "{\"t\":\"host.heartbeat\",\"leases\":[]}")!;
        Assert.Null(heartbeat.Capability); // the wire is bare — no capability block

        // The endpoint's router only invokes the refresh helper when heartbeat.Capability is present;
        // simulate that contract here.
        if (heartbeat.Capability is { } cap)
        {
            await HeartbeatCapabilityRefresh.ApplyAsync(
                fx.Connection, cap, fx.CapabilityStore, fx.Presence, NullLogger.Instance, ct: default);
        }

        // Live snapshot: still the hello-time ceiling.
        Assert.Equal(4, fx.Source.GetCapability(fx.HostId)!.MaxContracts);
        // Shared store: still the hello-time ceiling.
        Assert.Equal(4, fx.CapabilityStore.Get(fx.HostId.ToString())!.MaxContracts);
    }

    [Fact]
    public async Task Heartbeat_refresh_still_updates_persisted_isolation_and_gpu()
    {
        // AC 209: task #417 + #521 mid-session refresh behavior must be preserved — the same heartbeat
        // that refreshes capacity also updates the host row's advertised isolation and GPU (persisted
        // for the catalog). Same call as before; no regression.
        var fx = await Fixture.CreateAsync();
        var host = await fx.SeedOnlineHostAsync();
        fx.ConnectHost(WithCapacity(maxContracts: 4), hostIdOverride: host.Id);

        var cap = new HelloCapability
        {
            Images = new[] { "alpine" },
            Default = "alpine",
            IsolationLevels = new[] { "shared", "vm" },
            DefaultIsolation = "vm",
            Gpu = new HelloGpu
            {
                Supported = true,
                MaxGpus = 2,
                Devices = new[] { new HelloGpuDevice { Id = "gpu-0", Class = "nvidia-h100", VramMb = 81920 } },
            },
            Capacity = new HelloContractCapacity { MaxContracts = 3 },
        };

        await HeartbeatCapabilityRefresh.ApplyAsync(
            fx.Connection, cap, fx.CapabilityStore, fx.Presence, NullLogger.Instance, ct: default);

        var reloaded = (await fx.Hosts.GetByIdAsync(host.Id))!;
        Assert.Equal(new[] { "shared", "vm" }, reloaded.IsolationLevels);
        Assert.Equal("vm", reloaded.DefaultIsolation);
        Assert.Equal(new[] { "nvidia-h100" }, reloaded.GpuClasses);
        Assert.Equal(1, reloaded.GpuCount);
    }

    // ----- fixture / helpers ---------------------------------------------------------------------

    private static HelloCapability WithCapacity(int maxContracts) => new()
    {
        Images = new[] { "alpine" },
        Default = "alpine",
        IsolationLevels = new[] { "shared" },
        DefaultIsolation = "shared",
        Capacity = new HelloContractCapacity { MaxContracts = maxContracts },
    };

    private sealed class Fixture
    {
        public InMemoryHostRegistry Registry { get; } = new(NullLogger<InMemoryHostRegistry>.Instance);
        public InMemoryHostCapabilityStore CapabilityStore { get; } = new();
        public RegistryHostCapabilitySource Source { get; }
        public InMemoryHostRepository Hosts { get; } = new();
        public InMemoryHostImageRepository Images { get; } = new();
        public InMemoryUserRepository Users { get; } = new();
        public FakeTimeProvider Clock { get; } = new(T0);
        public IHostPresence Presence { get; }
        public Guid HostId { get; private set; }
        public TunnelConnection Connection { get; private set; } = null!;

        private Fixture()
        {
            Source = new RegistryHostCapabilitySource(Registry);
            Presence = new HostPresenceService(
                Hosts, Images, Users, Clock, NullLogger<HostPresenceService>.Instance);
        }

        public static Task<Fixture> CreateAsync() => Task.FromResult(new Fixture());

        public void ConnectHost(HelloCapability initialCapability, Guid? hostIdOverride = null)
        {
            HostId = hostIdOverride ?? Guid.NewGuid();
            Connection = new TunnelConnection(
                new StubWebSocket(), HostId.ToString(), sessionId: "sess-#61",
                maxReceiveBytes: 65536, NullLogger.Instance)
            {
                Capability = initialCapability,
            };
            Registry.RegisterAsync(Connection).GetAwaiter().GetResult();
        }

        public async Task<Host> SeedOnlineHostAsync()
        {
            var owner = await Users.CreateAsync(new User
            {
                CognitoSub = $"sub-{Guid.NewGuid()}",
                Email = $"{Guid.NewGuid()}@hosts.test",
                CreatedAt = T0,
                UpdatedAt = T0,
            });
            return await Hosts.CreateAsync(new Host
            {
                OwnerUserId = owner.Id,
                Status = HostStatus.Online,
                AgentTokenHash = "hash",
                CreatedAt = T0,
                UpdatedAt = T0,
            });
        }
    }

    /// <summary>A do-nothing <see cref="WebSocket"/>: the refresh code never touches the socket.</summary>
    private sealed class StubWebSocket : WebSocket
    {
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus s, string? d, CancellationToken ct) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus s, string? d, CancellationToken ct) => Task.CompletedTask;
        public override void Dispose() { }
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> b, CancellationToken ct) =>
            throw new NotSupportedException();
        public override Task SendAsync(ArraySegment<byte> b, WebSocketMessageType t, bool e, CancellationToken ct) =>
            Task.CompletedTask;
    }
}
