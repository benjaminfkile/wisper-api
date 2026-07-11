using System.Text.Json.Serialization;

namespace Wisper.Api.Tunnel.Messages;

/// <summary>
/// <c>shell.opened</c> (A→W) — the agent opened the wisp shell for a <c>shell.open</c>
/// request (docs/TUNNEL.md §5). Echoes the request <c>rid</c> and the stream <c>sid</c>
/// (both inherited) so Wisper can correlate it and start pumping bytes on the <c>sid</c>.
/// </summary>
public record ShellOpened : ControlEnvelope
{
    public ShellOpened() => T = FrameTypes.ShellOpened;
}
