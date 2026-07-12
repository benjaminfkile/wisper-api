using Wisper.Api.Infrastructure;
using Wisper.Api.Tunnel;
using Wisper.Api.Tunnel.Messages;

namespace Wisper.Api.Tests.TestSupport;

/// <summary>
/// An <see cref="ITunnelRelay"/> double for the consumer-lease suite (Grunt has no live agent tunnel).
/// It records the create/release calls the <c>LeaseService</c> makes and lets a test preset the outcome:
/// a ready <see cref="LeaseResult"/> for create, or a typed <see cref="ApiException"/> (host_offline /
/// upstream_timeout / lease_failed) to exercise the error-envelope paths. The streaming members throw —
/// the lease CRUD surface never opens exec/shell streams.
/// </summary>
public sealed class FakeTunnelRelay : ITunnelRelay
{
    /// <summary>The status stamped on the <see cref="LeaseResult"/> a successful create returns.</summary>
    public string CreateStatus { get; set; } = "active";

    /// <summary>The wisp contract id a successful create returns.</summary>
    public string WispContractId { get; set; } = "wisp-contract-1";

    /// <summary>When set, <see cref="CreateLeaseAsync"/> throws this instead of returning a lease.</summary>
    public ApiException? CreateError { get; set; }

    /// <summary>When set, <see cref="ReleaseAsync"/> throws this instead of completing.</summary>
    public ApiException? ReleaseError { get; set; }

    /// <summary>Recorded <c>(hostId, spec)</c> of each create call, in order.</summary>
    public List<(string HostId, LeaseCreate Spec)> CreateCalls { get; } = new();

    /// <summary>Recorded <c>(hostId, leaseId)</c> of each release call, in order.</summary>
    public List<(string HostId, string LeaseId)> ReleaseCalls { get; } = new();

    /// <summary>The <c>lease_&lt;guid&gt;</c> id the most recent successful create returned.</summary>
    public string? LastLeaseId { get; private set; }

    public Task<LeaseResult> CreateLeaseAsync(string hostId, LeaseCreate spec, CancellationToken ct = default)
    {
        CreateCalls.Add((hostId, spec));
        if (CreateError is not null)
        {
            throw CreateError;
        }

        var leaseId = "lease_" + Guid.NewGuid().ToString("N");
        LastLeaseId = leaseId;
        return Task.FromResult(new LeaseResult(leaseId, WispContractId, CreateStatus));
    }

    public Task ReleaseAsync(string hostId, string leaseId, CancellationToken ct = default)
    {
        ReleaseCalls.Add((hostId, leaseId));
        if (ReleaseError is not null)
        {
            throw ReleaseError;
        }

        return Task.CompletedTask;
    }

    public Task<ExecResult> ExecAsync(string hostId, string leaseId, string command, CancellationToken ct = default) =>
        throw new NotSupportedException("The lease CRUD surface does not exec.");

    public Task<ITunnelShell> OpenShellAsync(
        string hostId, string leaseId, int cols, int rows, CancellationToken ct = default) =>
        throw new NotSupportedException("The lease CRUD surface does not open shells.");

    public Task<ITunnelExec> OpenExecStreamAsync(
        string hostId, string leaseId, string command, CancellationToken ct = default) =>
        throw new NotSupportedException("The lease CRUD surface does not open exec streams.");

    public Task RouteAgentFrameAsync(
        TunnelConnection connection, string type, ReadOnlyMemory<byte> payload, CancellationToken ct) =>
        throw new NotSupportedException("The fake relay does not route frames.");

    public void OnConnectionClosed(TunnelConnection connection) =>
        throw new NotSupportedException("The fake relay has no connections.");
}
