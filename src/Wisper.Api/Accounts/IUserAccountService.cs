using System.Security.Claims;
using Wisper.Api.Domain;

namespace Wisper.Api.Accounts;

/// <summary>
/// The account layer over the users repository (docs/API.md §2, §5, P3.2). It turns a validated
/// Cognito principal into a persisted <see cref="User"/> row, creating it on first sight
/// (<b>bootstrap</b>) and applying the mutable profile fields <c>PATCH /v1/me</c> exposes. Identity
/// lives in Cognito; this row mirrors it (docs/DATA_MODEL.md §3), so bootstrap is keyed on the
/// <c>sub</c> claim and is idempotent — the second and later calls just re-read the same row.
/// </summary>
public interface IUserAccountService
{
    /// <summary>
    /// Returns the <see cref="User"/> for <paramref name="principal"/>, creating it from the token's
    /// <c>sub</c>/<c>email</c> claims on the first authenticated call. Idempotent and race-safe: a
    /// concurrent first request that lost the unique-<c>cognito_sub</c> insert re-reads the winner's row.
    /// Throws <see cref="Infrastructure.ApiException"/> when the token lacks a subject or (on first sight)
    /// an email claim.
    /// </summary>
    Task<User> BootstrapAsync(ClaimsPrincipal principal, CancellationToken ct = default);

    /// <summary>
    /// Applies the mutable profile <paramref name="changes"/> to the caller's row (bootstrapping it first
    /// if needed) and returns the stored result. A field left <c>null</c> in <paramref name="changes"/> is
    /// untouched. Throws <see cref="Infrastructure.ApiException"/> on invalid input (<c>validation_error</c>)
    /// or a unique-column collision such as an email already taken (<c>conflict</c>).
    /// </summary>
    Task<User> UpdateProfileAsync(
        ClaimsPrincipal principal, ProfileUpdate changes, CancellationToken ct = default);
}

/// <summary>
/// The mutable profile fields <c>PATCH /v1/me</c> accepts (docs/API.md §5). A <c>null</c> field means
/// "leave unchanged"; identity linkage (<c>cognito_sub</c>) and billing state (<c>connect_status</c>,
/// Stripe ids) are <b>not</b> caller-mutable and live on other surfaces (P6/P7/admin).
/// </summary>
public sealed record ProfileUpdate
{
    /// <summary>New account email, or <c>null</c> to leave it unchanged.</summary>
    public string? Email { get; init; }
}
