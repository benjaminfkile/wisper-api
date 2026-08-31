using System.Threading.Channels;
using Wisper.Api.Infrastructure;
using Wisper.Api.Tunnel;
using Wisper.Api.Tunnel.Messages;

namespace Wisper.Api.Tests.TestSupport;

/// <summary>
/// An <see cref="ITunnelRelay"/> double for the consumer-lease suite (Grunt has no live agent tunnel).
/// It records the create/release/exec calls the lease surface makes and lets a test preset the outcome:
/// a ready <see cref="LeaseResult"/> for create, an <see cref="Messages.ExecResult"/> (or streamed
/// <see cref="FakeTunnelExec"/>) for exec, or a typed <see cref="ApiException"/> (host_offline /
/// upstream_timeout / lease_failed) to exercise the error-envelope paths. Shell streams throw -- the
/// consumer exec surface never opens a shell.
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

    /// <summary>The result a sync <see cref="ExecAsync"/> returns when no <see cref="ExecError"/> is set.</summary>
    public ExecResult SyncExecResult { get; set; } =
        new() { Stdout = string.Empty, Stderr = string.Empty, ExitCode = 0 };

    /// <summary>When set, <see cref="ExecAsync"/> throws this instead of returning a result.</summary>
    public ApiException? ExecError { get; set; }

    /// <summary>The handle <see cref="OpenExecStreamAsync"/> returns when no <see cref="ExecStreamError"/> is set.</summary>
    public FakeTunnelExec ExecStream { get; set; } = new(Array.Empty<ExecChunk>(), exitCode: 0);

    /// <summary>When set, <see cref="OpenExecStreamAsync"/> throws this instead of opening a stream.</summary>
    public ApiException? ExecStreamError { get; set; }

    /// <summary>Recorded <c>(hostId, leaseId, command)</c> of each sync exec call, in order.</summary>
    public List<(string HostId, string LeaseId, string Command)> ExecCalls { get; } = new();

    /// <summary>Recorded <c>(hostId, leaseId, command)</c> of each streamed exec call, in order.</summary>
    public List<(string HostId, string LeaseId, string Command)> ExecStreamCalls { get; } = new();

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
        // Mirrors the real relay, whose sends honor the token: a release fired under an already-cancelled
        // token never reaches the host, so teardown paths must hand this call a live (detached) token.
        ct.ThrowIfCancellationRequested();

        ReleaseCalls.Add((hostId, leaseId));
        if (ReleaseError is not null)
        {
            throw ReleaseError;
        }

        return Task.CompletedTask;
    }

    public Task<ExecResult> ExecAsync(string hostId, string leaseId, string command, CancellationToken ct = default)
    {
        ExecCalls.Add((hostId, leaseId, command));
        if (ExecError is not null)
        {
            throw ExecError;
        }

        return Task.FromResult(SyncExecResult);
    }

    public Task<ITunnelShell> OpenShellAsync(
        string hostId, string leaseId, int cols, int rows, CancellationToken ct = default) =>
        throw new NotSupportedException("The consumer exec surface does not open shells.");

    public Task<ITunnelExec> OpenExecStreamAsync(
        string hostId, string leaseId, string command, CancellationToken ct = default)
    {
        ExecStreamCalls.Add((hostId, leaseId, command));
        if (ExecStreamError is not null)
        {
            throw ExecStreamError;
        }

        return Task.FromResult<ITunnelExec>(ExecStream);
    }

    /// <summary>The handle <see cref="OpenFileReadAsync"/> returns when no <see cref="FileReadError"/> is set.</summary>
    public FakeTunnelFileDownload FileRead { get; set; } = new(Array.Empty<byte[]>(), size: 0);

    /// <summary>When set, <see cref="OpenFileReadAsync"/> throws this instead of opening a stream.</summary>
    public ApiException? FileReadError { get; set; }

    /// <summary>Recorded <c>(hostId, leaseId, path)</c> of each file-download call, in order.</summary>
    public List<(string HostId, string LeaseId, string Path)> FileReadCalls { get; } = new();

    public Task<ITunnelFileDownload> OpenFileReadAsync(
        string hostId, string leaseId, string path, CancellationToken ct = default)
    {
        FileReadCalls.Add((hostId, leaseId, path));
        if (FileReadError is not null)
        {
            throw FileReadError;
        }

        return Task.FromResult<ITunnelFileDownload>(FileRead);
    }

    public Task RouteAgentFrameAsync(
        TunnelConnection connection, string type, ReadOnlyMemory<byte> payload, CancellationToken ct) =>
        throw new NotSupportedException("The fake relay does not route frames.");

    public void OnConnectionClosed(TunnelConnection connection) =>
        throw new NotSupportedException("The fake relay has no connections.");
}
