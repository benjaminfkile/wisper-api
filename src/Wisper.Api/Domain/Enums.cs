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
/// The kind of a <c>ledger_account</c> (docs/DATA_MODEL.md §2, §7, <c>ledger_account_kind</c>). The
/// <b>normal side</b> of each — how a positive balance accrues — is fixed:
/// <see cref="UserWallet"/>, <see cref="HostEarnings"/>, <see cref="LeaseHolds"/> and
/// <see cref="PlatformRevenue"/> are <i>credit-normal</i>; <see cref="PlatformCash"/> and
/// <see cref="StripeFees"/> are <i>debit-normal</i>. The two earmarked liabilities
/// (<see cref="UserWallet"/>, <see cref="LeaseHolds"/>) are guarded non-negative (§7d).
/// </summary>
public enum LedgerAccountKind
{
    UserWallet,
    HostEarnings,
    LeaseHolds,
    PlatformRevenue,
    PlatformCash,
    StripeFees,
}

/// <summary>
/// The kind of a <c>ledger_transaction</c> (docs/DATA_MODEL.md §2, §8, <c>ledger_txn_kind</c>) — the
/// money flow a balanced set of entries represents. <see cref="Topup"/>/<see cref="Refund"/> pair with
/// Stripe; the lease inner loop (<see cref="LeaseHold"/> → <see cref="LeaseCharge"/> →
/// <see cref="HoldRelease"/>) is pure internal ledger; <see cref="Payout"/> drains host earnings;
/// <see cref="Chargeback"/>/<see cref="Adjustment"/> are admin-initiated corrections (§7).
/// </summary>
public enum LedgerTxnKind
{
    Topup,
    LeaseHold,
    LeaseCharge,
    HoldRelease,
    Payout,
    Refund,
    Chargeback,
    Adjustment,
}

/// <summary>
/// Lifecycle of a host <see cref="Payout"/> (docs/DATA_MODEL.md §2, §9, <c>payout_status</c>) — a Connect
/// transfer draining <see cref="LedgerAccountKind.HostEarnings"/>. <see cref="Pending"/> is created before
/// the Stripe call; <see cref="InTransit"/>/<see cref="Paid"/> track the transfer; <see cref="Failed"/>/
/// <see cref="Canceled"/> are terminal non-success states. The multi-word <see cref="InTransit"/> maps to
/// the snake_case label <c>in_transit</c>.
/// </summary>
public enum PayoutStatus
{
    Pending,
    InTransit,
    Paid,
    Failed,
    Canceled,
}

/// <summary>
/// Processing state of a persisted <see cref="StripeEvent"/> (docs/DATA_MODEL.md §2, §9,
/// <c>stripe_event_status</c>). A webhook is persisted <see cref="Received"/> then handled to exactly one
/// of <see cref="Processed"/> (applied), <see cref="Ignored"/> (nothing to do), or <see cref="Failed"/>
/// (errored, retained for retry/inspection). Every webhook is processed exactly once (docs/PAYMENTS.md §9).
/// </summary>
public enum StripeEventStatus
{
    Received,
    Processed,
    Ignored,
    Failed,
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
