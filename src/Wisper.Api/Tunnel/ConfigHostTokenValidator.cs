using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Wisper.Api.Tunnel;

/// <summary>
/// Config-backed <see cref="IHostTokenValidator"/> for Phase 1: the allowed tokens and
/// their host ids come from <see cref="TunnelOptions.HostTokens"/>. Comparison is
/// constant-time (<see cref="CryptographicOperations.FixedTimeEquals"/>). If no tokens
/// are configured it <b>fails closed</b> — every connection is rejected.
/// </summary>
public sealed class ConfigHostTokenValidator : IHostTokenValidator
{
    private readonly IOptionsMonitor<TunnelOptions> _options;

    public ConfigHostTokenValidator(IOptionsMonitor<TunnelOptions> options) => _options = options;

    public HostTokenValidationResult Validate(string? bearerToken)
    {
        if (string.IsNullOrEmpty(bearerToken))
        {
            return HostTokenValidationResult.Failure();
        }

        var tokens = _options.CurrentValue.HostTokens;
        if (tokens.Count == 0)
        {
            // Fail closed: an unconfigured tunnel trusts nobody.
            return HostTokenValidationResult.Failure();
        }

        var presented = Encoding.UTF8.GetBytes(bearerToken);

        // Compare against every configured token without short-circuiting, so match
        // latency does not leak which (if any) token matched. FixedTimeEquals is itself
        // constant-time for equal-length inputs and returns false fast on a length
        // mismatch (length is not secret here).
        string? matchedHostId = null;
        foreach (var (token, hostId) in tokens)
        {
            var candidate = Encoding.UTF8.GetBytes(token);
            if (CryptographicOperations.FixedTimeEquals(candidate, presented))
            {
                matchedHostId = hostId;
            }
        }

        return matchedHostId is null
            ? HostTokenValidationResult.Failure()
            : HostTokenValidationResult.Success(matchedHostId);
    }
}
