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
    {
        local = new LocalHostRegistryStub();
        presence = new InMemoryHostPresenceStore();
        return new DistributedHostRegistry(
            local, presence, new WisperInstanceIdentity(instanceId), NullLogger<DistributedHostRegistry>.Instance);
    }

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
