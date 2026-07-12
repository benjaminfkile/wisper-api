using System.Collections.Concurrent;

namespace Wisper.Api.Tunnel.Backplane;

/// <summary>
/// In-process <see cref="IHostPresenceStore"/> backed by a <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// Used single-process (dev/tests): the same instance can be shared by two simulated managers so their
/// presence is visible to each other, exactly as a shared Redis would be.
/// </summary>
public sealed class InMemoryHostPresenceStore : IHostPresenceStore
{
    private readonly ConcurrentDictionary<string, string> _owners = new(StringComparer.Ordinal);

    public Task SetOwnerAsync(string hostId, string instanceId, CancellationToken ct = default)
    {
        _owners[hostId] = instanceId;
        return Task.CompletedTask;
    }

    public Task<string?> GetOwnerAsync(string hostId, CancellationToken ct = default) =>
        Task.FromResult(_owners.TryGetValue(hostId, out var instanceId) ? instanceId : null);

    public Task ClearOwnerAsync(string hostId, string instanceId, CancellationToken ct = default)
    {
        // Remove only if this exact instance is still the owner (supersede-safe).
        _owners.TryRemove(new KeyValuePair<string, string>(hostId, instanceId));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyCollection<HostPresence>> SnapshotAsync(CancellationToken ct = default)
    {
        var snapshot = _owners
            .Select(kv => new HostPresence(kv.Key, kv.Value))
            .ToArray();
        return Task.FromResult<IReadOnlyCollection<HostPresence>>(snapshot);
    }
}
