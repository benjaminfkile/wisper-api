using Wisper.Api.Tunnel.Backplane;
using Xunit;

namespace Wisper.Api.Tests.Tunnel.Backplane;

/// <summary>
/// Unit tests for the in-process <see cref="InMemoryHostPresenceStore"/> — presence (which host is on
/// which instance) that a shared Redis would hold in multi-instance mode (docs/DESIGN.md §7). The
/// supersede-safe clear is the subtle bit: a stale unregister from the old owner must not evict a record
/// a newer owner has already written (mirrors the registry's supersede-on-reconnect).
/// </summary>
public class HostPresenceStoreTests
{
    [Fact]
    public async Task Set_then_get_returns_the_owner()
    {
        var store = new InMemoryHostPresenceStore();
        await store.SetOwnerAsync("host-1", "inst-A");

        Assert.Equal("inst-A", await store.GetOwnerAsync("host-1"));
    }

    [Fact]
    public async Task Get_unknown_host_is_null()
    {
        var store = new InMemoryHostPresenceStore();
        Assert.Null(await store.GetOwnerAsync("ghost"));
    }

    [Fact]
    public async Task Set_overwrites_on_reconnect_elsewhere()
    {
        var store = new InMemoryHostPresenceStore();
        await store.SetOwnerAsync("host-1", "inst-A");
        await store.SetOwnerAsync("host-1", "inst-B");

        Assert.Equal("inst-B", await store.GetOwnerAsync("host-1"));
    }

    [Fact]
    public async Task Clear_by_current_owner_removes_the_record()
    {
        var store = new InMemoryHostPresenceStore();
        await store.SetOwnerAsync("host-1", "inst-A");

        await store.ClearOwnerAsync("host-1", "inst-A");

        Assert.Null(await store.GetOwnerAsync("host-1"));
    }

    [Fact]
    public async Task Clear_by_a_stale_owner_does_not_evict_the_new_owner()
    {
        var store = new InMemoryHostPresenceStore();
        // Host reconnected onto inst-B; the old inst-A now fires a late unregister.
        await store.SetOwnerAsync("host-1", "inst-B");

        await store.ClearOwnerAsync("host-1", "inst-A");

        Assert.Equal("inst-B", await store.GetOwnerAsync("host-1"));
    }

    [Fact]
    public async Task Snapshot_lists_every_online_host()
    {
        var store = new InMemoryHostPresenceStore();
        await store.SetOwnerAsync("host-1", "inst-A");
        await store.SetOwnerAsync("host-2", "inst-B");

        var snapshot = await store.SnapshotAsync();

        Assert.Equal(2, snapshot.Count);
        Assert.Contains(new HostPresence("host-1", "inst-A"), snapshot);
        Assert.Contains(new HostPresence("host-2", "inst-B"), snapshot);
    }
}
