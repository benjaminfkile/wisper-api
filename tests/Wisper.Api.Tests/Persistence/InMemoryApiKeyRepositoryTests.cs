using Wisper.Api.Domain;
using Wisper.Api.Persistence.ApiKeys;
using Xunit;

namespace Wisper.Api.Tests.Persistence;

/// <summary>
/// Contract tests for <see cref="IApiKeyRepository"/> against the in-memory double (Grunt has no
/// Postgres). Covers minting, the active-only hashed lookup, the owner-scoped listing, idempotent
/// revocation, and the best-effort last-used touch (docs/DATA_MODEL.md §3).
/// </summary>
public class InMemoryApiKeyRepositoryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 12, 0, 0, 0, TimeSpan.Zero);

    private static ApiKey NewKey(Guid user, string hash = "hash-1") => new()
    {
        UserId = user,
        Name = "orchestrator",
        TokenHash = hash,
        TokenPrefix = "wck_live_ab12",
        Scopes = new[] { "consumer" },
        CreatedAt = T0,
    };

    [Fact]
    public async Task Create_assigns_id_and_round_trips_via_hash_lookup()
    {
        var repo = new InMemoryApiKeyRepository();

        var created = await repo.CreateAsync(NewKey(Guid.NewGuid(), "secret-hash"));

        Assert.NotEqual(Guid.Empty, created.Id);
        Assert.Equal(created, await repo.GetByTokenHashAsync("secret-hash"));
    }

    [Fact]
    public async Task GetByTokenHash_returns_only_active_keys()
    {
        var repo = new InMemoryApiKeyRepository();
        var created = await repo.CreateAsync(NewKey(Guid.NewGuid(), "live-hash"));

        Assert.NotNull(await repo.GetByTokenHashAsync("live-hash"));
        Assert.Null(await repo.GetByTokenHashAsync("no-such-hash"));

        await repo.RevokeAsync(created.Id, T0.AddMinutes(1));

        // A revoked key fails closed — it no longer resolves.
        Assert.Null(await repo.GetByTokenHashAsync("live-hash"));
    }

    [Fact]
    public async Task ListByUser_scopes_and_orders_newest_first_including_revoked()
    {
        var repo = new InMemoryApiKeyRepository();
        var user = Guid.NewGuid();
        var other = Guid.NewGuid();
        var older = await repo.CreateAsync(NewKey(user, "k-older") with { CreatedAt = T0 });
        var newer = await repo.CreateAsync(NewKey(user, "k-newer") with { CreatedAt = T0.AddMinutes(10) });
        await repo.CreateAsync(NewKey(other, "k-other"));
        await repo.RevokeAsync(older.Id, T0.AddMinutes(20)); // revoked keys still list

        var mine = await repo.ListByUserAsync(user);

        Assert.Equal(new[] { newer.Id, older.Id }, mine.Select(k => k.Id).ToArray());
    }

    [Fact]
    public async Task Revoke_stamps_revoked_at_and_is_idempotent()
    {
        var repo = new InMemoryApiKeyRepository();
        var created = await repo.CreateAsync(NewKey(Guid.NewGuid()));

        var revoked = await repo.RevokeAsync(created.Id, T0.AddMinutes(5));
        Assert.NotNull(revoked);
        Assert.Equal(T0.AddMinutes(5), revoked!.RevokedAt);

        // Re-revoking an already-revoked (or missing) key returns null — no second stamp.
        Assert.Null(await repo.RevokeAsync(created.Id, T0.AddMinutes(9)));
        Assert.Null(await repo.RevokeAsync(Guid.NewGuid(), T0));
    }

    [Fact]
    public async Task TouchLastUsed_stamps_and_never_fails_on_a_missing_key()
    {
        var repo = new InMemoryApiKeyRepository();
        var created = await repo.CreateAsync(NewKey(Guid.NewGuid(), "touch-hash"));

        await repo.TouchLastUsedAsync(created.Id, T0.AddMinutes(3));
        Assert.Equal(T0.AddMinutes(3), (await repo.GetByTokenHashAsync("touch-hash"))!.LastUsedAt);

        // Best-effort: a touch of a non-existent key is a silent no-op, not an error.
        await repo.TouchLastUsedAsync(Guid.NewGuid(), T0);
    }
}
