using System.Net.WebSockets;
using Microsoft.Extensions.Logging.Abstractions;
using Wisper.Api.Tunnel;
using Wisper.Api.Tunnel.Messages;
using Xunit;

namespace Wisper.Api.Tests.Tunnel;

/// <summary>
/// The host's advertised container OS from the tunnel <c>hello.capability</c> (docs/TUNNEL.md §5, task #314):
/// a hello carrying the snake_case <c>os</c> field is parsed onto <see cref="HelloCapability.Os"/> and the
/// live capability the registry tracks (<see cref="RegistryHostCapabilitySource"/>) surfaces it on the
/// <see cref="HostCapabilitySnapshot"/>. It is surfacing only — an older agent that omits <c>os</c> leaves it
/// <c>null</c> (unknown) and never faults, so nothing downstream regresses.
/// </summary>
public class HostCapabilityOsTests
{
    [Theory]
    [InlineData("linux")]
    [InlineData("windows")]
    public void Hello_carrying_os_is_parsed_and_surfaced_on_the_snapshot(string os)
    {
        var hostId = Guid.NewGuid();
        var hello = ControlJson.Deserialize<Hello>(HelloJson(hostId, os));
        Assert.NotNull(hello);
        Assert.NotNull(hello!.Capability);
        Assert.Equal(os, hello.Capability!.Os);

        var source = SourceFor(hostId, hello.Capability);

        var snapshot = source.GetCapability(hostId);
        Assert.NotNull(snapshot);
        Assert.Equal(os, snapshot!.Os);
        // Sanity: the rest of the capability still flattens as before.
        Assert.Contains("alpine", snapshot.Images);
    }

    [Fact]
    public void Hello_without_os_leaves_it_null_and_does_not_fault()
    {
        var hostId = Guid.NewGuid();
        var hello = ControlJson.Deserialize<Hello>(HelloJson(hostId, os: null));
        Assert.NotNull(hello);
        Assert.NotNull(hello!.Capability);
        Assert.Null(hello.Capability!.Os);

        var source = SourceFor(hostId, hello.Capability);

        var snapshot = source.GetCapability(hostId);
        Assert.NotNull(snapshot);
        Assert.Null(snapshot!.Os);
    }

    /// <summary>Registers a live tunnel carrying <paramref name="capability"/> and reads it back via the registry source.</summary>
    private static RegistryHostCapabilitySource SourceFor(Guid hostId, HelloCapability capability)
    {
        var registry = new InMemoryHostRegistry(NullLogger<InMemoryHostRegistry>.Instance);
        var connection = new TunnelConnection(
            new StubWebSocket(), hostId.ToString(), sessionId: "sess-os", maxReceiveBytes: 65536, NullLogger.Instance)
        {
            Capability = capability,
        };
        registry.RegisterAsync(connection).GetAwaiter().GetResult();
        return new RegistryHostCapabilitySource(registry);
    }

    /// <summary>A minimal handshake hello, optionally advertising the snake_case <c>os</c> field.</summary>
    private static string HelloJson(Guid hostId, string? os)
    {
        var osField = os is null ? string.Empty : $"\"os\":\"{os}\",";
        return
            "{\"t\":\"hello\",\"proto\":1,\"agentVersion\":\"1.2.3\",\"wispVersion\":\"0.9.0\"," +
            "\"capability\":{\"images\":[\"alpine\"],\"default\":\"alpine\"," + osField +
            "\"limits\":{\"max_ttl_seconds\":3600,\"max_cpus\":4,\"max_memory_mb\":8192,\"pids_limit\":1024,\"networks\":[\"none\"]}}," +
            "\"capacity\":{\"maxLeases\":4,\"maxStreams\":8}}";
    }

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
