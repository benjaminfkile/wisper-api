using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Wisper.Api.Infrastructure;

namespace Wisper.Api.Tunnel;

/// <summary>
/// Relays a <b>streamed</b> exec's live output to an HTTP client as Server-Sent Events (docs/API.md §7):
/// an <c>event: chunk</c> per stdout/stderr chunk (<c>{"stream":"stdout"|"stderr","data":"…"}</c>), a
/// terminal <c>event: exit</c> (<c>{"exit_code":N}</c>), or an <c>event: error</c> (<c>{"error":"…"}</c>)
/// when the exec can't open or ends abnormally. Each event is flushed immediately, and credit flows back
/// to the agent as chunks are drained (docs/TUNNEL.md §9). Shared by the dev harness
/// (<c>/dev/leases/:id/exec?stream=1</c>) and the consumer surface (<c>/v1/leases/:id/exec?stream=1</c>)
/// so both frame the stream identically over the same relay exec-stream path.
/// </summary>
internal static class ExecStreamSse
{
    /// <summary>
    /// Opens a streamed exec of <paramref name="command"/> in <paramref name="leaseId"/> on
    /// <paramref name="hostId"/> and writes its output as SSE onto <paramref name="context"/>'s response.
    /// The caller must have already validated the request (ownership/ready state) -- once this starts the
    /// response is <c>200 text/event-stream</c>, so relay failures are reported as an <c>error</c> event,
    /// not the uniform error envelope.
    /// </summary>
    public static async Task RelayAsync(
        HttpContext context, ITunnelRelay relay, string hostId, string leaseId, string command, CancellationToken ct)
    {
        var response = context.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        // SSE must reach the client event-by-event, not batched by the server's response buffer.
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        ITunnelExec exec;
        try
        {
            exec = await relay.OpenExecStreamAsync(hostId, leaseId, command, ct);
        }
        catch (ApiException ex)
        {
            // host_offline / upstream_timeout etc. -- the exec never opened; report a terminal error.
            var (_, wire) = ApiErrors.Map(ex.Code);
            await WriteEventAsync(response, "error", new { error = wire }, ct);
            return;
        }

        try
        {
            await foreach (var chunk in exec.Output.ReadAllAsync(ct))
            {
                var stream = chunk.Channel == Channels.Stderr ? "stderr" : "stdout";
                // JSON-encode the bytes as a UTF-8 string so embedded newlines survive SSE framing.
                var text = Encoding.UTF8.GetString(chunk.Data);
                await WriteEventAsync(response, "chunk", new { stream, data = text }, ct);
                // Credit is granted only as the bytes are actually drained (docs/TUNNEL.md §9).
                await exec.AckDrainedAsync(chunk.Data.Length, ct);
            }

            await exec.Completion;

            if (exec.ExitCode is { } code)
            {
                await WriteEventAsync(response, "exit", new { exit_code = code }, ct);
            }
            else
            {
                var reason = exec.ClosedReason ?? "stream_closed";
                await WriteEventAsync(response, "error", new { error = reason }, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // The consumer went away -- nothing more to write.
        }
        finally
        {
            await exec.CloseAsync("consumer_closed", CancellationToken.None);
        }
    }

    /// <summary>Writes one SSE event (<c>event:</c> + JSON <c>data:</c> + blank line) and flushes it.</summary>
    private static async Task WriteEventAsync(HttpResponse response, string eventName, object data, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(data);
        var frame = $"event: {eventName}\ndata: {json}\n\n";
        await response.WriteAsync(frame, ct);
        await response.Body.FlushAsync(ct);
    }
}
