using Wisper.Api.Domain;
using Wisper.Api.Persistence.Policy;

namespace Wisper.Api.Policy;

/// <summary>
/// Reads and versions the admin-tunable platform policy (docs/DATA_MODEL.md §11). The policy is
/// append-only: <see cref="GetActiveAsync"/> returns the version in force <b>now</b> (the newest
/// <c>effective_from</c> at or before the clock), and <see cref="PublishAsync"/> adds a new version rather
/// than editing one, so every fee/limit change is auditable and a lease's fee basis is reproducible. The
/// billing paths (P6) read <see cref="PlatformPolicy.FeeBps"/> here to split a <c>lease_charge</c> into
/// host earnings + platform revenue (§8).
/// </summary>
public sealed class PlatformPolicyService
{
    private readonly IPlatformPolicyRepository _repo;
    private readonly TimeProvider _clock;

    public PlatformPolicyService(IPlatformPolicyRepository repo, TimeProvider? clock = null)
    {
        _repo = repo;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>The active policy version as of now, or <c>null</c> when none has taken effect yet.</summary>
    public Task<PlatformPolicy?> GetActiveAsync(CancellationToken ct = default) =>
        _repo.GetActiveAsync(_clock.GetUtcNow(), ct);

    /// <summary>
    /// The active policy version, or throws when none is configured. Use on paths that cannot proceed
    /// without a fee basis (e.g. a <c>lease_charge</c> split).
    /// </summary>
    public async Task<PlatformPolicy> GetActiveOrThrowAsync(CancellationToken ct = default) =>
        await GetActiveAsync(ct) ?? throw new InvalidOperationException(
            "No active platform policy is configured.");

    /// <summary>
    /// Publishes a new policy version. When <see cref="PlatformPolicy.EffectiveFrom"/> is unset it defaults
    /// to now (take effect immediately); a future value schedules the change. Returns the stored row.
    /// </summary>
    public Task<PlatformPolicy> PublishAsync(PlatformPolicy policy, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var toStore = policy.EffectiveFrom == default
            ? policy with { EffectiveFrom = _clock.GetUtcNow() }
            : policy;
        return _repo.AppendAsync(toStore, ct);
    }

    /// <summary>Every policy version, newest first -- the audit trail of policy changes.</summary>
    public Task<IReadOnlyList<PlatformPolicy>> ListVersionsAsync(CancellationToken ct = default) =>
        _repo.ListAsync(ct);
}
