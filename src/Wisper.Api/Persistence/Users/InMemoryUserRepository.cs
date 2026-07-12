using Wisper.Api.Domain;

namespace Wisper.Api.Persistence.Users;

/// <summary>
/// In-memory <see cref="IUserRepository"/> double for unit tests (Grunt has no Postgres). Semantics
/// mirror the SQL side: <see cref="CreateAsync"/> assigns an id when unset (like the DB default) and
/// rejects a collision on any unique column the way a unique constraint would, throwing
/// <see cref="InvalidOperationException"/>.
/// </summary>
public sealed class InMemoryUserRepository : InMemoryRepositoryBase<Guid, User>, IUserRepository
{
    protected override Guid KeyOf(User entity) => entity.Id;

    public Task<User?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult(Find(id));

    public Task<User?> GetByCognitoSubAsync(string cognitoSub, CancellationToken ct = default) =>
        Task.FromResult(FindBy(u => u.CognitoSub == cognitoSub));

    public Task<User?> GetByEmailAsync(string email, CancellationToken ct = default) =>
        Task.FromResult(FindBy(u => string.Equals(u.Email, email, StringComparison.OrdinalIgnoreCase)));

    public Task<User> CreateAsync(User user, CancellationToken ct = default)
    {
        var stored = user.Id == Guid.Empty ? user with { Id = Guid.NewGuid() } : user;
        GuardUnique(stored);
        Insert(stored);
        return Task.FromResult(stored);
    }

    public Task<User> UpdateAsync(User user, CancellationToken ct = default)
    {
        if (Find(user.Id) is null)
        {
            throw new InvalidOperationException($"User '{user.Id}' does not exist.");
        }

        GuardUnique(user);
        Upsert(user);
        return Task.FromResult(user);
    }

    /// <summary>Rejects a collision on any unique column against a different row (mirrors the DB).</summary>
    private void GuardUnique(User candidate)
    {
        if (FindBy(u => u.Id != candidate.Id && u.CognitoSub == candidate.CognitoSub) is not null)
        {
            throw new InvalidOperationException($"cognito_sub '{candidate.CognitoSub}' already exists.");
        }

        if (FindBy(u => u.Id != candidate.Id &&
                        string.Equals(u.Email, candidate.Email, StringComparison.OrdinalIgnoreCase)) is not null)
        {
            throw new InvalidOperationException($"email '{candidate.Email}' already exists.");
        }

        if (candidate.StripeCustomerId is not null &&
            FindBy(u => u.Id != candidate.Id && u.StripeCustomerId == candidate.StripeCustomerId) is not null)
        {
            throw new InvalidOperationException(
                $"stripe_customer_id '{candidate.StripeCustomerId}' already exists.");
        }

        if (candidate.ConnectAccountId is not null &&
            FindBy(u => u.Id != candidate.Id && u.ConnectAccountId == candidate.ConnectAccountId) is not null)
        {
            throw new InvalidOperationException(
                $"connect_account_id '{candidate.ConnectAccountId}' already exists.");
        }
    }
}
