using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Wisper.Api.Tunnel;

/// <summary>
/// Config-backed <see cref="IHostTokenValidator"/>: the allowed tokens and their host ids come from
/// <see cref="TunnelOptions.HostTokens"/>. Comparison is constant-time
/// (<see cref="CryptographicOperations.FixedTimeEquals"/>). If no tokens are configured it <b>fails
/// closed</b> -- every connection is rejected. It is no longer the primary validator: the DB-backed
/// <see cref="DbHostTokenValidator"/> resolves tokens against the <c>hosts</c> table and delegates here
/// only as a dev/bootstrap fallback for a DB-less boot.
/// <para>
/// Structurally gated on <see cref="IHostEnvironment"/>.<c>IsDevelopment()</c>: outside Development
/// (e.g. Staging, Production) the validator fails closed <b>regardless of any configured
/// <c>Tunnel:HostTokens</c></b>. A static host token in a deployed secret would otherwise be a long-lived,
/// non-rotating, non-revocable host bearer (host-impersonation: the tunnel supersedes on reconnect),
/// so the fallback is code-gated to the local host-dev convenience it exists for. The DB path is
/// unaffected in every environment.
/// </para>
/// </summary>
public sealed class ConfigHostTokenValidator : IHostTokenValidator
{
    private readonly IOptionsMonitor<TunnelOptions> _options;
    private readonly IHostEnvironment _environment;

    public ConfigHostTokenValidator(IOptionsMonitor<TunnelOptions> options, IHostEnvironment environment)
    {
        _options = options;
        _environment = environment;
    }

    public Task<HostTokenValidationResult> ValidateAsync(string? bearerToken, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(bearerToken))
        {
            return Task.FromResult(HostTokenValidationResult.Failure());
        }

        // Fail closed outside Development: a deployed environment must never honour a static
        // Tunnel:HostTokens entry (see class remarks). Only the DB path issues host tokens there.
        if (!_environment.IsDevelopment())
        {
            return Task.FromResult(HostTokenValidationResult.Failure());
        }

        var tokens = _options.CurrentValue.HostTokens;
        if (tokens.Count == 0)
        {
            // Fail closed: an unconfigured tunnel trusts nobody.
            return Task.FromResult(HostTokenValidationResult.Failure());
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

        return Task.FromResult(matchedHostId is null
            ? HostTokenValidationResult.Failure()
            : HostTokenValidationResult.Success(matchedHostId));
    }
}
