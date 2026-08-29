namespace Wisper.Api.Tunnel.Messages;

/// <summary>
/// <c>exec.opened</c> (A→W) -- the agent opened the streamed wisp exec for an <c>exec.open</c>
/// request (docs/TUNNEL.md §5). Echoes the request <c>rid</c> and the stream <c>sid</c> (both
/// inherited) so Wisper can correlate it and start draining output frames on the <c>sid</c>.
/// </summary>
public record ExecOpened : ControlEnvelope
{
    public ExecOpened() => T = FrameTypes.ExecOpened;
}
