using System.Text.Json.Serialization;

namespace Wisper.Api.Tunnel.Messages;

/// <summary>
/// <c>exec.result</c> (A→W) -- the fully-buffered result of a synchronous <c>exec.run</c>
/// (docs/TUNNEL.md §5). Echoes the request <c>rid</c> (inherited). <c>exit_code</c> is
/// snake_case, matching wisp's exec response.
/// </summary>
public record ExecResult : ControlEnvelope
{
    public ExecResult() => T = FrameTypes.ExecResult;

    [JsonPropertyName("stdout")]
    public string Stdout { get; init; } = string.Empty;

    [JsonPropertyName("stderr")]
    public string Stderr { get; init; } = string.Empty;

    [JsonPropertyName("exit_code")]
    public int ExitCode { get; init; }
}
