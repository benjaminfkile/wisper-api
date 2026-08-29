namespace Wisper.Api.Domain;

/// <summary>
/// A consumer <b>API key</b> -- a long-lived machine bearer for the authenticated <c>/v1</c> surface
/// (docs/DATA_MODEL.md §3, docs/API.md §2, <c>api_keys</c>). The key value is stored <b>hashed only</b>;
/// the key itself is shown to the user once at mint. Because a key's roles cannot come from Cognito
/// groups, the granted <see cref="Scopes"/> (the role labels) travel on the row. A key is revocable
/// (<see cref="RevokedAt"/>) and carries a best-effort <see cref="LastUsedAt"/> stamp.
/// </summary>
public sealed record ApiKey
{
    /// <summary>Key id (DB default <c>gen_random_uuid()</c>).</summary>
    public Guid Id { get; init; }

    /// <summary>Owner -- the user the key authenticates as.</summary>
    public required Guid UserId { get; init; }

    /// <summary>User-supplied display name (identifies the key in the listing UX).</summary>
    public required string Name { get; init; }

    /// <summary>SHA-256 hash of the key value -- never the key itself.</summary>
    public required string TokenHash { get; init; }

    /// <summary>Short non-secret prefix for identification/listing UX (e.g. <c>wck_live_ab12</c>).</summary>
    public required string TokenPrefix { get; init; }

    /// <summary>The granted scopes -- the role labels (<c>consumer</c>, <c>host</c>, …) this key carries.</summary>
    public IReadOnlyList<string> Scopes { get; init; } = Array.Empty<string>();

    /// <summary>Row creation time (UTC).</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Last time the key authenticated a request (UTC); best-effort, may lag. <c>null</c> if never used.</summary>
    public DateTimeOffset? LastUsedAt { get; init; }

    /// <summary>When the key was revoked (UTC); <c>null</c> while active. A revoked key no longer resolves.</summary>
    public DateTimeOffset? RevokedAt { get; init; }
}
