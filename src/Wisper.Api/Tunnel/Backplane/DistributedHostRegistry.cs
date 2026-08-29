using Microsoft.Extensions.Logging;

namespace Wisper.Api.Tunnel.Backplane;

/// <summary>
/// Multi-instance <see cref="IHostRegistry"/> (docs/DESIGN.md §7). Live tunnels are still physical
/// sockets on <b>this</b> instance, so the local <see cref="InMemoryHostRegistry"/> keeps owning them
/// (register/supersede/lookup/enumerate are all local). This wrapper's added jobs are <b>presence</b>
/// and <b>capability publishing</b>: when a host connects here it records <c>host → thisInstance</c>
/// in the shared <see cref="IHostPresenceStore"/> and serialises the <see cref="HostCapabilitySnapshot"/>
/// into the shared <see cref="IHostCapabilityStore"/>, so any instance can resolve both facts without
/// routing to the tunnel owner. On disconnect both records are cleared atomically in the background --
/// a dead tunnel never leaves a readable snapshot. <see cref="TryGet"/>/<see cref="Online"/> remain
/// local-only (a remote host has no local <see cref="TunnelConnection"/> to hand back); routing to a
/// remote host goes through <see cref="DistributedTunnelRelay"/>, not this registry.
/// </summary>
public sealed class DistributedHostRegistry : IHostRegistry
{
    private readonly IHostRegistry _local;
    private readonly IHostPresenceStore _presence;
    private readonly IHostCapabilityStore _capabilityStore;
    private readonly WisperInstanceIdentity _identity;
    private readonly ILogger<DistributedHostRegistry> _logger;

    public DistributedHostRegistry(
        IHostRegistry local,
        IHostPresenceStore presence,
        IHostCapabilityStore capabilityStore,
        WisperInstanceIdentity identity,
        ILogger<DistributedHostRegistry> logger)
    {
        _local = local;
        _presence = presence;
        _capabilityStore = capabilityStore;
        _identity = identity;
        _logger = logger;
    }

    public async Task RegisterAsync(TunnelConnection connection, CancellationToken ct = default)
    {
        await _local.RegisterAsync(connection, ct);
        await _presence.SetOwnerAsync(connection.HostId, _identity.InstanceId, ct);

        // Publish the capability snapshot so any instance can read it without routing to the tunnel
        // owner. Lifecycle is tied to presence: both are written here and cleared together on unregister
        // -- a stale capability can never outlive its tunnel (task #17).
        var snapshot = RegistryHostCapabilitySource.BuildSnapshot(connection.Capability);
        if (snapshot is not null)
        {
            await _capabilityStore.SetAsync(connection.HostId, snapshot, ct);
        }

        _logger.LogInformation(
            "presence: host {HostId} now owned by instance {InstanceId}", connection.HostId, _identity.InstanceId);
    }

    public void Unregister(TunnelConnection connection)
    {
        _local.Unregister(connection);

        // Presence and capability clear is I/O but the interface is synchronous; run both in the
        // background and log failures. Correctness does not hinge on them landing promptly: a later
        // register (this host reconnecting anywhere) overwrites both records.
        var hostId = connection.HostId;
        _ = Task.Run(async () =>
        {
            try
            {
                await _presence.ClearOwnerAsync(hostId, _identity.InstanceId);

                // Only clear the capability when no instance owns the host anymore. On a supersede --
                // the host already re-registered on another instance -- that instance has just written
                // a fresh snapshot, and an unconditional delete here would strand the host with
                // presence but no capability until its next reconnect (degraded os/effective fields
                // and host_offline on images validation from non-owner instances).
                if (await _presence.GetOwnerAsync(hostId) is null)
                {
                    await _capabilityStore.ClearAsync(hostId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "presence: failed to clear owner for host {HostId}", hostId);
            }
        });
    }

    public bool TryGet(string hostId, out TunnelConnection? connection) => _local.TryGet(hostId, out connection);

    public IReadOnlyCollection<TunnelConnection> Online => _local.Online;
}
