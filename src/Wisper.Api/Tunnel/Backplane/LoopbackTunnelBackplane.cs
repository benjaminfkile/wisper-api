using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Wisper.Api.Tunnel.Backplane;

/// <summary>
/// In-process <see cref="ITunnelBackplane"/>: publishes fan out to every subscriber registered on the
/// same channel <b>within this process</b>. Each subscription drains its own ordered queue on a
/// background pump, so delivery is asynchronous (like a real broker) yet ordered per subscription --
/// no message is lost between publish and handler invocation. This is the "fake/looped backplane"
/// the design calls for: it lets two simulated instances (distinct <see cref="WisperInstanceIdentity"/>
/// sharing one loopback) bridge each other with no Redis, which is exactly what the unit tests use.
/// </summary>
public sealed class LoopbackTunnelBackplane : ITunnelBackplane
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Subscription>> _channels =
        new(StringComparer.Ordinal);

    public Task PublishAsync(string channel, ReadOnlyMemory<byte> message, CancellationToken ct = default)
    {
        if (_channels.TryGetValue(channel, out var subscribers))
        {
            // Copy once -- subscribers get independent ownership of the bytes.
            var payload = message.ToArray();
            foreach (var subscriber in subscribers.Values)
            {
                subscriber.Enqueue(payload);
            }
        }

        return Task.CompletedTask;
    }

    public Task<IAsyncDisposable> SubscribeAsync(
        string channel,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task> handler,
        CancellationToken ct = default)
    {
        var subscribers = _channels.GetOrAdd(channel, _ => new ConcurrentDictionary<Guid, Subscription>());
        var id = Guid.NewGuid();
        var subscription = new Subscription(handler, () => subscribers.TryRemove(id, out _));
        subscribers[id] = subscription;
        return Task.FromResult<IAsyncDisposable>(subscription);
    }

    private sealed class Subscription : IAsyncDisposable
    {
        private readonly Channel<byte[]> _queue =
            Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
        private readonly CancellationTokenSource _cts = new();
        private readonly Action _detach;
        private readonly Task _pump;

        public Subscription(Func<ReadOnlyMemory<byte>, CancellationToken, Task> handler, Action detach)
        {
            _detach = detach;
            _pump = Task.Run(async () =>
            {
                try
                {
                    await foreach (var message in _queue.Reader.ReadAllAsync(_cts.Token))
                    {
                        try
                        {
                            await handler(message, _cts.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                        catch
                        {
                            // A handler fault must not kill the pump -- the next message still flows.
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                }
            });
        }

        public void Enqueue(byte[] message) => _queue.Writer.TryWrite(message);

        public async ValueTask DisposeAsync()
        {
            _detach();
            _cts.Cancel();
            _queue.Writer.TryComplete();

            try
            {
                await _pump;
            }
            catch (OperationCanceledException)
            {
            }

            _cts.Dispose();
        }
    }
}
