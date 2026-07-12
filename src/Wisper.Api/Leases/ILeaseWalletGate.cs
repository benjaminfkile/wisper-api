namespace Wisper.Api.Leases;

/// <summary>
/// The wallet-gate seam a lease-create runs through before any <c>lease.create</c> frame reaches the
/// host (docs/API.md §5, docs/DATA_MODEL.md §8, docs/PAYMENTS.md §4). In the finished system this places
/// the up-front wallet hold — <c>⌈ttl/60⌉·price</c> earmarked out of the consumer's wallet — and refuses
/// the lease with <c>402</c> when the balance can't cover it, so a consumer literally cannot start a
/// lease they can't afford and no compute is provisioned that can't be paid for.
/// <para>
/// <b>PHASE 6 HOOK.</b> Wallet-gating lands in P6.3 (billing: lease hold/charge/release + wallet gate on
/// lease start). Until then the only wired implementation is <see cref="AllowAllLeaseWalletGate"/>, which
/// <b>always allows</b> and places no hold — see its remarks.
/// </para>
/// </summary>
public interface ILeaseWalletGate
{
    /// <summary>
    /// Decides whether <paramref name="consumerUserId"/> may start a lease requiring a
    /// <paramref name="holdCents"/> hold in <paramref name="currency"/>, placing the hold when it does.
    /// A denied decision maps to <c>402 insufficient_funds</c> at the endpoint.
    /// </summary>
    Task<WalletGateDecision> AuthorizeHoldAsync(
        Guid consumerUserId, long holdCents, string currency, CancellationToken ct = default);
}

/// <summary>
/// The outcome of <see cref="ILeaseWalletGate.AuthorizeHoldAsync"/>: whether the hold was authorized and,
/// on refusal, the figures the <c>402</c> envelope reports (docs/API.md §3).
/// </summary>
public sealed record WalletGateDecision(bool Allowed, long RequiredCents, long AvailableCents)
{
    /// <summary>An authorized hold (no shortfall figures needed).</summary>
    public static WalletGateDecision Allow() => new(true, 0, 0);

    /// <summary>A refusal — the wallet is short of <paramref name="requiredCents"/>.</summary>
    public static WalletGateDecision Deny(long requiredCents, long availableCents) =>
        new(false, requiredCents, availableCents);
}

/// <summary>
/// The stand-in <see cref="ILeaseWalletGate"/> for phases before billing (P6). It <b>always allows</b>
/// and places <b>no</b> ledger hold — there is no wallet balance to check yet, so every lease-create is
/// authorized. This is the clearly-marked "allows for now" hook the P4.2 surface is built around; P6.3
/// replaces this registration with the real hold-placing gate and nothing above it has to change.
/// </summary>
public sealed class AllowAllLeaseWalletGate : ILeaseWalletGate
{
    public Task<WalletGateDecision> AuthorizeHoldAsync(
        Guid consumerUserId, long holdCents, string currency, CancellationToken ct = default) =>
        // TODO(P6.3 / task 210): place the real wallet hold and deny (402) on insufficient funds.
        Task.FromResult(WalletGateDecision.Allow());
}
