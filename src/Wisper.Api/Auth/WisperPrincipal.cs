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
