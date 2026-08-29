using Wisper.Api.Auth;
using Wisper.Api.Domain;
using Wisper.Api.Infrastructure;
using Wisper.Api.Persistence.ApiKeys;

namespace Wisper.Api.ApiKeys;

/// <summary>
/// The self-serve key lifecycle behind the <c>/v1/me/api-keys</c> surface (docs/API.md §5): mint a key
/// (shown once, hash-at-rest, scope-capped by the minter's roles), list the caller's keys (prefix only,
/// never the hash/key), and revoke a key (idempotent, owner-scoped). Everything runs against the
/// <see cref="IApiKeyRepository"/> interface so the unit suite needs no Postgres. Secret values are never
/// logged -- only the non-secret display prefix.
/// </summary>
public sealed class ApiKeyService
{
    private readonly IApiKeyRepository _keys;
    private readonly TimeProvider _time;
    private readonly ILogger<ApiKeyService> _logger;

    public ApiKeyService(IApiKeyRepository keys, TimeProvider time, ILogger<ApiKeyService> logger)
    {
        _keys = keys;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Mints a new API key for <paramref name="ownerUserId"/> (docs/API.md §5). The requested scopes default
    /// to <c>consumer</c> and every scope must be one of <paramref name="callerRoles"/> -- a key can never be
    /// granted a role its minter lacks (privilege containment, docs/API.md §2). Only the hash + prefix are
    /// persisted; the returned <see cref="ApiKeyMintedResponse.Key"/> is the full bearer, shown <b>once</b>.
    /// </summary>
    public async Task<ApiKeyMintedResponse> MintAsync(
        Guid ownerUserId,
        IReadOnlyCollection<WisperRole> callerRoles,
        MintApiKeyRequest request,
        CancellationToken ct = default)
    {
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            throw new ApiException(ApiErrorCode.ValidationError, "name is required.", new { field = "name" });
        }

        var scopes = ResolveScopes(request.Scopes, callerRoles);

        var issued = ApiKeyToken.Issue();
        var created = await _keys.CreateAsync(new ApiKey
        {
            UserId = ownerUserId,
            Name = name,
            TokenHash = issued.TokenHash,
            TokenPrefix = issued.TokenPrefix,
            Scopes = scopes.Select(WisperRoles.Name).ToList(),
            CreatedAt = _time.GetUtcNow(),
        }, ct);

        // Log the id + non-secret prefix + scopes only -- never the clear key.
        _logger.LogInformation(
            "api key {Key} minted by user {User} (prefix {Prefix}, scopes {Scopes})",
            created.Id, ownerUserId, issued.TokenPrefix, string.Join(',', created.Scopes));

        return new ApiKeyMintedResponse(
            created.Id, created.Name, issued.Token, created.TokenPrefix, created.Scopes, created.CreatedAt);
    }

    /// <summary>The caller's keys (docs/API.md §5), newest first -- prefix + scopes + lifecycle only.</summary>
    public async Task<ApiKeysResponse> ListAsync(Guid ownerUserId, CancellationToken ct = default)
    {
        var keys = await _keys.ListByUserAsync(ownerUserId, ct);
        return new ApiKeysResponse(keys.Select(ApiKeyView.From).ToList());
    }

    /// <summary>
    /// Revokes the caller's key (docs/API.md §5). Ownership-scoped and idempotent: a key that is not the
    /// caller's (or does not exist) is <c>404</c> so the API never reveals another user's key; a key already
    /// revoked is a no-op that still returns its (revoked) view. A revoked key fails auth on the next request.
    /// </summary>
    public async Task<ApiKeyView> RevokeAsync(Guid ownerUserId, Guid keyId, CancellationToken ct = default)
    {
        // Scope the lookup to the owner's keys, so another user's key is invisible (404, docs/API.md §3).
        var owned = await _keys.ListByUserAsync(ownerUserId, ct);
        var key = owned.FirstOrDefault(k => k.Id == keyId);
        if (key is null)
        {
            throw new ApiException(ApiErrorCode.NotFound, $"No such API key '{keyId}'.");
        }

        if (key.RevokedAt is not null)
        {
            // Idempotent: already revoked -- return the existing state without a second write.
            return ApiKeyView.From(key);
        }

        var now = _time.GetUtcNow();
        var revoked = await _keys.RevokeAsync(keyId, now, ct);

        // A concurrent revoke may have won the race (RevokeAsync then returns null); either way the key is now
        // revoked, so fall back to the row we loaded with the stamp applied.
        _logger.LogInformation("api key {Key} revoked by user {User}", keyId, ownerUserId);
        return ApiKeyView.From(revoked ?? key with { RevokedAt = now });
    }

    /// <summary>
    /// Resolves the requested scope labels to roles, defaulting to <c>consumer</c> when none are given, and
    /// caps each against <paramref name="callerRoles"/>. An unknown label is <c>400 validation_error</c>; a
    /// label naming a role the caller does not hold is <c>403 forbidden</c> (a key cannot exceed its minter).
    /// The result is de-duplicated and returned in the canonical role order (consumer → host → admin).
    /// </summary>
    private static IReadOnlyList<WisperRole> ResolveScopes(
        IReadOnlyList<string>? requested, IReadOnlyCollection<WisperRole> callerRoles)
    {
        var labels = requested is { Count: > 0 } ? requested : new[] { WisperRoles.Consumer };

        var resolved = new HashSet<WisperRole>();
        foreach (var label in labels)
        {
            var role = WisperRoles.FromGroup(label?.Trim() ?? string.Empty);
            if (role is null)
            {
                throw new ApiException(
                    ApiErrorCode.ValidationError,
                    $"Unknown scope '{label}'.",
                    new { field = "scopes", allowed = new[] { "consumer", "host", "admin" } });
            }

            if (!callerRoles.Contains(role.Value))
            {
                throw new ApiException(
                    ApiErrorCode.Forbidden,
                    $"The '{WisperRoles.Name(role.Value)}' scope exceeds the roles you hold; " +
                    "a key cannot be granted a scope its minter lacks.",
                    new { field = "scopes", scope = WisperRoles.Name(role.Value) });
            }

            resolved.Add(role.Value);
        }

        return Enum.GetValues<WisperRole>().Where(resolved.Contains).ToList();
    }
}
