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
/// a 401 (never a downstream 500) when the sub does not map to an active user. On a DB-less bootstrap the
/// authenticator seeds the <c>users</c> row from the grant's <c>Email</c> on first sight (idempotent,
/// task #185) so a fresh in-memory boot can drive the whole flow with one key. Mirrors
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
            new FakeTimeProvider(T0),
            NullLogger<ConfigApiKeyAuthenticator>.Instance);
    }

    private static ConfigApiKeyAuthenticator BuildWithGrants(
        InMemoryUserRepository users,
        params (string key, ApiKeyGrant grant)[] entries)
    {
        var options = new CognitoAuthOptions();
        foreach (var (key, grant) in entries)
        {
            options.ApiKeys[key] = grant;
        }

        return new ConfigApiKeyAuthenticator(
            new StaticOptionsMonitor<CognitoAuthOptions>(options),
            users,
            new FakeTimeProvider(T0),
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
    public async Task Matched_key_with_unresolvable_subject_and_no_email_fails_closed()
    {
        // Regression (task #36): a mistyped/stale UserId in the allow-list with no Email to bootstrap
        // from must fail authentication (→ 401), not fall through to downstream user resolution and 500
        // every authenticated route. (With an Email, the config path now seeds a users row; see below.)
        var users = new InMemoryUserRepository();
        var authenticator = Build(users, ("wck_live_dev-a", "user-a", new[] { "consumer" }));

        Assert.Null(await authenticator.AuthenticateAsync("wck_live_dev-a"));
    }

    [Fact]
    public async Task Matched_key_with_suspended_owner_fails_closed()
    {
        // A suspended existing owner is not authenticatable, mirroring the DB-key path's owner gate. The
        // bootstrap path only fires when NO row exists yet; a pre-existing suspended row is left alone.
        var users = new InMemoryUserRepository();
        await SeedUser(users, "user-a", UserStatus.Suspended);
        var authenticator = Build(users, ("wck_live_dev-a", "user-a", new[] { "consumer" }));

        Assert.Null(await authenticator.AuthenticateAsync("wck_live_dev-a"));
    }

    [Fact]
    public async Task Matched_key_with_unresolvable_subject_and_email_bootstraps_the_user_row()
    {
        // Task #185: the "drive the whole flow with one config key" path must work on a fresh DB-less
        // boot. The authenticator seeds a users row from the grant's Email on first sight, so the
        // matched key resolves to an active user instead of failing 401.
        var users = new InMemoryUserRepository();
        var authenticator = BuildWithGrants(
            users,
            ("wck_live_dev-a", new ApiKeyGrant
            {
                UserId = "self-host-operator",
                Email = "operator@example.test",
                Scopes = new[] { "consumer", "host" },
            }));

        var principal = await authenticator.AuthenticateAsync("wck_live_dev-a");

        Assert.NotNull(principal);
        Assert.Equal("self-host-operator", principal!.GetSubject());
        Assert.True(principal.HasRole(WisperRole.Consumer));
        Assert.True(principal.HasRole(WisperRole.Host));

        var seeded = await users.GetByCognitoSubAsync("self-host-operator");
        Assert.NotNull(seeded);
        Assert.Equal("operator@example.test", seeded!.Email);
        Assert.Equal(UserStatus.Active, seeded.Status);
        Assert.Equal(ConnectStatus.None, seeded.ConnectStatus);
        Assert.Equal(T0, seeded.CreatedAt);
    }

    [Fact]
    public async Task Bootstrap_is_idempotent_across_repeated_authentications()
    {
        // Two calls (or a concurrent second call) must not create a second row (the sub is unique).
        var users = new InMemoryUserRepository();
        var authenticator = BuildWithGrants(
            users,
            ("wck_live_dev-a", new ApiKeyGrant
            {
                UserId = "self-host-operator",
                Email = "operator@example.test",
                Scopes = new[] { "consumer" },
            }));

        await authenticator.AuthenticateAsync("wck_live_dev-a");
        await authenticator.AuthenticateAsync("wck_live_dev-a");

        var all = await users.SearchAsync(query: null, limit: 10, offset: 0);
        Assert.Single(all);
    }
}
