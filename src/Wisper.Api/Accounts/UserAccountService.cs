using System.Security.Claims;
using Wisper.Api.Auth;
using Wisper.Api.Domain;
using Wisper.Api.Infrastructure;
using Wisper.Api.Persistence.Users;

namespace Wisper.Api.Accounts;

/// <summary>
/// Default <see cref="IUserAccountService"/> over the <see cref="IUserRepository"/> (docs/API.md §2,
/// §5). Bootstrap reads by <c>cognito_sub</c> and inserts on miss; the clock is injected so both the
/// created timestamps and the tests are deterministic.
/// </summary>
public sealed class UserAccountService : IUserAccountService
{
    private readonly IUserRepository _users;
    private readonly TimeProvider _time;

    public UserAccountService(IUserRepository users, TimeProvider time)
    {
        _users = users;
        _time = time;
    }

    public async Task<User> BootstrapAsync(ClaimsPrincipal principal, CancellationToken ct = default)
    {
        var subject = RequireSubject(principal);

        var existing = await _users.GetByCognitoSubAsync(subject, ct);
        if (existing is not null)
        {
            return existing;
        }

        var email = principal.GetEmail();
        if (string.IsNullOrWhiteSpace(email))
        {
            // The row mirrors identity (docs/DATA_MODEL.md §3) and email is NOT NULL there; a token
            // without an email claim cannot bootstrap an account.
            throw new ApiException(
                ApiErrorCode.ValidationError, "The authenticated token is missing a required email claim.");
        }

        var now = _time.GetUtcNow();
        var toCreate = new User
        {
            CognitoSub = subject,
            Email = email,
            Status = UserStatus.Active,
            ConnectStatus = ConnectStatus.None,
            CreatedAt = now,
            UpdatedAt = now,
        };

        try
        {
            return await _users.CreateAsync(toCreate, ct);
        }
        catch (Exception)
        {
            // Lost the race to a concurrent first request (the unique cognito_sub insert failed) — if a
            // row now exists it's the winner's, so return it; otherwise the failure was something else.
            var raced = await _users.GetByCognitoSubAsync(subject, ct);
            if (raced is not null)
            {
                return raced;
            }

            throw;
        }
    }

    public async Task<User> UpdateProfileAsync(
        ClaimsPrincipal principal, ProfileUpdate changes, CancellationToken ct = default)
    {
        var current = await BootstrapAsync(principal, ct);

        var email = current.Email;
        if (changes.Email is not null)
        {
            var trimmed = changes.Email.Trim();
            if (trimmed.Length == 0 || !trimmed.Contains('@', StringComparison.Ordinal))
            {
                throw new ApiException(
                    ApiErrorCode.ValidationError, "email must be a non-empty address.",
                    new { field = "email" });
            }

            email = trimmed;
        }

        if (string.Equals(email, current.Email, StringComparison.Ordinal))
        {
            return current;
        }

        var updated = current with { Email = email, UpdatedAt = _time.GetUtcNow() };
        try
        {
            return await _users.UpdateAsync(updated, ct);
        }
        catch (InvalidOperationException ex)
        {
            // A unique column (email) collided with another account — mirror the DB constraint as a conflict.
            throw new ApiException(ApiErrorCode.Conflict, ex.Message);
        }
    }

    private static string RequireSubject(ClaimsPrincipal principal)
    {
        var subject = principal.GetSubject();
        if (string.IsNullOrWhiteSpace(subject))
        {
            throw new ApiException(
                ApiErrorCode.Unauthenticated, "The authenticated token is missing its subject claim.");
        }

        return subject;
    }
}
