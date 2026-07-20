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
using Wisper.Api.Auth;
using Wisper.Api.Persistence;
using Wisper.Api.Persistence.ApiKeys;
using Wisper.Api.Persistence.Users;
using Wisper.Api.Tests.TestSupport;
using Xunit;

namespace Wisper.Api.Tests.ApiKeys;

/// <summary>
/// Integration tests over the real app host for the self-serve API-key surface (docs/API.md §5): the full
/// mint→use→list→revoke→use-fails lifecycle, scope capping (a consumer JWT cannot mint a host scope), the
/// privilege-containment 403 for a key-authenticated mint (a key cannot mint keys), and owner scoping
/// (another user's key is invisible to list and 404 to revoke). The <c>api_keys</c>/<c>users</c>
/// repositories are in-memory doubles and the JWT validator is faked (Grunt has no Postgres/Cognito); the
/// <see cref="Db"/> reports configured (so the DB-backed hashed key lookup path runs) but is never opened.
/// </summary>
public class ApiKeyEndpointsTests
{
    private sealed class Fixture
    {
        public InMemoryUserRepository Users { get; } = new();
        public InMemoryApiKeyRepository Keys { get; } = new();

        public FakeJwtValidator Validator { get; } = new()
        {
            Principal = WisperPrincipal.Create("jwt-owner", "owner@example.com", Array.Empty<string>()),
        };

        /// <summary>Points the faked JWT at a subject with the given additive groups (host/admin).</summary>
        public void AsUser(string sub, params string[] groups) =>
            Validator.Principal = WisperPrincipal.Create(sub, $"{sub}@example.com", groups);

        /// <summary>A configured <see cref="Db"/> whose data source is never actually opened by these tests.</summary>
        private static Db ConfiguredDb() =>
            new(new NpgsqlDataSourceBuilder("Host=127.0.0.1;Database=none;Username=none").Build());

        public WebApplicationFactory<Program> Build() =>
            new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(
                    new Dictionary<string, string?> { ["Persistence:RunMigrationsAtStartup"] = "false" }));
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<Db>();
                    services.AddSingleton(ConfiguredDb());
                    services.RemoveAll<IJwtValidator>();
                    services.AddSingleton<IJwtValidator>(Validator);
                    services.RemoveAll<IUserRepository>();
                    services.AddSingleton<IUserRepository>(Users);
                    services.RemoveAll<IApiKeyRepository>();
                    services.AddSingleton<IApiKeyRepository>(Keys);
                });
            });
    }

    /// <summary>A client that presents the caller's Cognito JWT (any non-<c>wck_</c> bearer resolves via the fake).</summary>
    private static HttpClient Jwt(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", "Bearer jwt");
        return client;
    }

    /// <summary>A client that presents a minted API key as its bearer.</summary>
    private static HttpClient Key(WebApplicationFactory<Program> factory, string key)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {key}");
        return client;
    }

    [Fact]
    public async Task Mint_use_list_revoke_then_use_fails_401()
    {
        var fx = new Fixture();
        using var factory = fx.Build();

        // Mint (JWT-authenticated) — the full key is returned exactly once.
        var minted = await MintAsync(Jwt(factory), new { name = "orchestrator" });
        Assert.StartsWith("wck_live_", minted.Key);
        Assert.StartsWith("wck_live_", minted.TokenPrefix);
        Assert.Equal(new[] { "consumer" }, minted.Scopes);

        // The row stores only the hash + prefix, never the clear key.
        var stored = Assert.Single(await fx.Keys.ListByUserAsync((await fx.Users.GetByCognitoSubAsync("jwt-owner"))!.Id));
        Assert.NotEqual(minted.Key, stored.TokenHash);
        Assert.Equal(minted.TokenPrefix, stored.TokenPrefix);

        // Use the key: it authenticates the owning user on the consumer surface.
        var me = await Key(factory, minted.Key).GetFromJsonAsync<MeDto>("/v1/me");
        Assert.Equal("jwt-owner", me!.CognitoSub);

        // List: prefix + lifecycle only, never the key or hash; last_used_at is now stamped.
        var list = await Jwt(factory).GetFromJsonAsync<ApiKeysDto>("/v1/me/api-keys");
        var row = Assert.Single(list!.Data);
        Assert.Equal(minted.Id, row.Id);
        Assert.Equal(minted.TokenPrefix, row.TokenPrefix);
        Assert.NotNull(row.LastUsedAt);
        Assert.Null(row.RevokedAt);

        // Revoke.
        var revoke = await Jwt(factory).DeleteAsync($"/v1/me/api-keys/{minted.Id}");
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        var revoked = await revoke.Content.ReadFromJsonAsync<ApiKeyDto>();
        Assert.NotNull(revoked!.RevokedAt);

        // The revoked key fails auth on the next request (already enforced by the auth gate).
        var afterRevoke = await Key(factory, minted.Key).GetAsync("/v1/me");
        Assert.Equal(HttpStatusCode.Unauthorized, afterRevoke.StatusCode);
        var envelope = await afterRevoke.Content.ReadFromJsonAsync<ErrorEnvelopeDto>();
        Assert.Equal("unauthenticated", envelope!.Error.Code);
    }

    [Fact]
    public async Task Revoke_is_idempotent()
    {
        var fx = new Fixture();
        using var factory = fx.Build();
        var minted = await MintAsync(Jwt(factory), new { name = "k" });

        var first = await Jwt(factory).DeleteAsync($"/v1/me/api-keys/{minted.Id}");
        var second = await Jwt(factory).DeleteAsync($"/v1/me/api-keys/{minted.Id}");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var body = await second.Content.ReadFromJsonAsync<ApiKeyDto>();
        Assert.NotNull(body!.RevokedAt);
    }

    [Fact]
    public async Task Mint_defaults_scopes_to_consumer_when_omitted()
    {
        var fx = new Fixture();
        using var factory = fx.Build();

        var minted = await MintAsync(Jwt(factory), new { name = "k" });

        Assert.Equal(new[] { "consumer" }, minted.Scopes);
    }

    [Fact]
    public async Task A_host_scoped_mint_is_capped_by_the_callers_roles()
    {
        var fx = new Fixture();
        using var factory = fx.Build();

        // A consumer-only JWT cannot mint a host-scoped key.
        var response = await Jwt(factory).PostAsJsonAsync(
            "/v1/me/api-keys", new { name = "k", scopes = new[] { "host" } });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ErrorEnvelopeDto>();
        Assert.Equal("forbidden", envelope!.Error.Code);
    }

    [Fact]
    public async Task A_host_user_can_mint_a_host_scoped_key()
    {
        var fx = new Fixture();
        fx.AsUser("host-owner", "host");
        using var factory = fx.Build();

        var minted = await MintAsync(
            Jwt(factory), new { name = "k", scopes = new[] { "host", "consumer" } });

        // Returned in canonical order (consumer → host), de-duplicated.
        Assert.Equal(new[] { "consumer", "host" }, minted.Scopes);
    }

    [Fact]
    public async Task An_unknown_scope_is_400_validation_error()
    {
        var fx = new Fixture();
        using var factory = fx.Build();

        var response = await Jwt(factory).PostAsJsonAsync(
            "/v1/me/api-keys", new { name = "k", scopes = new[] { "superuser" } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ErrorEnvelopeDto>();
        Assert.Equal("validation_error", envelope!.Error.Code);
    }

    [Fact]
    public async Task Mint_requires_a_name()
    {
        var fx = new Fixture();
        using var factory = fx.Build();

        var response = await Jwt(factory).PostAsJsonAsync("/v1/me/api-keys", new { name = "  " });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ErrorEnvelopeDto>();
        Assert.Equal("validation_error", envelope!.Error.Code);
    }

    [Fact]
    public async Task A_key_authenticated_caller_cannot_mint_more_keys()
    {
        var fx = new Fixture();
        using var factory = fx.Build();

        // Mint a consumer key over JWT, then try to mint again presenting that key.
        var minted = await MintAsync(Jwt(factory), new { name = "k" });

        var response = await Key(factory, minted.Key).PostAsJsonAsync(
            "/v1/me/api-keys", new { name = "child" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ErrorEnvelopeDto>();
        Assert.Equal("forbidden", envelope!.Error.Code);
    }

    [Fact]
    public async Task Another_users_key_is_invisible_to_list_and_404_to_revoke()
    {
        var fx = new Fixture();
        using var factory = fx.Build();

        // User A mints a key.
        fx.AsUser("user-a");
        var aKey = await MintAsync(Jwt(factory), new { name = "a-key" });

        // User B sees none of A's keys...
        fx.AsUser("user-b");
        var list = await Jwt(factory).GetFromJsonAsync<ApiKeysDto>("/v1/me/api-keys");
        Assert.Empty(list!.Data);

        // ...and cannot revoke A's key — it is a 404, never revealing it exists.
        var revoke = await Jwt(factory).DeleteAsync($"/v1/me/api-keys/{aKey.Id}");
        Assert.Equal(HttpStatusCode.NotFound, revoke.StatusCode);

        // A's key is untouched (still active).
        var stillActive = Assert.Single(
            await fx.Keys.ListByUserAsync((await fx.Users.GetByCognitoSubAsync("user-a"))!.Id));
        Assert.Null(stillActive.RevokedAt);
    }

    [Fact]
    public async Task Mint_without_a_token_is_401()
    {
        var fx = new Fixture();
        using var factory = fx.Build();

        var response = await factory.CreateClient().PostAsJsonAsync("/v1/me/api-keys", new { name = "k" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task<ApiKeyMintedDto> MintAsync(HttpClient client, object body)
    {
        var response = await client.PostAsJsonAsync("/v1/me/api-keys", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ApiKeyMintedDto>())!;
    }

    private sealed record ApiKeyMintedDto(
        [property: JsonPropertyName("id")] Guid Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("key")] string Key,
        [property: JsonPropertyName("token_prefix")] string TokenPrefix,
        [property: JsonPropertyName("scopes")] string[] Scopes,
        [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt);

    private sealed record ApiKeyDto(
        [property: JsonPropertyName("id")] Guid Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("token_prefix")] string TokenPrefix,
        [property: JsonPropertyName("scopes")] string[] Scopes,
        [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
        [property: JsonPropertyName("last_used_at")] DateTimeOffset? LastUsedAt,
        [property: JsonPropertyName("revoked_at")] DateTimeOffset? RevokedAt);

    private sealed record ApiKeysDto(
        [property: JsonPropertyName("data")] ApiKeyDto[] Data);

    private sealed record MeDto(
        [property: JsonPropertyName("cognito_sub")] string CognitoSub);

    private sealed record ErrorEnvelopeDto(
        [property: JsonPropertyName("error")] ErrorBodyDto Error);

    private sealed record ErrorBodyDto(
        [property: JsonPropertyName("code")] string Code);
}
