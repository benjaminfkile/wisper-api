using Wisper.Api.Domain;

namespace Wisper.Api.Persistence.Users;

/// <summary>
/// Data access for <see cref="User"/> rows (docs/DATA_MODEL.md §3). The account bootstrap path
/// (docs/API.md, P3.2) looks a user up by their Cognito subject and creates one on first sight; the
/// billing/Connect paths update payment linkage and status. Two implementations exist — a Dapper +
/// explicit-SQL one over Postgres and an in-memory double for unit tests (Grunt has no Postgres).
/// Uniqueness of <c>cognito_sub</c>, <c>email</c>, <c>stripe_customer_id</c> and
/// <c>connect_account_id</c> is enforced by both.
/// </summary>
public interface IUserRepository : IRepository
{
    /// <summary>Gets a user by internal id, or <c>null</c> if none.</summary>
    Task<User?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Gets a user by Cognito subject, or <c>null</c> if none.</summary>
    Task<User?> GetByCognitoSubAsync(string cognitoSub, CancellationToken ct = default);

    /// <summary>Gets a user by email, or <c>null</c> if none.</summary>
    Task<User?> GetByEmailAsync(string email, CancellationToken ct = default);

    /// <summary>
    /// Gets a user by their Stripe Connect account id (<c>acct_…</c>), or <c>null</c> if none. The
    /// <c>account.updated</c> webhook resolves the host wallet from the account id this way, so recomputing
    /// <c>connect_status</c> is a pure function of the event (docs/PAYMENTS.md §5, §8).
    /// </summary>
    Task<User?> GetByConnectAccountIdAsync(string connectAccountId, CancellationToken ct = default);

    /// <summary>
    /// Gets a user by their Stripe customer id (<c>cus_…</c>), or <c>null</c> if none. The refund/dispute
    /// webhooks resolve the consumer whose wallet a disputed/refunded charge belongs to this way, so the
    /// <c>refund</c>/<c>chargeback</c> effect is a pure function of the event (docs/PAYMENTS.md §7, §8).
    /// </summary>
    Task<User?> GetByStripeCustomerIdAsync(string stripeCustomerId, CancellationToken ct = default);

    /// <summary>
    /// Inserts a new user and returns the stored row (with any DB-generated id). Throws when a unique
    /// column (cognito_sub, email, stripe_customer_id, connect_account_id) collides with an existing row.
    /// </summary>
    Task<User> CreateAsync(User user, CancellationToken ct = default);

    /// <summary>
    /// Updates the mutable columns (email, status, stripe_customer_id, connect_account_id,
    /// connect_status, updated_at) of the user identified by <see cref="User.Id"/> and returns the stored
    /// row. <c>cognito_sub</c> is immutable identity and is never written. Throws when the user does not
    /// exist or a unique column (email, stripe_customer_id, connect_account_id) would collide.
    /// </summary>
    Task<User> UpdateAsync(User user, CancellationToken ct = default);
}
