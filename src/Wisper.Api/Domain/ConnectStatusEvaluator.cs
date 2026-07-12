namespace Wisper.Api.Domain;

/// <summary>
/// Derives a host's <see cref="ConnectStatus"/> from a Stripe Connect account's capability flags
/// (docs/PAYMENTS.md §5). This is the single rule both the <c>account.updated</c> webhook and the status
/// endpoint funnel through, so the gate is computed one way everywhere:
/// <list type="bullet">
/// <item><b>enabled</b> — <c>charges_enabled &amp;&amp; payouts_enabled</c>: fully onboarded, may go online and
/// earn.</item>
/// <item><b>restricted</b> — not enabled but details were submitted: Stripe finished intake and is holding
/// the account (needs more info / reviewing). Earnings still accrue in the ledger; payouts are held and a
/// re-onboarding link is surfaced (§5).</item>
/// <item><b>pending</b> — not enabled and details not yet submitted: still mid-onboarding.</item>
/// </list>
/// </summary>
public static class ConnectStatusEvaluator
{
    /// <summary>Maps the capability flags to a <see cref="ConnectStatus"/> per the rule above.</summary>
    public static ConnectStatus Evaluate(bool chargesEnabled, bool payoutsEnabled, bool detailsSubmitted)
    {
        if (chargesEnabled && payoutsEnabled)
        {
            return ConnectStatus.Enabled;
        }

        return detailsSubmitted ? ConnectStatus.Restricted : ConnectStatus.Pending;
    }
}
