namespace Wisper.Api.Persistence.BillingIncidents;

/// <summary>
/// The two flavours of platform-policy fallback the metering flush observes (task #210, task #206,
/// docs/PAYMENTS.md §4). Recorded on a <c>billing_incidents</c> row so the admin overview can
/// aggregate across restarts and instances.
/// <list type="bullet">
///   <item><see cref="StaleFallback"/>: no active policy resolved but at least one version exists
///   (usually only future-dated rows, or an operator mis-set an <c>effective_from</c>). The flush
///   used the newest version regardless of <c>effective_from</c> and logged
///   <c>billing.policy.stale_fallback</c>.</item>
///   <item><see cref="MissingAtFlush"/>: no policy row exists at all (impossible after migration
///   <c>0017</c> but kept as a guard). The flush skipped the <c>lease_charge</c> and logged
///   <c>billing.policy.missing_at_flush</c> at Critical.</item>
/// </list>
/// </summary>
public enum PolicyFallbackKind
{
    StaleFallback,
    MissingAtFlush,
}

/// <summary>
/// Snake_case wire labels for <see cref="PolicyFallbackKind"/>. These are the exact strings written
/// to <c>billing_incidents.kind</c> and matched by the migration's CHECK constraint, so the C# enum
/// and the SQL domain never drift.
/// </summary>
public static class PolicyFallbackKindLabels
{
    public const string StaleFallback = "policy_stale_fallback";
    public const string MissingAtFlush = "policy_missing_at_flush";

    public static string ToLabel(PolicyFallbackKind kind) => kind switch
    {
        PolicyFallbackKind.StaleFallback => StaleFallback,
        PolicyFallbackKind.MissingAtFlush => MissingAtFlush,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown policy fallback kind."),
    };
}
