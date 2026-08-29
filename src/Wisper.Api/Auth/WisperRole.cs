namespace Wisper.Api.Auth;

/// <summary>
/// The additive roles a Wisper account can hold (docs/DESIGN.md §10, docs/API.md §2),
/// sourced from Cognito groups. Every authenticated user is implicitly
/// <see cref="Consumer"/>; <see cref="Host"/> and <see cref="Admin"/> are added on top --
/// they do not imply one another.
/// </summary>
public enum WisperRole
{
    /// <summary>Browse/lease/drive/billing. Implicitly held by every authenticated user.</summary>
    Consumer,

    /// <summary>Register wisp hosts, price images, view earnings.</summary>
    Host,

    /// <summary>Platform administration (<c>/v1/admin/*</c>).</summary>
    Admin,
}

/// <summary>The stable wire names for roles/Cognito groups and the mapping to/from <see cref="WisperRole"/>.</summary>
public static class WisperRoles
{
    public const string Consumer = "consumer";
    public const string Host = "host";
    public const string Admin = "admin";

    /// <summary>The wire/group name for a role.</summary>
    public static string Name(WisperRole role) => role switch
    {
        WisperRole.Consumer => Consumer,
        WisperRole.Host => Host,
        WisperRole.Admin => Admin,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "unknown role"),
    };

    /// <summary>
    /// Maps a Cognito group name to a role. Unknown groups map to <c>null</c> and are ignored,
    /// so adding a group in Cognito that Wisper doesn't model is harmless.
    /// </summary>
    public static WisperRole? FromGroup(string group) => group switch
    {
        Consumer => WisperRole.Consumer,
        Host => WisperRole.Host,
        Admin => WisperRole.Admin,
        _ => null,
    };
}
