using Npgsql;
using Wisper.Api.Tests.TestSupport;
using Wisper.Api.Domain;
using Wisper.Api.Hosts;
using Wisper.Api.Persistence;
using Wisper.Api.Persistence.Hosts;
using Wisper.Api.Tunnel;
using Xunit;
using Host = Wisper.Api.Domain.Host;

namespace Wisper.Api.Tests.Tunnel;

/// <summary>
/// Unit tests for <see cref="DbHostTokenValidator"/> (docs/TUNNEL.md §13, P7.1): a presented agent token is
/// resolved to its host id by a hashed lookup against the hosts table; an unknown token fails closed; and a
/// DB-less boot degrades to the config allow-list fallback. The <see cref="Db"/> is "configured" with a data
/// source that is never actually opened (the in-memory repository double serves every lookup), so no Postgres
/// is required.
/// </summary>
public class DbHostTokenValidatorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 12, 0, 0, 0, TimeSpan.Zero);

    /// <summary>A configured <see cref="Db"/> whose data source is never opened by these tests.</summary>
    private static Db ConfiguredDb() =>
        new(new NpgsqlDataSourceBuilder("Host=127.0.0.1;Database=none;Username=none").Build());

    private static ConfigHostTokenValidator Config(params (string token, string hostId)[] tokens)
    {
        var options = new TunnelOptions();
        foreach (var (token, hostId) in tokens)
        {
            options.HostTokens[token] = hostId;
        }

        return new ConfigHostTokenValidator(new StaticOptionsMonitor<TunnelOptions>(options));
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
        var validator = new DbHostTokenValidator(hosts, ConfiguredDb(), Config());

        var result = await validator.ValidateAsync(token);

        Assert.True(result.Succeeded);
        Assert.Equal(host.Id.ToString(), result.HostId);
    }

    [Fact]
    public async Task Unknown_token_fails_closed_when_db_configured_and_config_empty()
    {
        var hosts = new InMemoryHostRepository();
        SeedHost(hosts, HostAgentToken.Issue().Token);
        var validator = new DbHostTokenValidator(hosts, ConfiguredDb(), Config());

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
        var validator = new DbHostTokenValidator(hosts, ConfiguredDb(), Config());

        // Rotate the stored hash to a new token.
        var newIssued = HostAgentToken.Issue();
        await hosts.UpdateAsync(host with { AgentTokenHash = newIssued.TokenHash });

        Assert.False((await validator.ValidateAsync(oldToken)).Succeeded);
        var resolved = await validator.ValidateAsync(newIssued.Token);
        Assert.True(resolved.Succeeded);
        Assert.Equal(host.Id.ToString(), resolved.HostId);
    }

    [Fact]
    public async Task Falls_back_to_config_on_a_db_less_boot()
    {
        var hosts = new InMemoryHostRepository();
        // Db.Unconfigured → the DB path is skipped and the config allow-list resolves the token.
        var validator = new DbHostTokenValidator(hosts, Db.Unconfigured, Config(("dev-token", "host-alpha")));

        var result = await validator.ValidateAsync("dev-token");

        Assert.True(result.Succeeded);
        Assert.Equal("host-alpha", result.HostId);
    }

    [Fact]
    public async Task Null_or_empty_token_fails()
    {
        var validator = new DbHostTokenValidator(
            new InMemoryHostRepository(), ConfiguredDb(), Config(("dev-token", "host-alpha")));

        Assert.False((await validator.ValidateAsync(null)).Succeeded);
        Assert.False((await validator.ValidateAsync("")).Succeeded);
    }
}
