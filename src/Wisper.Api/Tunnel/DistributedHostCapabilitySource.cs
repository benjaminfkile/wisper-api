using Wisper.Api.Tunnel.Backplane;

namespace Wisper.Api.Tunnel;

/// <summary>
/// Multi-instance <see cref="IHostCapabilitySource"/> (task #17, docs/DESIGN.md §7). Reads the capability
/// snapshot from the local registry fast path (the tunnel-owner instance pays no I/O) and falls back to the
/// shared backplane store when the host's tunnel lives on a different instance. Bound instead of
/// <see cref="RegistryHostCapabilitySource"/> when the backplane is enabled; single-instance boots keep the
/// original registry-only source.
/// </summary>
public sealed class DistributedHostCapabilitySource : IHostCapabilitySource
{
    private readonly RegistryHostCapabilitySource _local;
    private readonly IHostCapabilityStore _store;

    public DistributedHostCapabilitySource(RegistryHostCapabilitySource local, IHostCapabilityStore store)
    {
        _local = local;
        _store = store;
    }

    public HostCapabilitySnapshot? GetCapability(Guid hostId)
    {
        // Fast path: local registry (the tunnel-owner instance, no backplane I/O).
        var snapshot = _local.GetCapability(hostId);
        if (snapshot is not null) return snapshot;

        // Remote path: backplane capability store. Synchronous per the interface contract; the
        // StackExchange.Redis sync API is used by the Redis-backed store (task #17 §3).
        return _store.Get(hostId.ToString());
    }
}
