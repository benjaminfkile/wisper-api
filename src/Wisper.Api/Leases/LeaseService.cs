using System.Text.Json;
using Wisper.Api.Domain;
using Wisper.Api.Infrastructure;
using Wisper.Api.Persistence.HostImages;
using Wisper.Api.Persistence.Hosts;
using Wisper.Api.Persistence.Leases;
using Wisper.Api.Policy;
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

    /// <summary>
    /// Create-time env caps, mirroring wisp's own limits so we guard the tunnel rather than re-impose a
    /// business rule (docs/TUNNEL.md §5, §13): at most <see cref="MaxEnvEntries"/> keys and
    /// <see cref="MaxEnvSerializedBytes"/> of serialized payload. Over either → <c>validation_error</c>.
    /// </summary>
    private const int MaxEnvEntries = 128;
    private const int MaxEnvSerializedBytes = 256 * 1024;

    private readonly ILeaseRepository _leases;
    private readonly IHostRepository _hosts;
    private readonly IHostImageRepository _images;
    private readonly ITunnelRelay _relay;
    private readonly IHostCapabilitySource _capabilities;
    private readonly ILeaseWalletGate _walletGate;
    private readonly PlatformPolicyService _policy;
    private readonly TimeProvider _time;

    public LeaseService(
        ILeaseRepository leases,
        IHostRepository hosts,
        IHostImageRepository images,
        ITunnelRelay relay,
        IHostCapabilitySource capabilities,
        ILeaseWalletGate walletGate,
        PlatformPolicyService policy,
        TimeProvider time)
    {
        _leases = leases;
        _hosts = hosts;
        _images = images;
        _relay = relay;
        _capabilities = capabilities;
        _walletGate = walletGate;
        _policy = policy;
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
        ValidateEnv(request.Env);
        var isolation = await ResolveIsolationAsync(request.Isolation, host, ct);

        // Wallet gate BEFORE any tunnel frame: no compute is provisioned that can't be paid for
        // (docs/DATA_MODEL.md §8, §14, docs/PAYMENTS.md §4). It also enforces the per-user concurrency cap
        // (throwing at_capacity). Insufficient wallet balance → 402, and no lease.create is ever sent.
        var holdCents = LeaseHoldPricing.EstimateHoldCents(ttlSeconds, image.PriceCentsPerMin);
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
                Gpus = resources.Gpus,
            },
            TtlSeconds = ttlSeconds,
            Userdata = request.Userdata,
            // The resolved isolation level travels down the tunnel to the agent and wisp, which re-validates
            // it as the real security boundary (task #418, docs/TUNNEL.md §5).
            Isolation = isolation,
            // Create-time env is forwarded down the tunnel for secret injection, but never lands on the
            // lease snapshot below: env may carry secrets, exists only to provision the container, and is
            // out of scope for the metering/read surface (docs/TUNNEL.md §13). Guarded by ValidateEnv above.
            Env = request.Env,
        };
        var result = await _relay.CreateLeaseAsync(host.Id.ToString(), spec, ct);

        // The relay awaits lease.ready before returning (docs/TUNNEL.md §5), so the container is up: the
        // lease is active with the meter started. Wisper owns the id space, so the relay-issued
        // lease_<guid> id is this row's primary key (see TunnelLeaseId).
        if (!TunnelLeaseId.TryParse(result.LeaseId, out var leaseId))
        {
            throw new ApiException(ApiErrorCode.Internal, "The relay returned a malformed lease id.");
        }

        // Persist the lease row BEFORE earmarking the hold (task #540). The hold's ledger transaction (and
        // its entries) carry this lease_id under a FK → leases.id (docs/DATA_MODEL.md §7); posting it before
        // the row exists violates ledger_transactions_lease_id_fkey against the real SQL store, 500s the
        // create, and leaks the host contract. hold_txn_id starts null (a valid state for the reverse FK
        // leases.hold_txn_id → ledger_transactions.id) and is stamped once the hold posts below. The balance/
        // caps authorization ran above, before any tunnel frame, so the 402 gate is unaffected — only the
        // hold *posting* moves after persistence, not the *authorization check*.
        var now = _time.GetUtcNow();
        var lease = new Lease
        {
            Id = leaseId,
            HoldTxnId = null,
            ConsumerUserId = consumerUserId,
            HostId = host.Id,
            HostImageId = image.Id,
            ImageRef = image.ImageRef,
            Network = network,
            Isolation = isolation,
            Cpus = resources.Cpus,
            MemoryMb = resources.MemoryMb,
            Pids = resources.Pids,
            Gpus = resources.Gpus,
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

        // Earmark the hold now the lease ROW exists (docs/PAYMENTS.md §4): wallet → lease_holds. The meter
        // debits it per tick; the remainder is released at lease end. A free image places no hold. If the
        // hold fails here — the wallet drained in the AuthorizeHold→PlaceHold race (→ 402), or any ledger
        // error — the container is already provisioned on the host: tear that downstream contract down and
        // mark the lease failed so no zombie contract rides out its TTL (task #540, docs/TUNNEL.md §8).
        Guid? holdTxnId;
        try
        {
            holdTxnId = await _walletGate.PlaceHoldAsync(consumerUserId, leaseId, holdCents, Usd, ct);
        }
        catch
        {
            await TearDownFailedCreateAsync(host.Id, result.LeaseId, leaseId, now, ct);
            throw;
        }

        // Stamp the hold transaction id onto the persisted row now it exists (a free image leaves it null).
        if (holdTxnId is { } txnId)
        {
            stored = await _leases.UpdateAsync(stored with { HoldTxnId = txnId }, ct);
        }

        // Surface the host's advertised container OS on the 201, like the dev endpoint and LeaseView do
        // (task #316) — read from the live capability, null-safe when offline/pre-os (surfacing only).
        return new LeaseCreationResult(stored, holdCents, OsOf(host.Id));
    }

    /// <summary>
    /// Undoes a create that provisioned the container on the host but could not complete (the wallet hold
    /// failed to post). It tears the downstream contract down over the tunnel — the same <c>lease.release</c>
    /// teardown <see cref="ReleaseAsync"/> uses — so it does not ride out its TTL as a zombie contract
    /// (task #540, docs/TUNNEL.md §8), and marks the persisted lease row <c>failed</c>. Both steps are
    /// best-effort: a host that is already gone (<c>host_offline</c>) means the container is gone too, and a
    /// teardown error must never mask the original failure the caller is about to see re-thrown.
    /// </summary>
    private async Task TearDownFailedCreateAsync(
        Guid hostId, string tunnelLeaseId, Guid leaseId, DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            await _relay.ReleaseAsync(hostId.ToString(), tunnelLeaseId, ct);
        }
        catch (ApiException)
        {
            // Best-effort: the container is either torn down or already unreachable. Either way the create
            // still fails; don't let a teardown error replace the real cause on its way up.
        }

        try
        {
            await _leases.TransitionStateAsync(
                leaseId, LeaseStatus.Failed, endReason: LeaseEndReason.PaymentFailed, endedAt: now, ct: ct);
        }
        catch (Exception)
        {
            // Marking the row failed is best-effort cleanup too; the original hold exception is the one that
            // matters, so a failure to flip the status here must not shadow it.
        }
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

            page.Add(LeaseView.From(lease, OsOf(lease.HostId)));
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
        return lease is null ? null : LeaseView.From(lease, OsOf(lease.HostId));
    }

    public async Task<LeaseExecTarget?> ResolveExecTargetAsync(
        Guid consumerUserId, Guid leaseId, CancellationToken ct = default)
    {
        var lease = await OwnedLeaseOrNullAsync(consumerUserId, leaseId, ct);
        if (lease is null)
        {
            // No such lease the caller can see — 404, never revealing another user's lease (docs/API.md §3).
            return null;
        }

        // Exec/shell are only valid against a ready lease; anything not yet (or no longer) active is
        // lease_not_ready (docs/API.md §7 — 409 unless active).
        if (lease.Status != LeaseStatus.Active)
        {
            throw new ApiException(
                ApiErrorCode.LeaseNotReady,
                "The lease is not ready — exec requires an active lease.",
                new { status = PgEnum.ToLabel(lease.Status) });
        }

        return new LeaseExecTarget(lease.HostId.ToString(), TunnelLeaseId.Format(lease.Id));
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
            return LeaseView.From(lease, OsOf(lease.HostId));
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

        // Return the unused remainder of the hold to the wallet (docs/PAYMENTS.md §4). Keyed by lease id, so
        // it is a safe no-op if the lease was already finalized (and released) by the reconciler.
        await _walletGate.ReleaseHoldAsync(lease.Id, ct);
        return LeaseView.From(ended ?? lease, OsOf(lease.HostId));
    }

    /// <summary>
    /// The host's advertised container OS from its live capability snapshot (docs/TUNNEL.md §5), or null
    /// when it has no live tunnel or its agent advertised none — surfacing only, always null-safe (task #316).
    /// </summary>
    private string? OsOf(Guid hostId) => _capabilities.GetCapability(hostId)?.Os;

    private async Task<Lease?> OwnedLeaseOrNullAsync(Guid consumerUserId, Guid leaseId, CancellationToken ct)
    {
        var lease = await _leases.GetByIdAsync(leaseId, ct);

        // Ownership failures return "not found" so the API never reveals a lease the caller can't see.
        return lease is not null && lease.ConsumerUserId == consumerUserId ? lease : null;
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

    /// <summary>
    /// Resolves and validates the lease's isolation level (task #418, docs/TUNNEL.md §5). An omitted/blank
    /// request defaults to <see cref="HostIsolation.Shared"/>; <c>confidential</c> or any unknown value is
    /// rejected. The resolved level must clear the <c>platform_policy.min_isolation</c> floor (ordered
    /// <c>shared</c> &lt; <c>sandboxed</c> &lt; <c>vm</c>) when one is configured, and — when the target
    /// <paramref name="host"/> advertises isolation levels — must be one the host can provide (fail fast). A
    /// host with no advertised levels recorded passes through: wisp re-validates as the real security boundary.
    /// </summary>
    private async Task<string> ResolveIsolationAsync(string? requested, Domain.Host host, CancellationToken ct)
    {
        var level = string.IsNullOrWhiteSpace(requested) ? HostIsolation.Shared : requested.Trim();
        if (!HostIsolation.IsRequestable(level))
        {
            throw new ApiException(
                ApiErrorCode.ValidationError,
                "Unknown 'isolation'.",
                new { field = "isolation", allowed = HostIsolation.RequestLevels });
        }

        // Enforce the platform-wide minimum isolation floor when the active policy sets one (ordered rank).
        var policy = await _policy.GetActiveAsync(ct);
        if (policy?.MinIsolation is { } min && HostIsolation.RankOf(min) is var minRank and >= 0 &&
            HostIsolation.RankOf(level) < minRank)
        {
            throw new ApiException(
                ApiErrorCode.ValidationError,
                "'isolation' is below the platform minimum.",
                new { field = "isolation", min_isolation = min });
        }

        // When the host advertises isolation levels (task #417), the requested level must be one it can
        // provide. A host with none recorded is allowed through — wisp is the real security boundary.
        if (host.IsolationLevels.Count > 0 &&
            !host.IsolationLevels.Contains(level, StringComparer.Ordinal))
        {
            throw new ApiException(
                ApiErrorCode.ValidationError,
                "The host cannot provide the requested 'isolation'.",
                new { field = "isolation", supported = host.IsolationLevels });
        }

        return level;
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
    /// snapshot to persist (missing dimensions stay null — the host applies its own default). GPU is the one
    /// exception to the clamp-friendly ceilings: it is priced into the offer, so an over-ask is rejected
    /// rather than silently reduced (task #522), and it snapshots as a concrete count (0 when none requested).
    /// </summary>
    private static (decimal? Cpus, int? MemoryMb, int? Pids, int Gpus) ValidateResources(
        LeaseResourcesRequest? resources, HostImage image)
    {
        if (resources is null)
        {
            return (null, null, null, 0);
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
        var gpus = ValidateGpus(resources.Gpus, image.MaxGpus);
        return (cpus, memoryMb, pids, gpus);
    }

    /// <summary>
    /// Validates the requested whole-device GPU count against the offer's <c>max_gpus</c> ceiling (task #522)
    /// and returns the concrete snapshot count (0 when omitted). Unlike the clampable ceilings an over-ask is
    /// <b>rejected</b> with the standard validation error, never clamped: GPU access is priced into the offer,
    /// so silently reducing it would change what the consumer thinks they bought. A negative count is rejected.
    /// </summary>
    private static int ValidateGpus(int? requested, int max)
    {
        if (requested is not { } value)
        {
            return 0;
        }

        if (value < 0)
        {
            throw new ApiException(
                ApiErrorCode.ValidationError, "'resources.gpus' must be non-negative.",
                new { field = "resources.gpus" });
        }

        if (value > max)
        {
            throw new ApiException(
                ApiErrorCode.ValidationError, "'resources.gpus' exceeds the image's maximum.",
                new { field = "resources.gpus", max });
        }

        return value;
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

    /// <summary>
    /// Guards the create-time <c>env</c> map against wisp's own caps before it reaches the tunnel
    /// (docs/TUNNEL.md §5, §13): reject over <see cref="MaxEnvEntries"/> keys or over
    /// <see cref="MaxEnvSerializedBytes"/> of serialized payload. Env VALUES are secrets-in-transit, so the
    /// error details carry only the count/size and the cap — never a key or value — and env is never logged.
    /// </summary>
    private static void ValidateEnv(Dictionary<string, string>? env)
    {
        if (env is null || env.Count == 0)
        {
            return;
        }

        if (env.Count > MaxEnvEntries)
        {
            throw new ApiException(
                ApiErrorCode.ValidationError,
                "'env' has too many entries.",
                new { field = "env", max = MaxEnvEntries, count = env.Count });
        }

        var serializedBytes = JsonSerializer.SerializeToUtf8Bytes(env).Length;
        if (serializedBytes > MaxEnvSerializedBytes)
        {
            throw new ApiException(
                ApiErrorCode.ValidationError,
                "'env' exceeds the maximum serialized size.",
                new { field = "env", max_bytes = MaxEnvSerializedBytes, bytes = serializedBytes });
        }
    }
}
