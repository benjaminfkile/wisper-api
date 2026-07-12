using System.Net.WebSockets;
using Microsoft.Extensions.Logging.Abstractions;
using Wisper.Api.Tunnel;
using Xunit;

namespace Wisper.Api.Tests.Tunnel;

/// <summary>
/// Unit tests for the connection-readiness gate (docs/TUNNEL.md §3): a host is only usable AFTER its
/// hello handshake fully completes (registered + <c>hello.ack</c> sent), which the relay awaits before
/// resolving a host so a create right after connect does not race to <c>host_offline</c>.
/// </summary>
public class TunnelConnectionReadinessTests
{
    [Fact]
    public async Task Connection_is_not_ready_until_marked_and_wait_reflects_it()
    {
        var connection = NewConnection();

        // Fresh connection: not ready, and a bounded wait times out to false instead of hanging.
        Assert.False(connection.IsReady);
        Assert.False(await connection.WaitUntilReadyAsync(TimeSpan.FromMilliseconds(50), default));

        connection.MarkReady();

        Assert.True(connection.IsReady);
        Assert.True(await connection.WaitUntilReadyAsync(TimeSpan.FromSeconds(1), default));

        // First result wins — a later MarkUnavailable cannot un-ready a ready tunnel.
        connection.MarkUnavailable();
        Assert.True(connection.IsReady);
    }

    [Fact]
    public async Task Marking_unavailable_unblocks_a_waiter_with_false()
    {
        var connection = NewConnection();

        // A caller already waiting on readiness unblocks immediately (false) when the handshake is
        // abandoned — it does not wait out its deadline.
        var waiting = connection.WaitUntilReadyAsync(TimeSpan.FromSeconds(30), default);
        connection.MarkUnavailable();

        Assert.False(await waiting);
        Assert.False(connection.IsReady);
    }

    private static TunnelConnection NewConnection() =>
        new(new StubWebSocket(), hostId: "host-x", sessionId: "sess-x", maxReceiveBytes: 65536, NullLogger.Instance);

    /// <summary>A do-nothing <see cref="WebSocket"/>: the readiness gate never touches the socket.</summary>
    private sealed class StubWebSocket : WebSocket
    {
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus s, string? d, CancellationToken ct) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus s, string? d, CancellationToken ct) => Task.CompletedTask;
        public override void Dispose() { }
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> b, CancellationToken ct) =>
            throw new NotSupportedException();
        public override Task SendAsync(ArraySegment<byte> b, WebSocketMessageType t, bool e, CancellationToken ct) =>
            Task.CompletedTask;
    }
}
