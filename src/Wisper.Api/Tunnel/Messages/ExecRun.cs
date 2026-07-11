using System.Text.Json.Serialization;

namespace Wisper.Api.Tunnel.Messages;

/// <summary>
/// <c>exec.run</c> (W→A) — run a command synchronously in the lease; the agent calls wisp
/// <c>POST /contracts/:id/exec</c> and buffers the result (docs/TUNNEL.md §5, §10).
/// Carries the server-assigned <c>rid</c> (inherited) so the <c>exec.result</c> can be
/// correlated.
/// </summary>
public record ExecRun : ControlEnvelope
{
    public ExecRun() => T = FrameTypes.ExecRun;

    [JsonPropertyName("leaseId")]
    public string LeaseId { get; init; } = string.Empty;

    [JsonPropertyName("command")]
    public string Command { get; init; } = string.Empty;
}
