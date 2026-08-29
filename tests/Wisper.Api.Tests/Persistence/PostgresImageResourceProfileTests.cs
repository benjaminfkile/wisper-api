using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Wisper.Api.Domain;
using Wisper.Api.Persistence;
using Wisper.Api.Persistence.HostImages;
using Wisper.Api.Persistence.Hosts;
using Wisper.Api.Persistence.Users;
using Wisper.Api.Tests.TestSupport;
using Xunit;
using Host = Wisper.Api.Domain.Host;

namespace Wisper.Api.Tests.Persistence;

/// <summary>
/// Migration <c>0014_ImageResourceProfile</c> (task #569) applied end to end against a <b>real</b>
/// Postgres-backed <see cref="HostImageRepository"/> on a throwaway server (<see cref="EphemeralPostgres"/>).
/// The in-memory double can never prove the DDL is right -- that the nullable <c>cpus</c>/<c>memory_mb</c>
/// columns exist and that <c>max_gpus</c> was renamed to <c>gpus</c> -- so this drives a sized offer through the
/// SQL store and asserts the profile round-trips (both a fully-sized offer and a NULL cpu/memory profile).
/// <para>
/// This stands up a real server, so it is gated behind the explicit <c>WISPER_RUN_PG_TESTS</c> opt-in
/// (task #558). When it is unset -- the default, including normal CI -- the fixture reports unavailable and each
/// test is reported <b>skipped</b> (a visible <c>[SkippableFact]</c> skip), so the suite stays deterministically
/// green. Set <c>WISPER_RUN_PG_TESTS=1</c> to run it for real (this repo ships PostgreSQL 15).
/// </para>
/// </summary>
public sealed class PostgresImageResourceProfileTests
    : IClassFixture<PostgresImageResourceProfileTests.PgFixture>
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 9, 0, 0, 0, TimeSpan.Zero);

    private readonly PgFixture _pg;

    public PostgresImageResourceProfileTests(PgFixture pg) => _pg = pg;

    [SkippableFact]
    public async Task Sized_offer_profile_round_trips_through_the_real_schema()
    {
        Skip.IfNot(_pg.Available, PgFixture.SkipReason); // visible skip unless WISPER_RUN_PG_TESTS opted in

        var db = new Db(_pg.DataSource!);
        var images = new HostImageRepository(db);
        var hostId = await SeedHostAsync(db);

        // A fully-sized offer: exact cpus/memory_mb and an exact gpus count (formerly the max_gpus ceiling).
        var created = await images.CreateAsync(new HostImage
        {
            HostId = hostId,
            ImageRef = "reg/wisp-base:latest",
            PriceCentsPerMin = 5,
            Networks = new[] { NetworkMode.None, NetworkMode.Open },
            MaxTtlSeconds = 3600,
            Cpus = 2,
            MemoryMb = 4096,
            Gpus = 3,
            Enabled = true,
            CreatedAt = T0,
            UpdatedAt = T0,
        });

        var stored = await images.GetByIdAsync(created.Id);
        Assert.NotNull(stored);
        Assert.Equal(2, stored!.Cpus);
        Assert.Equal(4096, stored.MemoryMb);
        Assert.Equal(3, stored.Gpus);

        // NULL cpus/memory_mb means "the host's own per-lease policy default applies downstream" (§4) -- the
        // nullable columns really are nullable in the applied schema.
        var unsized = await images.CreateAsync(new HostImage
        {
            HostId = hostId,
            ImageRef = "reg/wisp-base:unsized",
            PriceCentsPerMin = 0,
            Networks = new[] { NetworkMode.None },
            MaxTtlSeconds = 3600,
            Cpus = null,
            MemoryMb = null,
            Gpus = 0,
            Enabled = true,
            CreatedAt = T0,
            UpdatedAt = T0,
        });

        var storedUnsized = await images.GetByIdAsync(unsized.Id);
        Assert.NotNull(storedUnsized);
        Assert.Null(storedUnsized!.Cpus);
        Assert.Null(storedUnsized.MemoryMb);
        Assert.Equal(0, storedUnsized.Gpus);
    }

    private static async Task<Guid> SeedHostAsync(Db db)
    {
        var users = new UserRepository(db);
        var hosts = new HostRepository(db);
        var ownerId = Guid.NewGuid();
        await users.CreateAsync(new User
        {
            Id = ownerId,
            CognitoSub = $"owner-{ownerId:N}",
            Email = $"owner-{ownerId:N}@example.test",
            Status = UserStatus.Active,
            CreatedAt = T0,
            UpdatedAt = T0,
        });
        var host = await hosts.CreateAsync(new Host
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerId,
            Name = "home-server-1",
            Label = "us",
            Status = HostStatus.Online,
            AgentTokenHash = "hash",
            CreatedAt = T0,
            UpdatedAt = T0,
        });
        return host.Id;
    }

    /// <summary>
    /// Class fixture: stands up one throwaway server and runs the migrations once for the whole class. Unless
    /// the <c>WISPER_RUN_PG_TESTS</c> opt-in is set it reports <see cref="Available"/> = <c>false</c> and the
    /// tests report a visible skip.
    /// </summary>
    public sealed class PgFixture : IAsyncLifetime
    {
        public const string SkipReason =
            "ephemeral-Postgres regression is opt-in: set WISPER_RUN_PG_TESTS=1 to run it (task #558)";

        private EphemeralPostgres? _server;

        public NpgsqlDataSource? DataSource { get; private set; }

        public bool Available => DataSource is not null;

        public async Task InitializeAsync()
        {
            _server = await EphemeralPostgres.TryStartAsync();
            if (_server is null)
            {
                return;
            }

            // The Dapper snake_case↔PascalCase mapping the repositories rely on (set at app wiring).
            DefaultTypeMap.MatchNamesWithUnderscores = true;

            MigrationRunner.Run(_server.ConnectionString, NullLogger.Instance);
            DataSource = new NpgsqlDataSourceBuilder(_server.ConnectionString).Build();
        }

        public async Task DisposeAsync()
        {
            if (DataSource is not null)
            {
                await DataSource.DisposeAsync();
            }

            if (_server is not null)
            {
                await _server.DisposeAsync();
            }
        }
    }
}
