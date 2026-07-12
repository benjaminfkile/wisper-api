using Wisper.Api.Domain;

namespace Wisper.Api.Persistence.Payouts;

/// <summary>
/// In-memory <see cref="IPayoutRepository"/> double for unit tests (Grunt has no Postgres). Semantics
/// mirror the SQL side: <see cref="CreateAsync"/> assigns an id when unset (like the DB default) and
/// <see cref="UpdateAsync"/> rejects a collision on the unique <c>stripe_transfer_id</c> the way the
/// constraint would.
/// </summary>
public sealed class InMemoryPayoutRepository : InMemoryRepositoryBase<Guid, Payout>, IPayoutRepository
{
    protected override Guid KeyOf(Payout entity) => entity.Id;

    public Task<Payout> CreateAsync(Payout payout, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var stored = payout with
        {
            Id = payout.Id == Guid.Empty ? Guid.NewGuid() : payout.Id,
            CreatedAt = payout.CreatedAt == default ? now : payout.CreatedAt,
            UpdatedAt = payout.UpdatedAt == default ? now : payout.UpdatedAt,
        };
        GuardUniqueTransfer(stored);
        Insert(stored);
        return Task.FromResult(stored);
    }

    public Task<Payout?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult(Find(id));

    public Task<Payout> UpdateAsync(Payout payout, CancellationToken ct = default)
    {
        if (Find(payout.Id) is null)
        {
            throw new InvalidOperationException($"Payout '{payout.Id}' does not exist.");
        }

        GuardUniqueTransfer(payout);
        var updated = payout with { UpdatedAt = DateTimeOffset.UtcNow };
        Upsert(updated);
        return Task.FromResult(updated);
    }

    public Task<IReadOnlyList<Payout>> ListByHostAsync(Guid hostUserId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Payout>>(
            Where(p => p.HostUserId == hostUserId).OrderByDescending(p => p.CreatedAt).ToList());

    /// <summary>Rejects a collision on the unique <c>stripe_transfer_id</c> against a different row.</summary>
    private void GuardUniqueTransfer(Payout candidate)
    {
        if (candidate.StripeTransferId is not null &&
            FindBy(p => p.Id != candidate.Id && p.StripeTransferId == candidate.StripeTransferId) is not null)
        {
            throw new InvalidOperationException(
                $"stripe_transfer_id '{candidate.StripeTransferId}' already exists.");
        }
    }
}
