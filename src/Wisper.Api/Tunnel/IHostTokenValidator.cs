namespace Wisper.Api.Tunnel;

/// <summary>
/// Validates the Bearer host token presented on the <c>/agent</c> handshake and resolves
/// it to a stable <c>HostId</c> (docs/TUNNEL.md §3, §13). The production implementation
/// (<see cref="DbHostTokenValidator"/>) resolves a presented token to its host id via a
/// constant-time hashed lookup against the <c>hosts</c> table; the config-backed
/// <see cref="ConfigHostTokenValidator"/> remains a dev/bootstrap fallback for a DB-less boot.
/// </summary>
public interface IHostTokenValidator
{
    /// <summary>
    /// Validates <paramref name="bearerToken"/> (the raw token, without the "Bearer " prefix).
    /// Returns success carrying the host id, or failure. A null/empty token always fails.
    /// </summary>
    Task<HostTokenValidationResult> ValidateAsync(string? bearerToken, CancellationToken ct = default);
}

/// <summary>The outcome of <see cref="IHostTokenValidator.ValidateAsync"/>.</summary>
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
