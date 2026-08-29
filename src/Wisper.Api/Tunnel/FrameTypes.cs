namespace Wisper.Api.Tunnel;

/// <summary>
/// Wire-protocol constants for the Wisper ⇄ wisp-agent tunnel (docs/TUNNEL.md).
/// These values are part of the wire contract with the Go agent and MUST NOT
/// change without a coordinated protocol-version bump -- see §4.
/// </summary>
public static class FrameTypes
{
    // Connection & health (docs/TUNNEL.md §5).
    public const string Hello            = "hello";
    public const string HelloAck         = "hello.ack";
    public const string CapabilityUpdate = "capability.update";
    public const string HostHeartbeat    = "host.heartbeat";
    public const string Error            = "error";

    // Lease lifecycle.
    public const string LeaseCreate   = "lease.create";
    public const string LeaseAccepted = "lease.accepted";
    public const string LeaseReady    = "lease.ready";
    public const string LeaseFailed   = "lease.failed";
    public const string LeaseRelease  = "lease.release";
    public const string LeaseReleased = "lease.released";
    public const string LeaseEnded    = "lease.ended";

    // Exec (sync).
    public const string ExecRun    = "exec.run";
    public const string ExecResult = "exec.result";

    // Exec (streamed) & shell.
    public const string ExecOpen    = "exec.open";
    public const string ExecOpened  = "exec.opened";
    public const string ExecExit    = "exec.exit";
    public const string ShellOpen   = "shell.open";
    public const string ShellOpened = "shell.opened";
    public const string ShellResize = "shell.resize";

    // Stream flow-control & teardown.
    public const string StreamCredit = "stream.credit";
    public const string StreamClose  = "stream.close";
    public const string StreamClosed = "stream.closed";
}

/// <summary>Binary data-frame channels (docs/TUNNEL.md §2, §6).</summary>
public static class Channels
{
    /// <summary>Consumer keystrokes / PTY stdin (W→A).</summary>
    public const byte Stdin = 0;
    /// <summary>PTY output / exec stdout (A→W).</summary>
    public const byte Stdout = 1;
    /// <summary>Exec stderr (A→W).</summary>
    public const byte Stderr = 2;
}

/// <summary>WebSocket close codes (docs/TUNNEL.md §3).</summary>
public static class CloseCodes
{
    /// <summary>Normal shutdown (either side).</summary>
    public const int Normal = 1000;
    /// <summary>Bad / missing / expired host token -- reauth needed.</summary>
    public const int BadToken = 4401;
    /// <summary>Host token revoked mid-session (agent must not auto-reconnect).</summary>
    public const int Revoked = 4402;
    /// <summary>Host suspended by admin.</summary>
    public const int Suspended = 4403;
    /// <summary>Liveness timeout (missed pongs).</summary>
    public const int LivenessTimeout = 4408;
    /// <summary>Protocol version incompatible (see <c>hello</c>).</summary>
    public const int ProtocolIncompatible = 4409;
}

/// <summary>Top-level tunnel protocol constants.</summary>
public static class TunnelProtocol
{
    /// <summary>Current protocol/framing version (the <c>ver</c> byte and <c>hello.proto</c>).</summary>
    public const int ProtocolVersion = 1;
}
