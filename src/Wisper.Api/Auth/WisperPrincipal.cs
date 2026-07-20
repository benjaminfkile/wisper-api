using System.Security.Claims;

namespace Wisper.Api.Auth;

/// <summary>
/// Maps a validated Cognito JWT's claims to a <see cref="ClaimsPrincipal"/> and reads
/// Wisper identity/role facts back off it (docs/API.md §2). Roles are <b>additive</b>:
/// every authenticated user is implicitly <see cref="WisperRole.Consumer"/>; <c>host</c>
/// and <c>admin</c> are added when present in the Cognito <c>cognito:groups</c> claim.
/// </summary>
public static class WisperPrincipal
{
    /// <summary>Authentication type stamped on the identity — also marks it as Wisper-issued.</summary>
    public const string AuthenticationType = "wisper-jwt";

    /// <summary>
    /// Authentication type stamped on a principal resolved from an <b>API key</b> (docs/API.md §2). It
    /// marks the identity Wisper-issued just like <see cref="AuthenticationType"/>, so the same role gates
    /// treat it identically; the distinct value only records <i>how</i> the caller authenticated.
    /// </summary>
    public const string ApiKeyAuthenticationType = "wisper-api-key";

    /// <summary>Subject claim (Cognito <c>sub</c>) — the stable user id.</summary>
    public const string SubjectClaimType = "sub";

    /// <summary>Email claim.</summary>
    public const string EmailClaimType = "email";

    /// <summary>Cognito groups claim (array); each membership arrives as its own claim.</summary>
    public const string GroupsClaimType = "cognito:groups";

    /// <summary>
    /// Builds a Wisper principal from raw claim values. Adds a <see cref="ClaimTypes.Role"/>
    /// claim for <c>consumer</c> (always) plus every recognized group, so
    /// <see cref="ClaimsPrincipal.IsInRole"/> / <see cref="HasRole"/> work.
    /// </summary>
    public static ClaimsPrincipal Create(string subject, string? email, IEnumerable<string> groups)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            throw new ArgumentException("subject is required", nameof(subject));
        }

        // RoleClaimType is the default ClaimTypes.Role so IsInRole matches the role claims below.
        var identity = new ClaimsIdentity(
            claims: null, authenticationType: AuthenticationType,
            nameType: SubjectClaimType, roleType: ClaimTypes.Role);

        identity.AddClaim(new Claim(SubjectClaimType, subject));
        if (!string.IsNullOrWhiteSpace(email))
        {
            identity.AddClaim(new Claim(EmailClaimType, email));
        }

        // Additive roles: consumer is implicit; host/admin are added, never implied by one another.
        var roles = new HashSet<WisperRole> { WisperRole.Consumer };
        foreach (var group in groups)
        {
            if (string.IsNullOrWhiteSpace(group))
            {
                continue;
            }

            identity.AddClaim(new Claim(GroupsClaimType, group));
            if (WisperRoles.FromGroup(group) is { } role)
            {
                roles.Add(role);
            }
        }

        foreach (var role in roles)
        {
            identity.AddClaim(new Claim(ClaimTypes.Role, WisperRoles.Name(role)));
        }

        return new ClaimsPrincipal(identity);
    }

    /// <summary>
    /// Builds a Wisper principal from an already-validated <see cref="ClaimsIdentity"/>
    /// (as produced by the token handler), pulling <c>sub</c>/<c>email</c>/<c>cognito:groups</c>.
    /// </summary>
    public static ClaimsPrincipal Create(ClaimsIdentity validated)
    {
        var subject = validated.FindFirst(SubjectClaimType)?.Value
            ?? validated.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? throw new ArgumentException("validated token has no subject claim", nameof(validated));
        var email = validated.FindFirst(EmailClaimType)?.Value;
        var groups = validated.FindAll(GroupsClaimType).Select(c => c.Value);
        return Create(subject, email, groups);
    }

    /// <summary>
    /// Builds a Wisper principal from an <b>API key</b> (docs/API.md §2). Unlike the JWT path, roles come
    /// <b>solely</b> from the key's <paramref name="scopes"/> — a key carries its own grants, so there is
    /// <b>no</b> implicit <c>consumer</c> and Cognito groups are never consulted; a scope that names no
    /// known role is ignored. The principal is otherwise shaped exactly like a JWT one (same
    /// <see cref="SubjectClaimType"/>/<see cref="ClaimTypes.Role"/> claims), so the downstream role gates
    /// and identity resolution are unchanged.
    /// </summary>
    public static ClaimsPrincipal CreateForApiKey(string subject, string? email, IEnumerable<string> scopes)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            throw new ArgumentException("subject is required", nameof(subject));
        }

        var identity = new ClaimsIdentity(
            claims: null, authenticationType: ApiKeyAuthenticationType,
            nameType: SubjectClaimType, roleType: ClaimTypes.Role);

        identity.AddClaim(new Claim(SubjectClaimType, subject));
        if (!string.IsNullOrWhiteSpace(email))
        {
            identity.AddClaim(new Claim(EmailClaimType, email));
        }

        // Roles are exactly the key's scopes — never implicit consumer, never Cognito groups.
        var roles = new HashSet<WisperRole>();
        foreach (var scope in scopes)
        {
            if (!string.IsNullOrWhiteSpace(scope) && WisperRoles.FromGroup(scope) is { } role)
            {
                roles.Add(role);
            }
        }

        foreach (var role in roles)
        {
            identity.AddClaim(new Claim(ClaimTypes.Role, WisperRoles.Name(role)));
        }

        return new ClaimsPrincipal(identity);
    }

    /// <summary>Whether <paramref name="authenticationType"/> is one Wisper itself issued (JWT or API key).</summary>
    public static bool IsWisperAuthenticationType(string? authenticationType) =>
        authenticationType is AuthenticationType or ApiKeyAuthenticationType;

    /// <summary>
    /// Whether the caller authenticated with an <b>API key</b> rather than a Cognito JWT (docs/API.md §2).
    /// Used to enforce privilege containment: a key must not mint more keys, so key-authenticated callers are
    /// barred from <c>POST /v1/me/api-keys</c> even though they otherwise pass the same consumer gate.
    /// </summary>
    public static bool IsApiKeyPrincipal(this ClaimsPrincipal principal) =>
        principal.Identity?.AuthenticationType == ApiKeyAuthenticationType;

    /// <summary>The <c>sub</c> claim, or <c>null</c> if absent.</summary>
    public static string? GetSubject(this ClaimsPrincipal principal) =>
        principal.FindFirst(SubjectClaimType)?.Value;

    /// <summary>The <c>email</c> claim, or <c>null</c> if absent.</summary>
    public static string? GetEmail(this ClaimsPrincipal principal) =>
        principal.FindFirst(EmailClaimType)?.Value;

    /// <summary>Whether the principal holds <paramref name="role"/> (additive).</summary>
    public static bool HasRole(this ClaimsPrincipal principal, WisperRole role) =>
        principal.IsInRole(WisperRoles.Name(role));

    /// <summary>The distinct roles the principal holds.</summary>
    public static IReadOnlyCollection<WisperRole> GetRoles(this ClaimsPrincipal principal)
    {
        var roles = new HashSet<WisperRole>();
        foreach (var role in Enum.GetValues<WisperRole>())
        {
            if (principal.HasRole(role))
            {
                roles.Add(role);
            }
        }

        return roles;
    }
}
