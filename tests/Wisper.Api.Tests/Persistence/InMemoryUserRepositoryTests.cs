using System.Threading.Tasks;
using Wisper.Api.Domain;
using Wisper.Api.Persistence.Users;
using Xunit;

namespace Wisper.Api.Tests.Persistence;

/// <summary>
/// Contract tests for <see cref="IUserRepository"/> against the in-memory double (Grunt has no
/// Postgres). Exercises get/create-by-cognito-sub, update of the mutable payment/status columns, and
/// the unique constraints the SQL schema enforces (docs/DATA_MODEL.md §3).
/// </summary>
public class InMemoryUserRepositoryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 12, 0, 0, 0, TimeSpan.Zero);

    private static User NewUser(string sub = "sub-1", string email = "a@example.com") => new()
    {
        CognitoSub = sub,
        Email = email,
        CreatedAt = T0,
        UpdatedAt = T0,
    };

    [Fact]
    public async Task Create_assigns_id_and_round_trips_by_every_key()
    {
        var repo = new InMemoryUserRepository();

        var created = await repo.CreateAsync(NewUser());

        Assert.NotEqual(Guid.Empty, created.Id);
        Assert.Equal(UserStatus.Active, created.Status);
        Assert.Equal(ConnectStatus.None, created.ConnectStatus);
        Assert.Equal(created, await repo.GetByIdAsync(created.Id));
        Assert.Equal(created, await repo.GetByCognitoSubAsync("sub-1"));
        Assert.Equal(created, await repo.GetByEmailAsync("a@example.com"));
    }

    [Fact]
    public async Task Create_preserves_a_supplied_id()
    {
        var repo = new InMemoryUserRepository();
        var id = Guid.NewGuid();

        var created = await repo.CreateAsync(NewUser() with { Id = id });

        Assert.Equal(id, created.Id);
    }

    [Fact]
    public async Task GetByEmail_is_case_insensitive_like_the_unique_index()
    {
        var repo = new InMemoryUserRepository();
        await repo.CreateAsync(NewUser(email: "Mixed@Example.com"));

        Assert.NotNull(await repo.GetByEmailAsync("mixed@example.com"));
    }

    [Fact]
    public async Task Missing_lookups_return_null()
    {
        var repo = new InMemoryUserRepository();

        Assert.Null(await repo.GetByIdAsync(Guid.NewGuid()));
        Assert.Null(await repo.GetByCognitoSubAsync("nope"));
        Assert.Null(await repo.GetByEmailAsync("nope@example.com"));
    }

    [Fact]
    public async Task Update_changes_mutable_columns_and_persists()
    {
        var repo = new InMemoryUserRepository();
        var created = await repo.CreateAsync(NewUser());

        var updated = await repo.UpdateAsync(created with
        {
            Status = UserStatus.Suspended,
            StripeCustomerId = "cus_1",
            ConnectAccountId = "acct_1",
            ConnectStatus = ConnectStatus.Enabled,
            UpdatedAt = T0.AddMinutes(5),
        });

        Assert.Equal(UserStatus.Suspended, updated.Status);
        Assert.Equal("cus_1", updated.StripeCustomerId);
        Assert.Equal(ConnectStatus.Enabled, updated.ConnectStatus);
        Assert.Equal(updated, await repo.GetByIdAsync(created.Id));
    }

    [Fact]
    public async Task Update_of_a_missing_user_throws()
    {
        var repo = new InMemoryUserRepository();

        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.UpdateAsync(NewUser() with { Id = Guid.NewGuid() }));
    }

    [Fact]
    public async Task Duplicate_cognito_sub_is_rejected()
    {
        var repo = new InMemoryUserRepository();
        await repo.CreateAsync(NewUser(sub: "dup", email: "one@example.com"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repo.CreateAsync(NewUser(sub: "dup", email: "two@example.com")));
    }

    [Fact]
    public async Task Duplicate_email_is_rejected()
    {
        var repo = new InMemoryUserRepository();
        await repo.CreateAsync(NewUser(sub: "one", email: "dup@example.com"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repo.CreateAsync(NewUser(sub: "two", email: "dup@example.com")));
    }

    [Fact]
    public async Task Duplicate_stripe_customer_and_connect_account_ids_are_rejected()
    {
        var repo = new InMemoryUserRepository();
        var a = await repo.CreateAsync(NewUser(sub: "a", email: "a@example.com"));
        await repo.UpdateAsync(a with { StripeCustomerId = "cus_x", ConnectAccountId = "acct_x" });
        var b = await repo.CreateAsync(NewUser(sub: "b", email: "b@example.com"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repo.UpdateAsync(b with { StripeCustomerId = "cus_x" }));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repo.UpdateAsync(b with { ConnectAccountId = "acct_x" }));
    }

    [Fact]
    public async Task Update_may_keep_its_own_unique_values()
    {
        var repo = new InMemoryUserRepository();
        var created = await repo.CreateAsync(NewUser());
        await repo.UpdateAsync(created with { StripeCustomerId = "cus_self" });

        // Updating again without changing the unique value must not trip the self-collision guard.
        var again = await repo.UpdateAsync(created with { StripeCustomerId = "cus_self", Status = UserStatus.Suspended });

        Assert.Equal(UserStatus.Suspended, again.Status);
    }
}
