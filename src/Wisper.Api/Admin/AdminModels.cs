using System.Text.Json;
using System.Text.Json.Serialization;
using Wisper.Api.Domain;
using Host = Wisper.Api.Domain.Host;

namespace Wisper.Api.Admin;

/// <summary>
/// Response of <c>GET /v1/admin/overview</c> (docs/API.md §8): platform revenue (the accrued
/// <c>platform_revenue</c> ledger balance — the source of truth, docs/DATA_MODEL.md §7), the live active-lease
/// count, host/consumer counts, and a coarse health flag.
/// </summary>
public sealed record AdminOverviewResponse(
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("revenue_cents")] long RevenueCents,
    [property: JsonPropertyName("wallet_liability_cents")] long WalletLiabilityCents,
    [property: JsonPropertyName("host_earnings_cents")] long HostEarningsCents,
    [property: JsonPropertyName("active_lease_count")] int ActiveLeaseCount,
    [property: JsonPropertyName("host_count")] int HostCount,
    [property: JsonPropertyName("online_host_count")] int OnlineHostCount,
    [property: JsonPropertyName("user_count")] int UserCount,
    [property: JsonPropertyName("health")] string Health);

/// <summary>
/// A platform policy version on the wire (docs/API.md §8, docs/DATA_MODEL.md §11) — the admin-tunable fee,
/// caps, minimum top-up, and fraud guards, plus when it took effect and who authored it.
/// </summary>
public sealed record PolicyView(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("fee_bps")] int FeeBps,
    [property: JsonPropertyName("min_topup_cents")] long MinTopupCents,
    [property: JsonPropertyName("max_concurrent_leases_per_user")] int? MaxConcurrentLeasesPerUser,
    [property: JsonPropertyName("max_ttl_seconds_cap")] int? MaxTtlSecondsCap,
    [property: JsonPropertyName("first_topup_max_cents")] long? FirstTopupMaxCents,
    [property: JsonPropertyName("new_account_window_hours")] int? NewAccountWindowHours,
    [property: JsonPropertyName("new_account_max_topup_cents_per_day")] long? NewAccountMaxTopupCentsPerDay,
    [property: JsonPropertyName("max_spend_cents_per_day")] long? MaxSpendCentsPerDay,
    [property: JsonPropertyName("effective_from")] DateTimeOffset EffectiveFrom,
    [property: JsonPropertyName("created_by")] Guid? CreatedBy)
{
    /// <summary>Projects a stored <see cref="PlatformPolicy"/> to its wire view.</summary>
    public static PolicyView From(PlatformPolicy policy) => new(
        policy.Id,
        policy.FeeBps,
        policy.MinTopupCents,
        policy.MaxConcurrentLeasesPerUser,
        policy.MaxTtlSecondsCap,
        policy.FirstTopupMaxCents,
        policy.NewAccountWindowHours,
        policy.NewAccountMaxTopupCentsPerDay,
        policy.MaxSpendCentsPerDay,
        policy.EffectiveFrom,
        policy.CreatedBy);
}

/// <summary>Response of <c>GET /v1/admin/policy</c> — the version in force now plus the full version history.</summary>
public sealed record PolicyHistoryResponse(
    [property: JsonPropertyName("active")] PolicyView? Active,
    [property: JsonPropertyName("versions")] IReadOnlyList<PolicyView> Versions);

/// <summary>
/// Body of <c>PUT /v1/admin/policy</c> (docs/API.md §8, docs/DATA_MODEL.md §11): the fields of a <b>new</b>
/// versioned policy row. <c>fee_bps</c> is required (0..10000); the rest default to unset/unlimited. The row
/// is append-only — publishing never edits an existing version — and takes effect immediately unless
/// <c>effective_from</c> schedules it.
/// </summary>
public sealed record PolicyUpdateRequest(
    [property: JsonPropertyName("fee_bps")] int? FeeBps,
    [property: JsonPropertyName("min_topup_cents")] long? MinTopupCents = null,
    [property: JsonPropertyName("max_concurrent_leases_per_user")] int? MaxConcurrentLeasesPerUser = null,
    [property: JsonPropertyName("max_ttl_seconds_cap")] int? MaxTtlSecondsCap = null,
    [property: JsonPropertyName("first_topup_max_cents")] long? FirstTopupMaxCents = null,
    [property: JsonPropertyName("new_account_window_hours")] int? NewAccountWindowHours = null,
    [property: JsonPropertyName("new_account_max_topup_cents_per_day")] long? NewAccountMaxTopupCentsPerDay = null,
    [property: JsonPropertyName("max_spend_cents_per_day")] long? MaxSpendCentsPerDay = null,
    [property: JsonPropertyName("effective_from")] DateTimeOffset? EffectiveFrom = null);

/// <summary>A user as the admin sees it (docs/API.md §8) — identity, status, and payment linkage flags.</summary>
public sealed record UserAdminView(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("connect_status")] string ConnectStatus,
    [property: JsonPropertyName("has_stripe_customer")] bool HasStripeCustomer,
    [property: JsonPropertyName("has_connect_account")] bool HasConnectAccount,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt)
{
    /// <summary>Projects a <see cref="User"/> to its admin view (payment ids are flagged, never leaked).</summary>
    public static UserAdminView From(User user) => new(
        user.Id,
        user.Email,
        PgEnum.ToLabel(user.Status),
        PgEnum.ToLabel(user.ConnectStatus),
        !string.IsNullOrWhiteSpace(user.StripeCustomerId),
        !string.IsNullOrWhiteSpace(user.ConnectAccountId),
        user.CreatedAt);
}

/// <summary>A host as the admin sees it (docs/API.md §8) — identity, owner, presence, and capability.</summary>
public sealed record HostAdminView(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("owner_user_id")] Guid OwnerUserId,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("label")] string? Label,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("last_seen_at")] DateTimeOffset? LastSeenAt,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt)
{
    /// <summary>Projects a <see cref="Host"/> to its admin view (the agent token hash is never exposed).</summary>
    public static HostAdminView From(Host host) => new(
        host.Id,
        host.OwnerUserId,
        host.Name,
        host.Label,
        PgEnum.ToLabel(host.Status),
        host.LastSeenAt,
        host.CreatedAt);
}

/// <summary>A cursor-less page of admin search results (docs/API.md §8, §10) with the next page offset.</summary>
public sealed record AdminPage<T>(
    [property: JsonPropertyName("data")] IReadOnlyList<T> Data,
    [property: JsonPropertyName("next_offset")] int? NextOffset);

/// <summary>
/// Body of <c>POST /v1/admin/refunds</c> (docs/API.md §8): the <c>user_id</c> whose unspent wallet credit is
/// refunded, the amount, and optionally the top-up <c>payment_intent</c> to refund against. Audited.
/// </summary>
public sealed record AdminRefundRequest(
    [property: JsonPropertyName("user_id")] Guid? UserId,
    [property: JsonPropertyName("amount_cents")] long? AmountCents,
    [property: JsonPropertyName("payment_intent")] string? PaymentIntent = null,
    [property: JsonPropertyName("reason")] string? Reason = null);

/// <summary>
/// Body of <c>POST /v1/admin/adjustments</c> (docs/API.md §8, docs/DATA_MODEL.md §7, §12): a balanced
/// hand-correction moving <c>amount_cents</c> from <c>debit_account_id</c> to <c>credit_account_id</c>. This
/// is the <b>only</b> way to hand-correct money; it is always balanced and always audited. A <c>reason</c> is
/// recorded on the transaction memo + audit meta.
/// </summary>
public sealed record AdjustmentRequest(
    [property: JsonPropertyName("debit_account_id")] Guid? DebitAccountId,
    [property: JsonPropertyName("credit_account_id")] Guid? CreditAccountId,
    [property: JsonPropertyName("amount_cents")] long? AmountCents,
    [property: JsonPropertyName("reason")] string? Reason = null);

/// <summary>Response of <c>POST /v1/admin/adjustments</c> — the posted txn and the two accounts' new balances.</summary>
public sealed record AdjustmentResponse(
    [property: JsonPropertyName("transaction_id")] Guid TransactionId,
    [property: JsonPropertyName("amount_cents")] long AmountCents,
    [property: JsonPropertyName("debit_account_id")] Guid DebitAccountId,
    [property: JsonPropertyName("credit_account_id")] Guid CreditAccountId,
    [property: JsonPropertyName("debit_balance_cents")] long DebitBalanceCents,
    [property: JsonPropertyName("credit_balance_cents")] long CreditBalanceCents);

/// <summary>One audit-log row on the wire (docs/API.md §8, docs/DATA_MODEL.md §12).</summary>
public sealed record AuditView(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("actor_user_id")] Guid? ActorUserId,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("target_type")] string? TargetType,
    [property: JsonPropertyName("target_id")] Guid? TargetId,
    [property: JsonPropertyName("meta")] JsonElement? Meta,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt);

/// <summary>A keyset-paginated page of the audit trail (docs/API.md §8, §10).</summary>
public sealed record AuditPage(
    [property: JsonPropertyName("data")] IReadOnlyList<AuditView> Data,
    [property: JsonPropertyName("next_cursor")] string? NextCursor);

/// <summary>A ledger account on the wire for admin forensics (docs/API.md §8, docs/DATA_MODEL.md §7).</summary>
public sealed record LedgerAccountView(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("owner_user_id")] Guid? OwnerUserId,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("balance_cents")] long BalanceCents,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt)
{
    /// <summary>Projects a <see cref="LedgerAccount"/> to its wire view.</summary>
    public static LedgerAccountView From(LedgerAccount account) => new(
        account.Id,
        PgEnum.ToSnakeLabel(account.Kind),
        account.OwnerUserId,
        account.Currency,
        account.BalanceCents,
        account.CreatedAt);
}

/// <summary>One ledger entry on the wire for admin forensics (docs/API.md §8, docs/DATA_MODEL.md §7).</summary>
public sealed record LedgerEntryView(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("transaction_id")] Guid TransactionId,
    [property: JsonPropertyName("debit_cents")] long DebitCents,
    [property: JsonPropertyName("credit_cents")] long CreditCents,
    [property: JsonPropertyName("lease_id")] Guid? LeaseId,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt)
{
    /// <summary>Projects a <see cref="LedgerEntry"/> to its wire view.</summary>
    public static LedgerEntryView From(LedgerEntry entry) => new(
        entry.Id,
        entry.TransactionId,
        entry.DebitCents,
        entry.CreditCents,
        entry.LeaseId,
        entry.CreatedAt);
}

/// <summary>
/// Response of <c>GET /v1/admin/ledger/accounts/:id</c> (docs/API.md §8) — the account plus its journal
/// entries, newest first, for read-only forensics.
/// </summary>
public sealed record LedgerAccountForensicsResponse(
    [property: JsonPropertyName("account")] LedgerAccountView Account,
    [property: JsonPropertyName("entries")] IReadOnlyList<LedgerEntryView> Entries);
