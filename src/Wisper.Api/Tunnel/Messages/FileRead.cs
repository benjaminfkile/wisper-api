using System.Text.Json.Serialization;

namespace Wisper.Api.Tunnel.Messages;

/// <summary>
/// <c>file.read</c> (W→A) -- Wisper asks the agent to open a file inside <paramref name="LeaseId"/>'s
/// container and stream its bytes back (docs/TUNNEL.md §5, §10). Wisper owns the id space, so it carries
/// the server-assigned <c>rid</c> and <c>sid</c> (both inherited). The agent replies <see cref="FileOpened"/>
/// (by rid); binary frames then flow A→W on <c>sid</c> (channel 1) and end with <see cref="FileEof"/>.
/// The agent calls wisp's <c>GET /contracts/:id/files?path=</c> and pipes the bytes through without
/// buffering the whole file (per-stream credit flow control keeps memory bounded, docs/TUNNEL.md §9).
/// </summary>
public record FileRead : ControlEnvelope
{
    public FileRead() => T = FrameTypes.FileRead;

    [JsonPropertyName("leaseId")]
    public string LeaseId { get; init; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;
}
