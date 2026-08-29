using Wisper.Api.Tunnel;
using Wisper.Api.Tunnel.Messages;
using Xunit;

namespace Wisper.Api.Tests.Tunnel;

/// <summary>
/// The host's advertised isolation capability on the tunnel wire (task #417): the snake_case fields
/// <c>isolation_levels</c> / <c>default_isolation</c> are parsed off the <c>hello.capability</c> and off a
/// <c>host.heartbeat</c> that re-advertises. An older agent that omits them leaves the list empty and the
/// default null -- the manager normalizes that to <c>["shared"]</c>/<c>"shared"</c> downstream.
/// </summary>
public class HostIsolationWireTests
{
    [Fact]
    public void Hello_carrying_isolation_is_parsed_onto_the_capability()
    {
        const string json =
            "{\"t\":\"hello\",\"proto\":1,\"agentVersion\":\"1.2.3\",\"wispVersion\":\"0.9.0\"," +
            "\"capability\":{\"images\":[\"alpine\"],\"default\":\"alpine\"," +
            "\"isolation_levels\":[\"shared\",\"vm\"],\"default_isolation\":\"vm\"," +
            "\"limits\":{\"max_ttl_seconds\":3600,\"max_cpus\":4,\"max_memory_mb\":8192,\"pids_limit\":1024,\"networks\":[\"none\"]}}," +
            "\"capacity\":{\"maxLeases\":4,\"maxStreams\":8}}";

        var hello = ControlJson.Deserialize<Hello>(json);

        Assert.NotNull(hello);
        Assert.NotNull(hello!.Capability);
        Assert.Equal(new[] { "shared", "vm" }, hello.Capability!.IsolationLevels);
        Assert.Equal("vm", hello.Capability.DefaultIsolation);
    }

    [Fact]
    public void Hello_without_isolation_leaves_it_null_and_default_null()
    {
        const string json =
            "{\"t\":\"hello\",\"proto\":1,\"agentVersion\":\"1.2.3\",\"wispVersion\":\"0.9.0\"," +
            "\"capability\":{\"images\":[\"alpine\"],\"default\":\"alpine\"," +
            "\"limits\":{\"max_ttl_seconds\":3600,\"max_cpus\":4,\"max_memory_mb\":8192,\"pids_limit\":1024,\"networks\":[\"none\"]}}," +
            "\"capacity\":{\"maxLeases\":4,\"maxStreams\":8}}";

        var hello = ControlJson.Deserialize<Hello>(json);

        Assert.NotNull(hello);
        Assert.NotNull(hello!.Capability);
        // Nullable in the DTO (task #191): omitted list parses to null so the presence layer can
        // distinguish absent from an explicit empty tier list.
        Assert.Null(hello.Capability!.IsolationLevels);
        Assert.Null(hello.Capability.DefaultIsolation);
    }

    [Fact]
    public void Hello_without_the_capability_block_leaves_it_null()
    {
        // A hello that omits the capability block entirely (task #191): the DTO now parses that as null so
        // downstream persistence keeps the last-known advertised isolation instead of normalizing it away.
        const string json =
            "{\"t\":\"hello\",\"proto\":1,\"agentVersion\":\"1.2.3\",\"wispVersion\":\"0.9.0\"," +
            "\"capacity\":{\"maxLeases\":4,\"maxStreams\":8}}";

        var hello = ControlJson.Deserialize<Hello>(json);

        Assert.NotNull(hello);
        Assert.Null(hello!.Capability);
    }

    [Fact]
    public void Heartbeat_carrying_capability_is_parsed()
    {
        const string json =
            "{\"t\":\"host.heartbeat\",\"leases\":[]," +
            "\"capability\":{\"isolation_levels\":[\"shared\",\"gvisor\"],\"default_isolation\":\"gvisor\"}}";

        var heartbeat = ControlJson.Deserialize<HostHeartbeat>(json);

        Assert.NotNull(heartbeat);
        Assert.NotNull(heartbeat!.Capability);
        Assert.Equal(new[] { "shared", "gvisor" }, heartbeat.Capability!.IsolationLevels);
        Assert.Equal("gvisor", heartbeat.Capability.DefaultIsolation);
    }

    [Fact]
    public void Heartbeat_without_capability_leaves_it_null()
    {
        var heartbeat = ControlJson.Deserialize<HostHeartbeat>("{\"t\":\"host.heartbeat\",\"leases\":[]}");

        Assert.NotNull(heartbeat);
        Assert.Null(heartbeat!.Capability);
    }
}
