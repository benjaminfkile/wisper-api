using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Wisper.Api.Audit;
using Wisper.Api.Infrastructure.Idempotency;
using Wisper.Api.Persistence.Audit;
using Wisper.Api.Persistence.Idempotency;
using Wisper.Api.Persistence.Payouts;
using Wisper.Api.Persistence.Policy;
using Wisper.Api.Persistence.Stripe;
using Wisper.Api.Policy;
using Xunit;

namespace Wisper.Api.Tests.Infrastructure;

/// <summary>
/// The §9-§12 repositories and helpers must be wired into DI and constructible from the real app host
/// (WebApplicationFactory&lt;Program&gt;) -- including the optional-constructor helpers that take a shared
/// <see cref="TimeProvider"/> -- so the service composes even on a DB-less boot.
/// </summary>
public class InfraServicesRegistrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public InfraServicesRegistrationTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public void The_infra_repositories_and_helpers_resolve()
    {
        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;

        Assert.NotNull(sp.GetRequiredService<IStripeEventRepository>());
        Assert.NotNull(sp.GetRequiredService<IPayoutRepository>());
        Assert.NotNull(sp.GetRequiredService<IIdempotencyKeyRepository>());
        Assert.NotNull(sp.GetRequiredService<IPlatformPolicyRepository>());
        Assert.NotNull(sp.GetRequiredService<IAuditLogRepository>());

        Assert.NotNull(sp.GetRequiredService<IdempotencyService>());
        Assert.NotNull(sp.GetRequiredService<PlatformPolicyService>());
        Assert.NotNull(sp.GetRequiredService<AuditService>());
    }
}
