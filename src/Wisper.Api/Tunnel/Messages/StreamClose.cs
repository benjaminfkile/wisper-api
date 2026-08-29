namespace Wisper.Api.Tunnel.Messages;

/// <summary>
/// <c>stream.close</c> (W→A) -- a consumer aborted/disconnected, so Wisper tells the agent to
/// cancel the underlying wisp shell/exec for this <c>sid</c> (docs/TUNNEL.md §5, §6). Carries
/// only the stream <c>sid</c> (inherited); the agent replies <c>stream.closed</c>.
/// </summary>
public record StreamClose : ControlEnvelope
{
    public StreamClose() => T = FrameTypes.StreamClose;
}
