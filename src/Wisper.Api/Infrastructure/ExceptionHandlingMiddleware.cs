using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wisper.Api.Infrastructure;

/// <summary>
/// Turns any exception into the uniform error envelope (docs/API.md §3):
/// <see cref="ApiException"/> maps to its declared code/status; anything else is a
/// logged <c>internal</c> 500. The <c>request_id</c> comes from the trace identifier
/// set by <see cref="RequestIdMiddleware"/>.
/// </summary>
public sealed class ExceptionHandlingMiddleware
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;

    public ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (ApiException ex)
        {
            _logger.LogWarning(ex, "api error {Code}", ex.Code);
            await WriteAsync(context, ex.Code, ex.Message, ex.Details);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "unhandled error");
            await WriteAsync(context, ApiErrorCode.Internal, "An unexpected error occurred.", null);
        }
    }

    private async Task WriteAsync(HttpContext context, ApiErrorCode code, string message, object? details)
    {
        if (context.Response.HasStarted)
        {
            // The response is already on the wire; we can't rewrite it as an envelope.
            _logger.LogError("cannot write error envelope: response already started");
            return;
        }

        var (status, wire) = ApiErrors.Map(code);
        context.Response.Clear();
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";

        var envelope = new ErrorEnvelope(new ErrorBody(wire, message, context.TraceIdentifier, details));
        await context.Response.WriteAsync(JsonSerializer.Serialize(envelope, Json));
    }
}
