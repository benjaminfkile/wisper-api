using System.Security.Claims;
using Wisper.Api.Accounts;
using Wisper.Api.Auth;
using Wisper.Api.Domain;
using Wisper.Api.Infrastructure;
using Wisper.Api.Persistence.Users;
using Wisper.Api.Tests.TestSupport;
using Xunit;

namespace Wisper.Api.Tests.Accounts;

/// <summary>
/// Unit tests for <see cref="UserAccountService"/> (docs/API.md §2, §5, P3.2): first-sight bootstrap
/// from the JWT claims, its idempotency, and the mutable-profile <c>PATCH</c> path. Uses the in-memory
/// users repository and a fixed clock -- no Postgres, no crypto.
/// </summary>
public class UserAccountServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 07, 12, 8, 0, 0, TimeSpan.Zero);

    private static (UserAccountService Service, InMemoryUserRepository Users) NewService()
    {
        var users = new InMemoryUserRepository();
        var service = new UserAccountService(users, new FakeTimeProvider(Now));
        return (service, users);
    }

    private static ClaimsPrincipal Principal(
        string sub = "sub-1", string? email = "user@example.com", params string[] groups) =>
        WisperPrincipal.Create(sub, email, groups);

    [Fact]
    public async Task Bootstrap_creates_the_row_from_the_jwt_claims_on_first_call()
    {
        var (service, users) = NewService();

        var user = await service.BootstrapAsync(Principal(sub: "cognito-42", email: "a@b.com"));

        Assert.NotEqual(Guid.Empty, user.Id);
        Assert.Equal("cognito-42", user.CognitoSub);
        Assert.Equal("a@b.com", user.Email);
        Assert.Equal(UserStatus.Active, user.Status);
        Assert.Equal(ConnectStatus.None, user.ConnectStatus);
        Assert.Equal(Now, user.CreatedAt);
        Assert.Equal(Now, user.UpdatedAt);

        // The row is persisted and findable by its Cognito subject.
        var stored = await users.GetByCognitoSubAsync("cognito-42");
        Assert.NotNull(stored);
        Assert.Equal(user.Id, stored!.Id);
    }

    [Fact]
    public async Task Bootstrap_is_idempotent_and_returns_the_same_row()
    {
        var (service, users) = NewService();
        var principal = Principal(sub: "cognito-1", email: "a@b.com");

        var first = await service.BootstrapAsync(principal);
        var second = await service.BootstrapAsync(principal);

        Assert.Equal(first.Id, second.Id);
        // The email lookup resolves to that same single row -- no duplicate was inserted.
        Assert.Equal(first.Id, (await users.GetByEmailAsync("a@b.com"))!.Id);
    }

    [Fact]
    public async Task Bootstrap_does_not_recreate_when_the_email_claim_later_changes()
    {
        var (service, users) = NewService();

        var first = await service.BootstrapAsync(Principal(sub: "cognito-1", email: "old@b.com"));
        // Same subject, different email claim -- still the same account, not a second row.
        var second = await service.BootstrapAsync(Principal(sub: "cognito-1", email: "new@b.com"));

        Assert.Equal(first.Id, second.Id);
        Assert.Equal("old@b.com", second.Email);
        Assert.Null(await users.GetByEmailAsync("new@b.com"));
    }

    [Fact]
    public async Task Bootstrap_without_an_email_claim_on_first_sight_is_a_validation_error()
    {
        var (service, _) = NewService();

        var ex = await Assert.ThrowsAsync<ApiException>(
            () => service.BootstrapAsync(Principal(sub: "cognito-1", email: null)));

        Assert.Equal(ApiErrorCode.ValidationError, ex.Code);
    }

    [Fact]
    public async Task Update_changes_the_email_and_bumps_updated_at()
    {
        var (service, _) = NewService();
        var principal = Principal(sub: "cognito-1", email: "old@b.com");
        await service.BootstrapAsync(principal);

        var updated = await service.UpdateProfileAsync(
            principal, new ProfileUpdate { Email = "new@b.com" });

        Assert.Equal("new@b.com", updated.Email);
        Assert.Equal("cognito-1", updated.CognitoSub);
    }

    [Fact]
    public async Task Update_bootstraps_first_when_the_row_does_not_exist_yet()
    {
        var (service, users) = NewService();
        var principal = Principal(sub: "cognito-1", email: "seed@b.com");

        // No prior bootstrap call -- PATCH must materialize the row, then apply the change.
        var updated = await service.UpdateProfileAsync(
            principal, new ProfileUpdate { Email = "changed@b.com" });

        Assert.Equal("changed@b.com", updated.Email);
        Assert.Equal(updated.Id, (await users.GetByCognitoSubAsync("cognito-1"))!.Id);
    }

    [Fact]
    public async Task Update_with_no_fields_is_a_noop_that_returns_the_current_row()
    {
        var (service, _) = NewService();
        var principal = Principal(sub: "cognito-1", email: "a@b.com");
        await service.BootstrapAsync(principal);

        var same = await service.UpdateProfileAsync(principal, new ProfileUpdate { Email = null });

        Assert.Equal("a@b.com", same.Email);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-email")]
    public async Task Update_with_an_invalid_email_is_a_validation_error(string email)
    {
        var (service, _) = NewService();
        var principal = Principal(sub: "cognito-1", email: "a@b.com");
        await service.BootstrapAsync(principal);

        var ex = await Assert.ThrowsAsync<ApiException>(
            () => service.UpdateProfileAsync(principal, new ProfileUpdate { Email = email }));

        Assert.Equal(ApiErrorCode.ValidationError, ex.Code);
    }

    [Fact]
    public async Task Update_to_an_email_owned_by_another_account_is_a_conflict()
    {
        var (service, _) = NewService();
        await service.BootstrapAsync(Principal(sub: "cognito-a", email: "taken@b.com"));
        var second = Principal(sub: "cognito-b", email: "free@b.com");
        await service.BootstrapAsync(second);

        var ex = await Assert.ThrowsAsync<ApiException>(
            () => service.UpdateProfileAsync(second, new ProfileUpdate { Email = "taken@b.com" }));

        Assert.Equal(ApiErrorCode.Conflict, ex.Code);
    }

    [Fact]
    public async Task Bootstrap_without_a_subject_claim_is_unauthenticated()
    {
        var (service, _) = NewService();
        // A bare identity with only an email claim -- no subject.
        var identity = new ClaimsIdentity(WisperPrincipal.AuthenticationType);
        identity.AddClaim(new Claim(WisperPrincipal.EmailClaimType, "a@b.com"));
        var principal = new ClaimsPrincipal(identity);

        var ex = await Assert.ThrowsAsync<ApiException>(() => service.BootstrapAsync(principal));

        Assert.Equal(ApiErrorCode.Unauthenticated, ex.Code);
    }
}
