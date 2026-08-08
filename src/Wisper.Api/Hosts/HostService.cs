using Microsoft.Extensions.Options;
using Wisper.Api.Auth;
using Wisper.Api.Domain;
using Wisper.Api.Infrastructure;
using Wisper.Api.Payouts;
using Wisper.Api.Persistence.HostImages;
using Wisper.Api.Persistence.Hosts;
using Wisper.Api.Persistence.Users;
using Wisper.Api.Tunnel;
using Host = Wisper.Api.Domain.Host;

namespace Wisper.Api.Hosts;

/// <summary>
/// The host registration + pricing surface (docs/API.md §6): register a wisp host and issue its agent token
/// once (storing only the hash, docs/DATA_MODEL.md §4); list the caller's hosts with live presence + earnings;
/// rotate the agent token (revoking the old one and closing its live tunnel <c>4402</c>, docs/TUNNEL.md §13);
/// and manage the priced allow-list, validated live against the host's advertised wisp capability from its
/// tunnel <c>hello</c> (docs/TUNNEL.md §5). Ownership failures surface as <c>404</c> so the API never reveals
/// a host the caller cannot see (docs/API.md §3). Everything runs against interfaces so the unit suite needs
/// no Postgres/tunnel.
/// </summary>
public sealed class HostService
{
    private readonly IHostRepository _hosts;
    private readonly IHostImageRepository _images;
    private readonly IUserRepository _users;
    private readonly IHostRegistry _registry;
    private readonly IHostCapabilitySource _capabilities;
    private readonly IAgentTunnelCloser _tunnelCloser;
    private readonly PayoutService _payouts;
    private readonly IUserRoleGranter _roleGranter;
    private readonly TunnelOptions _tunnel;
    private readonly TimeProvider _time;
    private readonly ILogger<HostService> _logger;

    public HostService(
        IHostRepository hosts,
        IHostImageRepository images,
        IUserRepository users,
        IHostRegistry registry,
        IHostCapabilitySource capabilities,
        IAgentTunnelCloser tunnelCloser,
        PayoutService payouts,
        IUserRoleGranter roleGranter,
        IOptions<TunnelOptions> tunnel,
        TimeProvider time,
        ILogger<HostService> logger)
    {
        _hosts = hosts;
        _images = images;
        _users = users;
        _registry = registry;
        _capabilities = capabilities;
        _tunnelCloser = tunnelCloser;
        _payouts = payouts;
        _roleGranter = roleGranter;
        _tunnel = tunnel.Value;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Registers a new wisp host for the caller (docs/API.md §6): mints an agent token, stores only its hash +
    /// prefix, and returns the clear token <b>once</b> along with the <c>manager_ws</c> the agent dials. The
    /// host starts <c>offline</c> — pricing/earning stay inert until Connect is enabled and the agent connects.
    /// Becoming a host is additive (docs/API.md §184, docs/DESIGN.md §199): on success the caller gains the
    /// <c>host</c> group so their next token carries it. The grant is best-effort — a transient failure is
    /// logged loudly but never fails registration, because the live session already reflects the new role
    /// (<c>GET /v1/me</c> treats owning ≥1 host as implying <c>host</c>) and the next login reconciles the
    /// group from the persisted host row.
    /// </summary>
    /// <param name="ownerCognitoSub">
    /// The caller's Cognito subject to add to the <c>host</c> group, or <c>null</c> to skip the grant (api-key
    /// principals, whose roles come from explicit scopes rather than Cognito groups — docs/API.md §2).
    /// </param>
    public async Task<HostRegisteredResponse> RegisterAsync(
        Guid ownerUserId, RegisterHostRequest request, string? ownerCognitoSub = null,
        CancellationToken ct = default)
    {
        var issued = HostAgentToken.Issue();
        var now = _time.GetUtcNow();

        var host = await _hosts.CreateAsync(new Host
        {
            OwnerUserId = ownerUserId,
            Name = Trimmed(request.Name),
            Label = Trimmed(request.Label),
            Status = HostStatus.Offline,
            AgentTokenHash = issued.TokenHash,
            AgentTokenPrefix = issued.TokenPrefix,
            CreatedAt = now,
            UpdatedAt = now,
        }, ct);

        _logger.LogInformation(
            "host {Host} registered by user {User} (token prefix {Prefix})",
            host.Id, ownerUserId, issued.TokenPrefix);

        await GrantHostRoleAsync(ownerUserId, ownerCognitoSub, ct);

        return new HostRegisteredResponse(
            host.Id, host.Name, issued.Token, issued.TokenPrefix, _tunnel.ManagerWebSocketUrl,
            PgEnum.ToLabel(host.Status));
    }

    /// <summary>
    /// Best-effort grant of the <c>host</c> group on first host action (docs/API.md §184). The host row is
    /// already committed, so a transient group-write failure is logged loudly and swallowed — never propagated
    /// — leaving the persisted host to reconcile the role on the next login while the current session is
    /// covered by the owns-a-host role projection on <c>GET /v1/me</c>.
    /// </summary>
    private async Task GrantHostRoleAsync(Guid ownerUserId, string? ownerCognitoSub, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ownerCognitoSub))
        {
            return;
        }

        try
        {
            await _roleGranter.GrantHostAsync(ownerCognitoSub, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "failed to grant the host group to user {User} (subject {Subject}) after host registration; "
                + "the host row is created and the role will reconcile on the next login",
                ownerUserId, ownerCognitoSub);
        }
    }

    /// <summary>
    /// The caller's hosts with authoritative live presence (from the tunnel registry) and their advertised
    /// capacity, plus the owner-scoped earnings summary (docs/API.md §6).
    /// </summary>
    public async Task<HostsMineResponse> ListMineAsync(Guid ownerUserId, CancellationToken ct = default)
    {
        var hosts = await _hosts.ListByOwnerAsync(ownerUserId, ct);
        var summaries = hosts.Select(h => HostSummary.From(h, IsOnline(h.Id))).ToList();
        var earnings = await _payouts.GetEarningsAsync(ownerUserId, ct);
        return new HostsMineResponse(summaries, earnings);
    }

    /// <summary>
    /// Rotates the host's agent token (docs/API.md §6, docs/TUNNEL.md §13): stores a fresh hash + prefix
    /// (the old token stops resolving immediately) and closes the old token's live tunnel with <c>4402</c>
    /// (revoked → no auto-reconnect until re-provisioned). The new clear token is returned <b>once</b>.
    /// </summary>
    public async Task<AgentTokenRotatedResponse> RotateAgentTokenAsync(
        Guid ownerUserId, Guid hostId, CancellationToken ct = default)
    {
        var host = await LoadOwnedHostAsync(ownerUserId, hostId, ct);

        var issued = HostAgentToken.Issue();
        await _hosts.UpdateAsync(
            host with
            {
                AgentTokenHash = issued.TokenHash,
                AgentTokenPrefix = issued.TokenPrefix,
                UpdatedAt = _time.GetUtcNow(),
            },
            ct);

        // Revoke the old session: the old token no longer resolves, and any live tunnel it holds is closed
        // 4402 so the agent must re-provision before reconnecting (docs/TUNNEL.md §13).
        var tunnelClosed = await _tunnelCloser.CloseAsync(
            host.Id, CloseCodes.Revoked, "agent token rotated", ct);

        _logger.LogInformation(
            "host {Host} agent token rotated by user {User} (new prefix {Prefix}, tunnel closed {Closed})",
            host.Id, ownerUserId, issued.TokenPrefix, tunnelClosed);

        return new AgentTokenRotatedResponse(
            host.Id, issued.Token, issued.TokenPrefix, _tunnel.ManagerWebSocketUrl, tunnelClosed);
    }

    /// <summary>The host's full priced allow-list (docs/API.md §6, <c>GET /v1/hosts/:id/images</c>).</summary>
    public async Task<HostImagesResponse> ListImagesAsync(
        Guid ownerUserId, Guid hostId, CancellationToken ct = default)
    {
        await LoadOwnedHostAsync(ownerUserId, hostId, ct);
        var images = await _images.ListByHostAsync(hostId, enabledOnly: false, ct);
        return new HostImagesResponse(images.Select(HostImageView.From).ToList());
    }

    /// <summary>
    /// Replaces the host's priced allow-list (docs/API.md §6, <c>PUT /v1/hosts/:id/images</c>). Every entry is
    /// validated live against the host's advertised wisp capability (image in the allow-list, networks a
    /// subset, ceilings within the host's limits); a host with no live tunnel is <c>host_offline</c> since
    /// there is nothing to validate against. The resulting set replaces the prior one: entries no longer
    /// present are removed, matching refs are updated in place (preserving their id and price snapshot lineage),
    /// new refs are created.
    /// </summary>
    public async Task<HostImagesResponse> ReplaceImagesAsync(
        Guid ownerUserId, Guid hostId, ReplaceImagesRequest request, CancellationToken ct = default)
    {
        var host = await LoadOwnedHostAsync(ownerUserId, hostId, ct);
        var capability = RequireCapability(hostId);
        var connectStatus = await OwnerConnectStatusAsync(host, ct);

        var existing = await _images.ListByHostAsync(hostId, enabledOnly: false, ct);
        var existingByRef = existing.ToDictionary(i => i.ImageRef, StringComparer.Ordinal);

        var desired = request.Images ?? Array.Empty<ImageUpsert>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var validated = new List<(string ImageRef, ImageUpsert Spec, IReadOnlyList<NetworkMode> Networks)>(desired.Count);
        foreach (var entry in desired)
        {
            var imageRef = RequireImageRef(entry.ImageRef);
            if (!seen.Add(imageRef))
            {
                throw new ApiException(
                    ApiErrorCode.ValidationError,
                    $"Duplicate image_ref '{imageRef}' in the allow-list.",
                    new { field = "image_ref", image_ref = imageRef });
            }

            var networks = ParseNetworks(entry.Networks);
            ValidateAgainstCapability(
                capability, imageRef, entry.PriceCentsPerMin, entry.MaxTtlSeconds, networks,
                entry.MaxCpus, entry.MaxMemoryMb, entry.MaxPids, entry.MaxGpus);

            // A new entry defaults enabled; an update keeps the stored flag unless overridden. Charging a
            // non-Connect-enabled owner's image is rejected here so the online gate stays consistent (§6).
            var enabled = entry.Enabled
                ?? (existingByRef.TryGetValue(imageRef, out var current) ? current.Enabled : true);
            RejectIfChargesRequireConnect(connectStatus, enabled, entry.PriceCentsPerMin, imageRef);

            validated.Add((imageRef, entry, networks));
        }

        // Remove entries the replacement no longer contains.
        foreach (var stale in existing.Where(i => !seen.Contains(i.ImageRef)))
        {
            await _images.DeleteAsync(stale.Id, ct);
        }

        var now = _time.GetUtcNow();
        foreach (var (imageRef, spec, networks) in validated)
        {
            if (existingByRef.TryGetValue(imageRef, out var current))
            {
                await _images.UpdateAsync(
                    current with
                    {
                        PriceCentsPerMin = spec.PriceCentsPerMin,
                        Networks = networks,
                        MaxTtlSeconds = spec.MaxTtlSeconds,
                        MaxCpus = spec.MaxCpus,
                        MaxMemoryMb = spec.MaxMemoryMb,
                        MaxPids = spec.MaxPids,
                        MaxGpus = spec.MaxGpus,
                        Enabled = spec.Enabled ?? current.Enabled,
                        UpdatedAt = now,
                    },
                    ct);
            }
            else
            {
                await _images.CreateAsync(new HostImage
                {
                    HostId = hostId,
                    ImageRef = imageRef,
                    PriceCentsPerMin = spec.PriceCentsPerMin,
                    Networks = networks,
                    MaxTtlSeconds = spec.MaxTtlSeconds,
                    MaxCpus = spec.MaxCpus,
                    MaxMemoryMb = spec.MaxMemoryMb,
                    MaxPids = spec.MaxPids,
                    MaxGpus = spec.MaxGpus,
                    Enabled = spec.Enabled ?? true,
                    CreatedAt = now,
                    UpdatedAt = now,
                }, ct);
            }
        }

        var result = await _images.ListByHostAsync(hostId, enabledOnly: false, ct);
        return new HostImagesResponse(result.Select(HostImageView.From).ToList());
    }

    /// <summary>
    /// Patches one priced image's price/enable/limits/networks (docs/API.md §6, <c>PATCH …/images/:imageId</c>).
    /// The resulting entry is re-validated live against the host's advertised capability; a host with no live
    /// tunnel is <c>host_offline</c>. Omitted fields keep their stored value; the image ref is immutable.
    /// </summary>
    public async Task<HostImageView> PatchImageAsync(
        Guid ownerUserId, Guid hostId, Guid imageId, PatchImageRequest request, CancellationToken ct = default)
    {
        var host = await LoadOwnedHostAsync(ownerUserId, hostId, ct);
        var capability = RequireCapability(hostId);
        var connectStatus = await OwnerConnectStatusAsync(host, ct);

        var image = await _images.GetByIdAsync(imageId, ct);
        if (image is null || image.HostId != hostId)
        {
            throw new ApiException(ApiErrorCode.NotFound, $"No such image '{imageId}'.");
        }

        var price = request.PriceCentsPerMin ?? image.PriceCentsPerMin;
        var maxTtl = request.MaxTtlSeconds ?? image.MaxTtlSeconds;
        var networks = request.Networks is null ? image.Networks : ParseNetworks(request.Networks);
        var maxCpus = request.MaxCpus ?? image.MaxCpus;
        var maxMemoryMb = request.MaxMemoryMb ?? image.MaxMemoryMb;
        var maxPids = request.MaxPids ?? image.MaxPids;
        var maxGpus = request.MaxGpus ?? image.MaxGpus;
        var enabled = request.Enabled ?? image.Enabled;

        ValidateAgainstCapability(
            capability, image.ImageRef, price, maxTtl, networks, maxCpus, maxMemoryMb, maxPids, maxGpus);

        // Enabling a non-zero-priced image requires Connect onboarding (§6): reject before persisting so a
        // non-Connect owner can never move into the earning arm of the online gate mid-tunnel.
        RejectIfChargesRequireConnect(connectStatus, enabled, price, image.ImageRef);

        var updated = await _images.UpdateAsync(
            image with
            {
                PriceCentsPerMin = price,
                Networks = networks,
                MaxTtlSeconds = maxTtl,
                MaxCpus = maxCpus,
                MaxMemoryMb = maxMemoryMb,
                MaxPids = maxPids,
                MaxGpus = maxGpus,
                Enabled = enabled,
                UpdatedAt = _time.GetUtcNow(),
            },
            ct);

        return HostImageView.From(updated);
    }

    /// <summary>Loads a host and enforces ownership; a missing or unowned host is <c>404</c> (docs/API.md §3).</summary>
    private async Task<Host> LoadOwnedHostAsync(Guid ownerUserId, Guid hostId, CancellationToken ct)
    {
        var host = await _hosts.GetByIdAsync(hostId, ct);
        if (host is null || host.OwnerUserId != ownerUserId)
        {
            throw new ApiException(ApiErrorCode.NotFound, $"No such host '{hostId}'.");
        }

        return host;
    }

    /// <summary>The host's live advertised capability, or <c>409 host_offline</c> when it has no live tunnel.</summary>
    private HostCapabilitySnapshot RequireCapability(Guid hostId) =>
        _capabilities.GetCapability(hostId)
        ?? throw new ApiException(
            ApiErrorCode.HostOffline,
            "The host must have a live agent tunnel so its images can be validated against wisp capability.");

    /// <summary>True when the host has a live agent tunnel in the registry (authoritative presence).</summary>
    private bool IsOnline(Guid hostId) => _registry.TryGet(hostId.ToString(), out _);

    /// <summary>The host owner's Stripe Connect onboarding state, or <see cref="ConnectStatus.None"/> if unknown.</summary>
    private async Task<ConnectStatus> OwnerConnectStatusAsync(Host host, CancellationToken ct) =>
        (await _users.GetByIdAsync(host.OwnerUserId, ct))?.ConnectStatus ?? ConnectStatus.None;

    /// <summary>
    /// Rejects enabling a non-zero-priced image while the owner is not Connect-enabled (docs/API.md §6,
    /// docs/PAYMENTS.md §5): charging money requires an onboarded Connect account. A zero price (self-hosted,
    /// task #386) or a disabled entry is always allowed. Enforced at the only pricing mutation point so the
    /// host presence gate (<see cref="ConnectGate.CanHostGoOnline"/>) stays consistent.
    /// </summary>
    private static void RejectIfChargesRequireConnect(
        ConnectStatus ownerConnectStatus, bool enabled, long priceCentsPerMin, string imageRef)
    {
        if (ConnectGate.ChargesRequireConnect(ownerConnectStatus, enabled, priceCentsPerMin))
        {
            throw new ApiException(
                ApiErrorCode.ValidationError,
                "Charging for an image requires Stripe Connect onboarding. Complete Connect "
                + "(POST /v1/hosts/connect) before enabling a priced image, or set price_cents_per_min to 0 "
                + $"to self-host '{imageRef}' for free.",
                new
                {
                    field = "price_cents_per_min",
                    image_ref = imageRef,
                    connect_status = PgEnum.ToLabel(ownerConnectStatus),
                });
        }
    }

    /// <summary>
    /// Validates one priced image against the host's advertised capability (docs/API.md §6,
    /// docs/DATA_MODEL.md §4): the ref must be in the allow-list, the price non-negative, the TTL positive
    /// and within the host limit, every network a subset the host permits, and each resource ceiling within
    /// the host's advertised ceiling — including the GPU device ceiling (<c>max_gpus</c>, task #522).
    /// </summary>
    private static void ValidateAgainstCapability(
        HostCapabilitySnapshot capability,
        string imageRef,
        long priceCentsPerMin,
        int maxTtlSeconds,
        IReadOnlyList<NetworkMode> networks,
        decimal? maxCpus,
        int? maxMemoryMb,
        int? maxPids,
        int maxGpus)
    {
        if (!capability.AllowsImage(imageRef))
        {
            throw new ApiException(
                ApiErrorCode.ValidationError,
                $"Image '{imageRef}' is not in the host's advertised wisp capability.",
                new { field = "image_ref", image_ref = imageRef, allowed = capability.Images });
        }

        if (priceCentsPerMin < 0)
        {
            throw new ApiException(
                ApiErrorCode.ValidationError, "price_cents_per_min must be non-negative.",
                new { field = "price_cents_per_min" });
        }

        if (maxTtlSeconds < 1)
        {
            throw new ApiException(
                ApiErrorCode.ValidationError, "max_ttl_seconds must be a positive integer.",
                new { field = "max_ttl_seconds" });
        }

        if (capability.MaxTtlSeconds > 0 && maxTtlSeconds > capability.MaxTtlSeconds)
        {
            throw new ApiException(
                ApiErrorCode.ValidationError,
                $"max_ttl_seconds ({maxTtlSeconds}) exceeds the host's advertised limit ({capability.MaxTtlSeconds}).",
                new { field = "max_ttl_seconds", limit = capability.MaxTtlSeconds });
        }

        foreach (var network in networks)
        {
            if (!capability.Networks.Contains(network))
            {
                throw new ApiException(
                    ApiErrorCode.ValidationError,
                    $"Network '{PgEnum.ToLabel(network)}' is not permitted by the host's advertised capability.",
                    new
                    {
                        field = "networks",
                        network = PgEnum.ToLabel(network),
                        allowed = capability.Networks.Select(PgEnum.ToLabel),
                    });
            }
        }

        if (maxCpus is { } cpus)
        {
            if (cpus <= 0)
            {
                throw new ApiException(
                    ApiErrorCode.ValidationError, "max_cpus must be positive.", new { field = "max_cpus" });
            }

            if (capability.MaxCpus > 0 && (double)cpus > capability.MaxCpus)
            {
                throw new ApiException(
                    ApiErrorCode.ValidationError,
                    $"max_cpus ({cpus}) exceeds the host's advertised limit ({capability.MaxCpus}).",
                    new { field = "max_cpus", limit = capability.MaxCpus });
            }
        }

        if (maxMemoryMb is { } memory)
        {
            if (memory <= 0)
            {
                throw new ApiException(
                    ApiErrorCode.ValidationError, "max_memory_mb must be positive.", new { field = "max_memory_mb" });
            }

            if (capability.MaxMemoryMb > 0 && memory > capability.MaxMemoryMb)
            {
                throw new ApiException(
                    ApiErrorCode.ValidationError,
                    $"max_memory_mb ({memory}) exceeds the host's advertised limit ({capability.MaxMemoryMb}).",
                    new { field = "max_memory_mb", limit = capability.MaxMemoryMb });
            }
        }

        if (maxPids is { } pids)
        {
            if (pids <= 0)
            {
                throw new ApiException(
                    ApiErrorCode.ValidationError, "max_pids must be positive.", new { field = "max_pids" });
            }

            if (capability.MaxPids > 0 && pids > capability.MaxPids)
            {
                throw new ApiException(
                    ApiErrorCode.ValidationError,
                    $"max_pids ({pids}) exceeds the host's advertised limit ({capability.MaxPids}).",
                    new { field = "max_pids", limit = capability.MaxPids });
            }
        }

        // GPU is priced into the offer (task #522): max_gpus is the whole-device ceiling this offer sells, and
        // it may not exceed the host's advertised GPU device count. Unlike the cpu/mem/pids checks there is no
        // "advertised 0 means unlimited/unknown" escape — 0 devices genuinely means no GPU, so a host with no
        // GPU rejects any offer above 0. The default (0) always passes on a GPU-less host.
        if (maxGpus < 0)
        {
            throw new ApiException(
                ApiErrorCode.ValidationError, "max_gpus must be non-negative.", new { field = "max_gpus" });
        }

        if (maxGpus > capability.MaxGpus)
        {
            throw new ApiException(
                ApiErrorCode.ValidationError,
                $"max_gpus ({maxGpus}) exceeds the host's advertised GPU count ({capability.MaxGpus}).",
                new { field = "max_gpus", limit = capability.MaxGpus });
        }
    }

    /// <summary>Parses the requested network labels to <see cref="NetworkMode"/>, rejecting unknown values.</summary>
    private static IReadOnlyList<NetworkMode> ParseNetworks(IReadOnlyList<string>? labels)
    {
        if (labels is null || labels.Count == 0)
        {
            return Array.Empty<NetworkMode>();
        }

        var modes = new List<NetworkMode>(labels.Count);
        foreach (var label in labels)
        {
            if (!Enum.TryParse<NetworkMode>(label, ignoreCase: true, out var mode) || !Enum.IsDefined(mode))
            {
                throw new ApiException(
                    ApiErrorCode.ValidationError,
                    $"Unknown network '{label}'.",
                    new { field = "networks", allowed = new[] { "none", "open", "egress" } });
            }

            if (!modes.Contains(mode))
            {
                modes.Add(mode);
            }
        }

        return modes;
    }

    private static string RequireImageRef(string? imageRef)
    {
        var trimmed = imageRef?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new ApiException(
                ApiErrorCode.ValidationError, "image_ref is required.", new { field = "image_ref" });
        }

        return trimmed;
    }

    private static string? Trimmed(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
