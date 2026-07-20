using Wisper.Api.Domain;

namespace Wisper.Api.Persistence.ApiKeys;

/// <summary>
/// In-memory <see cref="IApiKeyRepository"/> double for unit tests (Grunt has no Postgres). Semantics
/// mirror the SQL side: <see cref="CreateAsync"/> assigns an id when unset, the hashed lookup returns
/// only active (non-revoked) keys, listings order newest first, revocation is idempotent, and the
/// last-used touch is best-effort (a missing key is a no-op, never an error).
/// </summary>
public sealed class InMemoryApiKeyRepository : InMemoryRepositoryBase<Guid, ApiKey>, IApiKeyRepository
{
    protected override Guid KeyOf(ApiKey entity) => entity.Id;

    public Task<ApiKey> CreateAsync(ApiKey apiKey, CancellationToken ct = default)
    {
        var stored = apiKey.Id == Guid.Empty ? apiKey with { Id = Guid.NewGuid() } : apiKey;
        Insert(stored);
        return Task.FromResult(stored);
    }

    public Task<ApiKey?> GetByTokenHashAsync(string tokenHash, CancellationToken ct = default) =>
        Task.FromResult(FindBy(k => k.TokenHash == tokenHash && k.RevokedAt is null));

    public Task<IReadOnlyList<ApiKey>> ListByUserAsync(Guid userId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ApiKey>>(
            Where(k => k.UserId == userId)
                .OrderByDescending(k => k.CreatedAt)
                .ThenByDescending(k => k.Id)
                .ToList());

    public Task<ApiKey?> RevokeAsync(Guid id, DateTimeOffset revokedAt, CancellationToken ct = default)
    {
        if (Find(id) is not { RevokedAt: null } key)
        {
            return Task.FromResult<ApiKey?>(null);
        }

        var revoked = key with { RevokedAt = revokedAt };
        Upsert(revoked);
        return Task.FromResult<ApiKey?>(revoked);
    }

    public Task TouchLastUsedAsync(Guid id, DateTimeOffset usedAt, CancellationToken ct = default)
    {
        // Best-effort: a missing key is a no-op, never an error (mirrors the swallowed SQL failure).
        if (Find(id) is { } key)
        {
            Upsert(key with { LastUsedAt = usedAt });
        }

        return Task.CompletedTask;
    }
}
