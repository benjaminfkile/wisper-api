using System.Text.Json;
using System.Text.Json.Serialization;
using Wisper.Api.Domain;
using Wisper.Api.Leases;
using Host = Wisper.Api.Domain.Host;

namespace Wisper.Api.Admin;

/// <summary>
/// Response of <c>GET /v1/admin/overview</c> (docs/API.md §8): platform revenue (the accrued
/// <c>platform_revenue</c> ledger balance — the source of truth, docs/DATA_MODEL.md §7), the live active-lease
/// count, host/consumer counts, a coarse health flag, and the last ledger reconciliation pass (§7e;
/// non-zero drift means the balance cache has diverged from the journal and an operator should page).
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
    [property: JsonPropertyName("health")] string Health,
    [property: JsonPropertyName("ledger_reconcile")] LedgerReconcileView LedgerReconcile);

/// <summary>
/// The most recent ledger reconciliation pass on the admin overview (docs/API.md §8, docs/DATA_MODEL.md
/// §7e). <see cref="RanAt"/> is <c>null</c> until the first pass completes; <see cref="HasDrift"/> is the
/// operator flag: non-zero drift means the maintained balance cache disagrees with the journal on at
/// least one account and should page.
/// </summary>
public sealed record LedgerReconcileView(
    [property: JsonPropertyName("ran_at")] DateTimeOffset? RanAt,
    [property: JsonPropertyName("accounts_checked")] int AccountsChecked,
    [property: JsonPropertyName("drift_account_count")] int DriftAccountCount,
    [property: JsonPropertyName("total_absolute_drift_cents")] long TotalAbsoluteDriftCents,
    [property: JsonPropertyName("has_drift")] bool HasDrift)
{
    /// <summary>The "never ran on this instance" default surfaced when the monitor is empty.</summary>
    public static LedgerReconcileView Empty { get; } = new(null, 0, 0, 0, false);
}

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
    [property: JsonPropertyName("min_isolation")] string? MinIsolation,
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
        policy.MinIsolation,
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
    [property: JsonPropertyName("min_isolation")] string? MinIsolation = null,
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
    [property: JsonPropertyName("isolation_levels")] IReadOnlyList<string> IsolationLevels,
    [property: JsonPropertyName("default_isolation")] string DefaultIsolation,
    [property: JsonPropertyName("gpu_classes")] IReadOnlyList<string> GpuClasses,
    [property: JsonPropertyName("gpu_count")] int GpuCount,
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
        host.IsolationLevels,
        host.DefaultIsolation,
        host.GpuClasses,
        host.GpuCount,
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

/// <summary>
/// One ledger account row on the admin listing (docs/API.md §8, <c>GET /v1/admin/ledger/accounts</c>,
/// task #194): identity + normal-side bucket, the owning user (id and joined email when present, both
/// <c>null</c> for platform singletons), currency, and maintained balance. This is the shape an operator
/// needs to find the two account ids <c>POST /v1/admin/adjustments</c> requires without opening the database.
/// </summary>
public sealed record LedgerAccountListView(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("owner_user_id")] Guid? OwnerUserId,
    [property: JsonPropertyName("owner_email")] string? OwnerEmail,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("balance_cents")] long BalanceCents)
{
    /// <summary>Projects a <see cref="LedgerAccount"/> to its listing view, folding in the joined email.</summary>
    public static LedgerAccountListView From(LedgerAccount account, string? ownerEmail) => new(
        account.Id,
        PgEnum.ToSnakeLabel(account.Kind),
        account.OwnerUserId,
        ownerEmail,
        account.Currency,
        account.BalanceCents);
}

/// <summary>
/// A lease as the admin sees it (docs/API.md §8, task #57) — identity, owner and host, current lifecycle
/// state, TTL/price snapshot and the metering timeline. The admin listing surfaces the <c>active +
/// suspended</c> set so an operator can find stuck leases (suspended older than X, active past TTL)
/// without SQL; <see cref="ExpiresAt"/> lets the caller/UI compute "past TTL" from the returned row.
/// </summary>
public sealed record AdminLeaseView(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("consumer_user_id")] Guid ConsumerUserId,
    [property: JsonPropertyName("host_id")] Guid HostId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("end_reason")] string? EndReason,
    [property: JsonPropertyName("ttl_seconds")] int TtlSeconds,
    [property: JsonPropertyName("price_cents_per_min")] long PriceCentsPerMin,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("started_at")] DateTimeOffset? StartedAt,
    [property: JsonPropertyName("last_metered_at")] DateTimeOffset? LastMeteredAt,
    [property: JsonPropertyName("billable_seconds")] long BillableSeconds,
    [property: JsonPropertyName("suspended_at")] DateTimeOffset? SuspendedAt,
    [property: JsonPropertyName("ended_at")] DateTimeOffset? EndedAt,
    [property: JsonPropertyName("expires_at")] DateTimeOffset? ExpiresAt)
{
    /// <summary>Projects a stored <see cref="Lease"/> to its admin wire view (task #57).</summary>
    public static AdminLeaseView From(Lease lease) => new(
        TunnelLeaseId.Format(lease.Id),
        lease.ConsumerUserId,
        lease.HostId,
        PgEnum.ToLabel(lease.Status),
        lease.EndReason is { } r ? PgEnum.ToSnakeLabel(r) : null,
        lease.TtlSeconds,
        lease.PriceCentsPerMin,
        lease.Currency,
        lease.CreatedAt,
        lease.StartedAt,
        lease.LastMeteredAt,
        lease.BillableSeconds,
        lease.SuspendedAt,
        lease.EndedAt,
        lease.StartedAt is { } started ? started.AddSeconds(lease.TtlSeconds) : null);
}
