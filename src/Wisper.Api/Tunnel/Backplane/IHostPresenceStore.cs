namespace Wisper.Api.Tunnel.Backplane;

/// <summary>One host's backplane presence: which manager instance currently owns its live tunnel.</summary>
public readonly record struct HostPresence(string HostId, string InstanceId);

/// <summary>
/// Ephemeral presence (docs/DESIGN.md §7): which host is online on which instance. Written by the
/// distributed registry when a tunnel connects/supersedes/disconnects, and read by the distributed
/// relay to find the instance that owns a host's socket so a request can be routed there. Backed by
/// Redis in multi-instance mode (<see cref="RedisHostPresenceStore"/>) and by an in-process map for
/// single-process dev + tests (<see cref="InMemoryHostPresenceStore"/>).
/// </summary>
public interface IHostPresenceStore
{
    /// <summary>Records (or overwrites) <paramref name="hostId"/> as owned by <paramref name="instanceId"/>.</summary>
    Task SetOwnerAsync(string hostId, string instanceId, CancellationToken ct = default);

    /// <summary>Returns the instance that owns <paramref name="hostId"/>'s tunnel, or <c>null</c> if none is online.</summary>
    Task<string?> GetOwnerAsync(string hostId, CancellationToken ct = default);

    /// <summary>
    /// Clears <paramref name="hostId"/>'s presence <b>only if</b> it is still owned by
    /// <paramref name="instanceId"/>, so a stale unregister cannot evict the record a newer owner wrote
    /// (mirrors the in-memory registry's supersede-safe unregister).
    /// </summary>
    Task ClearOwnerAsync(string hostId, string instanceId, CancellationToken ct = default);

    /// <summary>Snapshot of every online host and its owning instance.</summary>
    Task<IReadOnlyCollection<HostPresence>> SnapshotAsync(CancellationToken ct = default);
}
