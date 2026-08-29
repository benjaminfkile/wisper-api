namespace Wisper.Api.Auth;

/// <summary>
/// Grants a Wisper role to a user by writing to the identity provider's group membership (docs/API.md §2,
/// §6, docs/DESIGN.md §10). Becoming a host is <b>additive</b> -- a <c>consumer</c> gains the <c>host</c>
/// group on their first host action (docs/API.md §184) -- and roles are sourced from Cognito groups
/// (<see cref="WisperPrincipal"/>/<see cref="WisperRoles.FromGroup"/>), so the grant is a group write, not a
/// row in Wisper's own tables. Implementations must be <b>idempotent</b> (re-granting an existing member is a
/// no-op) and must <b>not</b> be relied on for correctness within the same request -- the caller reconciles the
/// live session from owned hosts (docs/API.md §184) so a transient grant failure never blocks host onboarding.
/// </summary>
public interface IUserRoleGranter
{
    /// <summary>
    /// Ensures the user identified by <paramref name="cognitoSub"/> holds the <c>host</c> role going forward,
    /// by adding them to the Cognito <c>host</c> group. Idempotent (no-op if already a member). In DB-less /
    /// api-key dev mode (no user pool configured) this degrades to a no-op -- api-key principals carry explicit
    /// scopes, not groups, so they are unaffected (docs/API.md §2).
    /// </summary>
    Task GrantHostAsync(string cognitoSub, CancellationToken ct = default);
}

/// <summary>
/// The no-op <see cref="IUserRoleGranter"/> used when no Cognito user pool is configured (in-memory / api-key
/// dev mode and tests). It skips the group write entirely: there is no pool to write to, and api-key
/// principals derive their roles from explicit scopes rather than Cognito groups (docs/API.md §2), so nothing
/// needs granting.
/// </summary>
public sealed class NoOpUserRoleGranter : IUserRoleGranter
{
    public Task GrantHostAsync(string cognitoSub, CancellationToken ct = default) => Task.CompletedTask;
}
