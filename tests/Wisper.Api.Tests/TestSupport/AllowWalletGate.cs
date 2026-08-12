using Wisper.Api.Leases;

namespace Wisper.Api.Tests.TestSupport;

/// <summary>
/// A permissive <see cref="ILeaseWalletGate"/> test double: it always authorizes, places no ledger hold
/// (returns a <c>null</c> hold txn id), and releases nothing. Used by the lease-surface tests that exercise
/// the HTTP/validation/relay paths without wiring the ledger — the real hold/charge/release behaviour is
/// covered by <c>WalletLeaseGateTests</c> against the in-memory ledger.
/// </summary>
public sealed class AllowWalletGate : ILeaseWalletGate
{
    public Task<WalletGateDecision> AuthorizeHoldAsync(
        Guid consumerUserId, long holdCents, string currency, CancellationToken ct = default) =>
        Task.FromResult(WalletGateDecision.Allow());

    public Task<Guid?> PlaceHoldAsync(
        Guid consumerUserId, Guid leaseId, long holdCents, string currency, CancellationToken ct = default) =>
        Task.FromResult<Guid?>(null);

    public Task ReleaseHoldAsync(Guid leaseId, CancellationToken ct = default) => Task.CompletedTask;

    public Task<RevivalHoldOutcome> PlaceRevivalHoldAsync(
        Guid consumerUserId, Guid leaseId, long holdCents, string currency, CancellationToken ct = default) =>
        Task.FromResult(RevivalHoldOutcome.Allow(holdTxnId: null));
}
