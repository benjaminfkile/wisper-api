using System.Text.Json.Serialization;

namespace Wisper.Api.Hosts;

/// <summary>
/// Response of <c>POST /v1/hosts/connect</c> (docs/API.md §6, docs/PAYMENTS.md §5): the hosted-onboarding URL
/// the host is redirected to, plus the caller's current <c>connect_status</c>. Completion is confirmed by the
/// <c>account.updated</c> webhook, never by the redirect — so this status is the state <i>before</i> the host
/// finishes on Stripe.
/// </summary>
public sealed record HostConnectResponse(
    [property: JsonPropertyName("onboarding_url")] string OnboardingUrl,
    [property: JsonPropertyName("connect_status")] string ConnectStatus);

/// <summary>
/// Response of <c>GET /v1/hosts/connect/status</c> (docs/API.md §6, docs/PAYMENTS.md §5): the derived
/// <c>connect_status</c>, the capability flags it comes from, whether the host may go online
/// (<see cref="Wisper.Api.Domain.ConnectGate"/>), and what Stripe still requires to finish/keep the account.
/// </summary>
public sealed record HostConnectStatusResponse(
    [property: JsonPropertyName("connect_status")] string ConnectStatus,
    [property: JsonPropertyName("charges_enabled")] bool ChargesEnabled,
    [property: JsonPropertyName("payouts_enabled")] bool PayoutsEnabled,
    [property: JsonPropertyName("details_submitted")] bool DetailsSubmitted,
    [property: JsonPropertyName("can_go_online")] bool CanGoOnline,
    [property: JsonPropertyName("requirements")] HostConnectRequirements Requirements);

/// <summary>
/// The outstanding Connect onboarding requirements (docs/PAYMENTS.md §5) — "what's still required": the
/// fields Stripe needs now (<see cref="CurrentlyDue"/>/<see cref="PastDue"/>), soon
/// (<see cref="EventuallyDue"/>), what it's verifying (<see cref="PendingVerification"/>), and the reason the
/// account is blocked if any (<see cref="DisabledReason"/>).
/// </summary>
public sealed record HostConnectRequirements(
    [property: JsonPropertyName("disabled_reason")] string? DisabledReason,
    [property: JsonPropertyName("currently_due")] IReadOnlyList<string> CurrentlyDue,
    [property: JsonPropertyName("past_due")] IReadOnlyList<string> PastDue,
    [property: JsonPropertyName("eventually_due")] IReadOnlyList<string> EventuallyDue,
    [property: JsonPropertyName("pending_verification")] IReadOnlyList<string> PendingVerification)
{
    /// <summary>An empty requirements set — for a host that has no Connect account yet.</summary>
    public static HostConnectRequirements Empty { get; } = new(
        DisabledReason: null,
        CurrentlyDue: Array.Empty<string>(),
        PastDue: Array.Empty<string>(),
        EventuallyDue: Array.Empty<string>(),
        PendingVerification: Array.Empty<string>());
}
