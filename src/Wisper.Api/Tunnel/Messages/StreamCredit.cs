using System.Text.Json.Serialization;

namespace Wisper.Api.Tunnel.Messages;

/// <summary>
/// <c>stream.credit</c> (both directions) -- replenishes the peer's send window for a
/// <c>sid</c> as bytes are drained downstream (docs/TUNNEL.md §9). Carries the stream
/// <c>sid</c> (inherited) and the number of <c>bytes</c> granted back. A sender may have at
/// most <c>window</c> unacknowledged bytes in flight on a <c>sid</c>; when the window hits 0
/// it stops sending until a credit arrives.
/// </summary>
public record StreamCredit : ControlEnvelope
{
    public StreamCredit() => T = FrameTypes.StreamCredit;

    [JsonPropertyName("bytes")]
    public int Bytes { get; init; }
}
