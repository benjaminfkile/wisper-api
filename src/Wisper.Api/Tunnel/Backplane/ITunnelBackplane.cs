namespace Wisper.Api.Tunnel.Backplane;

/// <summary>
/// The hand-rolled pub/sub transport that bridges Wisper instances (docs/DESIGN.md §7). The same
/// mechanism carries both routed relay RPC (drive a remote host's tunnel) and bridged byte streams,
/// exactly as the design intends ("we build it once and use it everywhere"). Two implementations:
/// <see cref="RedisTunnelBackplane"/> over StackExchange.Redis for real multi-instance, and
/// <see cref="LoopbackTunnelBackplane"/> in-process for single-process dev + unit tests (no Redis).
/// </summary>
public interface ITunnelBackplane
{
    /// <summary>Publishes <paramref name="message"/> to <paramref name="channel"/>. Fire-and-forget fan-out to subscribers.</summary>
    Task PublishAsync(string channel, ReadOnlyMemory<byte> message, CancellationToken ct = default);

    /// <summary>
    /// Subscribes <paramref name="handler"/> to every message published on <paramref name="channel"/>.
    /// Messages on a single channel from a single publisher are delivered in order. Dispose the returned
    /// handle to unsubscribe.
    /// </summary>
    Task<IAsyncDisposable> SubscribeAsync(
        string channel,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task> handler,
        CancellationToken ct = default);
}
