using System.Net.WebSockets;
using Microsoft.Extensions.Logging.Abstractions;
using Wisper.Api.Tunnel;
using Wisper.Api.Tunnel.Backplane;
using Xunit;

namespace Wisper.Api.Tests.Tunnel.Backplane;

/// <summary>
/// Unit tests for <see cref="DistributedHostRegistry"/>: the physical socket stays owned by the local
/// registry, but this wrapper additionally records host→instance <b>presence</b> in the shared store on
/// register and clears it on disconnect, so the distributed relay on any instance can find who owns a
/// host's tunnel (docs/DESIGN.md §7).
/// </summary>
public class DistributedHostRegistryTests
{
    private static TunnelConnection Connection(string hostId) =>
        new(WebSocket.CreateFromStream(Stream.Null, isServer: true, subProtocol: null, TimeSpan.FromSeconds(30)),
            hostId,
            sessionId: "sess",
            maxReceiveBytes: 65536,
            NullLogger.Instance);

    private static DistributedHostRegistry Build(
        out LocalHostRegistryStub local, out InMemoryHostPresenceStore presence, string instanceId = "inst-A")
        => Build(out local, out presence, out _, instanceId);

    private static DistributedHostRegistry Build(
        out LocalHostRegistryStub local,
        out InMemoryHostPresenceStore presence,
        out InMemoryHostCapabilityStore capabilities,
        string instanceId = "inst-A")
    {
        local = new LocalHostRegistryStub();
        presence = new InMemoryHostPresenceStore();
        capabilities = new InMemoryHostCapabilityStore();
        return new DistributedHostRegistry(
            local, presence, capabilities,
            new WisperInstanceIdentity(instanceId), NullLogger<DistributedHostRegistry>.Instance);
    }

    private static HostCapabilitySnapshot Snapshot() => new(
        new[] { "wisp-base" }, Array.Empty<Wisper.Api.Domain.NetworkMode>(),
        MaxTtlSeconds: 0, MaxCpus: 0, MaxMemoryMb: 0, MaxPids: 0, Os: "linux");

    [Fact]
    public async Task Register_records_presence_and_delegates_locally()
    {
        var registry = Build(out var local, out var presence, "inst-A");
        var conn = Connection("host-1");

        await registry.RegisterAsync(conn);

        Assert.Equal("inst-A", await presence.GetOwnerAsync("host-1"));
        Assert.Equal(new[] { "host-1" }, local.Registered);
        Assert.True(registry.TryGet("host-1", out _));
    }

    [Fact]
    public async Task Unregister_clears_presence_and_delegates_locally()
    {
        var registry = Build(out var local, out var presence, "inst-A");
        var conn = Connection("host-1");
        await registry.RegisterAsync(conn);

        registry.Unregister(conn);

        // Presence clear is fire-and-forget I/O; wait for it to land.
        await WaitUntilAsync(async () => await presence.GetOwnerAsync("host-1") is null);
        Assert.Contains("host-1", local.Unregistered);
    }

    [Fact]
    public async Task Unregister_after_supersede_elsewhere_leaves_the_new_owner()
    {
        var registry = Build(out _, out var presence, "inst-A");
        var conn = Connection("host-1");
        await registry.RegisterAsync(conn);

        // The host reconnected onto inst-B (presence now points there); inst-A's late unregister must
        // not evict inst-B's record.
        await presence.SetOwnerAsync("host-1", "inst-B");
        registry.Unregister(conn);

        await Task.Delay(100);
        Assert.Equal("inst-B", await presence.GetOwnerAsync("host-1"));
    }

    [Fact]
    public async Task Unregister_after_supersede_elsewhere_leaves_the_new_owners_capability()
    {
        var registry = Build(out _, out var presence, out var capabilities, "inst-A");
        var conn = Connection("host-1");
        await registry.RegisterAsync(conn);

        // The host reconnected onto inst-B, which wrote presence AND a fresh capability snapshot.
        // inst-A's late unregister must clear neither -- an unconditional capability delete would
        // strand the host with presence but no capability until its next reconnect.
        await presence.SetOwnerAsync("host-1", "inst-B");
        await capabilities.SetAsync("host-1", Snapshot());
        registry.Unregister(conn);

        await Task.Delay(100);
        Assert.Equal("inst-B", await presence.GetOwnerAsync("host-1"));
        Assert.NotNull(capabilities.Get("host-1"));
    }

    [Fact]
    public async Task Unregister_with_no_new_owner_clears_the_capability()
    {
        var registry = Build(out _, out var presence, out var capabilities, "inst-A");
        var conn = Connection("host-1");
        await registry.RegisterAsync(conn);
        await capabilities.SetAsync("host-1", Snapshot());

        registry.Unregister(conn);

        await WaitUntilAsync(async () =>
            await presence.GetOwnerAsync("host-1") is null && capabilities.Get("host-1") is null);
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
