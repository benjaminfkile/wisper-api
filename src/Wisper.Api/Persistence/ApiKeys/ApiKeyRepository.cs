using Dapper;
using Wisper.Api.Domain;

namespace Wisper.Api.Persistence.ApiKeys;

/// <summary>
/// Dapper + explicit-SQL <see cref="IApiKeyRepository"/> over Postgres (docs/DATA_MODEL.md §3). The
/// <c>text[]</c> <c>scopes</c> column has no direct Dapper mapping, so it is read via a <c>::text[]</c>
/// cast into a <see cref="Row"/> of string labels and written as a <c>text[]</c> parameter; scalar
/// snake_case columns map to PascalCase via <c>MatchNamesWithUnderscores</c>. Not exercised by the unit
/// suite (Grunt has no Postgres); the in-memory double is.
/// </summary>
public sealed class ApiKeyRepository : RepositoryBase, IApiKeyRepository
{
    // scopes cast to text[] so it maps to Row.Scopes; scalar snake_case columns map by underscore.
    private const string Columns =
        "id, user_id, name, token_hash, token_prefix, scopes::text[] AS scopes, created_at, " +
        "last_used_at, revoked_at";

    public ApiKeyRepository(Db db) : base(db)
    {
    }

    public async Task<ApiKey> CreateAsync(ApiKey apiKey, CancellationToken ct = default)
    {
        const string sql = $"""
            INSERT INTO api_keys (id, user_id, name, token_hash, token_prefix, scopes, created_at,
                                  last_used_at, revoked_at)
            VALUES (COALESCE(@Id, gen_random_uuid()), @UserId, @Name, @TokenHash, @TokenPrefix, @Scopes,
                    COALESCE(@CreatedAt, now()), @LastUsedAt, @RevokedAt)
            RETURNING {Columns}
            """;

        await using var conn = await OpenConnectionAsync(ct);
        var row = await conn.QuerySingleAsync<Row>(new CommandDefinition(sql, ToParameters(apiKey), cancellationToken: ct));
        return row.ToEntity();
    }

    public async Task<ApiKey?> GetByTokenHashAsync(string tokenHash, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM api_keys WHERE token_hash = @tokenHash AND revoked_at IS NULL",
            new { tokenHash }, cancellationToken: ct));
        return row?.ToEntity();
    }

    public async Task<IReadOnlyList<ApiKey>> ListByUserAsync(Guid userId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM api_keys WHERE user_id = @userId ORDER BY created_at DESC, id DESC",
            new { userId }, cancellationToken: ct));
        return rows.Select(r => r.ToEntity()).ToList();
    }

    public async Task<ApiKey?> RevokeAsync(Guid id, DateTimeOffset revokedAt, CancellationToken ct = default)
    {
        const string sql = $"""
            UPDATE api_keys
               SET revoked_at = @revokedAt
             WHERE id = @id AND revoked_at IS NULL
            RETURNING {Columns}
            """;

        await using var conn = await OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<Row>(
            new CommandDefinition(sql, new { id, revokedAt }, cancellationToken: ct));
        return row?.ToEntity();
    }

    public async Task TouchLastUsedAsync(Guid id, DateTimeOffset usedAt, CancellationToken ct = default)
    {
        // Best-effort, on the authenticated request path: a stale last_used_at is harmless, so a failed
        // touch must never fail or slow the request. Swallow any error (never log the key/secret).
        try
        {
            await using var conn = await OpenConnectionAsync(ct);
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE api_keys SET last_used_at = @usedAt WHERE id = @id",
                new { id, usedAt }, cancellationToken: ct));
        }
        catch
        {
            // swallowed on purpose — see summary above.
        }
    }

    /// <summary>Projects an <see cref="ApiKey"/> to SQL parameters; scopes as a plain <c>text[]</c>.</summary>
    private static object ToParameters(ApiKey apiKey) => new
    {
        Id = apiKey.Id == Guid.Empty ? (Guid?)null : apiKey.Id,
        apiKey.UserId,
        apiKey.Name,
        apiKey.TokenHash,
        apiKey.TokenPrefix,
        Scopes = apiKey.Scopes.ToArray(),
        CreatedAt = apiKey.CreatedAt == default ? (DateTimeOffset?)null : apiKey.CreatedAt,
        apiKey.LastUsedAt,
        apiKey.RevokedAt,
    };

    /// <summary>
    /// Dapper projection of an <c>api_keys</c> row: the <c>text[]</c> <c>scopes</c> column arrives as
    /// string labels (read via <c>::text[]</c>). Property names map to the columns via
    /// <c>MatchNamesWithUnderscores</c>.
    /// </summary>
    private sealed class Row
    {
        public Guid Id { get; init; }
        public Guid UserId { get; init; }
        public string Name { get; init; } = string.Empty;
        public string TokenHash { get; init; } = string.Empty;
        public string TokenPrefix { get; init; } = string.Empty;
        public string[] Scopes { get; init; } = Array.Empty<string>();
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset? LastUsedAt { get; init; }
        public DateTimeOffset? RevokedAt { get; init; }

        public ApiKey ToEntity() => new()
        {
            Id = Id,
            UserId = UserId,
            Name = Name,
            TokenHash = TokenHash,
            TokenPrefix = TokenPrefix,
            Scopes = Scopes.ToList(),
            CreatedAt = CreatedAt,
            LastUsedAt = LastUsedAt,
            RevokedAt = RevokedAt,
        };
    }
}
