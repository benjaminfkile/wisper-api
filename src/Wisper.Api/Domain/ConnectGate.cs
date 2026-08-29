namespace Wisper.Api.Domain;

/// <summary>
/// The capability gate that ties a host's Stripe Connect onboarding state to what the platform lets them do
/// (docs/PAYMENTS.md §5). A host may only flip a wisp host <see cref="HostStatus.Online"/> -- and thus earn --
/// once Connect is <see cref="ConnectStatus.Enabled"/>; until then the agent can connect and even run test
/// leases, but pricing/earning is inert. A <see cref="ConnectStatus.Restricted"/> account keeps accruing
/// earnings in the ledger (none are lost) but has <b>payouts held</b> until it re-enables. Both the
/// host-online path (P7.1) and the payout path (P6.5) funnel their check through here, so the one rule lives
/// in one place.
/// </summary>
public static class ConnectGate
{
    /// <summary>Whether a host with this Connect state may go online and earn (only when enabled).</summary>
    public static bool CanGoOnline(ConnectStatus status) => status == ConnectStatus.Enabled;

    /// <summary>
    /// Whether a host may flip <see cref="HostStatus.Online"/> when its agent tunnel comes up
    /// (docs/TUNNEL.md §3, §8, docs/PAYMENTS.md §5). Presence follows the tunnel, but going online is gated
    /// on <b>earning eligibility</b>, which passes either way:
    /// <list type="bullet">
    /// <item>the owner's Connect account is <see cref="ConnectStatus.Enabled"/>
    /// (<see cref="CanGoOnline(ConnectStatus)"/>) -- free to charge and earn; or</item>
    /// <item>the host <b>earns nothing</b> -- every enabled priced image is 0 cents/min
    /// (<see cref="EarnsNothing"/>): the self-hosted / zero-price posture (task #386). A host that charges
    /// nothing has no earning for Stripe Connect to gate, so it may advertise un-onboarded.</item>
    /// </list>
    /// The moment such a host prices a non-zero image the images surface requires Connect
    /// (<see cref="ChargesRequireConnect"/>), so the gate stays consistent at the one mutation point.
    /// </summary>
    public static bool CanHostGoOnline(
        ConnectStatus ownerConnectStatus, IEnumerable<long> enabledImagePricesCentsPerMin) =>
        CanGoOnline(ownerConnectStatus) || EarnsNothing(enabledImagePricesCentsPerMin);

    /// <summary>
    /// True when a host earns nothing -- every enabled priced image is 0 cents/min (an empty set earns
    /// nothing too). The zero-earn arm of <see cref="CanHostGoOnline"/> and the mirror of the images-surface
    /// guard (<see cref="ChargesRequireConnect"/>).
    /// </summary>
    public static bool EarnsNothing(IEnumerable<long> enabledImagePricesCentsPerMin) =>
        enabledImagePricesCentsPerMin.All(price => price <= 0);

    /// <summary>
    /// True when enabling a priced image must be rejected for lack of Connect onboarding (docs/API.md §6,
    /// docs/PAYMENTS.md §5): the resulting image would be <paramref name="enabled"/> at a non-zero
    /// <paramref name="priceCentsPerMin"/> while the owner is not <see cref="ConnectStatus.Enabled"/> --
    /// charging money requires an enabled Connect account. Enforced at the images PUT/PATCH surface (the
    /// only place pricing changes) so a non-Connect host can never move into the earning arm mid-tunnel.
    /// </summary>
    public static bool ChargesRequireConnect(
        ConnectStatus ownerConnectStatus, bool enabled, long priceCentsPerMin) =>
        enabled && priceCentsPerMin > 0 && !CanGoOnline(ownerConnectStatus);

    /// <summary>
    /// Whether a host with this Connect state may be paid out. Only <see cref="ConnectStatus.Enabled"/>
    /// releases payouts; <see cref="ConnectStatus.Restricted"/> (and every other state) <b>holds</b> them --
    /// earnings keep accruing in the ledger and pay out once the account re-enables (docs/PAYMENTS.md §5, §6).
    /// </summary>
    public static bool CanReceivePayouts(ConnectStatus status) => status == ConnectStatus.Enabled;
}
