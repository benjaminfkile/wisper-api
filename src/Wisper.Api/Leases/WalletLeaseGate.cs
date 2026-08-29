using Microsoft.Extensions.Logging;
using Wisper.Api.Domain;
using Wisper.Api.Infrastructure;
using Wisper.Api.Ledger;
using Wisper.Api.Metering;
using Wisper.Api.Persistence.Leases;
using Wisper.Api.Policy;

namespace Wisper.Api.Leases;

/// <summary>
/// The real <see cref="ILeaseWalletGate"/> (docs/PAYMENTS.md §4, docs/DATA_MODEL.md §8) -- the wallet-hold
/// lifecycle for a consumer lease, pure internal ledger against pre-funded wallet money (no Stripe). It
/// replaces the Phase-4 allow-hook: at <c>POST /leases</c> it enforces the per-user concurrency cap and
/// gates on the wallet balance (denying <c>402</c> before any <c>lease.create</c> reaches the host), then
/// earmarks the <c>⌈ttl/60⌉·price</c> hold once the lease id exists; at lease end it releases the unused
/// remainder. The ledger's non-negative guard on <c>lease_holds</c> (§7d) backs the guarantee the hold can
/// never be over-drawn -- because it covers the whole max lease, "insufficient funds mid-lease" cannot
/// happen (docs/PAYMENTS.md §4).
/// </summary>
public sealed class WalletLeaseGate : ILeaseWalletGate
{
    private readonly LedgerService _ledger;
    private readonly ILeaseRepository _leases;
    private readonly PlatformPolicyService _policy;
    private readonly FraudGuardService _fraud;
    private readonly ILogger<WalletLeaseGate> _logger;

    public WalletLeaseGate(
        LedgerService ledger,
        ILeaseRepository leases,
        PlatformPolicyService policy,
        FraudGuardService fraud,
        ILogger<WalletLeaseGate> logger)
    {
        _ledger = ledger;
        _leases = leases;
        _policy = policy;
        _fraud = fraud;
        _logger = logger;
    }

    public async Task<WalletGateDecision> AuthorizeHoldAsync(
        Guid consumerUserId, long holdCents, string currency, CancellationToken ct = default)
    {
        // Business quota first (docs/API.md §11): a user already at the concurrency cap is refused with
        // at_capacity (409), regardless of wallet balance.
        await EnforceConcurrencyCapAsync(consumerUserId, ct);

        // The per-user daily spend cap (docs/PAYMENTS.md §7) -- the fraud guard measured by up-front holds,
        // refused with limit_exceeded (429) before any wallet check or tunnel frame.
        await _fraud.EnforceLeaseSpendAllowedAsync(consumerUserId, holdCents, ct);

        // A free image needs no hold -- nothing to gate on.
        if (holdCents <= 0)
        {
            return WalletGateDecision.Allow();
        }

        // The hard gate: the wallet must be able to cover the whole hold up front (docs/PAYMENTS.md §4).
        // Checking the maintained balance here means an unaffordable lease is refused BEFORE any
        // lease.create frame is sent -- no compute is provisioned that can't be paid for.
        var wallet = await _ledger.GetOrCreateAccountAsync(
            LedgerAccountKind.UserWallet, consumerUserId, currency, ct);
        return wallet.BalanceCents >= holdCents
            ? WalletGateDecision.Allow()
            : WalletGateDecision.Deny(holdCents, wallet.BalanceCents);
    }

    public async Task<Guid?> PlaceHoldAsync(
        Guid consumerUserId, Guid leaseId, long holdCents, string currency, CancellationToken ct = default)
    {
        if (holdCents <= 0)
        {
            return null; // free image: no hold to earmark
        }

        var wallet = await _ledger.GetOrCreateAccountAsync(
            LedgerAccountKind.UserWallet, consumerUserId, currency, ct);
        var holds = await _ledger.GetOrCreateAccountAsync(
            LedgerAccountKind.LeaseHolds, null, currency, ct);

        try
        {
            // Keyed by the lease id so a retried create dedupes at the ledger and can't double-hold.
            var posted = await _ledger.PostAsync(
                LedgerFlows.LeaseHold(
                    wallet.Id, holds.Id, leaseId, holdCents,
                    idempotencyKey: HoldIdempotencyKey(leaseId),
                    memo: $"lease_hold {leaseId} {holdCents}¢"),
                ct);
            _logger.LogInformation(
                "placed wallet hold for lease {LeaseId}: {Hold}¢{Replay}",
                leaseId, holdCents, posted.WasDeduplicated ? " [replay]" : string.Empty);
            return posted.Transaction.Id;
        }
        catch (LedgerException ex) when (ex.Reason == LedgerViolation.InsufficientFunds)
        {
            // AuthorizeHoldAsync already gated on balance; reaching the non-negative guard here means the
            // wallet was drained between the gate and the post (a rare race). Surface it as 402 -- the same
            // hard gate, one step later -- rather than a 500.
            throw new ApiException(
                ApiErrorCode.InsufficientFunds,
                "Wallet balance is below the required hold.",
                new { required_cents = holdCents });
        }
    }

    public async Task ReleaseHoldAsync(Guid leaseId, CancellationToken ct = default)
    {
        var lease = await _leases.GetByIdAsync(leaseId, ct);
        if (lease is null)
        {
            return;
        }

        // The hold covered the whole max lease (⌈ttl/60⌉·price); the charged amount is the cumulative
        // integer-floor charge the meter posted for the accrued billable_seconds. The remainder is exactly
        // what is still earmarked in lease_holds for this lease, so releasing it zeroes the lease's hold.
        // (For a lease that was revived after a grace-expiry release, the pre-revive remainder already
        // returned to the wallet under a distinct release key; this second release, keyed to the revival
        // hold generation via lease.HoldTxnId, unwinds only what is still earmarked now -- the ledger's
        // non-negative guard on lease_holds is the backstop if the arithmetic is ever wrong.)
        var holdCents = LeaseHoldPricing.EstimateHoldCents(lease.TtlSeconds, lease.PriceCentsPerMin);
        if (holdCents <= 0)
        {
            return; // free image: no hold was placed
        }

        var chargedCents = MeteringService.ChargeCentsFor(lease.BillableSeconds, lease.PriceCentsPerMin);
        var remainderCents = holdCents - chargedCents;
        if (remainderCents <= 0)
        {
            return; // fully metered: nothing left to return
        }

        var currency = lease.Currency;
        var wallet = await _ledger.GetOrCreateAccountAsync(
            LedgerAccountKind.UserWallet, lease.ConsumerUserId, currency, ct);
        var holds = await _ledger.GetOrCreateAccountAsync(
            LedgerAccountKind.LeaseHolds, null, currency, ct);

        // Keyed per hold generation (lease id + current lease.HoldTxnId) so the several lease-end drivers
        // (consumer DELETE, grace expiry, container-lost) converge on a single release -- a second attempt
        // dedupes and moves no new money. Bundling the hold txn id in also means a post-revive end does
        // NOT collide with the original hold's release (task #23): after revive stamps the new
        // hold_txn_id, this key becomes distinct from the pre-revive release key, so the second cycle's
        // remainder actually posts back to the wallet.
        var posted = await _ledger.PostAsync(
            LedgerFlows.HoldRelease(
                holds.Id, wallet.Id, leaseId, remainderCents,
                idempotencyKey: ReleaseIdempotencyKey(leaseId, lease.HoldTxnId),
                memo: $"hold_release {leaseId} {remainderCents}¢"),
            ct);
        _logger.LogInformation(
            "released wallet hold remainder for lease {LeaseId}: {Remainder}¢ (hold {Hold}¢ − charged {Charged}¢){Replay}",
            leaseId, remainderCents, holdCents, chargedCents, posted.WasDeduplicated ? " [replay]" : string.Empty);
    }

    public async Task<RevivalHoldOutcome> PlaceRevivalHoldAsync(
        Guid consumerUserId, Guid leaseId, long holdCents, string currency, CancellationToken ct = default)
    {
        if (holdCents <= 0)
        {
            return RevivalHoldOutcome.Allow(holdTxnId: null); // free image: no hold to earmark
        }

        // Mirror the create-time balance gate (docs/PAYMENTS.md §4): a revived paid lease must not run
        // active without a wallet hold covering its remaining time. A shortfall denies the revive so the
        // caller ends the lease rather than silently reviving into an unbacked active state
        // (docs/TUNNEL.md §8 -- wisp's TTL reaper reclaims the container regardless).
        var wallet = await _ledger.GetOrCreateAccountAsync(
            LedgerAccountKind.UserWallet, consumerUserId, currency, ct);
        if (wallet.BalanceCents < holdCents)
        {
            _logger.LogWarning(
                "revive denied for lease {LeaseId}: wallet balance {Available}¢ short of revival hold {Required}¢",
                leaseId, wallet.BalanceCents, holdCents);
            return RevivalHoldOutcome.Deny(holdCents, wallet.BalanceCents);
        }

        var holds = await _ledger.GetOrCreateAccountAsync(
            LedgerAccountKind.LeaseHolds, null, currency, ct);

        try
        {
            // Keyed by the lease id (revival slot) so a repeated revive/flap for the same lease dedupes
            // at the ledger and cannot stack duplicate holds.
            var posted = await _ledger.PostAsync(
                LedgerFlows.LeaseHold(
                    wallet.Id, holds.Id, leaseId, holdCents,
                    idempotencyKey: RevivalHoldIdempotencyKey(leaseId),
                    memo: $"lease_hold {leaseId} {holdCents}¢ (revive)"),
                ct);
            _logger.LogInformation(
                "placed revival wallet hold for lease {LeaseId}: {Hold}¢{Replay}",
                leaseId, holdCents, posted.WasDeduplicated ? " [replay]" : string.Empty);
            return RevivalHoldOutcome.Allow(posted.Transaction.Id);
        }
        catch (LedgerException ex) when (ex.Reason == LedgerViolation.InsufficientFunds)
        {
            // Wallet drained between the balance check and the post (a rare race). Surface as a denied
            // outcome -- same hard gate, one step later -- so the reconciler ends the lease rather than
            // reviving unbacked.
            _logger.LogWarning(ex,
                "revive denied for lease {LeaseId}: wallet drained between check and post (required {Required}¢)",
                leaseId, holdCents);
            return RevivalHoldOutcome.Deny(holdCents, availableCents: 0);
        }
    }

    /// <summary>The <c>lease_hold</c> idempotency key -- stable per lease id.</summary>
    public static string HoldIdempotencyKey(Guid leaseId) => $"lease_hold:{leaseId:D}";

    /// <summary>
    /// The <c>lease_hold</c> idempotency key for a post-grace revival -- a distinct slot per lease id so a
    /// revive after the pre-drop hold was already released (grace expiry) posts a fresh earmark rather than
    /// dedup-replaying the original. Repeated revive attempts for the same lease still dedupe on this key,
    /// so a flap cannot stack duplicate holds.
    /// </summary>
    public static string RevivalHoldIdempotencyKey(Guid leaseId) => $"lease_hold:{leaseId:D}:revive";

    /// <summary>
    /// The <c>hold_release</c> idempotency key -- stable per hold generation, so multiple lease-end drivers
    /// converge on a single release and a post-revive end does not collide with the pre-revive release.
    /// Falls back to the lease-id-only form when the hold txn id is unknown (a lease whose hold never
    /// posted, e.g. a free image or a create that failed before stamping the txn id).
    /// </summary>
    public static string ReleaseIdempotencyKey(Guid leaseId, Guid? holdTxnId) =>
        holdTxnId is { } txnId
            ? $"hold_release:{leaseId:D}:{txnId:D}"
            : $"hold_release:{leaseId:D}";

    /// <summary>
    /// Enforces <c>platform_policy.max_concurrent_leases_per_user</c> (docs/API.md §11): counts the caller's
    /// leases that still hold a slot (pending/provisioning/active/suspended -- terminal ended/failed do not)
    /// and throws <c>at_capacity</c> when the cap is reached. No policy or an unset cap ⇒ unlimited.
    /// </summary>
    private async Task EnforceConcurrencyCapAsync(Guid consumerUserId, CancellationToken ct)
    {
        var policy = await _policy.GetActiveAsync(ct);
        if (policy?.MaxConcurrentLeasesPerUser is not { } cap)
        {
            return;
        }

        var leases = await _leases.ListByConsumerAsync(consumerUserId, ct);
        var live = leases.Count(l => l.Status
            is LeaseStatus.Pending
            or LeaseStatus.Provisioning
            or LeaseStatus.Active
            or LeaseStatus.Suspended);
        if (live >= cap)
        {
            throw new ApiException(
                ApiErrorCode.AtCapacity,
                "You have reached your maximum number of concurrent leases.",
                new { max_concurrent_leases_per_user = cap, active = live });
        }
    }
}
