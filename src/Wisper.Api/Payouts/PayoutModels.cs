using System.Text.Json.Serialization;

namespace Wisper.Api.Payouts;

/// <summary>
/// Response of <c>GET /v1/earnings</c> (docs/API.md §6): a host's money position -- the accrued, not-yet-paid
/// <c>host_earnings</c> balance (<see cref="AccruedCents"/>), the lifetime total already transferred out
/// (<see cref="PaidCents"/>), the payout threshold, and the Connect gate that decides whether a payout can be
/// made right now. All amounts are integer cents, derived from the ledger + <c>payouts</c> rows.
/// </summary>
public sealed record EarningsResponse(
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("accrued_cents")] long AccruedCents,
    [property: JsonPropertyName("paid_cents")] long PaidCents,
    [property: JsonPropertyName("payout_min_cents")] long PayoutMinCents,
    [property: JsonPropertyName("connect_status")] string ConnectStatus,
    [property: JsonPropertyName("can_receive_payouts")] bool CanReceivePayouts);

/// <summary>
/// One row of the payout history (docs/API.md §6, <c>GET /v1/earnings/payouts</c>) -- a Connect transfer that
/// drained accrued earnings, with its Stripe transfer id (the three-way tie-out ledger ↔ payouts ↔ Stripe,
/// docs/PAYMENTS.md §9) and its current status.
/// </summary>
public sealed record PayoutView(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("amount_cents")] long AmountCents,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("stripe_transfer_id")] string? StripeTransferId,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("period_start")] DateTimeOffset? PeriodStart,
    [property: JsonPropertyName("period_end")] DateTimeOffset? PeriodEnd,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt);

/// <summary>The caller's payout history (docs/API.md §6), newest-first.</summary>
public sealed record PayoutListResponse(
    [property: JsonPropertyName("data")] IReadOnlyList<PayoutView> Data);
