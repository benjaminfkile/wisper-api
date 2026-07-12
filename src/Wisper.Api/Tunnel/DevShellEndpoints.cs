using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Wisper.Api.Infrastructure;

namespace Wisper.Api.Tunnel;

/// <summary>
/// DEV-ONLY, money-free interactive shell endpoint — a Phase-1 test harness for driving a tunnel
/// shell stream end-to-end before accounts/billing/tickets exist. It maps a raw WebSocket
/// <c>GET /dev/leases/{leaseId}/shell</c> and bridges the consumer socket to a tunnel shell
/// stream via the shared <see cref="ShellBridge"/>, honouring per-stream credit flow control
/// (docs/TUNNEL.md §9) both ways. Gated behind <see cref="TunnelOptions.EnableDevEndpoints"/> (off by
/// default). This mirrors the framing of the real <c>WS /v1/leases/:id/shell</c> (docs/API.md §7),
/// which replaces it once accounts land — so there is no auth, no ticket, and the caller names the
/// host directly via a query parameter.
/// </summary>
public static class DevShellEndpoints
{
    private const string LogCategory = "Wisper.Api.Tunnel.DevShell";

    public static void MapDevShellEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/dev/leases/{leaseId}/shell", HandleAsync);
    }

    private static async Task HandleAsync(string leaseId, HttpContext context)
    {
        var logger = context.RequestServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger(LogCategory);

        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var hostId = context.Request.Query["hostId"].ToString();
        if (string.IsNullOrWhiteSpace(hostId))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var cols = ShellBridge.ParseDim(context.Request.Query["cols"], ShellBridge.DefaultCols);
        var rows = ShellBridge.ParseDim(context.Request.Query["rows"], ShellBridge.DefaultRows);

        var relay = context.RequestServices.GetRequiredService<ITunnelRelay>();

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var ct = context.RequestAborted;

        ITunnelShell shell;
        try
        {
            shell = await relay.OpenShellAsync(hostId, leaseId, cols, rows, ct);
        }
        catch (ApiException ex)
        {
            // host_offline / upstream_timeout etc. — the shell never opened; report a server-side
            // WebSocket error close (1011) carrying the typed code so a client can surface it.
            logger.LogInformation(
                "dev shell: open failed for lease {LeaseId} on host {HostId}: {Code}", leaseId, hostId, ex.Code);
            var (_, wire) = ApiErrors.Map(ex.Code);
            await TunnelConnection.CloseSocketAsync(socket, 1011, wire, CancellationToken.None);
            return;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "dev shell: open error for lease {LeaseId} on host {HostId}", leaseId, hostId);
            await TunnelConnection.CloseSocketAsync(socket, 1011, "internal", CancellationToken.None);
            return;
        }

        await ShellBridge.RunAsync(socket, shell, logger, ct);
    }
}
