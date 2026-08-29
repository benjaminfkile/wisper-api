using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Wisper.Api.Tunnel;
using Xunit;

namespace Wisper.Api.Tests.Tunnel;

/// <summary>
/// Unit tests for <see cref="ConfigHostTokenValidator"/>: token → host-id resolution, the
/// fail-closed behavior when no tokens are configured (docs/TUNNEL.md §13), and the environment
/// gate that fails closed outside Development regardless of the configured allow-list -- the
/// static fallback is a local-dev convenience, not a deployed trust anchor (task #39).
/// </summary>
public class ConfigHostTokenValidatorTests
{
    private static ConfigHostTokenValidator Build(params (string token, string hostId)[] tokens)
        => Build(Environments.Development, tokens);

    private static ConfigHostTokenValidator Build(
        string environmentName,
        params (string token, string hostId)[] tokens)
    {
        var options = new TunnelOptions();
        foreach (var (token, hostId) in tokens)
        {
            options.HostTokens[token] = hostId;
        }

        return new ConfigHostTokenValidator(new StaticOptionsMonitor(options), new FakeEnv(environmentName));
    }

    [Fact]
    public async Task Known_token_resolves_to_host_id()
    {
        var validator = Build(("tok-a", "host-a"), ("tok-b", "host-b"));

        var result = await validator.ValidateAsync("tok-b");

        Assert.True(result.Succeeded);
        Assert.Equal("host-b", result.HostId);
    }

    [Theory]
    [InlineData("tok-a ")]     // trailing space -- not a byte-for-byte match
    [InlineData("TOK-A")]      // wrong case
    [InlineData("unknown")]
    public async Task Unknown_token_fails(string token)
    {
        var validator = Build(("tok-a", "host-a"));

        var result = await validator.ValidateAsync(token);

        Assert.False(result.Succeeded);
        Assert.Null(result.HostId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Null_or_empty_token_fails(string? token)
    {
        var validator = Build(("tok-a", "host-a"));

        Assert.False((await validator.ValidateAsync(token)).Succeeded);
    }

    [Fact]
    public async Task Fails_closed_when_no_tokens_configured()
    {
        var validator = Build();

        Assert.False((await validator.ValidateAsync("anything")).Succeeded);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("QA")]
    public async Task Fails_closed_outside_Development_even_when_the_token_matches(string environment)
    {
        // A statically-configured token that WOULD resolve in Development…
        var validator = Build(environment, ("dev-token", "host-alpha"));

        // …is rejected in any non-Development environment, so a deployed secret carrying static
        // Tunnel:HostTokens can no longer mint a long-lived host bearer (task #39).
        var result = await validator.ValidateAsync("dev-token");

        Assert.False(result.Succeeded);
        Assert.Null(result.HostId);
    }

    private sealed class StaticOptionsMonitor : IOptionsMonitor<TunnelOptions>
    {
        public StaticOptionsMonitor(TunnelOptions value) => CurrentValue = value;

        public TunnelOptions CurrentValue { get; }

        public TunnelOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<TunnelOptions, string?> listener) => null;
    }

    private sealed class FakeEnv : IHostEnvironment
    {
        public FakeEnv(string environmentName) => EnvironmentName = environmentName;

        public string EnvironmentName { get; set; }
        public string ApplicationName { get; set; } = "Wisper.Api.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
