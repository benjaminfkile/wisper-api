using System.Text.Json.Serialization;

namespace Wisper.Api.Tunnel.Messages;

/// <summary>
/// <c>shell.open</c> (W→A) -- Wisper asks the agent to open an interactive PTY shell in the
/// lease; the agent opens wisp <c>WS /contracts/:id/shell</c> and pipes bytes onto the
/// stream (docs/TUNNEL.md §5, §6, §10). Wisper owns the id space, so it carries the
/// server-assigned <c>rid</c> and <c>sid</c> (both inherited). The agent replies
/// <c>shell.opened</c> (by rid); then binary frames flow both ways on <c>sid</c>
/// (ch 0 in, ch 1 out).
/// </summary>
public record ShellOpen : ControlEnvelope
{
    public ShellOpen() => T = FrameTypes.ShellOpen;

    [JsonPropertyName("leaseId")]
    public string LeaseId { get; init; } = string.Empty;

    [JsonPropertyName("cols")]
    public int Cols { get; init; }

    [JsonPropertyName("rows")]
    public int Rows { get; init; }
}
