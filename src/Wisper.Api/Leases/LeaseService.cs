using Wisper.Api.Domain;
using Wisper.Api.Infrastructure;
using Wisper.Api.Persistence.HostImages;
using Wisper.Api.Persistence.Hosts;
using Wisper.Api.Persistence.Leases;
using Wisper.Api.Tunnel;
using Wisper.Api.Tunnel.Messages;

namespace Wisper.Api.Leases;

/// <summary>
/// Default <see cref="ILeaseService"/> (docs/API.md §5, docs/DATA_MODEL.md §5). Create validates the
/// requested image/network/resources/TTL against the host's priced allow-list (docs/DATA_MODEL.md §4),
/// runs the wallet gate (docs/DATA_MODEL.md §8), then — and only then — sends <c>lease.create</c> down
/// the host tunnel via the relay and persists an immutable snapshot of what was booked. Wisper owns the
/// id space (docs/TUNNEL.md §1): the relay-minted <c>lease_&lt;guid&gt;</c> id is the lease's primary key,
/// so read/release address the same lease by that id on both the DB and the tunnel.
/// </summary>
public sealed class LeaseService : ILeaseService
{
    private const string Usd = "usd";

    private readonly ILeaseRepository _leases;
    private readonly IHostRepository _hosts;
    private readonly IHostImageRepository _images;
    private readonly ITunnelRelay _relay;
    private readonly ILeaseWalletGate _walletGate;
    private readonly TimeProvider _time;

    public LeaseService(
        ILeaseRepository leases,
        IHostRepository hosts,
        IHostImageRepository images,
        ITunnelRelay relay,
        ILeaseWalletGate walletGate,
        TimeProvider time)
    {
        _leases = leases;
        _hosts = hosts;
        _images = images;
        _relay = relay;
        _walletGate = walletGate;
        _time = time;
    }

    public async Task<LeaseCreationResult> CreateAsync(
        Guid consumerUserId, CreateLeaseRequest request, CancellationToken ct = default)
    {
        var hostId = RequireGuid(request.HostId, "host_id");
        var hostImageId = RequireGuid(request.HostImageId, "host_image_id");
        var network = ParseNetwork(request.Network);
        var ttlSeconds = RequireTtl(request.TtlSeconds);

        // The image must be in this host's priced allow-list and offered to consumers; otherwise the
        // request names something the host isn't selling (docs/API.md §3 — image_not_allowed).
        var image = await _images.GetByIdAsync(hostImageId, ct);
        if (image is null || image.HostId != hostId || !image.Enabled)
        {
            throw new ApiException(
                ApiErrorCode.ImageNotAllowed,
                "The requested image is not in the host's priced allow-list.",
                new { host_id = request.HostId, host_image_id = request.HostImageId });
        }

        // A gone or suspended host is not leasable and is not revealed to the caller (docs/API.md §3).
        var host = await _hosts.GetByIdAsync(hostId, ct);
        if (host is null || host.Status == HostStatus.Suspended)
        {
            throw new ApiException(
                ApiErrorCode.ImageNotAllowed,
                "The requested image is not in the host's priced allow-list.",
                new { host_id = request.HostId, host_image_id = request.HostImageId });
        }

        ValidateNetwork(network, image);
        ValidateTtl(ttlSeconds, image);
        var resources = ValidateResources(request.Resources, image);

        // Wallet gate BEFORE any tunnel frame: no compute is provisioned that can't be paid for
        // (docs/DATA_MODEL.md §8, §14). Wallet-gating itself is a Phase-6 hook that allows for now.
        var holdCents = EstimateHoldCents(ttlSeconds, image.PriceCentsPerMin);
        var decision = await _walletGate.AuthorizeHoldAsync(consumerUserId, holdCents, Usd, ct);
        if (!decision.Allowed)
        {
            throw new ApiException(
                ApiErrorCode.InsufficientFunds,
                "Wallet balance is below the required hold.",
                new { required_cents = decision.RequiredCents, available_cents = decision.AvailableCents });
        }

        // Provision over the tunnel. host_offline / upstream_timeout / lease_failed surface from the relay
        // as ApiExceptions and flow straight to the uniform error envelope (docs/API.md §3).
        var spec = new LeaseCreate
        {
            Image = image.ImageRef,
            Network = PgEnum.ToLabel(network),
            Resources = new LeaseResources
            {
                Cpus = resources.Cpus is { } c ? (double)c : 0,
                MemoryMb = resources.MemoryMb ?? 0,
                Pids = resources.Pids ?? 0,
            },
            TtlSeconds = ttlSeconds,
            Userdata = request.Userdata,
        };
        var result = await _relay.CreateLeaseAsync(host.Id.ToString(), spec, ct);

        // The relay awaits lease.ready before returning (docs/TUNNEL.md §5), so the container is up: the
        // lease is active with the meter started. Wisper owns the id space, so the relay-issued
        // lease_<guid> id is this row's primary key (see TunnelLeaseId).
        if (!TunnelLeaseId.TryParse(result.LeaseId, out var leaseId))
        {
            throw new ApiException(ApiErrorCode.Internal, "The relay returned a malformed lease id.");
        }

        var now = _time.GetUtcNow();
        var lease = new Lease
        {
            Id = leaseId,
            ConsumerUserId = consumerUserId,
            HostId = host.Id,
            HostImageId = image.Id,
            ImageRef = image.ImageRef,
            Network = network,
            Cpus = resources.Cpus,
            MemoryMb = resources.MemoryMb,
            Pids = resources.Pids,
            TtlSeconds = ttlSeconds,
            PriceCentsPerMin = image.PriceCentsPerMin,
            Currency = Usd,
            Status = LeaseStatus.Active,
            WispContractId = result.WispContractId,
            CreatedAt = now,
            StartedAt = now,
            LastMeteredAt = now,
            BillableSeconds = 0,
        };
        var stored = await _leases.CreateAsync(lease, ct);
        return new LeaseCreationResult(stored, holdCents);
    }

    public async Task<LeasePage> ListAsync(
        Guid consumerUserId, LeaseListQuery query, CancellationToken ct = default)
    {
        // ListByConsumerAsync already returns newest-first (leases_consumer_idx order). Apply the status
        // filter and cursor here and collect limit+1 to learn whether another page follows.
        var all = await _leases.ListByConsumerAsync(consumerUserId, ct);
        var ordered = all
            .Where(l => query.Status is not { } s || l.Status == s)
            .Where(l => After(l, query.Cursor));

        var page = new List<LeaseView>(query.Limit);
        Lease? lastIncluded = null;
        var more = false;
        foreach (var lease in ordered)
        {
            if (page.Count == query.Limit)
            {
                more = true;
                break;
            }

            page.Add(LeaseView.From(lease));
            lastIncluded = lease;
        }

        var nextCursor = more && lastIncluded is not null
            ? new LeaseCursor(lastIncluded.CreatedAt, lastIncluded.Id).Encode()
            : null;
        return new LeasePage(page, nextCursor);
    }

    public async Task<LeaseView?> GetAsync(
        Guid consumerUserId, Guid leaseId, CancellationToken ct = default)
    {
        var lease = await OwnedLeaseOrNullAsync(consumerUserId, leaseId, ct);
        return lease is null ? null : LeaseView.From(lease);
    }

    public async Task<LeaseView?> ReleaseAsync(
        Guid consumerUserId, Guid leaseId, CancellationToken ct = default)
    {
        var lease = await OwnedLeaseOrNullAsync(consumerUserId, leaseId, ct);
        if (lease is null)
        {
            return null;
        }

        // Idempotent: an already-ended lease is a safe no-op replay (docs/API.md §5, DELETE is retryable).
        if (lease.Status == LeaseStatus.Ended)
        {
            return LeaseView.From(lease);
        }

        try
        {
            await _relay.ReleaseAsync(lease.HostId.ToString(), TunnelLeaseId.Format(lease.Id), ct);
        }
        catch (ApiException ex) when (ex.Code == ApiErrorCode.HostOffline)
        {
            // No live tunnel means the container is already gone — there is nothing left to release, so
            // marking the lease ended locally is the correct, retry-safe outcome (docs/TUNNEL.md §8).
        }

        var now = _time.GetUtcNow();
        var ended = await _leases.TransitionStateAsync(
            lease.Id, LeaseStatus.Ended, endReason: LeaseEndReason.Released, endedAt: now, ct: ct);
        return LeaseView.From(ended ?? lease);
    }

    private async Task<Lease?> OwnedLeaseOrNullAsync(Guid consumerUserId, Guid leaseId, CancellationToken ct)
    {
        var lease = await _leases.GetByIdAsync(leaseId, ct);

        // Ownership failures return "not found" so the API never reveals a lease the caller can't see.
        return lease is not null && lease.ConsumerUserId == consumerUserId ? lease : null;
    }

    /// <summary>The up-front hold estimate: <c>⌈ttl/60⌉·price</c> (docs/DATA_MODEL.md §8).</summary>
    private static long EstimateHoldCents(int ttlSeconds, long priceCentsPerMin)
    {
        var minutes = (ttlSeconds + 59) / 60; // ceil, integer-only (money is never floats, §1)
        return minutes * priceCentsPerMin;
    }

    private static bool After(Lease lease, LeaseCursor? cursor) =>
        cursor is null || LeaseCursor.Compare(lease.CreatedAt, lease.Id, cursor.CreatedAt, cursor.Id) > 0;

    private static Guid RequireGuid(string? value, string field)
    {
        if (!Guid.TryParse(value, out var id))
        {
            throw new ApiException(
                ApiErrorCode.ValidationError, $"'{field}' must be a valid id.", new { field });
        }

        return id;
    }

    private static NetworkMode ParseNetwork(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw new ApiException(
                ApiErrorCode.ValidationError, "'network' is required.", new { field = "network" });
        }

        if (!Enum.TryParse<NetworkMode>(value, ignoreCase: true, out var mode) || !Enum.IsDefined(mode))
        {
            throw new ApiException(
                ApiErrorCode.ValidationError,
                "Unknown 'network'.",
                new { field = "network", allowed = new[] { "none", "open", "egress" } });
        }

        return mode;
    }

    private static int RequireTtl(int? ttlSeconds)
    {
        if (ttlSeconds is not { } ttl || ttl <= 0)
        {
            throw new ApiException(
                ApiErrorCode.ValidationError,
                "'ttl_seconds' must be a positive integer.",
                new { field = "ttl_seconds" });
        }

        return ttl;
    }

    private static void ValidateNetwork(NetworkMode network, HostImage image)
    {
        if (!image.Networks.Contains(network))
        {
            throw new ApiException(
                ApiErrorCode.ValidationError,
                "The image does not permit the requested network.",
                new
                {
                    field = "network",
                    allowed = image.Networks.Select(PgEnum.ToLabel).ToArray(),
                });
        }
    }

    private static void ValidateTtl(int ttlSeconds, HostImage image)
    {
        if (ttlSeconds > image.MaxTtlSeconds)
        {
            throw new ApiException(
                ApiErrorCode.ValidationError,
                "'ttl_seconds' exceeds the image's maximum.",
                new { field = "ttl_seconds", max = image.MaxTtlSeconds });
        }
    }

    /// <summary>
    /// Validates the requested resource ceilings against the image's offered limits and returns the
    /// snapshot to persist (missing dimensions stay null — the host applies its own default).
    /// </summary>
    private static (decimal? Cpus, int? MemoryMb, int? Pids) ValidateResources(
        LeaseResourcesRequest? resources, HostImage image)
    {
        if (resources is null)
        {
            return (null, null, null);
        }

        decimal? cpus = null;
        if (resources.Cpus is { } requestedCpus)
        {
            if (requestedCpus <= 0)
            {
                throw new ApiException(
                    ApiErrorCode.ValidationError, "'resources.cpus' must be positive.",
                    new { field = "resources.cpus" });
            }

            cpus = (decimal)requestedCpus;
            if (image.MaxCpus is { } maxCpus && cpus > maxCpus)
            {
                throw new ApiException(
                    ApiErrorCode.ValidationError, "'resources.cpus' exceeds the image's maximum.",
                    new { field = "resources.cpus", max = maxCpus });
            }
        }

        var memoryMb = ValidateCeiling(
            resources.MemoryMb, image.MaxMemoryMb, "resources.memory_mb");
        var pids = ValidateCeiling(resources.Pids, image.MaxPids, "resources.pids");
        return (cpus, memoryMb, pids);
    }

    private static int? ValidateCeiling(int? requested, int? max, string field)
    {
        if (requested is not { } value)
        {
            return null;
        }

        if (value <= 0)
        {
            throw new ApiException(
                ApiErrorCode.ValidationError, $"'{field}' must be positive.", new { field });
        }

        if (max is { } ceiling && value > ceiling)
        {
            throw new ApiException(
                ApiErrorCode.ValidationError, $"'{field}' exceeds the image's maximum.",
                new { field, max = ceiling });
        }

        return value;
    }
}
