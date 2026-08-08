using Amazon.CognitoIdentityProvider;
using Amazon.CognitoIdentityProvider.Model;
using Microsoft.Extensions.Options;

namespace Wisper.Api.Auth;

/// <summary>
/// The real <see cref="IUserRoleGranter"/> — grants the <c>host</c> role by adding the user to the Cognito
/// <c>host</c> group via <c>AdminAddUserToGroup</c> (docs/API.md §184, docs/DESIGN.md §199). The write is
/// idempotent: Cognito treats adding an already-member user as a success, so re-registering a host is a no-op.
/// It runs only when a user pool is configured (<see cref="CognitoAuthOptions.UserPoolId"/>); the DI wiring
/// substitutes <see cref="NoOpUserRoleGranter"/> otherwise, so this type is never reached DB-less.
/// </summary>
public sealed class CognitoUserRoleGranter : IUserRoleGranter
{
    private readonly IAmazonCognitoIdentityProvider _cognito;
    private readonly CognitoAuthOptions _options;
    private readonly ILogger<CognitoUserRoleGranter> _logger;

    public CognitoUserRoleGranter(
        IAmazonCognitoIdentityProvider cognito,
        IOptions<CognitoAuthOptions> options,
        ILogger<CognitoUserRoleGranter> logger)
    {
        _cognito = cognito;
        _options = options.Value;
        _logger = logger;
    }

    public async Task GrantHostAsync(string cognitoSub, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cognitoSub))
        {
            return;
        }

        var poolId = _options.UserPoolId;
        if (string.IsNullOrWhiteSpace(poolId))
        {
            // Defensive: the wiring only constructs this granter when a pool is configured, but stay a no-op
            // rather than throw if it is ever resolved without one.
            return;
        }

        // AdminAddUserToGroup is idempotent — an already-member user succeeds — so the first host action adds
        // the group and every later one is a harmless no-op (docs/API.md §184). The username is the Cognito
        // subject we authenticated the caller as.
        await _cognito.AdminAddUserToGroupAsync(
            new AdminAddUserToGroupRequest
            {
                UserPoolId = poolId,
                Username = cognitoSub,
                GroupName = WisperRoles.Host,
            },
            ct);

        _logger.LogInformation(
            "granted host group to user {Subject} in pool {Pool}", cognitoSub, poolId);
    }
}
