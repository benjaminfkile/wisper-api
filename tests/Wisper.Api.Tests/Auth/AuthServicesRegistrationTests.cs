using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Wisper.Api.Auth;
using Xunit;

namespace Wisper.Api.Tests.Auth;

/// <summary>
/// The Cognito auth services must be wired into DI and constructible from the real app host
/// (WebApplicationFactory&lt;Program&gt;) — the JWKS-backed validator and key provider — so the
/// role gates resolve even on a DB-less/auth-unconfigured boot (docs/API.md §2).
/// </summary>
public class AuthServicesRegistrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public AuthServicesRegistrationTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public void The_real_validator_and_key_provider_resolve()
    {
        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;

        Assert.IsType<CognitoJwtValidator>(sp.GetRequiredService<IJwtValidator>());
        Assert.IsType<JwksSigningKeyProvider>(sp.GetRequiredService<ISigningKeyProvider>());
    }

    [Fact]
    public void The_role_granter_is_a_no_op_when_no_user_pool_is_configured()
    {
        // DB-less / api-key dev mode (no Auth:UserPoolId): the become-a-host group write degrades to a no-op,
        // so registration needs no Cognito call (docs/API.md §184).
        using var scope = _factory.Services.CreateScope();

        Assert.IsType<NoOpUserRoleGranter>(scope.ServiceProvider.GetRequiredService<IUserRoleGranter>());
    }
}
