using System.Text.Json.Serialization;

namespace Wisper.Api.Tunnel.Messages;

/// <summary>
/// <c>exec.exit</c> (A→W) -- the streamed command finished; the stream is complete once this
/// arrives (docs/TUNNEL.md §5, §6). Carries the stream <c>sid</c> (inherited) and the process
/// <c>exit_code</c> (snake_case, matching <see cref="ExecResult"/> and wisp's exec response).
/// </summary>
public record ExecExit : ControlEnvelope
{
    public ExecExit() => T = FrameTypes.ExecExit;

    [JsonPropertyName("exit_code")]
    public int ExitCode { get; init; }
}
