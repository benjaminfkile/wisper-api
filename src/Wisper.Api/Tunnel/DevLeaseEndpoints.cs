using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Wisper.Api.Infrastructure;
using Wisper.Api.Tunnel.Messages;

namespace Wisper.Api.Tunnel;

/// <summary>
/// DEV-ONLY, money-free lease drive endpoints — a Phase-1 test harness for exercising the
/// tunnel relay end-to-end before accounts/billing exist. There is <b>no</b> auth, no wallet
/// gate, and no idempotency here; the caller names the target host directly. These are gated
/// behind <see cref="TunnelOptions.EnableDevEndpoints"/> (off by default) and are replaced by
/// the real consumer <c>/v1/leases</c> surface (docs/API.md §5) once accounts/billing land.
/// </summary>
public static class DevLeaseEndpoints
{
    public static void MapDevLeaseEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/dev/leases", CreateLeaseAsync);
        endpoints.MapPost("/dev/leases/{leaseId}/exec", ExecAsync);
        endpoints.MapDelete("/dev/leases/{leaseId}", ReleaseAsync);
    }

    private static async Task<IResult> CreateLeaseAsync(
        DevCreateLeaseRequest request, ITunnelRelay relay, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.HostId))
        {
            throw new ApiException(ApiErrorCode.ValidationError, "hostId is required");
        }

        var resources = request.Resources ?? new DevResources();
        var spec = new LeaseCreate
        {
            Image = request.Image ?? string.Empty,
            Network = request.Network ?? "none",
            Resources = new LeaseResources
            {
                Cpus = resources.Cpus,
                MemoryMb = resources.MemoryMb,
                Pids = resources.Pids,
            },
            TtlSeconds = request.TtlSeconds,
            Userdata = request.Userdata,
        };

        var lease = await relay.CreateLeaseAsync(request.HostId, spec, ct);
        return Results.Json(
            new { leaseId = lease.LeaseId, wispContractId = lease.WispContractId, status = lease.Status },
            statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> ExecAsync(
        string leaseId, DevExecRequest request, HttpContext context, ITunnelRelay relay, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.HostId))
        {
            throw new ApiException(ApiErrorCode.ValidationError, "hostId is required");
        }

        var command = request.Command ?? string.Empty;

        // ?stream=1 → streamed exec over SSE (docs/API.md §7); anything else is the sync exec.
        if (context.Request.Query["stream"] == "1")
        {
            await StreamExecAsync(context, relay, request.HostId, leaseId, command, ct);
            return Results.Empty;
        }

        var result = await relay.ExecAsync(request.HostId, leaseId, command, ct);
        return Results.Json(new { stdout = result.Stdout, stderr = result.Stderr, exit_code = result.ExitCode });
    }

    /// <summary>
    /// Runs <paramref name="command"/> as a streamed exec and relays its live output as Server-Sent
    /// Events (docs/API.md §7): an <c>event: chunk</c> per stdout/stderr chunk
    /// (<c>{"stream":"stdout"|"stderr","data":"…"}</c>), a terminal <c>event: exit</c>
    /// (<c>{"exit_code":N}</c>), or an <c>event: error</c> (<c>{"error":"…"}</c>) on failure. Each
    /// event is flushed immediately, and credit flows back to the agent as chunks are drained
    /// (docs/TUNNEL.md §9).
    /// </summary>
    private static async Task StreamExecAsync(
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
            // host_offline / upstream_timeout etc. — the exec never opened; report a terminal error.
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
            // The consumer went away — nothing more to write.
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

    private static async Task<IResult> ReleaseAsync(
        string leaseId, HttpContext context, ITunnelRelay relay, CancellationToken ct)
    {
        // hostId may come from the query string or a JSON body — support both for the harness.
        var hostId = context.Request.Query["hostId"].ToString();
        if (string.IsNullOrEmpty(hostId) && (context.Request.ContentLength ?? 0) > 0)
        {
            var body = await context.Request.ReadFromJsonAsync<DevReleaseRequest>(ct);
            hostId = body?.HostId ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(hostId))
        {
            throw new ApiException(ApiErrorCode.ValidationError, "hostId is required");
        }

        await relay.ReleaseAsync(hostId, leaseId, ct);
        return Results.Ok();
    }
}

/// <summary>Body of <c>POST /dev/leases</c> (mixed casing mirrors the tunnel wire, docs/TUNNEL.md §5).</summary>
public sealed record DevCreateLeaseRequest(
    [property: JsonPropertyName("hostId")] string HostId,
    [property: JsonPropertyName("image")] string? Image,
    [property: JsonPropertyName("network")] string? Network,
    [property: JsonPropertyName("resources")] DevResources? Resources,
    [property: JsonPropertyName("ttl_seconds")] int TtlSeconds,
    [property: JsonPropertyName("userdata")] string? Userdata);

/// <summary>Resource request block (snake_case, forwarded to wisp).</summary>
public sealed record DevResources(
    [property: JsonPropertyName("cpus")] double Cpus = 0,
    [property: JsonPropertyName("memory_mb")] int MemoryMb = 0,
    [property: JsonPropertyName("pids")] int Pids = 0);

/// <summary>Body of <c>POST /dev/leases/{leaseId}/exec</c>.</summary>
public sealed record DevExecRequest(
    [property: JsonPropertyName("hostId")] string HostId,
    [property: JsonPropertyName("command")] string? Command);

/// <summary>Optional JSON body of <c>DELETE /dev/leases/{leaseId}</c>.</summary>
public sealed record DevReleaseRequest(
    [property: JsonPropertyName("hostId")] string HostId);
