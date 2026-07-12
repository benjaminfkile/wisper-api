using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Wisper.Api.Tunnel;

/// <summary>
/// The shared consumer-socket ⇄ tunnel-shell bridge behind the interactive PTY console (docs/API.md §7).
/// It pumps a raw WebSocket both ways against an <see cref="ITunnelShell"/>: consumer BINARY frames →
/// PTY stdin (flow-controlled), PTY stdout → consumer BINARY (granting credit as bytes are written), and
/// a consumer TEXT <c>{t:resize,cols,rows}</c> control → <c>shell.resize</c>. The tunnel's per-stream
/// credit flow control (docs/TUNNEL.md §9) is honoured end-to-end to the browser socket. Both the
/// Phase-1 dev harness (<c>/dev/leases/:id/shell</c>) and the real, ticket-authenticated
/// <c>WS /v1/leases/:id/shell</c> drive this same passthrough so the framing is identical.
/// </summary>
public static class ShellBridge
{
    /// <summary>Cap on a single inbound consumer frame (§2: shell data ≤ 32 KiB payload, with margin).</summary>
    public const int MaxConsumerMessageBytes = 64 * 1024;

    /// <summary>Default terminal window used until a <c>resize</c> control frame arrives.</summary>
    public const int DefaultCols = 80;
    public const int DefaultRows = 24;

    /// <summary>Parses a positive integer <c>cols</c>/<c>rows</c> query value, falling back on anything else.</summary>
    public static int ParseDim(string? raw, int fallback) =>
        int.TryParse(raw, out var value) && value > 0 ? value : fallback;

    /// <summary>
    /// Bridges the consumer socket to <paramref name="shell"/> until either side ends: consumer BINARY →
    /// stdin (flow-controlled), stdout → consumer BINARY (granting credit as written), a consumer TEXT
    /// <c>{t:resize}</c> → <c>shell.resize</c>. Whichever side ends first cancels the other, then the
    /// consumer socket is closed with a code reflecting why.
    /// </summary>
    public static async Task RunAsync(WebSocket socket, ITunnelShell shell, ILogger logger, CancellationToken ct)
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
            logger.LogDebug("shell: dropping malformed control text frame");
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
