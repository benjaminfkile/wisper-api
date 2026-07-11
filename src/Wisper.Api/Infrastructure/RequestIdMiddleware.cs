namespace Wisper.Api.Infrastructure;

/// <summary>
/// Assigns a request id (from an inbound <c>X-Request-Id</c> or a fresh one),
/// echoes it on the response, sets it as the trace identifier, and pushes it into
/// the logging scope so every log line for the request carries it (docs/API.md §12).
/// </summary>
public sealed class RequestIdMiddleware
{
    public const string HeaderName = "X-Request-Id";

    private readonly RequestDelegate _next;
    private readonly ILogger<RequestIdMiddleware> _logger;

    public RequestIdMiddleware(RequestDelegate next, ILogger<RequestIdMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var inbound = context.Request.Headers[HeaderName].ToString();
        var requestId = string.IsNullOrWhiteSpace(inbound) ? Guid.NewGuid().ToString("N") : inbound;

        context.TraceIdentifier = requestId;
        context.Response.Headers[HeaderName] = requestId;

        using (_logger.BeginScope(new Dictionary<string, object> { ["request_id"] = requestId }))
        {
            await _next(context);
        }
    }
}
