using Microsoft.AspNetCore.Http.Features;
using Wisper.Api.Infrastructure;

namespace Wisper.Api.Tunnel;

/// <summary>
/// Relays a live <c>file.read</c> stream to an HTTP client as <c>application/octet-stream</c>
/// (docs/API.md §5): opens the tunnel download, sets <c>Content-Length</c> when the agent reported a
/// size, and pipes each binary frame straight to the response body -- draining credit back to the
/// agent per byte written (docs/TUNNEL.md §9), enforcing a manager-side <c>MaxDownloadBytes</c> cap
/// (413 <c>file_too_large</c>). Shared by the consumer surface (<c>GET /v1/leases/:id/files</c>) and
/// the dev harness (<c>GET /dev/leases/:leaseId/files</c>) so both frame downloads identically.
/// </summary>
internal static class FileDownloadRelay
{
    public const string ContentType = "application/octet-stream";

    /// <summary>
    /// Opens a <c>file.read</c> in <paramref name="leaseId"/> on <paramref name="hostId"/> and streams
    /// the file bytes onto <paramref name="context"/>'s response. Relay-open errors (host_offline,
    /// upstream_timeout, not_found, file_too_large, lease_failed) flow through as
    /// <see cref="ApiException"/> so the uniform envelope wraps them (the response has not been
    /// started yet). A mid-stream size-cap violation is enforced by aborting the response.
    /// </summary>
    public static async Task RelayAsync(
        HttpContext context, ITunnelRelay relay, string hostId, string leaseId, string path,
        long maxDownloadBytes, CancellationToken ct)
    {
        var download = await relay.OpenFileReadAsync(hostId, leaseId, path, ct);

        // A known size that exceeds the cap is a clean pre-body reject: nothing was written yet, so the
        // 413 envelope reaches the client instead of a truncated 200 body.
        if (download.Size >= 0 && download.Size > maxDownloadBytes)
        {
            await download.CloseAsync("cap_exceeded", CancellationToken.None);
            throw new ApiException(
                ApiErrorCode.FileTooLarge,
                "File exceeds the maximum download size.",
                new { max_bytes = maxDownloadBytes, size = download.Size });
        }

        var response = context.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = ContentType;
        if (download.Size >= 0)
        {
            response.ContentLength = download.Size;
        }

        // Immediate flush per chunk so backpressure at the socket propagates back into the credit
        // window without the response buffer holding on to bytes.
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        long written = 0;
        try
        {
            await foreach (var chunk in download.Bytes.ReadAllAsync(ct))
            {
                written += chunk.Length;
                if (written > maxDownloadBytes)
                {
                    // The agent overshot the cap mid-stream; abort so the client sees a truncated
                    // response rather than a silent oversize download. Best-effort abort of the WS
                    // stream too so the agent stops producing.
                    context.Abort();
                    return;
                }

                await response.Body.WriteAsync(chunk, ct);
                await response.Body.FlushAsync(ct);
                await download.AckDrainedAsync(chunk.Length, ct);
            }

            await download.Completion;
        }
        catch (OperationCanceledException)
        {
            // The consumer went away -- nothing more to write.
        }
        finally
        {
            await download.CloseAsync("consumer_closed", CancellationToken.None);
        }
    }
}
