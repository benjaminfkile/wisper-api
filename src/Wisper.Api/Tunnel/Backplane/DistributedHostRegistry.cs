using Microsoft.Extensions.Logging;

namespace Wisper.Api.Tunnel.Backplane;

/// <summary>
/// Multi-instance <see cref="IHostRegistry"/> (docs/DESIGN.md §7). Live tunnels are still physical
/// sockets on <b>this</b> instance, so the local <see cref="InMemoryHostRegistry"/> keeps owning them
/// (register/supersede/lookup/enumerate are all local). This wrapper's added job is <b>presence</b>:
/// when a host connects here it records <c>host → thisInstance</c> in the shared
/// <see cref="IHostPresenceStore"/>, and on disconnect it clears it — so the distributed relay on any
/// instance can find who owns a given host's tunnel. <see cref="TryGet"/>/<see cref="Online"/> remain
/// local-only (a remote host has no local <see cref="TunnelConnection"/> to hand back); routing to a
/// remote host goes through <see cref="DistributedTunnelRelay"/>, not this registry.
/// </summary>
public sealed class DistributedHostRegistry : IHostRegistry
{
    private readonly IHostRegistry _local;
    private readonly IHostPresenceStore _presence;
    private readonly WisperInstanceIdentity _identity;
    private readonly ILogger<DistributedHostRegistry> _logger;

    public DistributedHostRegistry(
        IHostRegistry local,
        IHostPresenceStore presence,
        WisperInstanceIdentity identity,
        ILogger<DistributedHostRegistry> logger)
    {
        _local = local;
        _presence = presence;
        _identity = identity;
        _logger = logger;
    }

    public async Task RegisterAsync(TunnelConnection connection, CancellationToken ct = default)
    {
        await _local.RegisterAsync(connection, ct);
        await _presence.SetOwnerAsync(connection.HostId, _identity.InstanceId, ct);
        _logger.LogInformation(
            "presence: host {HostId} now owned by instance {InstanceId}", connection.HostId, _identity.InstanceId);
    }

    public void Unregister(TunnelConnection connection)
    {
        _local.Unregister(connection);

        // Presence clear is I/O but the interface is synchronous; run it in the background and log
        // failures. Correctness does not hinge on it landing promptly: a later SetOwner (this host
        // reconnecting anywhere) overwrites the record, and a stale record only ever points routing at
        // an instance that will answer host_offline.
        var hostId = connection.HostId;
        _ = Task.Run(async () =>
        {
            try
            {
                await _presence.ClearOwnerAsync(hostId, _identity.InstanceId);
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
