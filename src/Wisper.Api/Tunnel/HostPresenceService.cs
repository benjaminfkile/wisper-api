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
/// until the row itself flips <see cref="HostStatus.Online"/> -- the wiring this service supplies:
/// <list type="bullet">
/// <item><b>Ready → online</b> (<see cref="GoOnlineIfEligibleAsync"/>): when the handshake completes, flip
/// the host online <b>if</b> it clears the earning gate (<see cref="ConnectGate.CanHostGoOnline"/>): owner
/// Connect-enabled, or every enabled image is zero-priced (task #386, #392). An admin-suspended host never
/// flips online here (docs/API.md §8).</item>
/// <item><b>Tunnel lost → offline</b> (<see cref="GoOfflineAsync"/>): the tunnel coordinator calls this once
/// the loss is durable -- grace expired, or the tunnel closed with no leases to protect (docs/TUNNEL.md §8) --
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
    public async Task RefreshAdvertisedVersionsAndCapacityAsync(
        string hostId,
        string? wispVersion,
        string? agentVersion,
        int maxLeases,
        int maxStreams,
        CancellationToken ct = default)
    {
        if (!Guid.TryParse(hostId, out var id))
        {
            return; // dev/no-DB tunnel host id (no row to refresh)
        }

        // Never mutate a suspended host, and a missing host has nothing to refresh.
        if (await _hosts.GetByIdAsync(id, ct) is not { } host || host.Status == HostStatus.Suspended)
        {
            return;
        }

        var wisp = NormalizeVersion(wispVersion);
        var agent = NormalizeVersion(agentVersion);
        int? leases = maxLeases > 0 ? maxLeases : null;
        int? streams = maxStreams > 0 ? maxStreams : null;

        if (wisp == host.WispVersion
            && agent == host.AgentVersion
            && leases == host.MaxLeases
            && streams == host.MaxStreams)
        {
            return; // no change: avoid a pointless write (and updated_at churn) on reconnect
        }

        await _hosts.SetAdvertisedVersionsAndCapacityAsync(
            id, wisp, agent, leases, streams, _time.GetUtcNow(), ct);
        _logger.LogInformation(
            "host {HostId} advertised versions/capacity refreshed: wisp={WispVersion} agent={AgentVersion} maxLeases={MaxLeases} maxStreams={MaxStreams}",
            id, wisp, agent, leases, streams);
    }

    private static string? NormalizeVersion(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <inheritdoc />
    public async Task GoOnlineIfEligibleAsync(
        string hostId,
        IReadOnlyList<string>? isolationLevels = null,
        string? defaultIsolation = null,
        IReadOnlyList<string>? gpuClasses = null,
        int gpuCount = 0,
        CancellationToken ct = default)
    {
        if (!Guid.TryParse(hostId, out var id))
        {
            return; // dev/no-DB tunnel host id -- no row to flip
        }

        if (await _hosts.GetByIdAsync(id, ct) is not { } host)
        {
            return;
        }

        // Admin-suspended hosts must never come back online on (re)connect (docs/API.md §8). No frame closes
        // a suspended host today: the 4403 close code is reserved but not sent (docs/TUNNEL.md §3), so the
        // tunnel stays up and suspension is enforced solely by this gate refusing to flip the row online.
        if (host.Status == HostStatus.Suspended)
        {
            _logger.LogInformation("host {HostId} tunnel ready but suspended -- staying offline", id);
            return;
        }

        // Persist the advertised isolation capability from this hello regardless of the earning gate, so the
        // catalog reflects what the (online-or-not-yet) host offers. An absent capability block, or one that
        // sends no isolation levels, is "no update, keep last known" (task #191): an agent cannot
        // legitimately advertise zero tiers, so a hello with an empty list must never overwrite a kata/gVisor
        // host's persisted advertisement. A fresh row's DB default of ["shared"]/"shared" remains, which is
        // what makes a first-ever hello with nothing known still surface as shared.
        if (isolationLevels is { Count: > 0 })
        {
            var (levels, def) = HostIsolation.Normalize(isolationLevels, defaultIsolation);
            await _hosts.SetAdvertisedIsolationAsync(id, levels, def, _time.GetUtcNow(), ct);
        }

        // Persist the advertised GPU capability from this hello the same way (task #521). A null gpuClasses is
        // an absent gpu block (an older agent) -- leave the persisted GPU as-is rather than nulling it; a
        // present-but-empty list is a GPU-aware agent reporting no devices, which resets to []/0.
        if (gpuClasses is not null)
        {
            await _hosts.SetAdvertisedGpuAsync(
                id, HostGpu.NormalizeClasses(gpuClasses), Math.Max(0, gpuCount), _time.GetUtcNow(), ct);
        }

        var connectStatus = (await _users.GetByIdAsync(host.OwnerUserId, ct))?.ConnectStatus
            ?? ConnectStatus.None;
        var enabled = await _images.ListByHostAsync(id, enabledOnly: true, ct);
        if (!ConnectGate.CanHostGoOnline(connectStatus, enabled.Select(i => i.PriceCentsPerMin)))
        {
            // A priced host whose owner has not completed Connect: the agent may connect and test, but the
            // host stays offline (and out of the catalog) until Connect is enabled (docs/PAYMENTS.md §5).
            _logger.LogInformation(
                "host {HostId} tunnel ready but earning-gated (connect {Status}) -- staying offline",
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

    /// <inheritdoc />
    public async Task RefreshAdvertisedIsolationAsync(
        string hostId,
        IReadOnlyList<string>? isolationLevels,
        string? defaultIsolation,
        CancellationToken ct = default)
    {
        if (!Guid.TryParse(hostId, out var id))
        {
            return; // dev/no-DB tunnel host id -- no row to refresh
        }

        // An absent isolation list is "no update, keep last known" (task #191): an agent cannot legitimately
        // advertise zero tiers, so a heartbeat capability that omits (or empties) the list must never
        // overwrite the persisted advertisement.
        if (isolationLevels is not { Count: > 0 })
        {
            return;
        }

        // Never resurrect isolation on a suspended host, and a missing host has nothing to refresh.
        if (await _hosts.GetByIdAsync(id, ct) is not { } host || host.Status == HostStatus.Suspended)
        {
            return;
        }

        var (levels, def) = HostIsolation.Normalize(isolationLevels, defaultIsolation);
        if (def == host.DefaultIsolation && levels.SequenceEqual(host.IsolationLevels, StringComparer.Ordinal))
        {
            return; // no change -- avoid a pointless write (and updated_at churn) every heartbeat
        }

        await _hosts.SetAdvertisedIsolationAsync(id, levels, def, _time.GetUtcNow(), ct);
        _logger.LogInformation("host {HostId} advertised isolation refreshed", id);
    }

    /// <inheritdoc />
    public async Task RefreshAdvertisedGpuAsync(
        string hostId,
        IReadOnlyList<string>? gpuClasses,
        int gpuCount,
        CancellationToken ct = default)
    {
        if (!Guid.TryParse(hostId, out var id))
        {
            return; // dev/no-DB tunnel host id -- no row to refresh
        }

        // Never resurrect GPU on a suspended host, and a missing host has nothing to refresh.
        if (await _hosts.GetByIdAsync(id, ct) is not { } host || host.Status == HostStatus.Suspended)
        {
            return;
        }

        var classes = HostGpu.NormalizeClasses(gpuClasses);
        var count = Math.Max(0, gpuCount);
        if (count == host.GpuCount && classes.SequenceEqual(host.GpuClasses, StringComparer.Ordinal))
        {
            return; // no change -- avoid a pointless write (and updated_at churn) every heartbeat
        }

        await _hosts.SetAdvertisedGpuAsync(id, classes, count, _time.GetUtcNow(), ct);
        _logger.LogInformation("host {HostId} advertised GPU refreshed", id);
    }
}
