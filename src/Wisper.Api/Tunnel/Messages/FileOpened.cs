using System.Text.Json.Serialization;

namespace Wisper.Api.Tunnel.Messages;

/// <summary>
/// <c>file.opened</c> (A→W) -- the agent opened the wisp file stream for a <see cref="FileRead"/> request
/// (docs/TUNNEL.md §5). Echoes the request <c>rid</c> and the stream <c>sid</c> (both inherited) and
/// announces the total <see cref="Size"/> in bytes (used to set <c>Content-Length</c> on the HTTP relay).
/// </summary>
public record FileOpened : ControlEnvelope
{
    public FileOpened() => T = FrameTypes.FileOpened;

    /// <summary>Total file size in bytes; set to <c>-1</c> when the source cannot report a length.</summary>
    [JsonPropertyName("size")]
    public long Size { get; init; }
}
