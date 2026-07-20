using Microsoft.Extensions.Logging;
using Wisper.Api.Domain;
using Wisper.Api.Persistence.HostImages;
using Wisper.Api.Persistence.Hosts;
using Wisper.Api.Persistence.Users;

namespace Wisper.Api.Tunnel;

/// <summary>
/// Drives a host's persisted presence (<c>host_status</c>) from the tunnel lifecycle (docs/TUNNEL.md §3, §8).
/// The tunnel registry is authoritative for <i>liveness</i>, but the catalog reads the DB-online subset
/// re-confirmed against the registry (<see cref="Wisper.Api.Catalog.CatalogService"/>), so nothing appears
/// until the row itself flips <see cref="HostStatus.Online"/> — the wiring this service supplies:
/// <list type="bullet">
/// <item><b>Ready → online</b> (<see cref="GoOnlineIfEligibleAsync"/>): when the handshake completes, flip
/// the host online <b>if</b> it clears the earning gate (<see cref="ConnectGate.CanHostGoOnline"/>): owner
/// Connect-enabled, or every enabled image is zero-priced (task #386, #392). An admin-suspended host never
/// flips online here (docs/API.md §8).</item>
/// <item><b>Tunnel lost → offline</b> (<see cref="GoOfflineAsync"/>): the tunnel coordinator calls this once
/// the loss is durable — grace expired, or the tunnel closed with no leases to protect (docs/TUNNEL.md §8) —
/// stamping last-seen at last-healthy. A momentary blip or a superseding reconnect never reaches here, so
/// the host stays online across those.</item>
/// </list>
/// A non-Guid host id (the dev/no-DB tunnel harness) is a no-op, mirroring
/// <see cref="TunnelDisconnectCoordinator"/>. State reads and the narrow presence write all go through the
/// repositories, so the same wiring serves Postgres and the in-memory dev boot.
/// </summary>
public sealed class HostPresenceService : IHostPresence
{
    private readonly IHostRepository _hosts;
    private readonly IHostImageRepository _images;
    private readonly IUserRepository _users;
    private readonly TimeProvider _time;
    private readonly ILogger<HostPresenceService> _logger;

    public HostPresenceService(
        IHostRepository hosts,
        IHostImageRepository images,
        IUserRepository users,
        TimeProvider time,
        ILogger<HostPresenceService> logger)
    {
        _hosts = hosts;
        _images = images;
        _users = users;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task GoOnlineIfEligibleAsync(string hostId, CancellationToken ct = default)
    {
        if (!Guid.TryParse(hostId, out var id))
        {
            return; // dev/no-DB tunnel host id — no row to flip
        }

        if (await _hosts.GetByIdAsync(id, ct) is not { } host)
        {
            return;
        }

        // Admin-suspended hosts must never come back online on (re)connect (docs/API.md §8). The tunnel also
        // closes such a host 4403, but the gate is fail-safe on its own so presence never clears a suspension.
        if (host.Status == HostStatus.Suspended)
        {
            _logger.LogInformation("host {HostId} tunnel ready but suspended — staying offline", id);
            return;
        }

        var connectStatus = (await _users.GetByIdAsync(host.OwnerUserId, ct))?.ConnectStatus
            ?? ConnectStatus.None;
        var enabled = await _images.ListByHostAsync(id, enabledOnly: true, ct);
        if (!ConnectGate.CanHostGoOnline(connectStatus, enabled.Select(i => i.PriceCentsPerMin)))
        {
            // A priced host whose owner has not completed Connect: the agent may connect and test, but the
            // host stays offline (and out of the catalog) until Connect is enabled (docs/PAYMENTS.md §5).
            _logger.LogInformation(
                "host {HostId} tunnel ready but earning-gated (connect {Status}) — staying offline",
                id, PgEnum.ToLabel(connectStatus));
            return;
        }

        var now = _time.GetUtcNow();
        await _hosts.SetOnlineStateAsync(id, HostStatus.Online, lastSeenAt: now, updatedAt: now, ct);
        _logger.LogInformation("host {HostId} online (tunnel ready)", id);
    }

    /// <inheritdoc />
    public async Task GoOfflineAsync(Guid hostId, DateTimeOffset lastHealthyAt, CancellationToken ct = default)
    {
        // Only a host we actually put online is flipped back; an already-offline host is a no-op and a
        // suspended host is left suspended, so the tunnel lifecycle never clears an admin suspension.
        if (await _hosts.GetByIdAsync(hostId, ct) is not { Status: HostStatus.Online })
        {
            return;
        }

        await _hosts.SetOnlineStateAsync(
            hostId, HostStatus.Offline, lastSeenAt: lastHealthyAt, updatedAt: _time.GetUtcNow(), ct);
        _logger.LogInformation("host {HostId} offline (tunnel lost)", hostId);
    }
}
