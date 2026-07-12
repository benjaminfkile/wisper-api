using System.Text.Json.Serialization;

namespace Wisper.Api.Billing;

/// <summary>Body of <c>POST /v1/billing/topup</c> (docs/API.md §5, docs/PAYMENTS.md §3): the amount to fund.</summary>
public sealed record TopupRequest(
    [property: JsonPropertyName("amount_cents")] long? AmountCents);

/// <summary>
/// Response of <c>POST /v1/billing/topup</c> — the PaymentIntent <c>client_secret</c> the browser confirms
/// with the Payment Element (SCA/3-D Secure handled by Stripe). The wallet is <b>not</b> credited here; that
/// happens only on the <c>payment_intent.succeeded</c> webhook (docs/PAYMENTS.md §3).
/// </summary>
public sealed record TopupResponse(
    [property: JsonPropertyName("client_secret")] string ClientSecret);

/// <summary>
/// Response of <c>POST /v1/billing/payment-methods</c> — the SetupIntent <c>client_secret</c> that saves a
/// payment method for future (off-session) top-ups (docs/PAYMENTS.md §3).
/// </summary>
public sealed record SetupIntentResponse(
    [property: JsonPropertyName("client_secret")] string ClientSecret);

/// <summary>
/// Response of <c>GET /v1/billing</c> (docs/API.md §5): the wallet balance (derived from the ledger, never a
/// stored number, docs/DATA_MODEL.md §7) plus a usage summary of the caller's leases.
/// </summary>
public sealed record BillingResponse(
    [property: JsonPropertyName("balance_cents")] long BalanceCents,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("usage")] BillingUsageSummary Usage);

/// <summary>
/// The caller's lease-usage summary (docs/API.md §5) — counts of leases, total metered seconds, and the
/// actual metered spend so far (Σ over the caller's leases, computed from each lease's billable seconds and
/// price, matching the metering engine's charge math, docs/DATA_MODEL.md §14).
/// </summary>
public sealed record BillingUsageSummary(
    [property: JsonPropertyName("lease_count")] int LeaseCount,
    [property: JsonPropertyName("active_lease_count")] int ActiveLeaseCount,
    [property: JsonPropertyName("billable_seconds")] long BillableSeconds,
    [property: JsonPropertyName("spent_cents")] long SpentCents);

/// <summary>
/// One row of the caller's ledger view (docs/API.md §5, <c>GET /v1/billing/transactions</c>): the money
/// flow, the <b>signed</b> amount it moved on the wallet (a top-up is positive, a hold/refund negative), and
/// enough context to render it. Drawn from the immutable journal — the source of truth (docs/DATA_MODEL.md §7).
/// </summary>
public sealed record BillingTransactionView(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("amount_cents")] long AmountCents,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("lease_id")] Guid? LeaseId,
    [property: JsonPropertyName("external_ref")] string? ExternalRef,
    [property: JsonPropertyName("memo")] string? Memo,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt);

/// <summary>A cursor-paginated page of the caller's ledger view (docs/API.md §10).</summary>
public sealed record BillingTransactionPage(
    [property: JsonPropertyName("data")] IReadOnlyList<BillingTransactionView> Data,
    [property: JsonPropertyName("next_cursor")] string? NextCursor);
