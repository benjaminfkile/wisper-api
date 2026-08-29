namespace Wisper.Api.Domain;

/// <summary>
/// An identity/account row (docs/DATA_MODEL.md §3, <c>users</c>). Roles are additive and
/// authoritative in Cognito groups; this row mirrors identity plus payment linkage. Each user gets
/// exactly one <c>user_wallet</c> and (if a host) one <c>host_earnings</c> ledger account, created
/// lazily and pinned by a unique constraint (§8) -- those live in the ledger schema, not here.
/// </summary>
public sealed record User
{
    /// <summary>Internal id (DB default <c>gen_random_uuid()</c>).</summary>
    public Guid Id { get; init; }

    /// <summary>Cognito subject -- unique, the stable external identity key.</summary>
    public required string CognitoSub { get; init; }

    /// <summary>Account email -- unique.</summary>
    public required string Email { get; init; }

    /// <summary>Account lifecycle; <see cref="UserStatus.Suspended"/> gates all activity.</summary>
    public UserStatus Status { get; init; } = UserStatus.Active;

    /// <summary>Stripe customer id -- created on first top-up (consumer side); unique when set.</summary>
    public string? StripeCustomerId { get; init; }

    /// <summary>Stripe Connect account id (host side); unique when set.</summary>
    public string? ConnectAccountId { get; init; }

    /// <summary>Connect onboarding state; gates a host going online (§9, §10).</summary>
    public ConnectStatus ConnectStatus { get; init; } = ConnectStatus.None;

    /// <summary>Row creation time (UTC).</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Last mutation time (UTC).</summary>
    public DateTimeOffset UpdatedAt { get; init; }
}
