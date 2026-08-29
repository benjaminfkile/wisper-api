using Wisper.Api.Auth;
using Xunit;

namespace Wisper.Api.Tests.Auth;

/// <summary>
/// Unit tests for <see cref="WisperPrincipal.CreateForApiKey"/> (docs/API.md §2): an API-key principal's
/// roles are <b>exactly</b> its scopes -- no implicit consumer, no Cognito groups -- while it is otherwise
/// shaped like a JWT principal so the same role gates apply unchanged.
/// </summary>
public class WisperPrincipalApiKeyTests
{
    [Fact]
    public void Roles_are_exactly_the_scopes()
    {
        var principal = WisperPrincipal.CreateForApiKey("owner-sub", "owner@example.com",
            new[] { "consumer", "admin" });

        Assert.Equal("owner-sub", principal.GetSubject());
        Assert.Equal("owner@example.com", principal.GetEmail());
        Assert.True(principal.HasRole(WisperRole.Consumer));
        Assert.True(principal.HasRole(WisperRole.Admin));
        Assert.False(principal.HasRole(WisperRole.Host));
    }

    [Fact]
    public void No_implicit_consumer_when_the_scope_is_absent()
    {
        var principal = WisperPrincipal.CreateForApiKey("owner-sub", null, new[] { "host" });

        Assert.False(principal.HasRole(WisperRole.Consumer));
        Assert.True(principal.HasRole(WisperRole.Host));
    }

    [Fact]
    public void Unknown_and_blank_scopes_are_ignored()
    {
        var principal = WisperPrincipal.CreateForApiKey("owner-sub", null,
            new[] { "consumer", "superuser", "", "  " });

        Assert.Equal(new[] { WisperRole.Consumer }, principal.GetRoles());
    }

    [Fact]
    public void The_identity_is_marked_wisper_issued_and_authenticated()
    {
        var principal = WisperPrincipal.CreateForApiKey("owner-sub", null, new[] { "consumer" });

        Assert.True(principal.Identity!.IsAuthenticated);
        Assert.True(WisperPrincipal.IsWisperAuthenticationType(principal.Identity.AuthenticationType));
    }

    [Fact]
    public void A_blank_subject_is_rejected()
    {
        Assert.Throws<ArgumentException>(() =>
            WisperPrincipal.CreateForApiKey(" ", null, new[] { "consumer" }));
    }
}
