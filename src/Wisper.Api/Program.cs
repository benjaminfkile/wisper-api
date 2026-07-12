using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Wisper.Api.Accounts;
using Wisper.Api.Auth;
using Wisper.Api.Catalog;
using Wisper.Api.Infrastructure;
using Wisper.Api.Leases;
using Wisper.Api.Metering;
using Wisper.Api.Payments;
using Wisper.Api.Persistence;
using Wisper.Api.Tunnel;

var builder = WebApplication.CreateBuilder(args);

// Structured JSON logging to stdout — one line per event, UTC, scope-aware so the
// request id (below) rides along on every log line. Matches the fleet's log shape.
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
    options.UseUtcTimestamp = true;
});

// PostgreSQL persistence (docs/DATA_MODEL.md §1): pooled data source from ConnectionStrings:Wisper,
// the DbUp migration runner, and the DB health probe. Boots DB-less (tunnel-only) when unset.
builder.Services.AddWisperPersistence(builder.Configuration);

// Cognito JWT auth (docs/API.md §2): validates Bearer tokens against the pool's JWKS behind
// IJwtValidator, and provides the RequireRole/RequireConsumer/RequireHost/RequireAdmin route-group
// gates. Config-driven (Auth section); fails closed when unconfigured. Endpoints land in P3.2+.
builder.Services.AddWisperAuth(builder.Configuration);

// Accounts (docs/API.md §2, §5, P3.2): the service that bootstraps the users row from the JWT on
// first authenticated call and backs the /v1/me identity/profile endpoints. Depends on the users
// repository (P2.2) and the shared clock, both registered by AddWisperPersistence above.
builder.Services.AddSingleton<IUserAccountService, UserAccountService>();

// Consumer catalog (docs/API.md §5, P4.1): reads online hosts and their priced, enabled images by
// joining the host_images allow-list (P2.2) with the live tunnel registry — the authoritative source
// of host presence. Backs GET /v1/catalog and GET /v1/hosts/:id, both consumer-gated.
builder.Services.AddSingleton<ICatalogService, CatalogService>();

// Consumer leases (docs/API.md §5, P4.2): the POST/GET/DELETE /v1/leases surface that validates a
// requested image against the host's priced allow-list, drives the host over the tunnel relay, and
// persists the leases row (P2.3). The wallet gate is a Phase-6 hook (P6.3) that ALLOWS FOR NOW —
// AllowAllLeaseWalletGate places no hold; the real balance-checking gate swaps in without touching
// anything above it.
builder.Services.AddSingleton<ILeaseWalletGate, AllowAllLeaseWalletGate>();
builder.Services.AddSingleton<ILeaseService, LeaseService>();

// Consumer interactive shell (docs/API.md §7, P4.4): the store that mints the single-use, ~30s,
// (user,lease)-bound WS tickets behind POST /v1/leases/:id/shell-ticket. In-memory for now (a shell
// ticket only needs to survive the seconds until the handshake on the same instance); a Redis-backed
// store lands with the multi-instance backplane (P8.1).
builder.Services.AddSingleton<IShellTicketStore, InMemoryShellTicketStore>();

// Metering engine (docs/DATA_MODEL.md §14, docs/PAYMENTS.md §4, P5.1): the manager-authoritative meter
// that accrues billable lease-minutes over healthy intervals and, on a fixed tick (default 60s) and on
// lease end, posts a lease_charge (hold → host_earnings + platform_revenue) + a lease_usage row, idempotent
// on (lease_id, period_start). Internal ledger only — no Stripe. The background loop is a no-op on a DB-less
// boot; it resumes each active lease from its persisted last_metered_at watermark on restart.
builder.Services.Configure<MeteringOptions>(builder.Configuration.GetSection(MeteringOptions.SectionName));
// The meter caps accrual at each host's last-healthy liveness point (via the live tunnel registry) so a
// blind disconnect window is structurally un-billable (docs/TUNNEL.md §8).
builder.Services.AddSingleton<IMeterLivenessSource, RegistryMeterLivenessSource>();
builder.Services.AddSingleton<MeteringService>();
builder.Services.AddHostedService<MeteringHostedService>();

// Disconnect / grace / reconnect reconciliation (docs/TUNNEL.md §8, P5.2): on tunnel loss the coordinator
// suspends the host's leases at last-healthy (pausing billing) and arms the bounded grace timer; a reconnect
// within the window resumes the still-present leases (same id) and ends the vanished ones (container_lost);
// grace expiry ends the rest (host_disconnect). The pure set-diff/metering logic lives in the reconciler.
builder.Services.AddSingleton<LeaseReconciliationService>();
builder.Services.AddSingleton<TunnelDisconnectCoordinator>();

// Stripe integration (docs/PAYMENTS.md §1, §8, P6.1): the config-driven Stripe client wrapper + webhook
// signature verifier (both behind interfaces, keys from the Stripe config section / secrets manager), and
// the POST /stripe/webhook ingest pipeline — persist-then-process into stripe_events (PK dedupe), then
// dispatch to idempotent, order-independent handlers with retry-safe failure recording. The payment/
// account/transfer handlers are stubs here; later billing tasks (P6.2+) fill in the ledger effects.
builder.Services.AddWisperPayments(builder.Configuration);

// Agent tunnel (docs/TUNNEL.md): operational params from config, the host-token validator
// (config-backed for Phase 1), and the in-memory host registry (singleton — one live tunnel
// per host, superseded on reconnect).
builder.Services.Configure<TunnelOptions>(builder.Configuration.GetSection(TunnelOptions.SectionName));
builder.Services.AddSingleton<IHostTokenValidator, ConfigHostTokenValidator>();
builder.Services.AddSingleton<IHostRegistry, InMemoryHostRegistry>();
// The server-side relay that drives a connected host (lease lifecycle + sync exec),
// correlating agent responses by rid/leaseId (docs/TUNNEL.md §5, §11).
builder.Services.AddSingleton<ITunnelRelay, TunnelRelay>();

var app = builder.Build();

// Apply pending DB migrations before serving (docs/DATA_MODEL.md §1). Idempotent; a no-op when
// no database is configured, so the app still boots for the tunnel.
app.ApplyDatabaseMigrations();

// Order is deliberate: the request id is assigned first so it tags every log line
// and every error envelope; the exception handler wraps the whole pipeline.
app.UseMiddleware<RequestIdMiddleware>();
app.UseMiddleware<ExceptionHandlingMiddleware>();

// Raw WebSockets are core to Wisper — the agent tunnel (docs/TUNNEL.md) and the
// consumer console relay (docs/API.md §7) both live on this server. Enable here so
// later work only has to map endpoints.
app.UseWebSockets();

// Liveness + readiness. `/api/health` is the fleet convention the gateway's aggregated health
// check calls; `/healthz` is the name used in docs/API.md §4. Both are unauthenticated. The
// report includes the DB probe (docs/DATA_MODEL.md §1), which degrades gracefully when no
// database is configured, so a tunnel-only boot still reports `ok`. Unhealthy → 503.
var healthService = app.Services.GetRequiredService<HealthCheckService>();
var health = async (CancellationToken ct) =>
{
    var report = await healthService.CheckHealthAsync(ct);
    var status = report.Status switch
    {
        HealthStatus.Healthy => "ok",
        HealthStatus.Degraded => "degraded",
        _ => "error",
    };
    var checks = report.Entries.ToDictionary(
        e => e.Key,
        e => new { status = e.Value.Status.ToString().ToLowerInvariant(), description = e.Value.Description });
    var httpStatus = report.Status == HealthStatus.Unhealthy ? 503 : 200;
    return Results.Json(new { status, checks }, statusCode: httpStatus);
};
app.MapGet("/api/health", health);
app.MapGet("/healthz", health);

// The host agent tunnel (docs/TUNNEL.md §3). Unversioned, raw WebSocket, alongside /healthz.
app.MapAgentTunnel();

// DEV-ONLY, money-free lease drive endpoints (Phase-1 test harness). Only mapped when
// Tunnel:EnableDevEndpoints is true; replaced by the real /v1/leases surface once accounts land.
var tunnelOptions = app.Services.GetRequiredService<IOptions<TunnelOptions>>().Value;
if (tunnelOptions.EnableDevEndpoints)
{
    app.MapDevLeaseEndpoints();
    // Raw-WebSocket interactive shell harness (docs/API.md §7); replaced by WS /v1/leases/:id/shell.
    app.MapDevShellEndpoints();
}

// Versioned API root (docs/API.md §1). Concrete consumer/host/admin endpoints are
// added by later work; this proves the versioned surface is wired.
var v1 = app.MapGroup("/v1");
v1.MapGet("/", () => Results.Json(new { service = "wisper-api", version = "v1" }));

// Consumer account surface (docs/API.md §5): GET/PATCH /v1/me, gated on the consumer role, which
// bootstraps the caller's users row on first authenticated call.
app.MapMeEndpoints();

// Consumer catalog surface (docs/API.md §5): GET /v1/catalog and GET /v1/hosts/:id, gated on the
// consumer role, listing online hosts and their priced, enabled images from the live tunnel registry.
app.MapCatalogEndpoints();

// Consumer lease surface (docs/API.md §5): POST/GET/DELETE /v1/leases, gated on the consumer role. The
// canonical, authenticated, billed replacement for the /dev/leases Phase-1 harness above.
app.MapLeaseEndpoints();

// Consumer interactive shell surface (docs/API.md §7): POST /v1/leases/:id/shell-ticket (consumer-gated,
// mints a one-time WS ticket) and WS /v1/leases/:id/shell?ticket=… (ticket-authenticated, bridges to the
// tunnel shell stream). The JWT never lands in a URL — the single-use, short-TTL ticket does.
app.MapShellEndpoints();

// Stripe webhook (docs/API.md §4, docs/PAYMENTS.md §8): POST /stripe/webhook, unauthenticated but
// signature-verified (no JWT), sitting alongside /healthz. Verifies the Stripe-Signature, dedupes via the
// stripe_events PK, and dispatches to the idempotent handler registry.
app.MapStripeWebhookEndpoints();

// Any unmatched route returns the uniform error envelope (docs/API.md §3) rather
// than an empty 404, so clients always get a consistent error shape.
app.MapFallback((HttpContext ctx) =>
    throw new ApiException(ApiErrorCode.NotFound, $"No route for {ctx.Request.Method} {ctx.Request.Path}"));

app.Run();

// Exposed so the integration tests can host the app via WebApplicationFactory<Program>.
public partial class Program;
