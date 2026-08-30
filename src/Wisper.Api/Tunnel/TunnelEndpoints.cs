using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wisper.Api.Leases;
using Wisper.Api.Tunnel.Backplane;
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
        var relay = context.RequestServices.GetRequiredService<ITunnelRelay>();
        var coordinator = context.RequestServices.GetRequiredService<TunnelDisconnectCoordinator>();
        var presence = context.RequestServices.GetRequiredService<IHostPresence>();
        // The shared capability store is always registered (in-memory in single-instance mode, Redis in
        // distributed). Republishing the heartbeat's fresh snapshot to it is what makes non-owner
        // instances see updated capacity/limits/os without waiting for the next reconnect (task #61).
        var capabilityStore = context.RequestServices.GetRequiredService<IHostCapabilityStore>();
        // Shared degraded set (task #62): the heartbeat handler writes into it on the owning instance so
        // catalog liveness / lease admission on every instance uniformly exclude a host whose agent has
        // reported "degraded" (its local wisp is unreachable).
        var degradedStore = context.RequestServices.GetRequiredService<IHostDegradedStore>();

        using var socket = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
        {
            KeepAliveInterval = TimeSpan.FromMilliseconds(options.PingIntervalMs),
        });

        var ct = context.RequestAborted;

        // (a) Authenticate. The header is read after upgrade so a bad/missing token is
        // reported as a 4401 WebSocket close (docs/TUNNEL.md §3), before any frames.
        var token = ReadBearerToken(context.Request.Headers.Authorization.ToString());
        var auth = await validator.ValidateAsync(token, ct);
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
        // Sized to hold whichever frame class needs more room: the largest inbound binary data frame
        // (header + payload) or a large control frame such as lease.create with a 1 MiB files array
        // (docs/API.md §5, docs/TUNNEL.md §2). Not related to the handshake `hello` cap above.
        var maxReceiveBytes = Math.Max(
            options.MaxControlFrameBytes, options.MaxFrameBytes + BinaryFrame.HeaderSize);
        var connection = new TunnelConnection(socket, hostId, sessionId, maxReceiveBytes, logger)
        {
            // The advertised wisp capability from this host's live hello (docs/TUNNEL.md §5). The host
            // API validates its priced allow-list against this live snapshot (docs/API.md §6).
            Capability = hello.Capability,

            // Route agent→server response frames (lease/exec) into the relay so pending
            // rid/leaseId awaiters complete (docs/TUNNEL.md §5, §11).
            ControlFrameRouter = relay.RouteAgentFrameAsync,

            // Route each heartbeat's live lease list into the disconnect coordinator so a reconnect
            // reconciles the suspended set via set-diff (docs/TUNNEL.md §8). A heartbeat that re-advertises
            // capability also refreshes the host's live capability snapshot (task #61) and its persisted
            // isolation/GPU (tasks #417, #521); an omitted capability means "no update -- keep last known"
            // (the agent deliberately omits it when its local wisp is unreachable). The refresh helper is
            // fail-safe on its own -- a hiccup there never disturbs lease reconciliation or the tunnel.
            HeartbeatRouter = async (conn, heartbeat, hbCt) =>
            {
                // Route with the connection, so the coordinator can best-effort tear down orphan
                // containers over this tunnel (task #73) and dedupe those teardowns per connection.
                await coordinator.OnHeartbeatAsync(conn, ParseLiveLeaseIds(heartbeat), hbCt);
                if (heartbeat.Capability is { } cap)
                {
                    await HeartbeatCapabilityRefresh.ApplyAsync(
                        conn, cap, capabilityStore, presence, logger, hbCt);
                }

                // Apply the agent's self-reported health flag (task #62): a "degraded" beat marks the
                // host degraded in the shared set so every instance's placement path excludes it; a
                // subsequent beat with no status clears the flag and restores placement. Lease state is
                // unaffected -- the containers keep running; only the agent's wisp API is unreachable.
                await HeartbeatDegradedApply.ApplyAsync(conn, heartbeat, degradedStore, logger, hbCt);
            },
        };

        logger.LogInformation(
            "agent tunnel: host {HostId} connected, session {SessionId}, agentVersion {AgentVersion}",
            hostId, sessionId, hello.AgentVersion);

        await registry.RegisterAsync(connection, ct);

        // A reconnect within the grace window: cancel the pending expiry timer so the returning agent's
        // leases are not ended; the first heartbeat then reconciles them (docs/TUNNEL.md §8). A no-op on a
        // first connect (no pending grace).
        coordinator.OnReconnected(hostId);

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

            // The handshake is fully complete now -- registered AND hello.ack sent -- so the host is
            // available to the relay (docs/TUNNEL.md §3). Until this point a create for this host waits
            // briefly for readiness rather than racing to host_offline on a freshly-connected agent.
            connection.MarkReady();

            // Persist the hello-reported versions and top-level capacity (task #182) FIRST, in its own
            // try, so a presence-flip failure below cannot skip the write: admin reads must see what this
            // connected agent advertised as soon as the handshake completed. Advisory only: per-host
            // admission is enforced against the live capability.capacity.max_contracts snapshot (task
            // #571), not these persisted fields; the heartbeat-driven capability refresh (task #61)
            // never rewrites them.
            try
            {
                await presence.RefreshAdvertisedVersionsAndCapacityAsync(
                    hostId, hello.WispVersion, hello.AgentVersion,
                    hello.Capacity.MaxLeases, hello.Capacity.MaxStreams, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "agent tunnel: persisting advertised versions and capacity for host {HostId} failed", hostId);
            }

            // Presence follows the tunnel (docs/TUNNEL.md §3): with the handshake complete, flip the host
            // online if it clears the earning gate (owner Connect-enabled, or every enabled image is
            // zero-priced -- task #392). Fail-safe: a presence hiccup must never abort a healthy tunnel.
            try
            {
                // An absent capability block (older agent, or an agent whose local wisp is unreachable at
                // handshake) leaves the persisted advertised isolation and GPU untouched, matching the
                // heartbeat rule (task #191). Persist the advertised GPU alongside isolation (task #521):
                // a null gpu block leaves the persisted GPU as-is rather than nulling it.
                var cap = hello.Capability;
                var gpu = cap?.Gpu;
                await presence.GoOnlineIfEligibleAsync(
                    hostId, cap?.IsolationLevels, cap?.DefaultIsolation,
                    gpu?.DeviceClasses, gpu?.Devices.Count ?? 0, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "agent tunnel: flipping host {HostId} online failed", hostId);
            }

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
            // Unblock any relay caller still waiting on this tunnel's readiness: if we reach here before
            // the handshake completed (MarkReady), the tunnel will never become ready, so waiters should
            // give up now instead of waiting out their deadline. A no-op once MarkReady has fired.
            connection.MarkUnavailable();

            // Was this connection superseded by a newer tunnel for the same host? If the registry now
            // points elsewhere, a fresh connection already took over -- do NOT suspend/arm grace (the host
            // is still connected). Only a genuine disconnect of the live tunnel triggers the §8 policy.
            var superseded = !(registry.TryGet(hostId, out var current) && ReferenceEquals(current, connection));

            registry.Unregister(connection);
            relay.OnConnectionClosed(connection);
            logger.LogInformation(
                "agent tunnel: host {HostId} disconnected, session {SessionId}", hostId, sessionId);

            if (!superseded)
            {
                // Tunnel loss: suspend the host's leases at the last-healthy liveness point and arm the
                // bounded grace window (docs/TUNNEL.md §8). Use last-received-frame time as last-healthy and
                // CancellationToken.None so the reconciliation is not cut off by the aborted request.
                var lastHealthy = new DateTimeOffset(connection.LastActivityUtc, TimeSpan.Zero);
                _ = await coordinator.OnDisconnectedAsync(hostId, lastHealthy, CancellationToken.None);

                // Clear any lingering degraded flag on a genuine disconnect (task #62): the flag is only
                // meaningful while a tunnel is up on some instance, and a returning agent's first
                // heartbeat now re-establishes the state authoritatively (task #65 -- every fresh
                // connection's first beat settles the store regardless of transition-edge state).
                // Skipped on supersede -- the newer owner tunnel is already live and its own heartbeats
                // govern the flag from here on, so an unconditional clear here would race and briefly
                // re-admit a still-degraded host; the task-#65 settle on the newer tunnel's first beat
                // is what closes the supersede-while-degraded leak safely.
                if (connection.IsDegraded)
                {
                    try
                    {
                        await degradedStore.ClearDegradedAsync(hostId, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(
                            ex,
                            "agent tunnel: clearing degraded flag on disconnect for host {HostId} failed",
                            hostId);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Extracts the host-reported live lease ids from a heartbeat as their <see cref="Guid"/> keys,
    /// dropping any that are not well-formed <c>lease_&lt;hex&gt;</c> tokens (docs/TUNNEL.md §5, §8).
    /// </summary>
    private static IReadOnlyCollection<Guid> ParseLiveLeaseIds(HostHeartbeat heartbeat)
    {
        var ids = new List<Guid>(heartbeat.Leases.Count);
        foreach (var lease in heartbeat.Leases)
        {
            if (TunnelLeaseId.TryParse(lease.LeaseId, out var id))
            {
                ids.Add(id);
            }
        }

        return ids;
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
