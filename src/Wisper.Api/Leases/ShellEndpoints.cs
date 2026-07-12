using Wisper.Api.Accounts;
using Wisper.Api.Auth;
using Wisper.Api.Infrastructure;
using Wisper.Api.Tunnel;

namespace Wisper.Api.Leases;

/// <summary>
/// The browser-safe interactive shell surface (docs/API.md §2, §7): the JWT-authenticated
/// <c>POST /v1/leases/:id/shell-ticket</c> that mints a single-use, short-TTL, <c>(user, lease)</c>-bound
/// ticket, and the raw <c>WS /v1/leases/:id/shell?ticket=…</c> that redeems it exactly once and bridges
/// the consumer socket to a tunnel shell stream. The JWT never appears in a URL — the ticket does, and it
/// is single-use and short-lived (docs/API.md §2). The WS bridge itself is the shared <see cref="ShellBridge"/>
/// the dev harness uses, so the binary-PTY + <c>{t:resize}</c> framing and the per-stream credit flow
/// control (docs/TUNNEL.md §9) are a straight passthrough.
/// </summary>
public static class ShellEndpoints
{
    private const string LogCategory = "Wisper.Api.Leases.Shell";

    public static void MapShellEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Minting is a normal JWT-authenticated consumer call.
        endpoints.MapPost("/v1/leases/{id}/shell-ticket", MintTicketAsync).RequireConsumer();
        // The WS handshake is authenticated by the ticket, NOT the JWT — a browser can't set a header on
        // it — so it carries no role gate; redemption below is the auth.
        endpoints.MapGet("/v1/leases/{id}/shell", OpenShellAsync);
    }

    /// <summary>
    /// Mints a one-time shell ticket for the caller's lease (docs/API.md §7). Ownership + ready state are
    /// checked first via the same resolver the exec surface uses: an unknown/other-user lease is a
    /// <c>404</c> (ownership is never revealed) and a not-yet-active lease is <c>409 lease_not_ready</c>.
    /// The ticket is bound to <c>(caller, lease)</c> so it can only ever reach this lease as this user.
    /// </summary>
    private static async Task<IResult> MintTicketAsync(
        string id,
        HttpContext http,
        ILeaseService leases,
        IUserAccountService accounts,
        IShellTicketStore tickets,
        CancellationToken ct)
    {
        var leaseId = RequireLeaseId(id);
        var user = await accounts.BootstrapAsync(http.User, ct);

        // Resolve BEFORE minting: null → 404, not-active → 409 lease_not_ready (thrown here). We never mint
        // a ticket for a lease the caller can't open.
        var target = await leases.ResolveExecTargetAsync(user.Id, leaseId, ct);
        if (target is null)
        {
            throw new ApiException(ApiErrorCode.NotFound, $"No such lease '{id}'.");
        }

        var ticket = tickets.Mint(user.Id, leaseId);
        return Results.Json(new ShellTicketResponse(ticket.Value, ticket.ExpiresInSeconds));
    }

    /// <summary>
    /// Redeems the <c>?ticket=</c> for the path lease (one-time) and bridges the consumer WebSocket to a
    /// tunnel shell stream (docs/API.md §7). Handshake failures are reported as the matching HTTP status
    /// BEFORE the socket is accepted (so the client's connect fails, not a mid-stream close): a non-WS
    /// request → 400, a malformed lease id → 404, an invalid/expired/used or wrong-lease ticket → 401. Once
    /// the ticket redeems, ownership/ready state is re-checked against the ticket's user, and the shell is
    /// opened and pumped through <see cref="ShellBridge"/>.
    /// </summary>
    private static async Task OpenShellAsync(
        string id,
        HttpContext http,
        ILeaseService leases,
        IShellTicketStore tickets,
        ITunnelRelay relay,
        CancellationToken ct)
    {
        var logger = http.RequestServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger(LogCategory);

        if (!http.WebSockets.IsWebSocketRequest)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (!TunnelLeaseId.TryParse(id, out var leaseId))
        {
            // A malformed id can never name a lease; 404 rather than leak the id shape (docs/API.md §3).
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // Redeem the ticket (one-time). Unknown/expired/used/wrong-lease → 401; the JWT never rode the URL.
        var redemption = tickets.Redeem(http.Request.Query["ticket"], leaseId);
        if (redemption is null)
        {
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        // Re-resolve against the ticket's user: the lease could have ended between mint and connect. null →
        // 404; a lease that's no longer active surfaces as the mapped status (409 lease_not_ready).
        LeaseExecTarget? target;
        try
        {
            target = await leases.ResolveExecTargetAsync(redemption.UserId, leaseId, ct);
        }
        catch (ApiException ex)
        {
            var (status, _) = ApiErrors.Map(ex.Code);
            http.Response.StatusCode = status;
            return;
        }

        if (target is null)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var cols = ShellBridge.ParseDim(http.Request.Query["cols"], ShellBridge.DefaultCols);
        var rows = ShellBridge.ParseDim(http.Request.Query["rows"], ShellBridge.DefaultRows);

        using var socket = await http.WebSockets.AcceptWebSocketAsync();

        ITunnelShell shell;
        try
        {
            shell = await relay.OpenShellAsync(target.HostId, target.LeaseId, cols, rows, ct);
        }
        catch (ApiException ex)
        {
            // host_offline / upstream_timeout etc. — the shell never opened; the socket is already accepted,
            // so report a server-side WS error close (1011) carrying the typed code.
            logger.LogInformation(
                "shell: open failed for lease {LeaseId} on host {HostId}: {Code}",
                target.LeaseId, target.HostId, ex.Code);
            var (_, wire) = ApiErrors.Map(ex.Code);
            await TunnelConnection.CloseSocketAsync(socket, 1011, wire, CancellationToken.None);
            return;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "shell: open error for lease {LeaseId} on host {HostId}", target.LeaseId, target.HostId);
            await TunnelConnection.CloseSocketAsync(socket, 1011, "internal", CancellationToken.None);
            return;
        }

        await ShellBridge.RunAsync(socket, shell, logger, http.RequestAborted);
    }

    /// <summary>Parses the <c>lease_&lt;guid&gt;</c> route id, 404ing anything malformed (docs/API.md §3).</summary>
    private static Guid RequireLeaseId(string id)
    {
        if (!TunnelLeaseId.TryParse(id, out var leaseId))
        {
            throw new ApiException(ApiErrorCode.NotFound, $"No such lease '{id}'.");
        }

        return leaseId;
    }
}
