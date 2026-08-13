using Microsoft.Extensions.Logging.Abstractions;
using Wisper.Api.Auth;
using Wisper.Api.Domain;
using Wisper.Api.Persistence.Users;
using Wisper.Api.Tests.TestSupport;
using Xunit;

namespace Wisper.Api.Tests.Auth;

/// <summary>
/// Unit tests for <see cref="ConfigApiKeyAuthenticator"/> (docs/API.md §2): the dev/bootstrap allow-list
/// resolves a raw key to its owner + scopes, <b>fails closed</b> when the map is empty (the production
/// default), and — since a config sub is authoritative only when it names a real user — fails closed with
/// a 401 (never a downstream 500) when the sub does not map to an active user. Mirrors
/// <c>ConfigHostTokenValidatorTests</c> for the tunnel's host tokens.
/// </summary>
public class ConfigApiKeyAuthenticatorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 13, 0, 0, 0, TimeSpan.Zero);

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

    private static ConfigApiKeyAuthenticator Build(
        InMemoryUserRepository users,
        params (string key, string userId, string[] scopes)[] entries)
    {
        var options = new CognitoAuthOptions();
        foreach (var (key, userId, scopes) in entries)
        {
            options.ApiKeys[key] = new ApiKeyGrant { UserId = userId, Scopes = scopes };
        }

        return new ConfigApiKeyAuthenticator(
            new StaticOptionsMonitor<CognitoAuthOptions>(options),
            users,
            NullLogger<ConfigApiKeyAuthenticator>.Instance);
    }

    [Fact]
    public async Task Known_key_resolves_to_owner_and_scopes()
    {
        var users = new InMemoryUserRepository();
        await SeedUser(users, "user-a");
        await SeedUser(users, "user-b");
        var authenticator = Build(
            users,
            ("wck_live_dev-a", "user-a", new[] { "consumer" }),
            ("wck_live_dev-b", "user-b", new[] { "consumer", "host" }));

        var principal = await authenticator.AuthenticateAsync("wck_live_dev-b");

        Assert.NotNull(principal);
        Assert.Equal("user-b", principal!.GetSubject());
        Assert.True(principal.HasRole(WisperRole.Consumer));
        Assert.True(principal.HasRole(WisperRole.Host));
        Assert.False(principal.HasRole(WisperRole.Admin));
    }

    [Theory]
    [InlineData("wck_live_dev-a ")] // trailing space — not a byte-for-byte match
    [InlineData("WCK_LIVE_DEV-A")]  // wrong case
    [InlineData("wck_live_unknown")]
    public async Task Unknown_key_fails_closed(string key)
    {
        var users = new InMemoryUserRepository();
        await SeedUser(users, "user-a");
        var authenticator = Build(users, ("wck_live_dev-a", "user-a", new[] { "consumer" }));

        Assert.Null(await authenticator.AuthenticateAsync(key));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Null_or_empty_key_fails_closed(string? key)
    {
        var users = new InMemoryUserRepository();
        await SeedUser(users, "user-a");
        var authenticator = Build(users, ("wck_live_dev-a", "user-a", new[] { "consumer" }));

        Assert.Null(await authenticator.AuthenticateAsync(key));
    }

    [Fact]
    public async Task Fails_closed_when_no_keys_configured()
    {
        // The production default: an empty map trusts nobody.
        var authenticator = Build(new InMemoryUserRepository());

        Assert.Null(await authenticator.AuthenticateAsync("wck_live_anything"));
    }

    [Fact]
    public async Task A_grant_with_no_scopes_resolves_with_no_roles()
    {
        // A scopeless key authenticates but holds no role, so every role gate would 403 it.
        var users = new InMemoryUserRepository();
        await SeedUser(users, "user-a");
        var authenticator = Build(users, ("wck_live_dev-a", "user-a", Array.Empty<string>()));

        var principal = await authenticator.AuthenticateAsync("wck_live_dev-a");

        Assert.NotNull(principal);
        Assert.False(principal!.HasRole(WisperRole.Consumer));
    }

    [Fact]
    public async Task Matched_key_with_unresolvable_subject_fails_closed()
    {
        // Regression: a mistyped/stale UserId in the allow-list must fail authentication (→ 401), not
        // fall through to downstream user resolution and 500 every authenticated route.
        var users = new InMemoryUserRepository();
        // Intentionally do NOT seed 'user-a' — the key names a subject that does not resolve.
        var authenticator = Build(users, ("wck_live_dev-a", "user-a", new[] { "consumer" }));

        Assert.Null(await authenticator.AuthenticateAsync("wck_live_dev-a"));
    }

    [Fact]
    public async Task Matched_key_with_suspended_owner_fails_closed()
    {
        // A suspended owner is not authenticatable — mirrors the DB-key path's owner gate.
        var users = new InMemoryUserRepository();
        await SeedUser(users, "user-a", UserStatus.Suspended);
        var authenticator = Build(users, ("wck_live_dev-a", "user-a", new[] { "consumer" }));

        Assert.Null(await authenticator.AuthenticateAsync("wck_live_dev-a"));
    }
}
