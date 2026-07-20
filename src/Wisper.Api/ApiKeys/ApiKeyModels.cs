using System.Text.Json.Serialization;
using Wisper.Api.Domain;

namespace Wisper.Api.ApiKeys;

/// <summary>
/// Body of <c>POST /v1/me/api-keys</c> (docs/API.md §5): the key's display <c>name</c> and its optional
/// requested <c>scopes</c>. Scopes default to <c>["consumer"]</c> and are capped by the roles the calling
/// JWT actually holds — a key can never be granted a scope its minter lacks (docs/API.md §2).
/// </summary>
public sealed record MintApiKeyRequest(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("scopes")] IReadOnlyList<string>? Scopes);

/// <summary>
/// Response of <c>POST /v1/me/api-keys</c> (docs/API.md §5): the freshly minted <c>key</c> — the full
/// bearer, shown <b>once</b> and never retrievable or logged again — plus its non-secret display prefix,
/// granted scopes, and identity/creation metadata. Only the hash + prefix are stored (docs/DATA_MODEL.md §3).
/// </summary>
public sealed record ApiKeyMintedResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("token_prefix")] string TokenPrefix,
    [property: JsonPropertyName("scopes")] IReadOnlyList<string> Scopes,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt);

/// <summary>
/// The caller-facing view of one API key (docs/API.md §5, <c>GET /v1/me/api-keys</c>). Carries the
/// non-secret display prefix, scopes and lifecycle timestamps — <b>never</b> the hash and never the key
/// itself (which exists only at mint).
/// </summary>
public sealed record ApiKeyView(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("token_prefix")] string TokenPrefix,
    [property: JsonPropertyName("scopes")] IReadOnlyList<string> Scopes,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("last_used_at")] DateTimeOffset? LastUsedAt,
    [property: JsonPropertyName("revoked_at")] DateTimeOffset? RevokedAt)
{
    /// <summary>Projects a stored <see cref="ApiKey"/> into the wire shape (never exposing its hash).</summary>
    public static ApiKeyView From(ApiKey key) => new(
        Id: key.Id,
        Name: key.Name,
        TokenPrefix: key.TokenPrefix,
        Scopes: key.Scopes,
        CreatedAt: key.CreatedAt,
        LastUsedAt: key.LastUsedAt,
        RevokedAt: key.RevokedAt);
}

/// <summary>The caller's keys (docs/API.md §5, <c>GET /v1/me/api-keys</c>), newest first.</summary>
public sealed record ApiKeysResponse(
    [property: JsonPropertyName("data")] IReadOnlyList<ApiKeyView> Data);
