using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Wisper.Api.Auth;
using Wisper.Api.Infrastructure;
using Wisper.Api.Tests.TestSupport;
using Xunit;

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
}

/// <summary>Adapts the tuple-returning invoke helper to a <see cref="Task"/> for <c>ThrowsAsync</c>.</summary>
internal static class TaskTupleExtensions
{
    public static async Task AsResult(this Task<(object? Result, bool NextCalled)> task) => await task;
}
