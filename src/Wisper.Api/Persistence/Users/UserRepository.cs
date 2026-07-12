using Dapper;
using Wisper.Api.Domain;

namespace Wisper.Api.Persistence.Users;

/// <summary>
/// Dapper + explicit-SQL <see cref="IUserRepository"/> over Postgres (docs/DATA_MODEL.md §3).
/// Native enum columns are written as text parameters cast to the enum type and read back with a
/// <c>::text</c> cast so Dapper's case-insensitive enum parse rehydrates them; snake_case columns map
/// to PascalCase properties via <c>DefaultTypeMap.MatchNamesWithUnderscores</c> (set at wiring). This
/// implementation is not exercised by the unit suite (Grunt has no Postgres); the in-memory double is.
/// </summary>
public sealed class UserRepository : RepositoryBase, IUserRepository
{
    // status/connect_status cast to text so Dapper parses the enum by name; others map by underscore.
    private const string Columns =
        "id, cognito_sub, email, status::text AS status, stripe_customer_id, " +
        "connect_account_id, connect_status::text AS connect_status, created_at, updated_at";

    public UserRepository(Db db) : base(db)
    {
    }

    public async Task<User?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<User>(
            new CommandDefinition($"SELECT {Columns} FROM users WHERE id = @id", new { id }, cancellationToken: ct));
    }

    public async Task<User?> GetByCognitoSubAsync(string cognitoSub, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<User>(new CommandDefinition(
            $"SELECT {Columns} FROM users WHERE cognito_sub = @cognitoSub", new { cognitoSub },
            cancellationToken: ct));
    }

    public async Task<User?> GetByEmailAsync(string email, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<User>(new CommandDefinition(
            $"SELECT {Columns} FROM users WHERE email = @email", new { email }, cancellationToken: ct));
    }

    public async Task<User> CreateAsync(User user, CancellationToken ct = default)
    {
        const string sql = $"""
            INSERT INTO users (id, cognito_sub, email, status, stripe_customer_id,
                               connect_account_id, connect_status, created_at, updated_at)
            VALUES (COALESCE(@Id, gen_random_uuid()), @CognitoSub, @Email, @Status::user_status,
                    @StripeCustomerId, @ConnectAccountId, @ConnectStatus::connect_status,
                    COALESCE(@CreatedAt, now()), COALESCE(@UpdatedAt, now()))
            RETURNING {Columns}
            """;

        await using var conn = await OpenConnectionAsync(ct);
        return await conn.QuerySingleAsync<User>(new CommandDefinition(sql, ToParameters(user), cancellationToken: ct));
    }

    public async Task<User> UpdateAsync(User user, CancellationToken ct = default)
    {
        const string sql = $"""
            UPDATE users
               SET email              = @Email,
                   status             = @Status::user_status,
                   stripe_customer_id = @StripeCustomerId,
                   connect_account_id = @ConnectAccountId,
                   connect_status     = @ConnectStatus::connect_status,
                   updated_at         = COALESCE(@UpdatedAt, now())
             WHERE id = @Id
            RETURNING {Columns}
            """;

        await using var conn = await OpenConnectionAsync(ct);
        var updated = await conn.QuerySingleOrDefaultAsync<User>(
            new CommandDefinition(sql, ToParameters(user), cancellationToken: ct));
        return updated ?? throw new InvalidOperationException($"User '{user.Id}' does not exist.");
    }

    /// <summary>Projects a <see cref="User"/> to SQL parameters, enums rendered as their text labels.</summary>
    private static object ToParameters(User user) => new
    {
        Id = user.Id == Guid.Empty ? (Guid?)null : user.Id,
        user.CognitoSub,
        user.Email,
        Status = PgEnum.ToLabel(user.Status),
        user.StripeCustomerId,
        user.ConnectAccountId,
        ConnectStatus = PgEnum.ToLabel(user.ConnectStatus),
        CreatedAt = user.CreatedAt == default ? (DateTimeOffset?)null : user.CreatedAt,
        UpdatedAt = user.UpdatedAt == default ? (DateTimeOffset?)null : user.UpdatedAt,
    };
}
