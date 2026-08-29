using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Wisper.Api.Accounts;
using Wisper.Api.Auth;
using Wisper.Api.Domain;
using Wisper.Api.Infrastructure;
using Wisper.Api.Persistence.Hosts;
using Wisper.Api.Persistence.Users;
using Wisper.Api.Tests.TestSupport;
using Xunit;
using Host = Wisper.Api.Domain.Host;

namespace Wisper.Api.Tests.Auth;

/// <summary>
/// Unit tests for the role-gating <see cref="WisperAuthFilter"/> (docs/API.md §2): missing/invalid
/// credentials → <c>401 unauthenticated</c>; authenticated-but-under-privileged → <c>403 forbidden</c>;
/// authorized calls flow through. Uses <see cref="FakeJwtValidator"/> — no crypto.
/// </summary>
public class WisperAuthFilterTests
{
    private static (EndpointFilterInvocationContext Context, HttpContext Http) NewContext(
        FakeJwtValidator validator, string? authorization = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IJwtValidator>(validator);

        var http = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        if (authorization is not null)
        {
            http.Request.Headers.Authorization = authorization;
        }

        return (EndpointFilterInvocationContext.Create(http), http);
    }

    private static async Task<(object? Result, bool NextCalled)> InvokeAsync(
        WisperAuthFilter filter, EndpointFilterInvocationContext context)
    {
        var nextCalled = false;
        var result = await filter.InvokeAsync(context, ctx =>
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>("ok");
        });
        return (result, nextCalled);
    }

    [Fact]
    public async Task Missing_bearer_throws_401_unauthenticated()
    {
        var filter = new WisperAuthFilter(WisperRole.Consumer);
        var (context, _) = NewContext(new FakeJwtValidator());

        var ex = await Assert.ThrowsAsync<ApiException>(() => InvokeAsync(filter, context).AsResult());

        Assert.Equal(ApiErrorCode.Unauthenticated, ex.Code);
    }

    [Fact]
    public async Task Invalid_token_throws_401_unauthenticated()
    {
        var filter = new WisperAuthFilter(WisperRole.Consumer);
        var (context, _) = NewContext(new FakeJwtValidator { Fail = true }, "Bearer bad-token");

        var ex = await Assert.ThrowsAsync<ApiException>(() => InvokeAsync(filter, context).AsResult());

        Assert.Equal(ApiErrorCode.Unauthenticated, ex.Code);
    }

    [Fact]
    public async Task Authorized_consumer_flows_through_and_sets_user()
    {
        var validator = new FakeJwtValidator
        {
            Principal = WisperPrincipal.Create("sub-1", "c@x.com", Array.Empty<string>()),
        };
        var filter = new WisperAuthFilter(WisperRole.Consumer);
        var (context, http) = NewContext(validator, "Bearer good");

        var (result, nextCalled) = await InvokeAsync(filter, context);

        Assert.True(nextCalled);
        Assert.Equal("ok", result);
        Assert.Equal("sub-1", http.User.GetSubject());
        Assert.Equal("good", validator.LastToken);
    }

    [Fact]
    public async Task Authenticated_but_missing_role_throws_403_forbidden()
    {
        var validator = new FakeJwtValidator
        {
            Principal = WisperPrincipal.Create("sub-1", null, Array.Empty<string>()), // consumer only
        };
        var filter = new WisperAuthFilter(WisperRole.Admin);
        var (context, _) = NewContext(validator, "Bearer good");

        var ex = await Assert.ThrowsAsync<ApiException>(() => InvokeAsync(filter, context).AsResult());

        Assert.Equal(ApiErrorCode.Forbidden, ex.Code);
    }

    [Fact]
    public async Task Admin_passes_admin_gate()
    {
        var validator = new FakeJwtValidator
        {
            Principal = WisperPrincipal.Create("sub-1", null, new[] { "admin" }),
        };
        var filter = new WisperAuthFilter(WisperRole.Admin);
        var (context, _) = NewContext(validator, "Bearer good");

        var (_, nextCalled) = await InvokeAsync(filter, context);

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task Second_gate_reuses_the_resolved_principal_without_revalidating()
    {
        // First gate (consumer) authenticates; a second stacked gate should not call the validator again.
        var validator = new FakeJwtValidator
        {
            Principal = WisperPrincipal.Create("sub-1", null, new[] { "host" }),
        };
        var (context, http) = NewContext(validator, "Bearer good");

        await InvokeAsync(new WisperAuthFilter(WisperRole.Consumer), context);
        validator.Fail = true; // if the host gate re-validates, it would now 401
        var (_, nextCalled) = await InvokeAsync(new WisperAuthFilter(WisperRole.Host), context);

        Assert.True(nextCalled);
        Assert.True(http.User.HasRole(WisperRole.Host));
    }

    // --- Host gate honors DB host-ownership (docs/API.md §184): become-a-host is effective on the same token. ---

    /// <summary>
    /// Builds a filter context whose service provider can resolve host ownership: a real
    /// <see cref="UserAccountService"/> over an in-memory users repo plus a call-counting host repo. The JWT
    /// principal is authenticated via <see cref="FakeJwtValidator"/> from a <c>Bearer good</c> header.
    /// </summary>
    private static (EndpointFilterInvocationContext Context, HttpContext Http,
        CountingHostRepository Hosts, InMemoryUserRepository Users) NewOwnershipContext(
        ClaimsPrincipal principal, bool preAuthenticated = false)
    {
        var users = new InMemoryUserRepository();
        var hosts = new CountingHostRepository();

        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IJwtValidator>(new FakeJwtValidator { Principal = principal });
        services.AddSingleton<IUserRepository>(users);
        services.AddSingleton<IUserAccountService, UserAccountService>();
        services.AddSingleton<IHostRepository>(hosts);

        var http = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        if (preAuthenticated)
        {
            // An api-key principal is resolved upstream; the filter sees it already on http.User.
            http.User = principal;
        }
        else
        {
            http.Request.Headers.Authorization = "Bearer good";
        }

        return (EndpointFilterInvocationContext.Create(http), http, hosts, users);
    }

    /// <summary>Seeds a users row for <paramref name="sub"/> that owns one host, returning the owner id.</summary>
    private static async Task<Guid> SeedOwnerWithHostAsync(
        InMemoryUserRepository users, CountingHostRepository hosts, string sub, string email)
    {
        var user = await users.CreateAsync(new User
        {
            CognitoSub = sub,
            Email = email,
            Status = UserStatus.Active,
            ConnectStatus = ConnectStatus.None,
        });
        await hosts.CreateAsync(new Host
        {
            OwnerUserId = user.Id,
            AgentTokenHash = "hash",
            AgentTokenPrefix = "prefix",
            Status = HostStatus.Offline,
        });
        return user.Id;
    }

    [Fact]
    public async Task Consumer_owning_a_host_passes_the_host_gate_without_the_group()
    {
        // The live bug: a consumer whose current token predates the host-group add owns a host but 403s.
        // The host gate now honors DB ownership, so this call passes on the pre-existing token — no re-login.
        var principal = WisperPrincipal.Create("owner-sub", "owner@example.com", Array.Empty<string>());
        Assert.False(principal.HasRole(WisperRole.Host)); // the token itself carries no host group
        var (context, _, hosts, users) = NewOwnershipContext(principal);
        await SeedOwnerWithHostAsync(users, hosts, "owner-sub", "owner@example.com");

        var (_, nextCalled) = await InvokeAsync(new WisperAuthFilter(WisperRole.Host), context);

        Assert.True(nextCalled);
        Assert.Equal(1, hosts.ListByOwnerCalls);
    }

    [Fact]
    public async Task Consumer_owning_no_host_is_still_forbidden_on_the_host_gate()
    {
        var principal = WisperPrincipal.Create("plain-sub", "plain@example.com", Array.Empty<string>());
        var (context, _, hosts, _) = NewOwnershipContext(principal);

        var ex = await Assert.ThrowsAsync<ApiException>(
            () => InvokeAsync(new WisperAuthFilter(WisperRole.Host), context).AsResult());

        Assert.Equal(ApiErrorCode.Forbidden, ex.Code);
        // Ownership was consulted (and came back empty) exactly once.
        Assert.Equal(1, hosts.ListByOwnerCalls);
    }

    [Fact]
    public async Task Api_key_principal_does_not_derive_host_from_ownership()
    {
        // An api-key principal authorizes purely by its explicit scopes (docs/API.md §2): even though its owner
        // owns a host, a key without the host scope is forbidden, and ownership is never queried.
        var principal = WisperPrincipal.CreateForApiKey("owner-sub", "owner@example.com", new[] { "consumer" });
        var (context, _, hosts, users) = NewOwnershipContext(principal, preAuthenticated: true);
        await SeedOwnerWithHostAsync(users, hosts, "owner-sub", "owner@example.com");

        var ex = await Assert.ThrowsAsync<ApiException>(
            () => InvokeAsync(new WisperAuthFilter(WisperRole.Host), context).AsResult());

        Assert.Equal(ApiErrorCode.Forbidden, ex.Code);
        Assert.Equal(0, hosts.ListByOwnerCalls); // scopes-only: ownership is never consulted
    }

    [Fact]
    public async Task Host_ownership_is_resolved_once_across_stacked_host_gates()
    {
        // Two host gates in one request must share the per-request ownership answer (HttpContext.Items), so the
        // repo is queried at most once regardless of how many gates stack (docs/API.md §184, efficiency).
        var principal = WisperPrincipal.Create("owner-sub", "owner@example.com", Array.Empty<string>());
        var (context, _, hosts, users) = NewOwnershipContext(principal);
        await SeedOwnerWithHostAsync(users, hosts, "owner-sub", "owner@example.com");

        await InvokeAsync(new WisperAuthFilter(WisperRole.Host), context);
        var (_, nextCalled) = await InvokeAsync(new WisperAuthFilter(WisperRole.Host), context);

        Assert.True(nextCalled);
        Assert.Equal(1, hosts.ListByOwnerCalls);
    }

    [Fact]
    public async Task Consumer_gate_never_queries_host_ownership()
    {
        // A non-host gate must not add a DB round-trip for host ownership (docs/API.md §184, efficiency): the
        // consumer gate passes on the implicit consumer role alone.
        var principal = WisperPrincipal.Create("owner-sub", "owner@example.com", Array.Empty<string>());
        var (context, _, hosts, users) = NewOwnershipContext(principal);
        await SeedOwnerWithHostAsync(users, hosts, "owner-sub", "owner@example.com");

        var (_, nextCalled) = await InvokeAsync(new WisperAuthFilter(WisperRole.Consumer), context);

        Assert.True(nextCalled);
        Assert.Equal(0, hosts.ListByOwnerCalls);
    }
}

/// <summary>
/// An <see cref="IHostRepository"/> that counts <see cref="ListByOwnerAsync"/> calls (the host-ownership
/// query) and delegates everything else to an in-memory double — so a test can assert ownership is resolved
/// at most once per request.
/// </summary>
internal sealed class CountingHostRepository : IHostRepository
{
    private readonly InMemoryHostRepository _inner = new();

    public int ListByOwnerCalls { get; private set; }

    public Task<IReadOnlyList<Host>> ListByOwnerAsync(Guid ownerUserId, CancellationToken ct = default)
    {
        ListByOwnerCalls++;
        return _inner.ListByOwnerAsync(ownerUserId, ct);
    }

    public Task<Host?> GetByIdAsync(Guid id, CancellationToken ct = default) => _inner.GetByIdAsync(id, ct);

    public Task<IReadOnlyList<Host>> SearchAsync(string? query, int limit, int offset, CancellationToken ct = default) =>
        _inner.SearchAsync(query, limit, offset, ct);

    public Task<int> CountAsync(CancellationToken ct = default) => _inner.CountAsync(ct);

    public Task<IReadOnlyList<Host>> ListOnlineAsync(CancellationToken ct = default) => _inner.ListOnlineAsync(ct);

    public Task<Host?> GetByAgentTokenHashAsync(string agentTokenHash, CancellationToken ct = default) =>
        _inner.GetByAgentTokenHashAsync(agentTokenHash, ct);

    public Task<Host> CreateAsync(Host host, CancellationToken ct = default) => _inner.CreateAsync(host, ct);

    public Task<Host> UpdateAsync(Host host, CancellationToken ct = default) => _inner.UpdateAsync(host, ct);

    public Task<Host?> SetOnlineStateAsync(
        Guid id, HostStatus status, DateTimeOffset? lastSeenAt, DateTimeOffset updatedAt, CancellationToken ct = default) =>
        _inner.SetOnlineStateAsync(id, status, lastSeenAt, updatedAt, ct);

    public Task<Host?> SetAdvertisedIsolationAsync(
        Guid id, IReadOnlyList<string> isolationLevels, string defaultIsolation, DateTimeOffset updatedAt,
        CancellationToken ct = default) =>
        _inner.SetAdvertisedIsolationAsync(id, isolationLevels, defaultIsolation, updatedAt, ct);

    public Task<Host?> SetAdvertisedGpuAsync(
        Guid id, IReadOnlyList<string> gpuClasses, int gpuCount, DateTimeOffset updatedAt, CancellationToken ct = default) =>
        _inner.SetAdvertisedGpuAsync(id, gpuClasses, gpuCount, updatedAt, ct);

    public Task<Host?> SetAdvertisedVersionsAndCapacityAsync(
        Guid id, string? wispVersion, string? agentVersion, int? maxLeases, int? maxStreams,
        DateTimeOffset updatedAt, CancellationToken ct = default) =>
        _inner.SetAdvertisedVersionsAndCapacityAsync(
            id, wispVersion, agentVersion, maxLeases, maxStreams, updatedAt, ct);

    public Task<bool> DeleteAsync(Guid id, CancellationToken ct = default) => _inner.DeleteAsync(id, ct);
}

/// <summary>Adapts the tuple-returning invoke helper to a <see cref="Task"/> for <c>ThrowsAsync</c>.</summary>
internal static class TaskTupleExtensions
{
    public static async Task AsResult(this Task<(object? Result, bool NextCalled)> task) => await task;
}
