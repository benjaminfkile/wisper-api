using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Wisper.Api.Tests.TestSupport;
using Wisper.Api.Domain;
using Wisper.Api.Hosts;
using Wisper.Api.Persistence.Hosts;
using Wisper.Api.Tunnel;
using Xunit;
using Host = Wisper.Api.Domain.Host;

namespace Wisper.Api.Tests.Tunnel;

/// <summary>
/// Unit tests for <see cref="DbHostTokenValidator"/> (docs/TUNNEL.md §13, P7.1): a presented agent token is
/// resolved to its host id by a hashed lookup against the host store; an unknown token fails closed; and a
/// token the store does not hold degrades to the config allow-list fallback. The in-memory repository double
/// serves every lookup, so no Postgres is required.
/// </summary>
public class DbHostTokenValidatorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 12, 0, 0, 0, TimeSpan.Zero);

    private static ConfigHostTokenValidator Config(params (string token, string hostId)[] tokens)
        => Config(Environments.Development, tokens);

    private static ConfigHostTokenValidator Config(
        string environmentName,
        params (string token, string hostId)[] tokens)
    {
        var options = new TunnelOptions();
        foreach (var (token, hostId) in tokens)
        {
            options.HostTokens[token] = hostId;
        }

        return new ConfigHostTokenValidator(
            new StaticOptionsMonitor<TunnelOptions>(options),
            new FakeHostEnvironment(environmentName));
    }

    private static Host SeedHost(InMemoryHostRepository hosts, string token)
    {
        var issuedHash = HostAgentToken.Hash(token);
        return hosts.CreateAsync(new Host
        {
            OwnerUserId = Guid.NewGuid(),
            Status = HostStatus.Offline,
            AgentTokenHash = issuedHash,
            AgentTokenPrefix = "wht_live_seed",
            CreatedAt = T0,
            UpdatedAt = T0,
        }).Result;
    }

    [Fact]
    public async Task Resolves_a_known_token_to_its_host_id_by_hash()
    {
        var hosts = new InMemoryHostRepository();
        var token = HostAgentToken.Issue().Token;
        var host = SeedHost(hosts, token);
        var validator = new DbHostTokenValidator(hosts, Config());

        var result = await validator.ValidateAsync(token);

        Assert.True(result.Succeeded);
        Assert.Equal(host.Id.ToString(), result.HostId);
    }

    [Fact]
    public async Task Unknown_token_fails_closed_when_store_has_no_match_and_config_empty()
    {
        var hosts = new InMemoryHostRepository();
        SeedHost(hosts, HostAgentToken.Issue().Token);
        var validator = new DbHostTokenValidator(hosts, Config());

        var result = await validator.ValidateAsync("wht_live_not-a-real-token");

        Assert.False(result.Succeeded);
        Assert.Null(result.HostId);
    }

    [Fact]
    public async Task Rotated_token_stops_resolving_and_new_one_resolves()
    {
        var hosts = new InMemoryHostRepository();
        var oldToken = HostAgentToken.Issue().Token;
        var host = SeedHost(hosts, oldToken);
        var validator = new DbHostTokenValidator(hosts, Config());

        // Rotate the stored hash to a new token.
        var newIssued = HostAgentToken.Issue();
        await hosts.UpdateAsync(host with { AgentTokenHash = newIssued.TokenHash });

        Assert.False((await validator.ValidateAsync(oldToken)).Succeeded);
        var resolved = await validator.ValidateAsync(newIssued.Token);
        Assert.True(resolved.Succeeded);
        Assert.Equal(host.Id.ToString(), resolved.HostId);
    }

    [Fact]
    public async Task Falls_back_to_config_when_the_store_does_not_hold_the_token()
    {
        var hosts = new InMemoryHostRepository();
        // The store has no matching host → the lookup misses and the config allow-list resolves the token.
        var validator = new DbHostTokenValidator(hosts, Config(("dev-token", "host-alpha")));

        var result = await validator.ValidateAsync("dev-token");

        Assert.True(result.Succeeded);
        Assert.Equal("host-alpha", result.HostId);
    }

    [Fact]
    public async Task Null_or_empty_token_fails()
    {
        var validator = new DbHostTokenValidator(
            new InMemoryHostRepository(), Config(("dev-token", "host-alpha")));

        Assert.False((await validator.ValidateAsync(null)).Succeeded);
        Assert.False((await validator.ValidateAsync("")).Succeeded);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task Static_fallback_is_not_consulted_outside_Development(string environment)
    {
        // A DB-issued token (via the store) MUST still resolve -- the env gate only closes the
        // static fallback, not the DB path (task #39).
        var hosts = new InMemoryHostRepository();
        var dbToken = HostAgentToken.Issue().Token;
        var host = SeedHost(hosts, dbToken);

        // The config carries a static token that WOULD resolve in Development. Outside Development
        // it must fail closed -- a deployed secret can no longer mint a long-lived host bearer.
        var validator = new DbHostTokenValidator(hosts, Config(environment, ("static-dev-token", "host-static")));

        var staticResult = await validator.ValidateAsync("static-dev-token");
        Assert.False(staticResult.Succeeded);
        Assert.Null(staticResult.HostId);

        var dbResult = await validator.ValidateAsync(dbToken);
        Assert.True(dbResult.Succeeded);
        Assert.Equal(host.Id.ToString(), dbResult.HostId);
    }

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public FakeHostEnvironment(string environmentName) => EnvironmentName = environmentName;

        public string EnvironmentName { get; set; }
        public string ApplicationName { get; set; } = "Wisper.Api.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
