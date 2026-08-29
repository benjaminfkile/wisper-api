using System.Text.Json.Serialization;

namespace Wisper.Api.Tunnel.Messages;

/// <summary>
/// <c>stream.closed</c> (A→W, or W→A on a detected fault) -- the stream was torn down: peer
/// exit, error, or a <c>flow_violation</c> (docs/TUNNEL.md §5, §9). Carries the stream
/// <c>sid</c> (inherited) and a <c>reason</c>. Wisper emits this itself when a peer sends past
/// its granted credit (the overflow-is-a-fault guarantee, §9).
/// </summary>
public record StreamClosed : ControlEnvelope
{
    public StreamClosed() => T = FrameTypes.StreamClosed;

    [JsonPropertyName("reason")]
    public string Reason { get; init; } = string.Empty;
}
