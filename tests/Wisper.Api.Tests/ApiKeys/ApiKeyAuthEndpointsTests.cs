using System.Collections.Generic;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Wisper.Api.ApiKeys;
using Wisper.Api.Auth;
using Wisper.Api.Domain;
using Wisper.Api.Persistence;
using Wisper.Api.Persistence.ApiKeys;
using Wisper.Api.Persistence.Users;
using Wisper.Api.Tests.TestSupport;
using Xunit;

namespace Wisper.Api.Tests.ApiKeys;

/// <summary>
/// End-to-end tests over the real app host for API-key auth (docs/API.md §2): a <c>wck_</c> bearer flows
/// through the <b>same</b> role gates the JWT path uses. A consumer-scoped key reaches the consumer-gated
/// <c>GET /v1/me</c> and is identified as the owning user; a key lacking the scope is 403 by the existing
/// gate; every fail-closed case (unknown, revoked, suspended owner, malformed bearer) is 401; and a
/// config-map key works on a DB-less boot. The api_keys/users repositories are the in-memory doubles
/// (Grunt has no Postgres/Cognito); the JWT path is left untouched (covered by the JWT suites).
/// </summary>
public class ApiKeyAuthEndpointsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 20, 0, 0, 0, TimeSpan.Zero);

    /// <summary>A configured <see cref="Db"/> whose data source is never opened by these tests.</summary>
    private static Db ConfiguredDb() =>
        new(new NpgsqlDataSourceBuilder("Host=127.0.0.1;Database=none;Username=none").Build());

    /// <summary>
    /// A factory whose <see cref="Db"/> reports configured (so the DB-backed hashed lookup path runs) but is
    /// never opened — the in-memory api_keys/users doubles serve every lookup. Startup migrations are off.
    /// </summary>
    private static WebApplicationFactory<Program> DbBackedFactory(
        InMemoryApiKeyRepository keys, InMemoryUserRepository users) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Persistence:RunMigrationsAtStartup"] = "false" }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<Db>();
                services.AddSingleton(ConfiguredDb());
                services.RemoveAll<IApiKeyRepository>();
                services.AddSingleton<IApiKeyRepository>(keys);
                services.RemoveAll<IUserRepository>();
                services.AddSingleton<IUserRepository>(users);
            });
        });

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

    private static async Task<string> SeedKey(
        InMemoryApiKeyRepository keys, Guid ownerId, string[] scopes, DateTimeOffset? revokedAt = null)
    {
        var issued = ApiKeyToken.Issue();
        await keys.CreateAsync(new ApiKey
        {
            UserId = ownerId,
            Name = "test key",
            TokenHash = issued.TokenHash,
            TokenPrefix = issued.TokenPrefix,
            Scopes = scopes,
            CreatedAt = T0,
            RevokedAt = revokedAt,
        });
        return issued.Token;
    }

    private static HttpClient WithKey(WebApplicationFactory<Program> factory, string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
        return client;
    }

    [Fact]
    public async Task Consumer_scoped_key_reaches_me_and_is_identified_as_the_owner()
    {
        var users = new InMemoryUserRepository();
        var keys = new InMemoryApiKeyRepository();
        var owner = await SeedUser(users, "cognito-owner");
        var token = await SeedKey(keys, owner.Id, new[] { "consumer" });
        using var factory = DbBackedFactory(keys, users);

        var me = await WithKey(factory, token).GetFromJsonAsync<MeDto>("/v1/me");

        Assert.NotNull(me);
        Assert.Equal(owner.Id, me!.Id);
        Assert.Equal("cognito-owner", me.CognitoSub);
        Assert.Equal(new[] { "consumer" }, me.Roles);
    }

    [Fact]
    public async Task Key_without_the_needed_scope_is_403_by_the_existing_role_gate()
    {
        var users = new InMemoryUserRepository();
        var keys = new InMemoryApiKeyRepository();
        var owner = await SeedUser(users, "cognito-owner");
        var token = await SeedKey(keys, owner.Id, new[] { "host" }); // host only — no consumer scope
        using var factory = DbBackedFactory(keys, users);

        var response = await WithKey(factory, token).GetAsync("/v1/me");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ErrorEnvelopeDto>();
        Assert.Equal("forbidden", envelope!.Error.Code);
    }

    [Fact]
    public async Task Unknown_key_is_401_unauthenticated()
    {
        using var factory = DbBackedFactory(new InMemoryApiKeyRepository(), new InMemoryUserRepository());

        var response = await WithKey(factory, ApiKeyToken.Issue().Token).GetAsync("/v1/me");

        await AssertUnauthenticated(response);
    }

    [Fact]
    public async Task Revoked_key_is_401_unauthenticated()
    {
        var users = new InMemoryUserRepository();
        var keys = new InMemoryApiKeyRepository();
        var owner = await SeedUser(users, "cognito-owner");
        var token = await SeedKey(keys, owner.Id, new[] { "consumer" }, revokedAt: T0.AddMinutes(1));
        using var factory = DbBackedFactory(keys, users);

        var response = await WithKey(factory, token).GetAsync("/v1/me");

        await AssertUnauthenticated(response);
    }

    [Fact]
    public async Task Suspended_owner_is_401_unauthenticated()
    {
        var users = new InMemoryUserRepository();
        var keys = new InMemoryApiKeyRepository();
        var owner = await SeedUser(users, "cognito-owner", UserStatus.Suspended);
        var token = await SeedKey(keys, owner.Id, new[] { "consumer" });
        using var factory = DbBackedFactory(keys, users);

        var response = await WithKey(factory, token).GetAsync("/v1/me");

        await AssertUnauthenticated(response);
    }

    [Theory]
    [InlineData("wck_")]                 // namespace only — looks like a key, resolves to nothing
    [InlineData("wck_live_")]            // prefix only, empty secret
    [InlineData("wck_live_not-hex-xx")]  // malformed key body
    public async Task Malformed_key_bearer_is_401_unauthenticated(string token)
    {
        using var factory = DbBackedFactory(new InMemoryApiKeyRepository(), new InMemoryUserRepository());

        var response = await WithKey(factory, token).GetAsync("/v1/me");

        await AssertUnauthenticated(response);
    }

    [Fact]
    public async Task Empty_bearer_is_401_unauthenticated()
    {
        using var factory = DbBackedFactory(new InMemoryApiKeyRepository(), new InMemoryUserRepository());
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", "Bearer ");

        var response = await client.GetAsync("/v1/me");

        await AssertUnauthenticated(response);
    }

    [Fact]
    public async Task Config_map_key_works_on_a_db_less_boot()
    {
        // No connection string → Db.Unconfigured → the DB path is skipped and the Auth:ApiKeys allow-list
        // resolves the key. The owner row is served by the in-memory users double (the /v1/me bootstrap).
        var users = new InMemoryUserRepository();
        var owner = await SeedUser(users, "dev-subject");
        const string rawKey = "wck_live_devbootstrap";
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Auth:ApiKeys:" + rawKey + ":userId"] = "dev-subject",
                    ["Auth:ApiKeys:" + rawKey + ":scopes:0"] = "consumer",
                }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IUserRepository>();
                services.AddSingleton<IUserRepository>(users);
            });
        });

        using (factory)
        {
            var me = await WithKey(factory, rawKey).GetFromJsonAsync<MeDto>("/v1/me");

            Assert.NotNull(me);
            Assert.Equal(owner.Id, me!.Id);
            Assert.Equal("dev-subject", me.CognitoSub);
            Assert.Equal(new[] { "consumer" }, me.Roles);
        }
    }

    [Fact]
    public async Task An_unconfigured_config_map_fails_closed_on_a_db_less_boot()
    {
        // Production shape: no DB and no Auth:ApiKeys → every wck_ key is rejected.
        using var factory = new WebApplicationFactory<Program>();

        var response = await WithKey(factory, "wck_live_anything").GetAsync("/v1/me");

        await AssertUnauthenticated(response);
    }

    private static async Task AssertUnauthenticated(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ErrorEnvelopeDto>();
        Assert.Equal("unauthenticated", envelope!.Error.Code);
    }

    private sealed record MeDto(
        [property: JsonPropertyName("id")] Guid Id,
        [property: JsonPropertyName("cognito_sub")] string CognitoSub,
        [property: JsonPropertyName("roles")] string[] Roles);

    private sealed record ErrorEnvelopeDto(
        [property: JsonPropertyName("error")] ErrorBodyDto Error);

    private sealed record ErrorBodyDto(
        [property: JsonPropertyName("code")] string Code);
}
