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

    /// <summary>
    /// Fraud guard — the <b>first-top-up hold</b> (docs/PAYMENTS.md §7): a cap in cents on a user's
    /// <i>first-ever</i> top-up, so a fresh account can't fund a large balance before any charge has
    /// materially cleared the dispute window. <c>null</c> ⇒ no first-top-up cap.
    /// </summary>
    public long? FirstTopupMaxCents { get; init; }

    /// <summary>
    /// Fraud guard — how long (hours since <see cref="User.CreatedAt"/>) an account counts as <b>new</b> for
    /// the new-account velocity limits (docs/PAYMENTS.md §7). <c>null</c> or <c>0</c> ⇒ no new-account window
    /// (the velocity limits below never apply).
    /// </summary>
    public int? NewAccountWindowHours { get; init; }

    /// <summary>
    /// Fraud guard — new-account <b>top-up velocity</b> (docs/PAYMENTS.md §7): the maximum cumulative top-up
    /// in cents a <i>new</i> account may fund per rolling 24 hours. <c>null</c> ⇒ no new-account top-up cap.
    /// </summary>
    public long? NewAccountMaxTopupCentsPerDay { get; init; }

    /// <summary>
    /// Fraud guard — per-user <b>spend cap</b> (docs/PAYMENTS.md §7): the maximum cumulative lease commitment
    /// in cents a user may authorize per rolling 24 hours (measured by the up-front lease holds, which bound
    /// spend). Enforced at lease start alongside <see cref="MaxConcurrentLeasesPerUser"/>. <c>null</c> ⇒ no
    /// daily spend cap.
    /// </summary>
    public long? MaxSpendCentsPerDay { get; init; }

    /// <summary>When this version becomes active (UTC); the newest such row is the active policy.</summary>
    public DateTimeOffset EffectiveFrom { get; init; }

    /// <summary>The admin who authored this version, or <c>null</c> for a seed/system default.</summary>
    public Guid? CreatedBy { get; init; }
}
