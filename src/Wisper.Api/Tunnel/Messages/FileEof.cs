namespace Wisper.Api.Tunnel.Messages;

/// <summary>
/// <c>file.eof</c> (A→W) -- the agent finished streaming the file's bytes on the <c>sid</c> that a
/// <see cref="FileRead"/> opened (docs/TUNNEL.md §5). Carries the stream <c>sid</c> (inherited); no
/// payload. Wisper completes the HTTP relay's response body on receipt.
/// </summary>
public record FileEof : ControlEnvelope
{
    public FileEof() => T = FrameTypes.FileEof;
}
