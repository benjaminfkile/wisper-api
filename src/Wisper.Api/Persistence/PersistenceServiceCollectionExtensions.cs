using Dapper;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Wisper.Api.Audit;
using Wisper.Api.Infrastructure.Idempotency;
using Wisper.Api.Ledger;
using Wisper.Api.Persistence.ApiKeys;
using Wisper.Api.Persistence.Audit;
using Wisper.Api.Persistence.HostImages;
using Wisper.Api.Persistence.Hosts;
using Wisper.Api.Persistence.Idempotency;
using Wisper.Api.Persistence.Leases;
using Wisper.Api.Persistence.Payouts;
using Wisper.Api.Persistence.Policy;
using Wisper.Api.Persistence.Stripe;
using Wisper.Api.Persistence.Users;
using Wisper.Api.Policy;

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

        // Consumer API keys — long-lived machine bearers for the /v1 surface (docs/DATA_MODEL.md §3). The
        // storage + token mechanics only; the auth gate that resolves a presented key lands next.
        services.AddSingleton<IApiKeyRepository, ApiKeyRepository>();

        // Lease + metering repositories (docs/DATA_MODEL.md §5, §6).
        services.AddSingleton<ILeaseRepository, LeaseRepository>();
        services.AddSingleton<ILeaseUsageRepository, LeaseUsageRepository>();

        // The double-entry ledger — the money source of truth (docs/DATA_MODEL.md §7, §8). The Dapper
        // store leans on the schema's triggers as defense-in-depth; the LedgerService enforces the same
        // invariants in C# and is what billing (P6) posts through. Unit tests use the in-memory store.
        services.AddSingleton<ILedgerStore, LedgerStore>();
        services.AddSingleton<LedgerService>();

        // Stripe/idempotency/policy/audit infra (docs/DATA_MODEL.md §9–§12). The webhook dedupe store, host
        // payouts, API idempotency, the versioned platform policy, and the append-only audit log. A shared
        // clock backs the TTL/versioning/timestamp logic and is swappable in tests.
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IStripeEventRepository, StripeEventRepository>();
        services.AddSingleton<IPayoutRepository, PayoutRepository>();
        services.AddSingleton<IIdempotencyKeyRepository, IdempotencyKeyRepository>();
        services.AddSingleton<IPlatformPolicyRepository, PlatformPolicyRepository>();
        services.AddSingleton<IAuditLogRepository, AuditLogRepository>();

        // The helpers over those repos: Idempotency-Key replay/conflict/lock (§10), the active-policy reader
        // (§11), and the audit recorder (§12). Wired now; the money-mutating endpoints call them in P6+.
        services.AddSingleton<IdempotencyService>();
        services.AddSingleton<PlatformPolicyService>();
        services.AddSingleton<AuditService>();

        // The day-one fraud guards (docs/PAYMENTS.md §7): first-top-up hold + new-account top-up velocity +
        // per-user daily spend cap, all read from platform_policy and enforced at top-up (BillingService) and
        // lease start (WalletLeaseGate). Depends on the ledger, lease repo, active policy, and the clock.
        services.AddSingleton<FraudGuardService>();

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
