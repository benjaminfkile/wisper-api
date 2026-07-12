using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Wisper.Api.Auth;

/// <summary>
/// DI wiring and route-group role-gating helpers for Cognito JWT auth (docs/API.md §2).
/// <see cref="AddWisperAuth"/> registers the validator and JWKS key provider;
/// <see cref="RequireRole{TBuilder}"/> (and the per-role shortcuts) attach the
/// <see cref="WisperAuthFilter"/> to a route group or endpoint.
/// </summary>
public static class WisperAuthExtensions
{
    /// <summary>
    /// Registers Cognito JWT auth: binds <see cref="CognitoAuthOptions"/> from the
    /// <c>Auth</c> section, the JWKS-backed <see cref="ISigningKeyProvider"/> (singleton, so
    /// its key cache is shared), and the <see cref="IJwtValidator"/>. Boots fine when unset —
    /// the validator then fails closed (docs/API.md §2).
    /// </summary>
    public static IServiceCollection AddWisperAuth(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<CognitoAuthOptions>(configuration.GetSection(CognitoAuthOptions.SectionName));

        // A shared clock backs the JWKS cache TTL and is swappable in tests.
        services.TryAddSingleton(TimeProvider.System);
        services.AddHttpClient();

        // Singleton so the JWKS key cache survives across requests.
        services.TryAddSingleton<ISigningKeyProvider, JwksSigningKeyProvider>();
        services.TryAddSingleton<IJwtValidator, CognitoJwtValidator>();

        return services;
    }

    /// <summary>Gates the group/endpoint on the minimum <paramref name="role"/>.</summary>
    public static TBuilder RequireRole<TBuilder>(this TBuilder builder, WisperRole role)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.AddEndpointFilter(new WisperAuthFilter(role));
        return builder;
    }

    /// <summary>Requires an authenticated user (implicitly a <c>consumer</c>, docs/API.md §2).</summary>
    public static TBuilder RequireConsumer<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder => builder.RequireRole(WisperRole.Consumer);

    /// <summary>Requires the <c>host</c> role.</summary>
    public static TBuilder RequireHost<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder => builder.RequireRole(WisperRole.Host);

    /// <summary>Requires the <c>admin</c> role.</summary>
    public static TBuilder RequireAdmin<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder => builder.RequireRole(WisperRole.Admin);
}
