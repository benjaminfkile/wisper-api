using System.Net.WebSockets;
using Microsoft.Extensions.Logging.Abstractions;
using Wisper.Api.Tunnel;
using Wisper.Api.Tunnel.Messages;
using Xunit;

namespace Wisper.Api.Tests.Tunnel;

/// <summary>
/// The host's advertised contract capacity on the tunnel wire (task #571): the snake_case <c>capacity</c> block
/// nested inside <c>hello.capability</c> (<c>max_contracts</c> / <c>active_contracts</c> / resource totals) is
/// parsed off the hello, and the live capability the registry tracks (<see cref="RegistryHostCapabilitySource"/>)
/// surfaces the concurrent-contract ceiling as <see cref="HostCapabilitySnapshot.MaxContracts"/>. Only the ceiling
/// drives a manager decision (the fast-fail); the resource totals are informational. An older agent that omits the
/// block leaves it <c>null</c> and surfaces as unlimited (<c>0</c>) — it never faults.
/// </summary>
public class HostCapacityWireTests
{
    [Fact]
    public void Hello_carrying_capacity_is_parsed_onto_the_capability()
    {
        var hello = ControlJson.Deserialize<Hello>(HelloJson(WithCapacity));

        Assert.NotNull(hello);
        var capacity = hello!.Capability!.Capacity;
        Assert.NotNull(capacity);
        Assert.Equal(4, capacity!.MaxContracts);
        Assert.Equal(1, capacity.ActiveContracts);
        Assert.Equal(16d, capacity.TotalCpus);
        Assert.Equal(2.5d, capacity.UsedCpus);
        Assert.Equal(65536, capacity.TotalMemoryMb);
        Assert.Equal(8192, capacity.UsedMemoryMb);
    }

    [Fact]
    public void Hello_without_capacity_leaves_it_null()
    {
        var hello = ControlJson.Deserialize<Hello>(HelloJson(capacityField: null));

        Assert.NotNull(hello);
        Assert.Null(hello!.Capability!.Capacity);
    }

    [Fact]
    public void Hello_capacity_surfaces_max_contracts_on_the_snapshot()
    {
        var hostId = Guid.NewGuid();
        var hello = ControlJson.Deserialize<Hello>(HelloJson(WithCapacity));
        Assert.NotNull(hello);

        var snapshot = SourceFor(hostId, hello!.Capability!).GetCapability(hostId);

        Assert.NotNull(snapshot);
        Assert.Equal(4, snapshot!.MaxContracts);
        Assert.True(snapshot.HasContractLimit);
    }

    [Fact]
    public void Hello_without_capacity_surfaces_zero_and_unlimited_on_the_snapshot()
    {
        var hostId = Guid.NewGuid();
        var hello = ControlJson.Deserialize<Hello>(HelloJson(capacityField: null));
        Assert.NotNull(hello);

        var snapshot = SourceFor(hostId, hello!.Capability!).GetCapability(hostId);

        Assert.NotNull(snapshot);
        Assert.Equal(0, snapshot!.MaxContracts); // absent block ⇒ unlimited
        Assert.False(snapshot.HasContractLimit);
    }

    /// <summary>Registers a live tunnel carrying <paramref name="capability"/> and reads it back via the registry source.</summary>
    private static RegistryHostCapabilitySource SourceFor(Guid hostId, HelloCapability capability)
    {
        var registry = new InMemoryHostRegistry(NullLogger<InMemoryHostRegistry>.Instance);
        var connection = new TunnelConnection(
            new StubWebSocket(), hostId.ToString(), sessionId: "sess-cap", maxReceiveBytes: 65536, NullLogger.Instance)
        {
            Capability = capability,
        };
        registry.RegisterAsync(connection).GetAwaiter().GetResult();
        return new RegistryHostCapabilitySource(registry);
    }

    /// <summary>A capacity block advertising a 4-contract ceiling, 1 active, and the informational resource totals.</summary>
    private const string WithCapacity =
        "\"capacity\":{\"max_contracts\":4,\"active_contracts\":1,\"total_cpus\":16,\"used_cpus\":2.5," +
        "\"total_memory_mb\":65536,\"used_memory_mb\":8192},";

    /// <summary>A minimal handshake hello, optionally advertising the nested snake_case <c>capacity</c> block.</summary>
    private static string HelloJson(string? capacityField) =>
        "{\"t\":\"hello\",\"proto\":1,\"agentVersion\":\"1.2.3\",\"wispVersion\":\"0.9.0\"," +
        "\"capability\":{\"images\":[\"alpine\"],\"default\":\"alpine\"," + (capacityField ?? string.Empty) +
        "\"limits\":{\"max_ttl_seconds\":3600,\"max_cpus\":4,\"max_memory_mb\":8192,\"pids_limit\":1024,\"networks\":[\"none\"]}}}";

    /// <summary>A do-nothing <see cref="WebSocket"/>: the capability source never touches the socket.</summary>
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
