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
/// Lifecycle state of a <see cref="Lease"/> (docs/DATA_MODEL.md §2, §5, <c>lease_status</c>).
/// The state machine is <c>pending → provisioning → active ⇄ suspended → ended</c>, with
/// <c>provisioning → failed</c> and <c>suspended → ended</c> edges (docs/TUNNEL.md §8).
/// <see cref="Suspended"/> never bills; <c>billable_seconds</c> accrues only over healthy intervals.
/// </summary>
public enum LeaseStatus
{
    Pending,
    Provisioning,
    Active,
    Suspended,
    Ended,
    Failed,
}

/// <summary>
/// Why a lease ended (docs/DATA_MODEL.md §2, §5, <c>lease_end_reason</c>). <see cref="Released"/> is a
/// consumer DELETE; <see cref="Expired"/> is the TTL reaper; <see cref="HostDisconnect"/> /
/// <see cref="ContainerLost"/> come from the tunnel grace/reconnect reconciliation (docs/TUNNEL.md §8);
/// <see cref="Admin"/> is a moderator action; <see cref="PaymentFailed"/> is a billing gate.
/// </summary>
public enum LeaseEndReason
{
    Released,
    Expired,
    HostDisconnect,
    ContainerLost,
    Admin,
    PaymentFailed,
}

/// <summary>
/// Maps the domain enums to and from their PostgreSQL native-enum labels (docs/DATA_MODEL.md §2).
/// Single-word labels are the lowercase enum name (<see cref="ToLabel{TEnum}"/>); multi-word enums
/// (e.g. <c>lease_end_reason</c>'s <c>host_disconnect</c>) use the snake_case pair
/// (<see cref="ToSnakeLabel{TEnum}"/> / <see cref="ParseSnake{TEnum}"/>). The Dapper repositories pass
/// the label as a text parameter cast to the enum type, and read the column back as text so Dapper's
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

    /// <summary>
    /// The snake_case PostgreSQL enum label for <paramref name="value"/> — the enum name with an
    /// underscore before each interior capital, lowercased (<c>HostDisconnect</c> → <c>host_disconnect</c>).
    /// </summary>
    public static string ToSnakeLabel<TEnum>(TEnum value) where TEnum : struct, Enum
    {
        var name = value.ToString();
        var sb = new System.Text.StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0)
                {
                    sb.Append('_');
                }

                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    /// <summary>Parses a snake_case PostgreSQL enum label back to <typeparamref name="TEnum"/>.</summary>
    public static TEnum ParseSnake<TEnum>(string label) where TEnum : struct, Enum =>
        Enum.Parse<TEnum>(label.Replace("_", string.Empty, StringComparison.Ordinal), ignoreCase: true);
}
