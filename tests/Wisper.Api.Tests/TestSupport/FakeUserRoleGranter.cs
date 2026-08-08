using System.Collections.Concurrent;
using Wisper.Api.Auth;

namespace Wisper.Api.Tests.TestSupport;

/// <summary>
/// An <see cref="IUserRoleGranter"/> double: records every <c>host</c>-group grant so a test can assert that
/// registering a host granted the caller the host role (docs/API.md §184) — with the Cognito subject it was
/// called with — without a real Cognito user pool. Optionally throws to exercise the best-effort path where a
/// transient grant failure must never fail host registration.
/// </summary>
public sealed class FakeUserRoleGranter : IUserRoleGranter
{
    /// <summary>Every Cognito subject granted the host role, in order.</summary>
    public ConcurrentQueue<string> HostGrants { get; } = new();

    /// <summary>When set, every grant throws it — to prove registration still succeeds on a grant failure.</summary>
    public Exception? Throws { get; set; }

    public Task GrantHostAsync(string cognitoSub, CancellationToken ct = default)
    {
        HostGrants.Enqueue(cognitoSub);
        return Throws is null ? Task.CompletedTask : Task.FromException(Throws);
    }
}
