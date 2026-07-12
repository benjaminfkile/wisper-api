using System.Security.Claims;
using Wisper.Api.Auth;
using Xunit;

namespace Wisper.Api.Tests.Auth;

/// <summary>
/// Unit tests for the claims → principal mapping (docs/API.md §2, docs/DESIGN.md §10):
/// additive roles from Cognito groups, with <c>consumer</c> always implicit.
/// </summary>
public class WisperPrincipalTests
{
    [Fact]
    public void Every_authenticated_user_is_implicitly_a_consumer()
    {
        var principal = WisperPrincipal.Create("sub-1", "a@b.com", Array.Empty<string>());

        Assert.True(principal.Identity?.IsAuthenticated);
        Assert.True(principal.HasRole(WisperRole.Consumer));
        Assert.False(principal.HasRole(WisperRole.Host));
        Assert.False(principal.HasRole(WisperRole.Admin));
        Assert.Equal("sub-1", principal.GetSubject());
        Assert.Equal("a@b.com", principal.GetEmail());
    }

    [Fact]
    public void Host_group_adds_host_role_but_keeps_consumer()
    {
        var principal = WisperPrincipal.Create("sub-1", null, new[] { "host" });

        Assert.True(principal.HasRole(WisperRole.Consumer));
        Assert.True(principal.HasRole(WisperRole.Host));
        Assert.False(principal.HasRole(WisperRole.Admin));
    }

    [Fact]
    public void Admin_group_does_not_imply_host()
    {
        var principal = WisperPrincipal.Create("sub-1", null, new[] { "admin" });

        Assert.True(principal.HasRole(WisperRole.Consumer));
        Assert.True(principal.HasRole(WisperRole.Admin));
        Assert.False(principal.HasRole(WisperRole.Host));
    }

    [Fact]
    public void Roles_accumulate_additively()
    {
        var principal = WisperPrincipal.Create("sub-1", null, new[] { "consumer", "host", "admin" });

        var roles = principal.GetRoles();
        Assert.Contains(WisperRole.Consumer, roles);
        Assert.Contains(WisperRole.Host, roles);
        Assert.Contains(WisperRole.Admin, roles);
    }

    [Fact]
    public void Unknown_groups_are_ignored_but_preserved_as_claims()
    {
        var principal = WisperPrincipal.Create("sub-1", null, new[] { "host", "some-other-group" });

        Assert.True(principal.HasRole(WisperRole.Host));
        Assert.Equal(2, principal.FindAll(WisperPrincipal.GroupsClaimType).Count());
        // The unrecognized group produced no role.
        Assert.DoesNotContain("some-other-group", principal.GetRoles().Select(WisperRoles.Name));
    }

    [Fact]
    public void Blank_subject_is_rejected()
    {
        Assert.Throws<ArgumentException>(() =>
            WisperPrincipal.Create(" ", null, Array.Empty<string>()));
    }

    [Fact]
    public void Create_from_validated_identity_extracts_sub_email_groups()
    {
        var identity = new ClaimsIdentity(
            new[]
            {
                new Claim("sub", "sub-42"),
                new Claim("email", "host@example.com"),
                new Claim("cognito:groups", "host"),
                new Claim("cognito:groups", "admin"),
            },
            authenticationType: "jwt");

        var principal = WisperPrincipal.Create(identity);

        Assert.Equal("sub-42", principal.GetSubject());
        Assert.Equal("host@example.com", principal.GetEmail());
        Assert.True(principal.HasRole(WisperRole.Host));
        Assert.True(principal.HasRole(WisperRole.Admin));
    }
}
