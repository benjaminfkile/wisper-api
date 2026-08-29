using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Wisper.Api.Infrastructure;
using Wisper.Api.Tunnel.Messages;

namespace Wisper.Api.Tunnel;

/// <summary>
/// DEV-ONLY, money-free lease drive endpoints -- a Phase-1 test harness for exercising the
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
        DevCreateLeaseRequest request, ITunnelRelay relay, IHostRegistry registry, CancellationToken ct)
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
            Env = request.Env,
            Isolation = request.Isolation ?? Domain.HostIsolation.Shared,
        };

        var lease = await relay.CreateLeaseAsync(request.HostId, spec, ct);

        // Surface the target host's advertised container OS ("linux"|"windows"), read from the live
        // hello capability the tunnel registry tracks for this dev host id (a plain string like
        // 'dev-host-1', so we key the registry directly rather than by Guid). Null when the host has no
        // live tunnel or its (older) agent advertised no os -- surfacing only, back-compatible (task #316).
        var os = registry.TryGet(request.HostId, out var connection) ? connection?.Capability?.Os : null;
        return Results.Json(
            new { leaseId = lease.LeaseId, wispContractId = lease.WispContractId, status = lease.Status, os },
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
            await ExecStreamSse.RelayAsync(context, relay, request.HostId, leaseId, command, ct);
            return Results.Empty;
        }

        var result = await relay.ExecAsync(request.HostId, leaseId, command, ct);
        return Results.Json(new { stdout = result.Stdout, stderr = result.Stderr, exit_code = result.ExitCode });
    }

    private static async Task<IResult> ReleaseAsync(
        string leaseId, HttpContext context, ITunnelRelay relay, CancellationToken ct)
    {
        // hostId may come from the query string or a JSON body -- support both for the harness.
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
    [property: JsonPropertyName("userdata")] string? Userdata,
    [property: JsonPropertyName("env")] Dictionary<string, string>? Env = null,
    [property: JsonPropertyName("isolation")] string? Isolation = null);

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
