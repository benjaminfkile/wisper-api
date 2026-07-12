using Microsoft.Extensions.Options;
using Wisper.Api.Tunnel;
using Xunit;

namespace Wisper.Api.Tests.Tunnel;

/// <summary>
/// Unit tests for <see cref="ConfigHostTokenValidator"/>: token → host-id resolution and the
/// fail-closed behavior when no tokens are configured (docs/TUNNEL.md §13).
/// </summary>
public class ConfigHostTokenValidatorTests
{
    private static ConfigHostTokenValidator Build(params (string token, string hostId)[] tokens)
    {
        var options = new TunnelOptions();
        foreach (var (token, hostId) in tokens)
        {
            options.HostTokens[token] = hostId;
        }

        return new ConfigHostTokenValidator(new StaticOptionsMonitor(options));
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
    [InlineData("tok-a ")]     // trailing space — not a byte-for-byte match
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

    private sealed class StaticOptionsMonitor : IOptionsMonitor<TunnelOptions>
    {
        public StaticOptionsMonitor(TunnelOptions value) => CurrentValue = value;

        public TunnelOptions CurrentValue { get; }

        public TunnelOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<TunnelOptions, string?> listener) => null;
    }
}
