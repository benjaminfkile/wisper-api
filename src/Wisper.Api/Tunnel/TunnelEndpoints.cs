using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wisper.Api.Tunnel.Messages;

namespace Wisper.Api.Tunnel;

/// <summary>
/// Maps the raw WebSocket <c>GET /agent</c> endpoint the host's wisp-agent dials into,
/// implementing the connection lifecycle in docs/TUNNEL.md §3: token auth, the
/// <c>hello</c>/<c>hello.ack</c> handshake, registration (with supersede), and the
/// receive/liveness loop. Lease/exec/shell relaying is intentionally out of scope here.
/// </summary>
public static class TunnelEndpoints
{
    /// <summary>Max bytes accepted for the handshake <c>hello</c> control frame (§2: control &lt; 64 KiB).</summary>
    private const int HandshakeMaxBytes = 64 * 1024;

    private const string LogCategory = "Wisper.Api.Tunnel.Agent";

    public static void MapAgentTunnel(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/agent", HandleAsync);
    }

    private static async Task HandleAsync(HttpContext context)
    {
        var logger = context.RequestServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger(LogCategory);

        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var options = context.RequestServices.GetRequiredService<IOptions<TunnelOptions>>().Value;
        var validator = context.RequestServices.GetRequiredService<IHostTokenValidator>();
        var registry = context.RequestServices.GetRequiredService<IHostRegistry>();

        using var socket = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
        {
            KeepAliveInterval = TimeSpan.FromMilliseconds(options.PingIntervalMs),
        });

        var ct = context.RequestAborted;

        // (a) Authenticate. The header is read after upgrade so a bad/missing token is
        // reported as a 4401 WebSocket close (docs/TUNNEL.md §3), before any frames.
        var token = ReadBearerToken(context.Request.Headers.Authorization.ToString());
        var auth = validator.Validate(token);
        if (!auth.Succeeded)
        {
            logger.LogWarning("agent tunnel: rejecting connection with bad/missing host token");
            await TunnelConnection.CloseSocketAsync(socket, CloseCodes.BadToken, "bad or missing host token", ct);
            return;
        }

        var hostId = auth.HostId!;

        // (b) First frame MUST be `hello`; validate the protocol version.
        Hello? hello;
        try
        {
            hello = await ReadHelloAsync(socket, ct);
        }
        catch (WebSocketException)
        {
            return;
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (hello is null)
        {
            logger.LogWarning("agent tunnel: host {HostId} did not send a valid hello first", hostId);
            await TunnelConnection.CloseSocketAsync(socket, CloseCodes.ProtocolIncompatible, "expected hello", ct);
            return;
        }

        if (hello.Proto != TunnelProtocol.ProtocolVersion)
        {
            logger.LogWarning(
                "agent tunnel: host {HostId} proto {AgentProto} incompatible with {ServerProto}; closing {CloseCode}",
                hostId, hello.Proto, TunnelProtocol.ProtocolVersion, CloseCodes.ProtocolIncompatible);
            await TunnelConnection.CloseSocketAsync(
                socket, CloseCodes.ProtocolIncompatible, "protocol version incompatible", ct);
            return;
        }

        // (c) Establish the session: register (superseding any prior tunnel) and ack.
        var sessionId = "sess_" + Guid.NewGuid().ToString("N");
        var maxReceiveBytes = Math.Max(HandshakeMaxBytes, options.MaxFrameBytes + BinaryFrame.HeaderSize);
        var connection = new TunnelConnection(socket, hostId, sessionId, maxReceiveBytes, logger);

        logger.LogInformation(
            "agent tunnel: host {HostId} connected, session {SessionId}, agentVersion {AgentVersion}",
            hostId, sessionId, hello.AgentVersion);

        await registry.RegisterAsync(connection, ct);

        try
        {
            await connection.SendControlAsync(new HelloAck
            {
                Proto = TunnelProtocol.ProtocolVersion,
                SessionId = sessionId,
                PingIntervalMs = options.PingIntervalMs,
                MaxFrameBytes = options.MaxFrameBytes,
                InitialWindowBytes = options.InitialWindowBytes,
                GraceSeconds = options.GraceSeconds,
            }, ct);

            // (d) Steady state until close/cancel/liveness timeout.
            var livenessTimeout = TimeSpan.FromMilliseconds(options.EffectiveLivenessTimeoutMs);
            await connection.RunAsync(livenessTimeout, ct);
        }
        catch (WebSocketException ex)
        {
            logger.LogDebug(ex, "agent tunnel: session {SessionId} ended on transport error", sessionId);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            registry.Unregister(connection);
            logger.LogInformation(
                "agent tunnel: host {HostId} disconnected, session {SessionId}", hostId, sessionId);
        }
    }

    private static async Task<Hello?> ReadHelloAsync(WebSocket socket, CancellationToken ct)
    {
        var message = await TunnelConnection.ReceiveMessageAsync(socket, HandshakeMaxBytes, ct);
        if (message.Type != WebSocketMessageType.Text)
        {
            return null;
        }

        if (ControlJson.PeekType(message.Data) != FrameTypes.Hello)
        {
            return null;
        }

        try
        {
            return ControlJson.Deserialize<Hello>(Encoding.UTF8.GetString(message.Data));
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static string? ReadBearerToken(string authorizationHeader)
    {
        const string prefix = "Bearer ";
        if (authorizationHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return authorizationHeader[prefix.Length..].Trim();
        }

        return null;
    }
}
