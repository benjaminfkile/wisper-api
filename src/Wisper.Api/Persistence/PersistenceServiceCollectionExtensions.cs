using Dapper;
using Npgsql;
using Wisper.Api.Persistence.HostImages;
using Wisper.Api.Persistence.Hosts;
using Wisper.Api.Persistence.Leases;
using Wisper.Api.Persistence.Users;

namespace Wisper.Api.Persistence;

/// <summary>
/// Wiring for the persistence layer (docs/DATA_MODEL.md §1). Builds a pooled
/// <see cref="NpgsqlDataSource"/> from <c>ConnectionStrings:Wisper</c> when present, registers the
/// shared <see cref="Db"/> handle and the DB health probe, and — via
/// <see cref="ApplyDatabaseMigrations"/> — runs DbUp migrations at startup. When no connection
/// string is configured the app still boots (the tunnel needs no DB): <see cref="Db"/> is
/// unconfigured, migrations are skipped, and the health probe degrades gracefully.
/// </summary>
public static class PersistenceServiceCollectionExtensions
{
    /// <summary>
    /// Registers the persistence services. Reads the connection string from
    /// <c>ConnectionStrings:Wisper</c> (env <c>ConnectionStrings__Wisper</c>) and binds
    /// <see cref="PersistenceOptions"/> from the <c>Persistence</c> section.
    /// </summary>
    public static IServiceCollection AddWisperPersistence(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<PersistenceOptions>(configuration.GetSection(PersistenceOptions.SectionName));

        // The schema is snake_case (docs/DATA_MODEL.md); let Dapper map snake_case columns to the
        // PascalCase entity properties so repositories don't have to alias every scalar column.
        DefaultTypeMap.MatchNamesWithUnderscores = true;

        var connectionString = configuration.GetConnectionString(PersistenceOptions.ConnectionStringName);

        services.AddSingleton(sp =>
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                return Db.Unconfigured;
            }

            var dataSource = new NpgsqlDataSourceBuilder(connectionString).Build();
            return new Db(dataSource);
        });

        // Identity + catalog repositories (docs/DATA_MODEL.md §3, §4). The Dapper implementations are
        // registered for the running service; unit tests use the in-memory doubles directly. They only
        // open a connection when a query runs, so they are safe to register on a DB-less boot.
        services.AddSingleton<IUserRepository, UserRepository>();
        services.AddSingleton<IHostRepository, HostRepository>();
        services.AddSingleton<IHostImageRepository, HostImageRepository>();

        // Lease + metering repositories (docs/DATA_MODEL.md §5, §6).
        services.AddSingleton<ILeaseRepository, LeaseRepository>();
        services.AddSingleton<ILeaseUsageRepository, LeaseUsageRepository>();

        // Extend the health surface with the DB probe (degrades gracefully when no DB — see DbHealthCheck).
        services.AddHealthChecks().AddCheck<DbHealthCheck>(DbHealthCheck.Name);

        return services;
    }

    /// <summary>
    /// Applies pending DbUp migrations at startup when a database is configured and
    /// <see cref="PersistenceOptions.RunMigrationsAtStartup"/> is set. Idempotent and a no-op when
    /// no database is configured, so the app still boots for the tunnel.
    /// </summary>
    public static void ApplyDatabaseMigrations(this IHost app)
    {
        var db = app.Services.GetRequiredService<Db>();
        var options = app.Services.GetRequiredService<
            Microsoft.Extensions.Options.IOptions<PersistenceOptions>>().Value;
        var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger(typeof(MigrationRunner).FullName!);

        if (!db.IsConfigured)
        {
            logger.LogInformation("no database configured — skipping migrations (tunnel-only boot)");
            return;
        }

        if (!options.RunMigrationsAtStartup)
        {
            logger.LogInformation("RunMigrationsAtStartup is disabled — skipping migrations");
            return;
        }

        var connectionString = app.Services.GetRequiredService<IConfiguration>()
            .GetConnectionString(PersistenceOptions.ConnectionStringName)!;

        logger.LogInformation("applying database migrations ({Count} embedded script(s))",
            MigrationRunner.DiscoverScripts().Count);
        var result = MigrationRunner.Run(connectionString, logger);
        logger.LogInformation("database migrations up to date ({Applied} applied this run)",
            result.Scripts.Count());
    }
}
