using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Wisper.Api.Accounts;
using Wisper.Api.Admin;
using Wisper.Api.ApiKeys;
using Wisper.Api.Auth;
using Wisper.Api.Billing;
using Wisper.Api.Catalog;
using Wisper.Api.Hosts;
using Wisper.Api.Infrastructure;
using Wisper.Api.Infrastructure.Idempotency;
using Wisper.Api.Leases;
using Wisper.Api.Ledger;
using Wisper.Api.Metering;
using Wisper.Api.Payments;
using Wisper.Api.Payouts;
using Wisper.Api.Persistence;
using Wisper.Api.Tunnel;
using Wisper.Api.Tunnel.Backplane;

var builder = WebApplication.CreateBuilder(args);

// Structured JSON logging to stdout -- one line per event, UTC, scope-aware so the
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

// Self-serve API keys (docs/API.md §2, §5, P3.x): the /v1/me/api-keys lifecycle -- mint (JWT-only,
// scope-capped, shown once, hash-at-rest), list (prefix only), revoke (idempotent). Backs a machine
// client driving the /v1 surface with one long-lived bearer. Behind the api_keys repository (registered
// by AddWisperPersistence) + the shared clock; no Postgres needed to test.
builder.Services.AddSingleton<ApiKeyService>();

// Consumer catalog (docs/API.md §5, P4.1): reads online hosts and their priced, enabled images by
// joining the host_images allow-list (P2.2) with the live tunnel registry -- the authoritative source
// of host presence. Backs GET /v1/catalog and GET /v1/hosts/:id, both consumer-gated.
builder.Services.AddSingleton<ICatalogService, CatalogService>();

// Consumer leases (docs/API.md §5, P4.2): the POST/GET/DELETE /v1/leases surface that validates a
// requested image against the host's priced allow-list, drives the host over the tunnel relay, and
// persists the leases row (P2.3). The wallet gate (P6.3, docs/PAYMENTS.md §4) is now the real billing
// gate: at POST /leases it enforces the per-user concurrency cap and places a ⌈ttl/60⌉·price hold from
// the wallet (402 insufficient_funds before any lease.create), the meter debits it per tick, and lease
// end releases the remainder -- pure internal ledger against pre-funded wallet money, no Stripe.
builder.Services.AddSingleton<ILeaseWalletGate, WalletLeaseGate>();
builder.Services.AddSingleton<ILeaseService, LeaseService>();

// Consumer interactive shell (docs/API.md §7, P4.4): the store that mints the single-use, ~30s,
// (user,lease)-bound WS tickets behind POST /v1/leases/:id/shell-ticket. Registered by
// AddTunnelBackplane: InMemoryShellTicketStore when single-instance (backplane disabled), or
// RedisShellTicketStore when the Redis backplane is active -- reusing the same IConnectionMultiplexer.

// Metering engine (docs/DATA_MODEL.md §14, docs/PAYMENTS.md §4, P5.1): the manager-authoritative meter
// that accrues billable lease-minutes over healthy intervals and, on a fixed tick (default 60s) and on
// lease end, posts a lease_charge (hold → host_earnings + platform_revenue) + a lease_usage row, idempotent
// on (lease_id, period_start). Internal ledger only -- no Stripe. The background loop is a no-op on a DB-less
// boot; it resumes each active lease from its persisted last_metered_at watermark on restart.
builder.Services.Configure<MeteringOptions>(builder.Configuration.GetSection(MeteringOptions.SectionName));
// The meter caps accrual at each host's last-healthy liveness point (via the live tunnel registry) so a
// blind disconnect window is structurally un-billable (docs/TUNNEL.md §8).
builder.Services.AddSingleton<IMeterLivenessSource, RegistryMeterLivenessSource>();
// Process-local counter of platform-policy fallbacks the metering flush observed (task #206). Wired
// into MeteringService and read from the admin overview so an operator sees the incident without
// tailing logs.
builder.Services.AddSingleton<PolicyFallbackMonitor>();
builder.Services.AddSingleton<MeteringService>();
builder.Services.AddHostedService<MeteringHostedService>();

// Disconnect / grace / reconnect reconciliation (docs/TUNNEL.md §8, P5.2): on tunnel loss the coordinator
// suspends the host's leases at last-healthy (pausing billing) and arms the bounded grace timer; a reconnect
// within the window resumes the still-present leases (same id) and ends the vanished ones (container_lost);
// grace expiry ends the rest (host_disconnect). The pure set-diff/metering logic lives in the reconciler.
builder.Services.AddSingleton<LeaseReconciliationService>();
// Host presence follows the tunnel (docs/TUNNEL.md §3, §8, task #392): tunnel-ready flips the host online
// when it clears the earning gate (owner Connect-enabled OR every enabled image zero-priced), and a durable
// tunnel loss (grace expiry / no-lease close, driven by the coordinator) flips it back offline -- the wiring
// that was missing, leaving a live agent's host stuck offline and absent from the catalog.
builder.Services.AddSingleton<IHostPresence, HostPresenceService>();
// The coordinator resolves ITunnelRelay lazily via a factory to break the DI cycle with TunnelRelay
// (task #73): TunnelRelay optionally depends on the coordinator itself (task #56 lease.ended routing),
// so a direct constructor injection would recurse. Passing the factory defers resolution until the
// coordinator actually needs the relay (an orphan teardown), by which point the graph is fully built.
builder.Services.AddSingleton<TunnelDisconnectCoordinator>(sp => new TunnelDisconnectCoordinator(
    sp.GetRequiredService<LeaseReconciliationService>(),
    sp.GetRequiredService<IOptionsMonitor<TunnelOptions>>(),
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ILogger<TunnelDisconnectCoordinator>>(),
    delay: null,
    presence: sp.GetRequiredService<IHostPresence>(),
    tunnelRelayFactory: () => sp.GetRequiredService<ITunnelRelay>()));
// Durable grace backstop (task #55, docs/TUNNEL.md §8): the in-process grace timer above lives ONLY in
// memory, so a restart / crash / scale-in with any host inside grace strands its leases in `suspended`
// forever (wallet hold never released, host + consumer concurrency slots consumed forever). The sweep
// discovers stale suspended leases via the durable `suspended_at` stamp and ends them as
// host_disconnect on the same finalize path -- CAS-guarded so two instances converge on one transition.
builder.Services.AddHostedService<SuspensionSweepService>();

// Stripe integration (docs/PAYMENTS.md §1, §8, P6.1): the config-driven Stripe client wrapper + webhook
// signature verifier (both behind interfaces, keys from the Stripe config section / secrets manager), and
// the POST /stripe/webhook ingest pipeline -- persist-then-process into stripe_events (PK dedupe), then
// dispatch to idempotent, order-independent handlers with retry-safe failure recording. The payment/
// account/transfer handlers are stubs here; later billing tasks (P6.2+) fill in the ledger effects.
// The dev environment carries Stripe *test* keys (sk_test_/whsec_) in Secrets Manager; unset ⇒ fail-closed.
builder.Services.AddWisperPayments(builder.Configuration);

// Scheduled host payouts (docs/PAYMENTS.md §6, P6.5): a background loop (default daily) that, per host whose
// accrued host_earnings clears the minimum and whose Connect account is enabled, makes a Stripe Transfer
// (idempotency key = payouts.id), writes a payouts row, and posts the `payout` ledger txn (host_earnings →
// platform_cash). A failed transfer retains earnings and retries next run. No-op on a DB-less boot.
builder.Services.AddHostedService<PayoutHostedService>();

// Scheduled ledger reconciliation (docs/DATA_MODEL.md §7e, §14): a background loop that re-derives every
// account's balance from the immutable journal and compares it against the maintained balance cache. Any
// drift is logged and surfaced on the admin overview. Multi-instance safe via a Postgres advisory lock
// so exactly one instance runs each pass. No-op on a DB-less boot.
builder.Services.Configure<LedgerReconcileOptions>(
    builder.Configuration.GetSection(LedgerReconcileOptions.SectionName));
builder.Services.AddSingleton<LedgerReconcileMonitor>();
builder.Services.AddHostedService<LedgerReconcileHostedService>();

// Scheduled idempotency TTL sweep (docs/DATA_MODEL.md §10, §14): a background loop that deletes expired
// idempotency_keys rows so the table doesn't accumulate stale records between low-traffic windows. The
// same rows are also swept lazily on retry; the loop is the proactive backstop. Multi-instance safe via
// a Postgres advisory lock. No-op on a DB-less boot.
builder.Services.Configure<IdempotencySweepOptions>(
    builder.Configuration.GetSection(IdempotencySweepOptions.SectionName));
builder.Services.AddHostedService<IdempotencySweepHostedService>();

// Agent tunnel (docs/TUNNEL.md): operational params from config, the host-token validator
// (config-backed for Phase 1), and the in-memory host registry (singleton -- one live tunnel
// per host, superseded on reconnect).
builder.Services.Configure<TunnelOptions>(builder.Configuration.GetSection(TunnelOptions.SectionName));
// Host-token auth is now DB-backed (docs/TUNNEL.md §13, P7.1): a presented agent token is resolved to its
// host id by a constant-time hashed lookup against the hosts table. The config-backed validator is retained
// only as a dev/bootstrap fallback for a DB-less boot (it is empty, and thus fail-closed, in production).
builder.Services.AddSingleton<ConfigHostTokenValidator>();
builder.Services.AddSingleton<IHostTokenValidator, DbHostTokenValidator>();
// The live host registry + server-side relay (docs/TUNNEL.md §5, §11). AddTunnelBackplane registers the
// in-memory registry + relay directly for a single instance (the default), or -- when Tunnel:Backplane is
// enabled (docs/DESIGN.md §7, P8.1) -- fronts them with the Redis pub/sub backplane so a host tunnel pinned
// to one instance can be driven from any other. Consumers see the same IHostRegistry/ITunnelRelay either way.
builder.Services.AddTunnelBackplane(builder.Configuration);
// IHostCapabilitySource is registered inside AddTunnelBackplane: RegistryHostCapabilitySource (single
// instance) or DistributedHostCapabilitySource (backplane enabled). Force-closes the tunnel on token rotate.
builder.Services.AddSingleton<IAgentTunnelCloser, RegistryAgentTunnelCloser>();

// Host registration + pricing surface (docs/API.md §6, P7.1): register a wisp host and issue its agent token
// once (hash-at-rest), list the caller's hosts with live presence + earnings, rotate the token (revoking the
// old one and closing its tunnel 4402), and manage the priced allow-list validated live against the host's
// advertised wisp capability. Behind interfaces + the persistence repos above; no Postgres/tunnel needed to test.
builder.Services.AddSingleton<HostService>();

// Admin API (docs/API.md §8, P7.2): the admin-group-gated operations surface -- platform overview, the
// versioned platform policy (read + publish), host/user search + suspend/unsuspend moderation, manual
// refunds, the balanced ledger `adjustment` (the only hand-correction of money), the audit trail, and
// read-only ledger forensics. Every admin write records an audit_log row (docs/DATA_MODEL.md §12). Depends
// on the repos + LedgerService/PlatformPolicyService/AuditService (AddWisperPersistence) and BillingService
// (AddWisperPayments) above; no Postgres/Stripe needed to test.
builder.Services.AddSingleton<AdminService>();

var app = builder.Build();

// One loud line naming the persistence backend (docs/DATA_MODEL.md §1): postgres, or the in-memory dev
// mode (no connection string) whose state resets on restart and which must never be used in production.
app.LogPersistenceMode();

// Apply pending DB migrations before serving (docs/DATA_MODEL.md §1). Idempotent; a no-op when
// no database is configured, so the app still boots for the tunnel.
app.ApplyDatabaseMigrations();

// Seed the platform_policy default row for the in-memory dev mode (task #184). Postgres gets the same
// seed via migration 0017 above; this hook keeps the DB-less boot in the same shape so billing never
// throws for lack of a policy. Skipped when the store already carries a version.
await app.SeedInMemoryDefaultsAsync();

// Order is deliberate: the request id is assigned first so it tags every log line
// and every error envelope; the exception handler wraps the whole pipeline.
app.UseMiddleware<RequestIdMiddleware>();
app.UseMiddleware<ExceptionHandlingMiddleware>();

// Raw WebSockets are core to Wisper -- the agent tunnel (docs/TUNNEL.md) and the
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

// DEV-ONLY, money-free lease drive endpoints (Phase-1 test harness). Structurally gated on the
// hosting environment being Development -- the deployed container runs as Production, so these
// endpoints are unreachable in any deployed environment regardless of secret misconfiguration
// (unauthenticated RCE against any connected host). The Tunnel:EnableDevEndpoints flag is still
// honoured on top so a local `dotnet run` can leave them off. Replaced by the real /v1/leases
// surface once accounts land.
var tunnelOptions = app.Services.GetRequiredService<IOptions<TunnelOptions>>().Value;
if (app.Environment.IsDevelopment() && tunnelOptions.EnableDevEndpoints)
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

// Self-serve API-key surface (docs/API.md §5): POST/GET/DELETE /v1/me/api-keys, consumer-gated. Mint is
// JWT-only (a key cannot mint more keys -- privilege containment, §2) and scope-capped by the caller's roles;
// the full key is shown once, stored hashed. List returns the prefix only; revoke is idempotent + 404-scoped.
app.MapApiKeyEndpoints();

// Consumer catalog surface (docs/API.md §5): GET /v1/catalog and GET /v1/hosts/:id, gated on the
// consumer role, listing online hosts and their priced, enabled images from the live tunnel registry.
app.MapCatalogEndpoints();

// Consumer lease surface (docs/API.md §5): POST/GET/DELETE /v1/leases, gated on the consumer role. The
// canonical, authenticated, billed replacement for the /dev/leases Phase-1 harness above.
app.MapLeaseEndpoints();

// Consumer interactive shell surface (docs/API.md §7): POST /v1/leases/:id/shell-ticket (consumer-gated,
// mints a one-time WS ticket) and WS /v1/leases/:id/shell?ticket=… (ticket-authenticated, bridges to the
// tunnel shell stream). The JWT never lands in a URL -- the single-use, short-TTL ticket does.
app.MapShellEndpoints();

// Consumer billing surface (docs/API.md §5, docs/PAYMENTS.md §3): POST /v1/billing/topup (create a
// PaymentIntent, Idempotency-Key required), GET /v1/billing (balance + usage summary), GET
// /v1/billing/transactions (the caller's ledger view, paginated), and POST /v1/billing/payment-methods
// (SetupIntent). Consumer-gated; the wallet is credited only on the payment_intent.succeeded webhook below.
app.MapBillingEndpoints();

// Host registration + pricing surface (docs/API.md §6, P7.1): POST /v1/hosts (register → agent token once +
// manager_ws), GET /v1/hosts/mine, POST /v1/hosts/:id/agent-token (rotate → revoke + close tunnel 4402), and
// GET/PUT /v1/hosts/:id/images + PATCH /v1/hosts/:id/images/:imageId (priced allow-list validated live against
// the host's advertised wisp capability). All host-gated (§6).
app.MapHostEndpoints();

// Host Connect onboarding surface (docs/API.md §6, docs/PAYMENTS.md §5): POST /v1/hosts/connect (create/
// continue Stripe Connect Express onboarding → onboarding_url) and GET /v1/hosts/connect/status
// (connect_status + requirements), both host-gated. account.updated (webhook below) gates going online.
app.MapHostConnectEndpoints();

// Host earnings + payout surface (docs/API.md §6, docs/PAYMENTS.md §6): GET /v1/earnings (accrued + paid),
// GET /v1/earnings/payouts (payout history → Stripe transfer ids), and POST /v1/payouts (on-demand payout,
// Idempotency-Key required, connect_incomplete → 403). Host-gated; the same path the scheduled run uses.
app.MapHostEarningsEndpoints();

// Admin API surface (docs/API.md §8, P7.2): /v1/admin/* -- GET /overview, GET/PUT /policy (versioned,
// audited), GET /hosts · /users (search), POST /hosts|users/:id/suspend|unsuspend (moderation, audited),
// POST /refunds · /adjustments (Idempotency-Key required, audited), GET /audit, and GET
// /ledger/accounts/:id (read-only forensics). All admin-group-gated; every write records an audit_log row.
app.MapAdminEndpoints();

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
