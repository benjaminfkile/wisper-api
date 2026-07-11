using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Wisper.Api.Infrastructure;

namespace Wisper.Api.Tunnel;

/// <summary>
/// DEV-ONLY, money-free interactive shell endpoint — a Phase-1 test harness for driving a tunnel
/// shell stream end-to-end before accounts/billing/tickets exist. It maps a raw WebSocket
/// <c>GET /dev/leases/{leaseId}/shell</c> and bridges the consumer socket to a tunnel shell
/// stream, honouring per-stream credit flow control (docs/TUNNEL.md §9) both ways. Gated behind
/// <see cref="TunnelOptions.EnableDevEndpoints"/> (off by default). This mirrors the framing of the
/// real <c>WS /v1/leases/:id/shell</c> (docs/API.md §7), which replaces it once accounts land — so
/// there is no auth, no ticket, and the caller names the host directly via a query parameter.
/// </summary>
public static class DevShellEndpoints
{
    private const string LogCategory = "Wisper.Api.Tunnel.DevShell";

    /// <summary>Cap on a single inbound consumer frame (§2: shell data ≤ 32 KiB payload, with margin).</summary>
    private const int MaxConsumerMessageBytes = 64 * 1024;

    private const int DefaultCols = 80;
    private const int DefaultRows = 24;

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

        var cols = ParseDim(context.Request.Query["cols"], DefaultCols);
        var rows = ParseDim(context.Request.Query["rows"], DefaultRows);

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

        await BridgeAsync(socket, shell, logger, ct);
    }

    /// <summary>
    /// Bridges the consumer socket to the shell stream until either side ends: consumer BINARY →
    /// stdin (flow-controlled), stdout → consumer BINARY (granting credit as written), a consumer
    /// TEXT <c>{t:resize}</c> → <c>shell.resize</c>. Whichever side ends first cancels the other,
    /// then the consumer socket is closed with a code reflecting why.
    /// </summary>
    private static async Task BridgeAsync(WebSocket socket, ITunnelShell shell, ILogger logger, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var consumerToShell = PumpConsumerToShellAsync(socket, shell, logger, linked);
        var shellToConsumer = PumpShellToConsumerAsync(socket, shell, linked);

        await Task.WhenAny(consumerToShell, shellToConsumer, shell.Completion);
        linked.Cancel();

        await Swallow(consumerToShell);
        await Swallow(shellToConsumer);

        var (code, description) = CloseFor(shell);
        await shell.CloseAsync(description, CancellationToken.None);
        await TunnelConnection.CloseSocketAsync(socket, code, description, CancellationToken.None);
    }

    private static async Task PumpConsumerToShellAsync(
        WebSocket socket, ITunnelShell shell, ILogger logger, CancellationTokenSource linked)
    {
        var ct = linked.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var message = await TunnelConnection.ReceiveMessageAsync(socket, MaxConsumerMessageBytes, ct);

                if (message.Type == WebSocketMessageType.Close)
                {
                    break;
                }

                if (message.Type == WebSocketMessageType.Binary)
                {
                    await shell.WriteStdinAsync(message.Data, ct);
                }
                else if (message.Type == WebSocketMessageType.Text)
                {
                    await HandleResizeAsync(message.Data, shell, logger, ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException)
        {
        }
        catch (InvalidOperationException)
        {
            // The stream closed under us while writing stdin — treat as end of the consumer side.
        }
        finally
        {
            linked.Cancel();
        }
    }

    private static async Task PumpShellToConsumerAsync(
        WebSocket socket, ITunnelShell shell, CancellationTokenSource linked)
    {
        var ct = linked.Token;
        try
        {
            await foreach (var chunk in shell.Output.ReadAllAsync(ct))
            {
                await socket.SendAsync(chunk, WebSocketMessageType.Binary, endOfMessage: true, ct);
                // Credit is granted only as the bytes are actually written out (docs/TUNNEL.md §9).
                await shell.AckOutputDrainedAsync(chunk.Length, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException)
        {
        }
        finally
        {
            linked.Cancel();
        }
    }

    private static async Task HandleResizeAsync(
        byte[] data, ITunnelShell shell, ILogger logger, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (!root.TryGetProperty("t", out var t) || t.GetString() != "resize")
            {
                return;
            }

            var cols = root.TryGetProperty("cols", out var c) && c.TryGetInt32(out var cv) ? cv : DefaultCols;
            var rows = root.TryGetProperty("rows", out var r) && r.TryGetInt32(out var rv) ? rv : DefaultRows;
            await shell.ResizeAsync(cols, rows, ct);
        }
        catch (JsonException)
        {
            logger.LogDebug("dev shell: dropping malformed control text frame");
        }
    }

    /// <summary>Picks the consumer-facing close code once the bridge unwinds.</summary>
    private static (int Code, string Description) CloseFor(ITunnelShell shell)
    {
        if (!shell.Completion.IsCompleted)
        {
            // The consumer closed (or the request aborted) before the stream ended.
            return (CloseCodes.Normal, "closed");
        }

        return shell.ClosedReason switch
        {
            TunnelStream.FlowViolationReason => (1011, TunnelStream.FlowViolationReason),
            "host_offline" => (1011, "host_offline"),
            _ => (CloseCodes.Normal, shell.ClosedReason ?? "closed"),
        };
    }

    private static int ParseDim(string? raw, int fallback) =>
        int.TryParse(raw, out var value) && value > 0 ? value : fallback;

    private static async Task Swallow(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }
}
