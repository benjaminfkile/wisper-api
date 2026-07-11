using Wisper.Api.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// Structured JSON logging to stdout — one line per event, UTC, scope-aware so the
// request id (below) rides along on every log line. Matches the fleet's log shape.
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
    options.UseUtcTimestamp = true;
});

builder.Services.AddHealthChecks();

var app = builder.Build();

// Order is deliberate: the request id is assigned first so it tags every log line
// and every error envelope; the exception handler wraps the whole pipeline.
app.UseMiddleware<RequestIdMiddleware>();
app.UseMiddleware<ExceptionHandlingMiddleware>();

// Raw WebSockets are core to Wisper — the agent tunnel (docs/TUNNEL.md) and the
// consumer console relay (docs/API.md §7) both live on this server. Enable here so
// later work only has to map endpoints.
app.UseWebSockets();

// Liveness. `/api/health` is the fleet convention the gateway's aggregated health
// check calls; `/healthz` is the name used in docs/API.md §4. Both are unauthenticated.
var health = () => Results.Json(new { status = "ok" });
app.MapGet("/api/health", health);
app.MapGet("/healthz", health);

// Versioned API root (docs/API.md §1). Concrete consumer/host/admin endpoints are
// added by later work; this proves the versioned surface is wired.
var v1 = app.MapGroup("/v1");
v1.MapGet("/", () => Results.Json(new { service = "wisper-api", version = "v1" }));

// Any unmatched route returns the uniform error envelope (docs/API.md §3) rather
// than an empty 404, so clients always get a consistent error shape.
app.MapFallback((HttpContext ctx) =>
    throw new ApiException(ApiErrorCode.NotFound, $"No route for {ctx.Request.Method} {ctx.Request.Path}"));

app.Run();

// Exposed so the integration tests can host the app via WebApplicationFactory<Program>.
public partial class Program;
