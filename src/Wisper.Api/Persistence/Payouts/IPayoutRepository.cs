using Wisper.Api.Domain;

namespace Wisper.Api.Persistence.Payouts;

/// <summary>
/// Data access for <see cref="Payout"/> rows — host Connect transfers that drain accrued earnings
/// (docs/DATA_MODEL.md §9, docs/PAYMENTS.md §6). A payout is created <see cref="PayoutStatus.Pending"/>,
/// then advanced as the Stripe transfer progresses; <see cref="Payout.StripeTransferId"/> is unique so a
/// retried run can't double-pay. A Dapper + explicit-SQL implementation backs Postgres; an in-memory
/// double backs the unit suite (Grunt has no Postgres).
/// </summary>
public interface IPayoutRepository : IRepository
{
    /// <summary>Inserts a new payout (assigning an id when unset) and returns the stored row.</summary>
    Task<Payout> CreateAsync(Payout payout, CancellationToken ct = default);

    /// <summary>Gets a payout by id, or <c>null</c> if none.</summary>
    Task<Payout?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Updates the mutable columns (status, stripe_transfer_id, payout_txn_id, error, updated_at) of the
    /// payout identified by <see cref="Payout.Id"/> and returns the stored row. Throws when the payout
    /// does not exist or the Stripe transfer id would collide with another row.
    /// </summary>
    Task<Payout> UpdateAsync(Payout payout, CancellationToken ct = default);

    /// <summary>A host's payouts, newest first.</summary>
    Task<IReadOnlyList<Payout>> ListByHostAsync(Guid hostUserId, CancellationToken ct = default);
}
