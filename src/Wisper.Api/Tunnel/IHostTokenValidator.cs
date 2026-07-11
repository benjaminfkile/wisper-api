namespace Wisper.Api.Tunnel;

/// <summary>
/// Validates the Bearer host token presented on the <c>/agent</c> handshake and resolves
/// it to a stable <c>HostId</c> (docs/TUNNEL.md §3, §13). Phase 1 is config-backed
/// (<see cref="ConfigHostTokenValidator"/>); the interface is deliberately narrow so a
/// later DB-backed validator (hashed, revocable, rotatable tokens) can replace it.
/// </summary>
public interface IHostTokenValidator
{
    /// <summary>
    /// Validates <paramref name="bearerToken"/> (the raw token, without the "Bearer " prefix).
    /// Returns success carrying the host id, or failure. A null/empty token always fails.
    /// </summary>
    HostTokenValidationResult Validate(string? bearerToken);
}

/// <summary>The outcome of <see cref="IHostTokenValidator.Validate"/>.</summary>
public readonly record struct HostTokenValidationResult
{
    private HostTokenValidationResult(bool succeeded, string? hostId)
    {
        Succeeded = succeeded;
        HostId = hostId;
    }

    /// <summary>Whether the token was recognized.</summary>
    public bool Succeeded { get; }

    /// <summary>The stable host id on success; <c>null</c> on failure.</summary>
    public string? HostId { get; }

    public static HostTokenValidationResult Success(string hostId) => new(true, hostId);

    public static HostTokenValidationResult Failure() => new(false, null);
}
