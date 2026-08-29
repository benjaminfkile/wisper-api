using Dapper;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Wisper.Api.Audit;
using Wisper.Api.Infrastructure.Idempotency;
using Wisper.Api.Ledger;
using Wisper.Api.Persistence.ApiKeys;
using Wisper.Api.Persistence.Audit;
using Wisper.Api.Persistence.BillingIncidents;
using Wisper.Api.Persistence.HostImages;
using Wisper.Api.Persistence.Hosts;
using Wisper.Api.Persistence.Idempotency;
using Wisper.Api.Persistence.Leases;
using Wisper.Api.Persistence.Payouts;
using Wisper.Api.Persistence.Policy;
using Wisper.Api.Persistence.Stripe;
using Wisper.Api.Persistence.Users;
using Wisper.Api.Policy;
using Wisper.Api.Domain;

namespace Wisper.Api.Persistence;

/// <summary>
/// Wiring for the persistence layer (docs/DATA_MODEL.md §1). Reads <c>ConnectionStrings:Wisper</c> and
/// picks one of two backends for <b>every</b> repository:
/// <list type="bullet">
///   <item><b>Postgres</b> (a connection string is present, the production path): the Dapper repositories
///   over a pooled <see cref="NpgsqlDataSource"/>, with DbUp migrations run at startup
///   (<see cref="ApplyDatabaseMigrations"/>) and the live DB health probe.</item>
///   <item><b>In-memory</b> (no connection string, an explicit dev mode): the same in-memory repository
///   doubles the test suite uses, promoted to first class so the whole <c>/v1</c> path runs with no
///   Postgres. <see cref="Db"/> is unconfigured, migrations are a no-op, and the health probe reports
///   <c>in-memory</c>. <b>State resets on every restart -- never for production.</b></item>
/// </list>
/// The higher-level services over the repositories (ledger, idempotency, policy, audit, fraud) and the
/// health-check registration are backend-agnostic; only the repository set differs.
/// </summary>
public static class PersistenceServiceCollectionExtensions
{
    /// <summary>
    /// Registers the persistence services. Reads the connection string from
    /// <c>ConnectionStrings:Wisper</c> (env <c>ConnectionStrings__Wisper</c>) and binds
    /// <see cref="PersistenceOptions"/> from the <c>Persistence</c> section. When the connection string is
    /// unset it registers the in-memory repository doubles for every interface (the DB-less dev mode);
    /// otherwise the Postgres repositories. Production behaviour with a connection string is unchanged.
    /// </summary>
    public static IServiceCollection AddWisperPersistence(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<PersistenceOptions>(configuration.GetSection(PersistenceOptions.SectionName));

        // The schema is snake_case (docs/DATA_MODEL.md); let Dapper map snake_case columns to the
        // PascalCase entity properties so repositories don't have to alias every scalar column.
        DefaultTypeMap.MatchNamesWithUnderscores = true;

        var connectionString = configuration.GetConnectionString(PersistenceOptions.ConnectionStringName);
        var inMemory = string.IsNullOrWhiteSpace(connectionString);

        services.AddSingleton(sp =>
        {
            if (inMemory)
            {
                return Db.Unconfigured;
            }

            var dataSource = new NpgsqlDataSourceBuilder(connectionString).Build();
            return new Db(dataSource);
        });

        // Pick the repository backend. With no connection string the in-memory doubles back every
        // interface so the full /v1 path runs without Postgres (state resets on restart); otherwise the
        // Dapper/Postgres repositories. Everything below this is identical either way.
        if (inMemory)
        {
            AddInMemoryRepositories(services);
        }
        else
        {
            AddPostgresRepositories(services);
        }

        // The LedgerService enforces the double-entry invariants in C# and is what billing (P6) posts
        // through, over whichever ILedgerStore backend was registered above (docs/DATA_MODEL.md §7, §8).
        services.AddSingleton<LedgerService>();

        // A shared clock backs the TTL/versioning/timestamp logic and is swappable in tests.
        services.TryAddSingleton(TimeProvider.System);

        // The helpers over those repos: Idempotency-Key replay/conflict/lock (§10), the active-policy reader
        // (§11), and the audit recorder (§12). Wired now; the money-mutating endpoints call them in P6+.
        services.AddSingleton<IdempotencyService>();
        services.AddSingleton<PlatformPolicyService>();
        services.AddSingleton<AuditService>();

        // The day-one fraud guards (docs/PAYMENTS.md §7): first-top-up hold + new-account top-up velocity +
        // per-user daily spend cap, all read from platform_policy and enforced at top-up (BillingService) and
        // lease start (WalletLeaseGate). Depends on the ledger, lease repo, active policy, and the clock.
        services.AddSingleton<FraudGuardService>();

        // Extend the health surface with the DB probe (reports in-memory in the dev mode -- see DbHealthCheck).
        services.AddHealthChecks().AddCheck<DbHealthCheck>(DbHealthCheck.Name);

        return services;
    }

    /// <summary>
    /// Registers the Dapper/Postgres repositories over the pooled <see cref="Db"/> -- the production path
    /// (docs/DATA_MODEL.md §3-§12). They only open a connection when a query runs.
    /// </summary>
    private static void AddPostgresRepositories(IServiceCollection services)
    {
        // Identity + catalog repositories (docs/DATA_MODEL.md §3, §4).
        services.AddSingleton<IUserRepository, UserRepository>();
        services.AddSingleton<IHostRepository, HostRepository>();
        services.AddSingleton<IHostImageRepository, HostImageRepository>();

        // Consumer API keys -- long-lived machine bearers for the /v1 surface (docs/DATA_MODEL.md §3).
        services.AddSingleton<IApiKeyRepository, ApiKeyRepository>();

        // Lease + metering repositories (docs/DATA_MODEL.md §5, §6).
        services.AddSingleton<ILeaseRepository, LeaseRepository>();
        services.AddSingleton<ILeaseUsageRepository, LeaseUsageRepository>();

        // The double-entry ledger store -- the money source of truth (docs/DATA_MODEL.md §7, §8). The Dapper
        // store leans on the schema's triggers as defense-in-depth.
        services.AddSingleton<ILedgerStore, LedgerStore>();

        // Stripe/idempotency/policy/audit infra (docs/DATA_MODEL.md §9-§12): the webhook dedupe store, host
        // payouts, API idempotency, the versioned platform policy, and the append-only audit log.
        services.AddSingleton<IStripeEventRepository, StripeEventRepository>();
        services.AddSingleton<IPayoutRepository, PayoutRepository>();
        services.AddSingleton<IIdempotencyKeyRepository, IdempotencyKeyRepository>();
        services.AddSingleton<IPlatformPolicyRepository, PlatformPolicyRepository>();
        services.AddSingleton<IAuditLogRepository, AuditLogRepository>();

        // Persistent platform-policy fallback signal (task #210, docs/PAYMENTS.md §4). Backs the
        // admin overview's fallback_count / last_fallback_* + the ack endpoint so the badge is
        // durable across restarts and visible on every instance.
        services.AddSingleton<IPolicyFallbackStore, PolicyFallbackStore>();
    }

    /// <summary>
    /// Registers the in-memory repository doubles for every interface -- the DB-less dev mode
    /// (docs/DATA_MODEL.md §1). These are the same doubles the test suite drives; here they are promoted to
    /// first class so a boot with no connection string still serves the full <c>/v1</c> path. Every double
    /// keeps its state for the process lifetime only, so the store is empty again on the next restart.
    /// </summary>
    private static void AddInMemoryRepositories(IServiceCollection services)
    {
        services.AddSingleton<IUserRepository, InMemoryUserRepository>();
        services.AddSingleton<IHostRepository, InMemoryHostRepository>();
        services.AddSingleton<IHostImageRepository, InMemoryHostImageRepository>();
        services.AddSingleton<IApiKeyRepository, InMemoryApiKeyRepository>();
        services.AddSingleton<ILeaseRepository, InMemoryLeaseRepository>();
        services.AddSingleton<ILeaseUsageRepository, InMemoryLeaseUsageRepository>();
        services.AddSingleton<ILedgerStore, InMemoryLedgerStore>();
        services.AddSingleton<IStripeEventRepository, InMemoryStripeEventRepository>();
        services.AddSingleton<IPayoutRepository, InMemoryPayoutRepository>();
        services.AddSingleton<IIdempotencyKeyRepository, InMemoryIdempotencyKeyRepository>();
        services.AddSingleton<IPlatformPolicyRepository, InMemoryPlatformPolicyRepository>();
        services.AddSingleton<IAuditLogRepository, InMemoryAuditLogRepository>();
        services.AddSingleton<IPolicyFallbackStore, InMemoryPolicyFallbackStore>();
    }

    /// <summary>
    /// Emits the single, loud startup line that names the persistence backend (docs/DATA_MODEL.md §1). The
    /// in-memory dev mode is logged at <b>warning</b> so an accidental production boot with no connection
    /// string is unmissable in the logs; the Postgres path logs an ordinary informational line.
    /// </summary>
    public static void LogPersistenceMode(this IHost app)
    {
        var db = app.Services.GetRequiredService<Db>();
        var logger = app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(PersistenceServiceCollectionExtensions).FullName!);

        if (db.IsConfigured)
        {
            logger.LogInformation("persistence: postgres (connection string configured)");
        }
        else
        {
            logger.LogWarning(
                "persistence: in-memory (no connection string) -- state resets on restart");
        }
    }

    /// <summary>
    /// Seeds the platform_policy default row in the in-memory dev mode when the store is still empty
    /// (task #184). Postgres gets the same seed via migration 0017 (idempotent); the in-memory doubles
    /// have no migration runner, so this hook is what makes billing work on a fresh DB-less boot. The
    /// insert is skipped when the store already carries at least one version, so it never overrides an
    /// admin-published policy. No-op when a database is configured (the migration is the source of truth
    /// there).
    /// </summary>
    public static async Task SeedInMemoryDefaultsAsync(this IHost app, CancellationToken ct = default)
    {
        var db = app.Services.GetRequiredService<Db>();
        if (db.IsConfigured)
        {
            return; // Postgres seeds via migration 0017; nothing to do here.
        }

        var policies = app.Services.GetRequiredService<IPlatformPolicyRepository>();
        var existing = await policies.ListAsync(ct);
        if (existing.Count > 0)
        {
            return;
        }

        // Same conservative defaults as migration 0017: fee_bps=0 (no platform cut until an admin sets one)
        // and every optional cap left NULL/0 ("no restriction"). effective_from is now so it is the active
        // version immediately; created_by is NULL to mark it as a system seed.
        await policies.AppendAsync(new PlatformPolicy { FeeBps = 0 }, ct);
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
            logger.LogInformation("no database configured -- skipping migrations (tunnel-only boot)");
            return;
        }

        if (!options.RunMigrationsAtStartup)
        {
            logger.LogInformation("RunMigrationsAtStartup is disabled -- skipping migrations");
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
