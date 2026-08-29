namespace Wisper.Api.Leases;

/// <summary>
/// The wallet-hold lifecycle a consumer lease runs through (docs/API.md §5, docs/DATA_MODEL.md §8,
/// docs/PAYMENTS.md §4) -- the real billing gate that replaces the Phase-4 allow-hook (P6.3). It has three
/// steps around a lease's life:
/// <list type="number">
/// <item><see cref="AuthorizeHoldAsync"/> -- the pre-provision hard gate at <c>POST /leases</c>: enforce
/// <c>max_concurrent_leases_per_user</c> and check the wallet can cover the <c>⌈ttl/60⌉·price</c> hold.
/// A shortfall denies with <c>402 insufficient_funds</c> <b>before any <c>lease.create</c> frame reaches
/// the host</b> -- no compute is provisioned that can't be paid for.</item>
/// <item><see cref="PlaceHoldAsync"/> -- once the lease id exists, earmark the hold out of the wallet with a
/// <c>lease_hold</c> ledger transaction (wallet → lease_holds); the metering engine then debits it per tick
/// (<c>lease_charge</c>).</item>
/// <item><see cref="ReleaseHoldAsync"/> -- at lease end, return the unused remainder to the wallet with a
/// <c>hold_release</c> transaction (lease_holds → wallet). Because the hold covered the entire max lease,
/// it can never be exhausted mid-run.</item>
/// </list>
/// The lease inner loop touches Stripe zero times -- it is pure internal ledger against pre-funded wallet
/// money (docs/PAYMENTS.md §2). Built against interfaces so the money logic is unit-testable without
/// Postgres (Grunt has no database).
/// </summary>
public interface ILeaseWalletGate
{
    /// <summary>
    /// The pre-provision gate for a lease requiring a <paramref name="holdCents"/> hold in
    /// <paramref name="currency"/> (docs/PAYMENTS.md §4). Enforces the per-user concurrency cap (throwing
    /// <c>at_capacity</c>) and checks the wallet balance covers the hold; a shortfall returns a denied
    /// <see cref="WalletGateDecision"/> the endpoint maps to <c>402 insufficient_funds</c>. A zero hold
    /// (free image) always allows. No ledger is written here -- the hold is placed post-provision by
    /// <see cref="PlaceHoldAsync"/>.
    /// </summary>
    Task<WalletGateDecision> AuthorizeHoldAsync(
        Guid consumerUserId, long holdCents, string currency, CancellationToken ct = default);

    /// <summary>
    /// Earmarks the hold for <paramref name="leaseId"/> once the lease exists: posts the <c>lease_hold</c>
    /// ledger transaction (wallet → lease_holds) and returns its transaction id. A zero hold places nothing
    /// and returns <c>null</c>. Idempotent per lease id, so a retried create can't double-hold.
    /// </summary>
    Task<Guid?> PlaceHoldAsync(
        Guid consumerUserId, Guid leaseId, long holdCents, string currency, CancellationToken ct = default);

    /// <summary>
    /// At lease end, returns the unused remainder of the hold to the wallet: posts a <c>hold_release</c>
    /// (lease_holds → wallet) for <c>hold − charged</c>, where the hold is <c>⌈ttl/60⌉·price</c> and the
    /// charged amount is derived from the lease's metered <c>billable_seconds</c>. A free lease (no hold) or
    /// a fully-charged lease releases nothing. Idempotent per hold generation (keyed by
    /// <see cref="Wisper.Api.Domain.Lease.HoldTxnId"/>), so the several lease-end drivers
    /// (consumer DELETE, grace expiry, container-lost) converge on a single release -- and a post-revive end
    /// does not collide with the original hold's release.
    /// </summary>
    Task ReleaseHoldAsync(Guid leaseId, CancellationToken ct = default);

    /// <summary>
    /// On a post-grace (or heartbeat) revive of an <c>ended (host_disconnect)</c> lease whose container
    /// survived the agent restart, re-establish the wallet hold for the remaining lease time before the
    /// lease transitions back to <c>active</c>. Mirrors <see cref="AuthorizeHoldAsync"/> +
    /// <see cref="PlaceHoldAsync"/> in a single step: enforce the wallet balance covers
    /// <paramref name="holdCents"/> (returning a denied <see cref="RevivalHoldOutcome"/> the caller maps to
    /// "insufficient funds → end the lease" per docs/PAYMENTS.md §4 and docs/TUNNEL.md §8), and on allow
    /// post the <c>lease_hold</c> (wallet → lease_holds) so the revived lease never runs unbacked. A zero
    /// hold (free image) always allows and places nothing. Idempotent per lease id -- a repeated
    /// revive/flap dedupes on the revival hold key and does not stack duplicate holds.
    /// </summary>
    Task<RevivalHoldOutcome> PlaceRevivalHoldAsync(
        Guid consumerUserId, Guid leaseId, long holdCents, string currency, CancellationToken ct = default);
}

/// <summary>
/// The outcome of <see cref="ILeaseWalletGate.AuthorizeHoldAsync"/>: whether the hold was authorized and,
/// on refusal, the figures the <c>402</c> envelope reports (docs/API.md §3).
/// </summary>
public sealed record WalletGateDecision(bool Allowed, long RequiredCents, long AvailableCents)
{
    /// <summary>An authorized hold (no shortfall figures needed).</summary>
    public static WalletGateDecision Allow() => new(true, 0, 0);

    /// <summary>A refusal -- the wallet is short of <paramref name="requiredCents"/>.</summary>
    public static WalletGateDecision Deny(long requiredCents, long availableCents) =>
        new(false, requiredCents, availableCents);
}

/// <summary>
/// The up-front hold estimate for a lease: <c>⌈ttl/60⌉·price</c> in integer cents (docs/DATA_MODEL.md §8,
/// docs/PAYMENTS.md §4). Money is never floats (§1), so the ceiling is integer-only. Shared by the
/// lease-create path (which authorizes and places the hold) and the release path (which recomputes the same
/// figure from the lease's immutable snapshot to size the remainder) so the two can never drift.
/// </summary>
public static class LeaseHoldPricing
{
    /// <summary>The hold for a lease of <paramref name="ttlSeconds"/> at <paramref name="priceCentsPerMin"/>.</summary>
    public static long EstimateHoldCents(int ttlSeconds, long priceCentsPerMin)
    {
        var minutes = (ttlSeconds + 59) / 60; // ceil, integer-only (money is never floats, §1)
        return minutes * priceCentsPerMin;
    }

    /// <summary>
    /// The hold needed to cover a revived lease's remaining time (docs/TUNNEL.md §8): the lease's max
    /// billable ceiling (<c>TtlSeconds</c>) minus the seconds already metered (<c>BillableSeconds</c>),
    /// clamped at zero, then <c>⌈remaining/60⌉·price</c>. Because the release at grace-expiry already
    /// returned the pre-drop hold's remainder to the wallet, this is the exact figure that re-earmarks
    /// the same amount of runway for the same lease id -- a subsequent
    /// <see cref="ILeaseWalletGate.ReleaseHoldAsync"/> then unwinds it against the lease's cumulative
    /// <c>BillableSeconds</c> just as it does for a never-revived lease.
    /// </summary>
    public static long EstimateRevivalHoldCents(int ttlSeconds, long billableSeconds, long priceCentsPerMin)
    {
        var remaining = ttlSeconds - billableSeconds;
        if (remaining <= 0)
        {
            return 0;
        }

        var remainingInt = (int)Math.Min(int.MaxValue, remaining);
        return EstimateHoldCents(remainingInt, priceCentsPerMin);
    }
}

/// <summary>
/// The outcome of <see cref="ILeaseWalletGate.PlaceRevivalHoldAsync"/>: the balance
/// <see cref="Decision"/> the reconciler uses to gate the ended→active transition, and -- on allow --
/// the id of the freshly-posted <c>lease_hold</c> transaction (<c>null</c> for a free image, where no
/// hold is posted). The reconciler stamps <see cref="HoldTxnId"/> onto <c>lease.hold_txn_id</c> so
/// subsequent release/charge bookkeeping (docs/PAYMENTS.md §4) is keyed to the new generation and does
/// not collide with the pre-revive hold.
/// </summary>
public sealed record RevivalHoldOutcome(WalletGateDecision Decision, Guid? HoldTxnId)
{
    /// <summary>Whether the revive was funded and the hold placed (or trivially allowed for a free image).</summary>
    public bool Allowed => Decision.Allowed;

    /// <summary>Allow (with the fresh hold txn id, or <c>null</c> for a free image).</summary>
    public static RevivalHoldOutcome Allow(Guid? holdTxnId) => new(WalletGateDecision.Allow(), holdTxnId);

    /// <summary>Deny -- the wallet is short of the required revival hold.</summary>
    public static RevivalHoldOutcome Deny(long requiredCents, long availableCents) =>
        new(WalletGateDecision.Deny(requiredCents, availableCents), null);
}
