using Wisper.Api.Auth;
using Wisper.Api.Tests.TestSupport;
using Xunit;

namespace Wisper.Api.Tests.Auth;

/// <summary>
/// Unit tests for <see cref="ConfigApiKeyAuthenticator"/> (docs/API.md §2): the dev/bootstrap allow-list
/// resolves a raw key to its owner + scopes, and <b>fails closed</b> when the map is empty (the production
/// default). Mirrors <c>ConfigHostTokenValidatorTests</c> for the tunnel's host tokens.
/// </summary>
public class ConfigApiKeyAuthenticatorTests
{
    private static ConfigApiKeyAuthenticator Build(params (string key, string userId, string[] scopes)[] entries)
    {
        var options = new CognitoAuthOptions();
        foreach (var (key, userId, scopes) in entries)
        {
            options.ApiKeys[key] = new ApiKeyGrant { UserId = userId, Scopes = scopes };
        }

        return new ConfigApiKeyAuthenticator(new StaticOptionsMonitor<CognitoAuthOptions>(options));
    }

    [Fact]
    public async Task Known_key_resolves_to_owner_and_scopes()
    {
        var authenticator = Build(
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
        var authenticator = Build(("wck_live_dev-a", "user-a", new[] { "consumer" }));

        Assert.Null(await authenticator.AuthenticateAsync(key));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Null_or_empty_key_fails_closed(string? key)
    {
        var authenticator = Build(("wck_live_dev-a", "user-a", new[] { "consumer" }));

        Assert.Null(await authenticator.AuthenticateAsync(key));
    }

    [Fact]
    public async Task Fails_closed_when_no_keys_configured()
    {
        // The production default: an empty map trusts nobody.
        var authenticator = Build();

        Assert.Null(await authenticator.AuthenticateAsync("wck_live_anything"));
    }

    [Fact]
    public async Task A_grant_with_no_scopes_resolves_with_no_roles()
    {
        // A scopeless key authenticates but holds no role, so every role gate would 403 it.
        var authenticator = Build(("wck_live_dev-a", "user-a", Array.Empty<string>()));

        var principal = await authenticator.AuthenticateAsync("wck_live_dev-a");

        Assert.NotNull(principal);
        Assert.False(principal!.HasRole(WisperRole.Consumer));
    }
}
