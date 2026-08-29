using System.Collections.Concurrent;

namespace Wisper.Api.Tunnel.Backplane;

/// <summary>
/// In-process <see cref="IHostCapabilityStore"/> for single-instance boots and loopback tests.
/// Two simulated instances can share one store to verify that a capability written by "instance A" is
/// visible on "instance B" -- the same guarantee a Redis-backed store provides in production.
/// </summary>
public sealed class InMemoryHostCapabilityStore : IHostCapabilityStore
{
    private readonly ConcurrentDictionary<string, HostCapabilitySnapshot> _snapshots =
        new(StringComparer.Ordinal);

    public Task SetAsync(string hostId, HostCapabilitySnapshot snapshot, CancellationToken ct = default)
    {
        _snapshots[hostId] = snapshot;
        return Task.CompletedTask;
    }

    public HostCapabilitySnapshot? Get(string hostId) =>
        _snapshots.TryGetValue(hostId, out var snapshot) ? snapshot : null;

    public Task ClearAsync(string hostId, CancellationToken ct = default)
    {
        _snapshots.TryRemove(hostId, out _);
        return Task.CompletedTask;
    }
}
