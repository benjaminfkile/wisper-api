using Wisper.Api.Auth;
using Wisper.Api.Domain;
using Wisper.Api.Infrastructure;

namespace Wisper.Api.Catalog;

/// <summary>
/// The consumer catalog surface (docs/API.md §5): <c>GET /v1/catalog</c> lists online hosts and their
/// priced, enabled images with the documented <c>image</c>/<c>network</c>/<c>max_price_cents_per_min</c>
/// filters and cursor pagination (§10), and <c>GET /v1/hosts/:id</c> returns one host's public detail
/// plus its priced images and limits. Both sit behind the <c>consumer</c> gate (every authenticated
/// user is implicitly a consumer, §2). Query parsing/validation happens here at the edge; the join and
/// paging live in <see cref="ICatalogService"/>.
/// </summary>
public static class CatalogEndpoints
{
    /// <summary>Cap and default for the page size (docs/API.md §10).</summary>
    private const int MaxLimit = 100;
    private const int DefaultLimit = 25;

    public static void MapCatalogEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/v1/catalog", ListCatalogAsync).RequireConsumer();
        endpoints.MapGet("/v1/hosts/{id}", GetHostAsync).RequireConsumer();
    }

    private static async Task<IResult> ListCatalogAsync(
        HttpContext http, ICatalogService catalog, CancellationToken ct)
    {
        var query = ParseQuery(http.Request.Query);
        var page = await catalog.ListAsync(query, ct);
        return Results.Json(page);
    }

    private static async Task<IResult> GetHostAsync(
        string id, ICatalogService catalog, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var hostId))
        {
            // A non-uuid id can never name a host; 404 rather than leak the id shape (docs/API.md §3).
            throw new ApiException(ApiErrorCode.NotFound, $"No such host '{id}'.");
        }

        var detail = await catalog.GetHostAsync(hostId, ct);
        if (detail is null)
        {
            throw new ApiException(ApiErrorCode.NotFound, $"No such host '{id}'.");
        }

        return Results.Json(detail);
    }

    /// <summary>
    /// Parses the documented catalog query params into a validated <see cref="CatalogQuery"/>.
    /// Unknown params are ignored (§10); a malformed value of a <i>known</i> filter/pager is a
    /// <c>400 validation_error</c> naming the offending field.
    /// </summary>
    private static CatalogQuery ParseQuery(IQueryCollection q)
    {
        return new CatalogQuery
        {
            ImageRef = NonEmpty(q["image"]),
            Network = ParseNetwork(q["network"]),
            MaxPriceCentsPerMin = ParseMaxPrice(q["max_price_cents_per_min"]),
            MinGpus = ParseMinGpus(q["min_gpus"]),
            GpuClass = NonEmpty(q["gpu_class"]),
            Limit = ParseLimit(q["limit"]),
            Cursor = ParseCursor(q["cursor"]),
        };
    }

    private static string? NonEmpty(string? value) =>
        string.IsNullOrEmpty(value) ? null : value;

    private static NetworkMode? ParseNetwork(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (!Enum.TryParse<NetworkMode>(value, ignoreCase: true, out var mode) ||
            !Enum.IsDefined(mode))
        {
            throw new ApiException(
                ApiErrorCode.ValidationError,
                "Unknown 'network' filter.",
                new { field = "network", allowed = new[] { "none", "open", "egress" } });
        }

        return mode;
    }

    private static long? ParseMaxPrice(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (!long.TryParse(value, out var max) || max < 0)
        {
            throw new ApiException(
                ApiErrorCode.ValidationError,
                "'max_price_cents_per_min' must be a non-negative integer.",
                new { field = "max_price_cents_per_min" });
        }

        return max;
    }

    private static int? ParseMinGpus(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (!int.TryParse(value, out var min) || min < 0)
        {
            throw new ApiException(
                ApiErrorCode.ValidationError,
                "'min_gpus' must be a non-negative integer.",
                new { field = "min_gpus" });
        }

        return min;
    }

    private static int ParseLimit(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return DefaultLimit;
        }

        if (!int.TryParse(value, out var limit) || limit < 1 || limit > MaxLimit)
        {
            throw new ApiException(
                ApiErrorCode.ValidationError,
                $"'limit' must be an integer between 1 and {MaxLimit}.",
                new { field = "limit", max = MaxLimit });
        }

        return limit;
    }

    private static CatalogCursor? ParseCursor(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (!CatalogCursor.TryParse(value, out var cursor))
        {
            throw new ApiException(
                ApiErrorCode.ValidationError, "'cursor' is malformed.", new { field = "cursor" });
        }

        return cursor;
    }
}
