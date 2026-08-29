using System.Text.Json.Serialization;

namespace Wisper.Api.Tunnel.Messages;

/// <summary>
/// <c>hello.ack</c> (W→A) -- Wisper's reply to <see cref="Hello"/> (docs/TUNNEL.md §3, §5).
/// Carries the negotiated protocol version, the assigned <c>sessionId</c>, and the
/// operational params the agent runs by: liveness cadence (§7), max binary payload,
/// per-stream flow window (§9), and disconnect grace (§8).
/// </summary>
public record HelloAck : ControlEnvelope
{
    public HelloAck() => T = FrameTypes.HelloAck;

    /// <summary>The negotiated protocol version (see <see cref="TunnelProtocol.ProtocolVersion"/>).</summary>
    [JsonPropertyName("proto")]
    public int Proto { get; init; }

    /// <summary>Server-assigned session id for this tunnel connection.</summary>
    [JsonPropertyName("sessionId")]
    public string SessionId { get; init; } = string.Empty;

    /// <summary>WebSocket ping cadence in milliseconds (docs/TUNNEL.md §7).</summary>
    [JsonPropertyName("pingIntervalMs")]
    public int PingIntervalMs { get; init; }

    /// <summary>Maximum binary payload per data frame in bytes (docs/TUNNEL.md §2).</summary>
    [JsonPropertyName("maxFrameBytes")]
    public int MaxFrameBytes { get; init; }

    /// <summary>Initial per-stream send window in bytes (docs/TUNNEL.md §9).</summary>
    [JsonPropertyName("initialWindowBytes")]
    public int InitialWindowBytes { get; init; }

    /// <summary>Disconnect grace window in seconds (docs/TUNNEL.md §8).</summary>
    [JsonPropertyName("graceSeconds")]
    public int GraceSeconds { get; init; }
}
