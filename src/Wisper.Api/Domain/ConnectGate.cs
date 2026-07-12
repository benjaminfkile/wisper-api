namespace Wisper.Api.Domain;

/// <summary>
/// The capability gate that ties a host's Stripe Connect onboarding state to what the platform lets them do
/// (docs/PAYMENTS.md §5). A host may only flip a wisp host <see cref="HostStatus.Online"/> — and thus earn —
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
    /// Whether a host with this Connect state may be paid out. Only <see cref="ConnectStatus.Enabled"/>
    /// releases payouts; <see cref="ConnectStatus.Restricted"/> (and every other state) <b>holds</b> them —
    /// earnings keep accruing in the ledger and pay out once the account re-enables (docs/PAYMENTS.md §5, §6).
    /// </summary>
    public static bool CanReceivePayouts(ConnectStatus status) => status == ConnectStatus.Enabled;
}
