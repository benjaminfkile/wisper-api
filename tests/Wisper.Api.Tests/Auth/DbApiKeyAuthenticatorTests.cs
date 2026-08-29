using Microsoft.Extensions.Logging.Abstractions;
using Wisper.Api.ApiKeys;
using Wisper.Api.Auth;
using Wisper.Api.Domain;
using Wisper.Api.Persistence.ApiKeys;
using Wisper.Api.Persistence.Users;
using Wisper.Api.Tests.TestSupport;
using Xunit;

namespace Wisper.Api.Tests.Auth;

/// <summary>
/// Unit tests for <see cref="DbApiKeyAuthenticator"/> (docs/API.md §2): a presented <c>wck_</c> key is
/// resolved to the owning user's principal by a hashed lookup against the api_keys store, roles come from
/// the key's stored scopes, and every fail-closed condition rejects — unknown key, revoked key, a
/// suspended/missing owner, and a null/empty bearer. A key the store does not hold degrades to the config
/// allow-list. The in-memory doubles serve every lookup, so no Postgres is required — mirroring
/// <c>DbHostTokenValidatorTests</c>.
/// </summary>
public class DbApiKeyAuthenticatorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 20, 0, 0, 0, TimeSpan.Zero);

    private static ConfigApiKeyAuthenticator Config(
        InMemoryUserRepository users, params (string key, string userId, string[] scopes)[] entries)
    {
        var options = new CognitoAuthOptions();
        foreach (var (key, userId, scopes) in entries)
        {
            options.ApiKeys[key] = new ApiKeyGrant { UserId = userId, Scopes = scopes };
        }

        return new ConfigApiKeyAuthenticator(
            new StaticOptionsMonitor<CognitoAuthOptions>(options),
            users,
            new FakeTimeProvider(T0),
            NullLogger<ConfigApiKeyAuthenticator>.Instance);
    }

    private static async Task<User> SeedUser(
        InMemoryUserRepository users, string sub, UserStatus status = UserStatus.Active) =>
        await users.CreateAsync(new User
        {
            CognitoSub = sub,
            Email = $"{sub}@example.com",
            Status = status,
            CreatedAt = T0,
            UpdatedAt = T0,
        });

    private static async Task<(ApiKey Key, string Token)> SeedKey(
        InMemoryApiKeyRepository keys, Guid ownerId, string[] scopes, DateTimeOffset? revokedAt = null)
    {
        var issued = ApiKeyToken.Issue();
        var key = await keys.CreateAsync(new ApiKey
        {
            UserId = ownerId,
            Name = "test key",
            TokenHash = issued.TokenHash,
            TokenPrefix = issued.TokenPrefix,
            Scopes = scopes,
            CreatedAt = T0,
            RevokedAt = revokedAt,
        });
        return (key, issued.Token);
    }

    private static DbApiKeyAuthenticator Build(
        InMemoryApiKeyRepository keys, InMemoryUserRepository users,
        ConfigApiKeyAuthenticator? config = null, TimeProvider? time = null) =>
        new(
            keys,
            users,
            time ?? new FakeTimeProvider(T0),
            config ?? Config(users),
            NullLogger<DbApiKeyAuthenticator>.Instance);

    [Fact]
    public async Task Resolves_an_active_key_to_the_owner_with_key_scopes()
    {
        var users = new InMemoryUserRepository();
        var keys = new InMemoryApiKeyRepository();
        var owner = await SeedUser(users, "cognito-owner");
        var (_, token) = await SeedKey(keys, owner.Id, new[] { "consumer", "host" });
        var authenticator = Build(keys, users);

        var principal = await authenticator.AuthenticateAsync(token);

        Assert.NotNull(principal);
        Assert.Equal("cognito-owner", principal!.GetSubject());
        Assert.Equal("cognito-owner@example.com", principal.GetEmail());
        // Roles are exactly the key's scopes — never Cognito groups, never implicit consumer beyond scope.
        Assert.True(principal.HasRole(WisperRole.Consumer));
        Assert.True(principal.HasRole(WisperRole.Host));
        Assert.False(principal.HasRole(WisperRole.Admin));
    }

    [Fact]
    public async Task Roles_come_only_from_scopes_not_an_implicit_consumer()
    {
        var users = new InMemoryUserRepository();
        var keys = new InMemoryApiKeyRepository();
        var owner = await SeedUser(users, "cognito-owner");
        var (_, token) = await SeedKey(keys, owner.Id, new[] { "host" }); // host only, no consumer
        var authenticator = Build(keys, users);

        var principal = await authenticator.AuthenticateAsync(token);

        Assert.NotNull(principal);
        Assert.False(principal!.HasRole(WisperRole.Consumer));
        Assert.True(principal.HasRole(WisperRole.Host));
    }

    [Fact]
    public async Task Stamps_last_used_best_effort()
    {
        var users = new InMemoryUserRepository();
        var keys = new InMemoryApiKeyRepository();
        var owner = await SeedUser(users, "cognito-owner");
        var (key, token) = await SeedKey(keys, owner.Id, new[] { "consumer" });
        var clock = new FakeTimeProvider(T0.AddHours(3));
        var authenticator = Build(keys, users, time: clock);

        await authenticator.AuthenticateAsync(token);

        var stored = await keys.GetByTokenHashAsync(key.TokenHash);
        Assert.Equal(T0.AddHours(3), stored!.LastUsedAt);
    }

    [Fact]
    public async Task Unknown_key_fails_closed()
    {
        var authenticator = Build(new InMemoryApiKeyRepository(), new InMemoryUserRepository());

        Assert.Null(await authenticator.AuthenticateAsync(ApiKeyToken.Issue().Token));
    }

    [Fact]
    public async Task Revoked_key_fails_closed()
    {
        var users = new InMemoryUserRepository();
        var keys = new InMemoryApiKeyRepository();
        var owner = await SeedUser(users, "cognito-owner");
        var (_, token) = await SeedKey(keys, owner.Id, new[] { "consumer" }, revokedAt: T0.AddMinutes(5));
        var authenticator = Build(keys, users);

        Assert.Null(await authenticator.AuthenticateAsync(token));
    }

    [Fact]
    public async Task Suspended_owner_fails_closed()
    {
        var users = new InMemoryUserRepository();
        var keys = new InMemoryApiKeyRepository();
        var owner = await SeedUser(users, "cognito-owner", UserStatus.Suspended);
        var (_, token) = await SeedKey(keys, owner.Id, new[] { "consumer" });
        // The config allow-list holds the same raw token, to prove a recognized-but-suspended key never
        // falls through to the fallback.
        var authenticator = Build(keys, users, Config(users, (token, "cfg-user", new[] { "consumer" })));

        Assert.Null(await authenticator.AuthenticateAsync(token));
    }

    [Fact]
    public async Task Missing_owner_fails_closed()
    {
        var keys = new InMemoryApiKeyRepository();
        // Key owned by a user id with no matching row — must reject as 401 (not throw), and never fall
        // through to the config allow-list.
        var (_, token) = await SeedKey(keys, Guid.NewGuid(), new[] { "consumer" });
        var authenticator = Build(keys, new InMemoryUserRepository());

        Assert.Null(await authenticator.AuthenticateAsync(token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Null_or_empty_bearer_fails_closed(string? token)
    {
        var authenticator = Build(new InMemoryApiKeyRepository(), new InMemoryUserRepository());

        Assert.Null(await authenticator.AuthenticateAsync(token));
    }

    [Fact]
    public async Task Falls_back_to_config_when_the_store_does_not_hold_the_key()
    {
        // The store has no matching key → the lookup misses and the config allow-list resolves the key.
        // The config subject must map to an existing user, per the same owner-must-exist gate the DB path enforces.
        var users = new InMemoryUserRepository();
        await SeedUser(users, "dev-user");
        var authenticator = Build(
            new InMemoryApiKeyRepository(), users,
            Config(users, ("wck_live_dev", "dev-user", new[] { "consumer" })));

        var principal = await authenticator.AuthenticateAsync("wck_live_dev");

        Assert.NotNull(principal);
        Assert.Equal("dev-user", principal!.GetSubject());
        Assert.True(principal.HasRole(WisperRole.Consumer));
    }

    [Fact]
    public async Task Fallback_config_key_with_unresolvable_subject_fails_closed()
    {
        // Regression: when the store misses and the config fallback fires but the config's UserId names no
        // user, the whole authenticator rejects (401) instead of returning a principal that would 500
        // every downstream user-resolution call.
        var authenticator = Build(
            new InMemoryApiKeyRepository(), new InMemoryUserRepository(),
            Config(new InMemoryUserRepository(), ("wck_live_dev", "no-such-sub", new[] { "consumer" })));

        Assert.Null(await authenticator.AuthenticateAsync("wck_live_dev"));
    }
}
