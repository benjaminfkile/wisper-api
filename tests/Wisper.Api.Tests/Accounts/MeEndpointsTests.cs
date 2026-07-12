using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wisper.Api.Auth;
using Wisper.Api.Domain;
using Wisper.Api.Persistence.Users;
using Wisper.Api.Tests.TestSupport;
using Xunit;

namespace Wisper.Api.Tests.Accounts;

/// <summary>
/// Integration tests over the real app host for the account surface (docs/API.md §2, §5): the
/// consumer gate on <c>/v1/me</c>, first-call bootstrap of the <c>users</c> row from the JWT, and the
/// GET/PATCH response shape. The JWT validator and users repository are swapped for the in-memory
/// doubles (Grunt has no Cognito/Postgres).
/// </summary>
public class MeEndpointsTests
{
    private static WebApplicationFactory<Program> NewFactory(
        FakeJwtValidator validator, InMemoryUserRepository users) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IJwtValidator>();
                services.AddSingleton<IJwtValidator>(validator);
                services.RemoveAll<IUserRepository>();
                services.AddSingleton<IUserRepository>(users);
            }));

    private static HttpClient Authed(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", "Bearer good");
        return client;
    }

    [Fact]
    public async Task Get_me_without_a_token_is_401_unauthenticated()
    {
        using var factory = NewFactory(new FakeJwtValidator(), new InMemoryUserRepository());
        var client = factory.CreateClient();

        var response = await client.GetAsync("/v1/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ErrorEnvelopeDto>();
        Assert.Equal("unauthenticated", envelope!.Error.Code);
    }

    [Fact]
    public async Task Get_me_bootstraps_the_row_and_returns_identity_roles_and_connect_status()
    {
        var users = new InMemoryUserRepository();
        var validator = new FakeJwtValidator
        {
            Principal = WisperPrincipal.Create("cognito-99", "me@example.com", Array.Empty<string>()),
        };
        using var factory = NewFactory(validator, users);

        var response = await Authed(factory).GetAsync("/v1/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var me = await response.Content.ReadFromJsonAsync<MeDto>();
        Assert.NotNull(me);
        Assert.NotEqual(Guid.Empty, me!.Id);
        Assert.Equal("cognito-99", me.CognitoSub);
        Assert.Equal("me@example.com", me.Email);
        Assert.Equal("active", me.Status);
        Assert.Equal("none", me.ConnectStatus);
        Assert.Equal(new[] { "consumer" }, me.Roles);

        // The bootstrap actually persisted the row.
        var stored = await users.GetByCognitoSubAsync("cognito-99");
        Assert.NotNull(stored);
        Assert.Equal(me.Id, stored!.Id);
    }

    [Fact]
    public async Task Get_me_reports_additive_roles_in_order()
    {
        var validator = new FakeJwtValidator
        {
            Principal = WisperPrincipal.Create("cognito-1", "h@example.com", new[] { "admin", "host" }),
        };
        using var factory = NewFactory(validator, new InMemoryUserRepository());

        var me = await Authed(factory).GetFromJsonAsync<MeDto>("/v1/me");

        Assert.Equal(new[] { "consumer", "host", "admin" }, me!.Roles);
    }

    [Fact]
    public async Task Get_me_reflects_a_preexisting_connect_status()
    {
        var users = new InMemoryUserRepository();
        await users.CreateAsync(new User
        {
            CognitoSub = "cognito-h",
            Email = "host@example.com",
            Status = UserStatus.Active,
            ConnectStatus = ConnectStatus.Enabled,
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        });
        var validator = new FakeJwtValidator
        {
            Principal = WisperPrincipal.Create("cognito-h", "host@example.com", new[] { "host" }),
        };
        using var factory = NewFactory(validator, users);

        var me = await Authed(factory).GetFromJsonAsync<MeDto>("/v1/me");

        Assert.Equal("enabled", me!.ConnectStatus);
    }

    [Fact]
    public async Task Patch_me_updates_the_email_and_is_visible_on_the_next_get()
    {
        var users = new InMemoryUserRepository();
        var validator = new FakeJwtValidator
        {
            Principal = WisperPrincipal.Create("cognito-1", "before@example.com", Array.Empty<string>()),
        };
        using var factory = NewFactory(validator, users);
        var client = Authed(factory);

        var patch = await client.PatchAsJsonAsync("/v1/me", new { email = "after@example.com" });

        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        var patched = await patch.Content.ReadFromJsonAsync<MeDto>();
        Assert.Equal("after@example.com", patched!.Email);

        var me = await client.GetFromJsonAsync<MeDto>("/v1/me");
        Assert.Equal("after@example.com", me!.Email);
    }

    [Fact]
    public async Task Patch_me_with_an_invalid_email_is_400_validation_error()
    {
        var validator = new FakeJwtValidator
        {
            Principal = WisperPrincipal.Create("cognito-1", "a@example.com", Array.Empty<string>()),
        };
        using var factory = NewFactory(validator, new InMemoryUserRepository());

        var response = await Authed(factory).PatchAsJsonAsync("/v1/me", new { email = "nope" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ErrorEnvelopeDto>();
        Assert.Equal("validation_error", envelope!.Error.Code);
    }

    private sealed record MeDto(
        [property: JsonPropertyName("id")] Guid Id,
        [property: JsonPropertyName("cognito_sub")] string CognitoSub,
        [property: JsonPropertyName("email")] string Email,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("roles")] string[] Roles,
        [property: JsonPropertyName("connect_status")] string ConnectStatus);

    private sealed record ErrorEnvelopeDto(
        [property: JsonPropertyName("error")] ErrorBodyDto Error);

    private sealed record ErrorBodyDto(
        [property: JsonPropertyName("code")] string Code,
        [property: JsonPropertyName("message")] string Message);
}
