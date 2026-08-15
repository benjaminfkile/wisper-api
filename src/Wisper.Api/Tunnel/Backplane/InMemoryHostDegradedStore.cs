using System.Collections.Concurrent;

namespace Wisper.Api.Tunnel.Backplane;

/// <summary>
/// In-process <see cref="IHostDegradedStore"/> backed by a <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// Used single-process (dev/tests): two simulated instances can share one store so a host marked
/// degraded on "instance A" is visible on "instance B", exactly as a shared Redis would be.
/// </summary>
public sealed class InMemoryHostDegradedStore : IHostDegradedStore
{
    private readonly ConcurrentDictionary<string, byte> _degraded = new(StringComparer.Ordinal);

    public Task SetDegradedAsync(string hostId, CancellationToken ct = default)
    {
        _degraded[hostId] = 1;
        return Task.CompletedTask;
    }

    public Task ClearDegradedAsync(string hostId, CancellationToken ct = default)
    {
        _degraded.TryRemove(hostId, out _);
        return Task.CompletedTask;
    }

    public Task<bool> IsDegradedAsync(string hostId, CancellationToken ct = default) =>
        Task.FromResult(_degraded.ContainsKey(hostId));

    public Task<IReadOnlyCollection<string>> SnapshotAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyCollection<string>>(_degraded.Keys.ToArray());
}
