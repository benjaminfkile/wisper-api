using System.Net.WebSockets;
using Microsoft.Extensions.Logging.Abstractions;
using Wisper.Api.Tunnel;
using Wisper.Api.Tunnel.Messages;
using Xunit;

namespace Wisper.Api.Tests.Tunnel;

/// <summary>
/// The host's advertised GPU capability on the tunnel wire (task #521): the snake_case <c>gpu</c> block
/// (<c>supported</c> / <c>devices[].{id,class,vram_mb}</c> / <c>max_gpus</c> / <c>isolations</c>) is parsed
/// off the <c>hello.capability</c> and off a <c>host.heartbeat</c> that re-advertises, and the live capability
/// the registry tracks (<see cref="RegistryHostCapabilitySource"/>) surfaces the device ceiling and distinct
/// classes on the <see cref="HostCapabilitySnapshot"/>. Class strings are opaque — mirrored, never
/// interpreted. An older agent that omits the block leaves it <c>null</c> and never faults.
/// </summary>
public class HostGpuWireTests
{
    [Fact]
    public void Hello_carrying_gpu_is_parsed_onto_the_capability()
    {
        var hello = ControlJson.Deserialize<Hello>(HelloJson(Guid.NewGuid(), WithGpu));

        Assert.NotNull(hello);
        var gpu = hello!.Capability!.Gpu;
        Assert.NotNull(gpu);
        Assert.True(gpu!.Supported);
        Assert.Equal(8, gpu.MaxGpus);
        Assert.Equal(new[] { "vgpu", "passthrough" }, gpu.Isolations);
        Assert.Equal(3, gpu.Devices.Count);
        Assert.Equal("gpu-0", gpu.Devices[0].Id);
        Assert.Equal("nvidia-a100", gpu.Devices[0].Class);
        Assert.Equal(81920, gpu.Devices[0].VramMb);
        // The device class strings in advertised order (a repeated class stays repeated until de-duped).
        Assert.Equal(new[] { "nvidia-a100", "nvidia-a100", "nvidia-h100" }, gpu.DeviceClasses);
    }

    [Fact]
    public void Hello_without_gpu_leaves_it_null()
    {
        var hello = ControlJson.Deserialize<Hello>(HelloJson(Guid.NewGuid(), gpuField: null));

        Assert.NotNull(hello);
        Assert.Null(hello!.Capability!.Gpu);
    }

    [Fact]
    public void Hello_gpu_is_surfaced_as_max_gpus_and_distinct_classes_on_the_snapshot()
    {
        var hostId = Guid.NewGuid();
        var hello = ControlJson.Deserialize<Hello>(HelloJson(hostId, WithGpu));
        Assert.NotNull(hello);

        var snapshot = SourceFor(hostId, hello!.Capability!).GetCapability(hostId);

        Assert.NotNull(snapshot);
        Assert.Equal(8, snapshot!.MaxGpus);
        Assert.Equal(new[] { "nvidia-a100", "nvidia-h100" }, snapshot.GpuClasses); // distinct, order-preserving
    }

    [Fact]
    public void Hello_without_gpu_surfaces_zero_and_empty_on_the_snapshot()
    {
        var hostId = Guid.NewGuid();
        var hello = ControlJson.Deserialize<Hello>(HelloJson(hostId, gpuField: null));
        Assert.NotNull(hello);

        var snapshot = SourceFor(hostId, hello!.Capability!).GetCapability(hostId);

        Assert.NotNull(snapshot);
        Assert.Equal(0, snapshot!.MaxGpus);
        Assert.Empty(snapshot.GpuClasses); // never null — an absent gpu block is no GPU
    }

    [Fact]
    public void Heartbeat_carrying_gpu_is_parsed()
    {
        const string json =
            "{\"t\":\"host.heartbeat\",\"leases\":[]," +
            "\"capability\":{\"isolation_levels\":[\"shared\"],\"default_isolation\":\"shared\"," +
            "\"gpu\":{\"supported\":true,\"max_gpus\":2,\"devices\":[{\"id\":\"gpu-0\",\"class\":\"nvidia-h100\",\"vram_mb\":81920}]}}}";

        var heartbeat = ControlJson.Deserialize<HostHeartbeat>(json);

        Assert.NotNull(heartbeat);
        Assert.NotNull(heartbeat!.Capability);
        var gpu = heartbeat.Capability!.Gpu;
        Assert.NotNull(gpu);
        Assert.Equal(2, gpu!.MaxGpus);
        Assert.Equal(new[] { "nvidia-h100" }, gpu.DeviceClasses);
    }

    /// <summary>Registers a live tunnel carrying <paramref name="capability"/> and reads it back via the registry source.</summary>
    private static RegistryHostCapabilitySource SourceFor(Guid hostId, HelloCapability capability)
    {
        var registry = new InMemoryHostRegistry(NullLogger<InMemoryHostRegistry>.Instance);
        var connection = new TunnelConnection(
            new StubWebSocket(), hostId.ToString(), sessionId: "sess-gpu", maxReceiveBytes: 65536, NullLogger.Instance)
        {
            Capability = capability,
        };
        registry.RegisterAsync(connection).GetAwaiter().GetResult();
        return new RegistryHostCapabilitySource(registry);
    }

    /// <summary>A gpu block advertising two a100s and an h100, an 8-GPU ceiling, and two opaque isolation modes.</summary>
    private const string WithGpu =
        "\"gpu\":{\"supported\":true,\"max_gpus\":8,\"isolations\":[\"vgpu\",\"passthrough\"]," +
        "\"devices\":[{\"id\":\"gpu-0\",\"class\":\"nvidia-a100\",\"vram_mb\":81920}," +
        "{\"id\":\"gpu-1\",\"class\":\"nvidia-a100\",\"vram_mb\":81920}," +
        "{\"id\":\"gpu-2\",\"class\":\"nvidia-h100\",\"vram_mb\":81920}]},";

    /// <summary>A minimal handshake hello, optionally advertising the snake_case <c>gpu</c> block.</summary>
    private static string HelloJson(Guid hostId, string? gpuField) =>
        "{\"t\":\"hello\",\"proto\":1,\"agentVersion\":\"1.2.3\",\"wispVersion\":\"0.9.0\"," +
        "\"capability\":{\"images\":[\"alpine\"],\"default\":\"alpine\"," + (gpuField ?? string.Empty) +
        "\"limits\":{\"max_ttl_seconds\":3600,\"max_cpus\":4,\"max_memory_mb\":8192,\"pids_limit\":1024,\"networks\":[\"none\"]}}," +
        "\"capacity\":{\"maxLeases\":4,\"maxStreams\":8}}";

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
