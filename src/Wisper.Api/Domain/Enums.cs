namespace Wisper.Api.Domain;

/// <summary>
/// Lifecycle of a <see cref="User"/> account (docs/DATA_MODEL.md §2, <c>user_status</c>).
/// <see cref="Suspended"/> gates all activity; <see cref="Deleted"/> is a soft-delete tombstone.
/// </summary>
public enum UserStatus
{
    Active,
    Suspended,
    Deleted,
}

/// <summary>
/// Presence/gating state of a <see cref="Host"/> (docs/DATA_MODEL.md §2, <c>host_status</c>).
/// A host only appears in the consumer catalog while <see cref="Online"/>.
/// </summary>
public enum HostStatus
{
    Offline,
    Online,
    Suspended,
}

/// <summary>
/// Stripe Connect onboarding state for a host owner (docs/DATA_MODEL.md §2, <c>connect_status</c>).
/// A host cannot go <see cref="HostStatus.Online"/> until this is <see cref="Enabled"/> (§9, §10).
/// </summary>
public enum ConnectStatus
{
    None,
    Pending,
    Restricted,
    Enabled,
    Disabled,
}

/// <summary>
/// The network exposure a priced image permits (docs/DATA_MODEL.md §2, <c>network_mode</c>):
/// <see cref="None"/> (isolated), <see cref="Open"/> (full network), <see cref="Egress"/> (outbound only).
/// </summary>
public enum NetworkMode
{
    None,
    Open,
    Egress,
}

/// <summary>
/// Maps the identity/catalog enums to and from their PostgreSQL native-enum labels
/// (docs/DATA_MODEL.md §2). Labels are the lowercase enum name; the Dapper repositories pass the
/// label as a text parameter cast to the enum type, and read the column back as text so Dapper's
/// case-insensitive enum parse rehydrates it.
/// </summary>
public static class PgEnum
{
    /// <summary>The PostgreSQL enum label for <paramref name="value"/> (its lowercase name).</summary>
    public static string ToLabel<TEnum>(TEnum value) where TEnum : struct, Enum =>
        value.ToString().ToLowerInvariant();

    /// <summary>Parses a PostgreSQL enum label back to <typeparamref name="TEnum"/> (case-insensitive).</summary>
    public static TEnum Parse<TEnum>(string label) where TEnum : struct, Enum =>
        Enum.Parse<TEnum>(label, ignoreCase: true);
}
