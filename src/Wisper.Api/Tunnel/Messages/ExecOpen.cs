using System.Text.Json.Serialization;

namespace Wisper.Api.Tunnel.Messages;

/// <summary>
/// <c>exec.open</c> (W→A) -- Wisper asks the agent to run a command with its output <b>streamed</b>
/// live; the agent calls wisp <c>POST /contracts/:id/exec?stream=1</c> and pipes the parsed SSE
/// onto the stream as binary frames (docs/TUNNEL.md §5, §6, §10). Wisper owns the id space, so it
/// carries the server-assigned <c>rid</c> and <c>sid</c> (both inherited). The agent replies
/// <c>exec.opened</c> (by rid); then binary frames flow A→W on <c>sid</c> (ch 1 stdout, ch 2
/// stderr), terminated by <c>exec.exit</c>.
/// </summary>
public record ExecOpen : ControlEnvelope
{
    public ExecOpen() => T = FrameTypes.ExecOpen;

    [JsonPropertyName("leaseId")]
    public string LeaseId { get; init; } = string.Empty;

    [JsonPropertyName("command")]
    public string Command { get; init; } = string.Empty;
}
