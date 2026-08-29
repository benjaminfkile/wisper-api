using System.Text.Json.Serialization;

namespace Wisper.Api.Tunnel.Messages;

/// <summary>
/// <c>shell.resize</c> (W→A) -- the consumer's terminal changed size; the agent forwards the
/// new window to the PTY (docs/TUNNEL.md §5, §6). Carries the stream <c>sid</c> (inherited);
/// no <c>rid</c> -- it is a fire-and-forget notification on an open shell.
/// </summary>
public record ShellResize : ControlEnvelope
{
    public ShellResize() => T = FrameTypes.ShellResize;

    [JsonPropertyName("cols")]
    public int Cols { get; init; }

    [JsonPropertyName("rows")]
    public int Rows { get; init; }
}
