using StackExchange.Redis;

namespace Wisper.Api.Tunnel.Backplane;

/// <summary>
/// StackExchange.Redis <see cref="ITunnelBackplane"/>: publishes/subscribes over Redis pub/sub, the
/// routing fabric that lets any instance drive any host's tunnel (docs/DESIGN.md §7). Exercised against
/// a real Redis in a separate integration environment — Grunt builds and unit-tests against the
/// <see cref="LoopbackTunnelBackplane"/> instead, so this class only needs to be correct and compile.
/// </summary>
public sealed class RedisTunnelBackplane : ITunnelBackplane
{
    private readonly IConnectionMultiplexer _multiplexer;

    public RedisTunnelBackplane(IConnectionMultiplexer multiplexer) => _multiplexer = multiplexer;

    public async Task PublishAsync(string channel, ReadOnlyMemory<byte> message, CancellationToken ct = default)
    {
        var subscriber = _multiplexer.GetSubscriber();
        await subscriber.PublishAsync(RedisChannel.Literal(channel), message.ToArray());
    }

    public async Task<IAsyncDisposable> SubscribeAsync(
        string channel,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task> handler,
        CancellationToken ct = default)
    {
        var subscriber = _multiplexer.GetSubscriber();
        var redisChannel = RedisChannel.Literal(channel);

        // A dedicated queue keeps messages for this channel ordered (SubscribeAsync onto the returned
        // ChannelMessageQueue delivers sequentially), matching the loopback's per-subscription ordering.
        var queue = await subscriber.SubscribeAsync(redisChannel);
        queue.OnMessage(async message =>
        {
            if (message.Message.HasValue)
            {
                await handler((byte[])message.Message!, CancellationToken.None);
            }
        });

        return new Subscription(queue);
    }

    private sealed class Subscription : IAsyncDisposable
    {
        private readonly ChannelMessageQueue _queue;

        public Subscription(ChannelMessageQueue queue) => _queue = queue;

        public async ValueTask DisposeAsync() => await _queue.UnsubscribeAsync();
    }
}
