namespace Wisper.Api.Domain;

/// <summary>
/// A version of the admin-tunable platform policy (docs/DATA_MODEL.md §11, <c>platform_policy</c>). Rows
/// are <b>append-only and versioned</b> — the active policy is the one with the latest
/// <see cref="EffectiveFrom"/> — so every pricing/limit change is auditable and a lease's fee basis is
/// reproducible. <see cref="FeeBps"/> is the platform cut applied when a <c>lease_charge</c> splits a
/// tick into host earnings + platform revenue (§8).
/// </summary>
public sealed record PlatformPolicy
{
    /// <summary>Row id (DB default <c>gen_random_uuid()</c>).</summary>
    public Guid Id { get; init; }

    /// <summary>Platform cut in basis points (0..10000; 1500 = 15%).</summary>
    public required int FeeBps { get; init; }

    /// <summary>Minimum wallet top-up in cents.</summary>
    public long MinTopupCents { get; init; }

    /// <summary>Cap on a user's concurrent leases, or <c>null</c> for unlimited.</summary>
    public int? MaxConcurrentLeasesPerUser { get; init; }

    /// <summary>Global TTL ceiling in seconds over per-host limits, or <c>null</c> for none.</summary>
    public int? MaxTtlSecondsCap { get; init; }

    /// <summary>When this version becomes active (UTC); the newest such row is the active policy.</summary>
    public DateTimeOffset EffectiveFrom { get; init; }

    /// <summary>The admin who authored this version, or <c>null</c> for a seed/system default.</summary>
    public Guid? CreatedBy { get; init; }
}
