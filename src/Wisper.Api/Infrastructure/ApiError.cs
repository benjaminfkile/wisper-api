using System.Net;

namespace Wisper.Api.Infrastructure;

/// <summary>
/// Typed API error codes. Each maps to a stable wire string and HTTP status
/// (see docs/API.md §3). Add codes here as the surface grows.
/// </summary>
public enum ApiErrorCode
{
    ValidationError,
    Unauthenticated,
    Forbidden,
    ConnectIncomplete,
    NotFound,
    PaymentRequired,
    InsufficientFunds,
    Conflict,
    HostOffline,
    LeaseNotReady,
    AtCapacity,
    ImageNotAllowed,
    RateLimited,
    UpstreamTimeout,
    Internal,
}

/// <summary>Maps <see cref="ApiErrorCode"/> to its wire string and HTTP status.</summary>
public static class ApiErrors
{
    public static (int Status, string Wire) Map(ApiErrorCode code) => code switch
    {
        ApiErrorCode.ValidationError   => ((int)HttpStatusCode.BadRequest, "validation_error"),
        ApiErrorCode.Unauthenticated   => ((int)HttpStatusCode.Unauthorized, "unauthenticated"),
        ApiErrorCode.Forbidden         => ((int)HttpStatusCode.Forbidden, "forbidden"),
        ApiErrorCode.ConnectIncomplete => ((int)HttpStatusCode.Forbidden, "connect_incomplete"),
        ApiErrorCode.NotFound          => ((int)HttpStatusCode.NotFound, "not_found"),
        ApiErrorCode.PaymentRequired   => ((int)HttpStatusCode.PaymentRequired, "payment_required"),
        ApiErrorCode.InsufficientFunds => ((int)HttpStatusCode.PaymentRequired, "insufficient_funds"),
        ApiErrorCode.Conflict          => ((int)HttpStatusCode.Conflict, "conflict"),
        ApiErrorCode.HostOffline       => ((int)HttpStatusCode.Conflict, "host_offline"),
        ApiErrorCode.LeaseNotReady     => ((int)HttpStatusCode.Conflict, "lease_not_ready"),
        ApiErrorCode.AtCapacity        => ((int)HttpStatusCode.Conflict, "at_capacity"),
        ApiErrorCode.ImageNotAllowed   => ((int)HttpStatusCode.BadRequest, "image_not_allowed"),
        ApiErrorCode.RateLimited       => ((int)HttpStatusCode.TooManyRequests, "rate_limited"),
        ApiErrorCode.UpstreamTimeout   => ((int)HttpStatusCode.GatewayTimeout, "upstream_timeout"),
        ApiErrorCode.Internal          => ((int)HttpStatusCode.InternalServerError, "internal"),
        _                              => ((int)HttpStatusCode.InternalServerError, "internal"),
    };
}

/// <summary>
/// A handled application error. The <see cref="ExceptionHandlingMiddleware"/>
/// turns it into the uniform error envelope with the right HTTP status.
/// </summary>
public sealed class ApiException : Exception
{
    public ApiErrorCode Code { get; }
    public object? Details { get; }

    public ApiException(ApiErrorCode code, string message, object? details = null)
        : base(message)
    {
        Code = code;
        Details = details;
    }
}

/// <summary>The wire shape of an error (docs/API.md §3): <c>{ "error": { ... } }</c>.</summary>
public sealed record ErrorEnvelope(ErrorBody Error);

public sealed record ErrorBody(string Code, string Message, string RequestId, object? Details);
