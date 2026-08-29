using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Npgsql;

namespace Wisper.Api.Tests.TestSupport;

/// <summary>
/// A throwaway PostgreSQL cluster for the few tests that must run against the REAL SQL stores rather than the
/// in-memory doubles (task #540: the ledger <c>lease_id</c> FK ordering is only observable against Postgres --
/// the in-memory ledger enforces no FK, which is exactly why the unit suite never caught the bug). It
/// <c>initdb</c>'s a fresh data directory under the temp folder, starts a private server on a free loopback
/// TCP port with <c>trust</c> auth, and tears both down on dispose.
/// <para>
/// <b>Opt-in (task #558).</b> These regressions stand up a real server, so they are gated behind an explicit
/// <c>WISPER_RUN_PG_TESTS</c> env var. When it is unset (the default -- including normal CI on GitHub's
/// ubuntu-latest, which ships Postgres under <c>/usr/lib/postgresql/*/bin</c>) <see cref="TryStartAsync"/>
/// returns <c>null</c> and the callers skip <i>visibly</i> -- deterministically, regardless of whether ambient
/// Postgres binaries happen to exist. The first CI run hung for 14+ minutes precisely because discovery keyed
/// off ambient binaries rather than an explicit opt-in; it no longer does. Set <c>WISPER_RUN_PG_TESTS=1</c> to
/// run the regression for real. Set <c>WISPER_TEST_PG_BIN</c> to point at a <c>bin</c> directory to override
/// binary discovery.
/// </para>
/// <para>
/// <b>Bounded startup.</b> Even when opted in, every external process (<c>initdb</c>, <c>pg_ctl</c>) and the
/// readiness wait honour an overall <see cref="DeadlineSeconds"/> deadline and the caller's
/// <see cref="CancellationToken"/>; <see cref="StartAsync"/> throws rather than blocking indefinitely, and
/// <see cref="RunToolAsync"/> kills a process that overruns the deadline. See the class remarks in the source
/// for why <c>pg_ctl -w -t 60</c> alone did not bound the observed hang.
/// </para>
/// </summary>
// Why pg_ctl -w -t 60 did NOT bound the original 14-minute hang, and what actually fixes it:
//   * `pg_ctl start` launches the long-lived postgres server as a child that INHERITS this process's
//     redirected stdout/stderr pipes. pg_ctl itself exits after the readiness wait (bounded by -w -t), but the
//     daemon keeps the WRITE end of those pipes open -- so `StandardOutput/StandardError.ReadToEndAsync()` never
//     observes EOF and blocks for the life of the server (i.e. until the test host is killed). The `-t 60` only
//     bounds pg_ctl's own wait, never that dangling read. Task #214 tightened this: pg_ctl start now runs
//     WITHOUT stdio redirection at all (captureIo:false on RunToolAsync), so there are no pipe handles for the
//     postmaster to inherit, and we poll for readiness on a fresh Npgsql connection under the same deadline
//     (WaitForReadyAsync). The `-l <root>/server.log` argument keeps the server's own output out of the
//     parent's console so nothing spills into the test-runner output either. initdb and pg_ctl stop still
//     redirect stdio (nothing they spawn survives their exit, so no inherited-pipe hang can occur there) and
//     their captured stderr flows into the InvalidOperationException on failure so operators can diagnose.
//     This change is platform-neutral -- macOS/Linux pg_ctl also forked postmaster with inheritable handles,
//     they just tended not to hold them long -- but the Windows worker was where the hang was observed.
//   * `initdb` has no `-t`-style timeout at all, so a slow/prompting initdb (entropy, a version-mismatched data
//     directory, a stdin prompt) is unbounded. Fix: every tool runs under an overall deadline that KILLS the
//     process on overrun (RunToolAsync), so nothing can wedge the job.
//   * Discovery can surface several candidate bin dirs; a data directory initted by one major version cannot be
//     started by pg_ctl of another, which manifests as a wedged/looping start. Fix: EnsureMatchingMajorAsync
//     verifies the chosen dir's initdb and pg_ctl are the same PostgreSQL major before we init anything.
public sealed class EphemeralPostgres : IAsyncDisposable
{
    /// <summary>Overall wall-clock budget for the whole startup (initdb + readiness wait), and per tool.</summary>
    private const int DeadlineSeconds = 60;

    /// <summary>Explicit opt-in: real-server regressions run only when this env var is set to a truthy value.</summary>
    public const string OptInEnvVar = "WISPER_RUN_PG_TESTS";

    private readonly string _root;
    private readonly string _dataDir;
    private readonly string _binDir;
    private readonly int _port;
    private bool _started;

    private EphemeralPostgres(string root, string binDir, int port)
    {
        _root = root;
        _dataDir = Path.Combine(root, "data");
        _binDir = binDir;
        _port = port;
    }

    /// <summary>The Npgsql connection string to the throwaway server's <c>wisper_test</c> database.</summary>
    public string ConnectionString =>
        $"Host=127.0.0.1;Port={_port};Username=postgres;Database=wisper_test;Include Error Detail=true";

    /// <summary>
    /// Stands up a throwaway server, or returns <c>null</c> when the regression is not opted in
    /// (<see cref="OptInEnvVar"/> unset -- the default, so the caller skips) or the PostgreSQL server binaries
    /// (<c>initdb</c>/<c>pg_ctl</c>) are not available. When opted in <i>and</i> the binaries are present a
    /// startup failure throws (bounded by the deadline), so a genuinely broken server is not silently skipped.
    /// </summary>
    public static async Task<EphemeralPostgres?> TryStartAsync(CancellationToken ct = default)
    {
        // Explicit opt-in gate: the regression NEVER runs off the mere presence of ambient binaries. This is the
        // whole fix for task #558 -- CI is deterministic regardless of what Postgres the runner happens to ship.
        if (!OptedIn())
        {
            return null;
        }

        var binDir = FindBinDir();
        if (binDir is null)
        {
            return null;
        }

        var root = Path.Combine(Path.GetTempPath(), "wisper-pg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var pg = new EphemeralPostgres(root, binDir, FreeTcpPort());
        try
        {
            await pg.StartAsync(ct);
            return pg;
        }
        catch
        {
            await pg.DisposeAsync();
            throw;
        }
    }

    /// <summary>Whether the caller has explicitly opted into the real-server regressions.</summary>
    public static bool OptedIn()
    {
        var v = Environment.GetEnvironmentVariable(OptInEnvVar);
        return !string.IsNullOrWhiteSpace(v)
            && !v.Equals("0", StringComparison.OrdinalIgnoreCase)
            && !v.Equals("false", StringComparison.OrdinalIgnoreCase);
    }

    private async Task StartAsync(CancellationToken ct)
    {
        // Bound the WHOLE startup: initdb + readiness wait share one wall-clock budget, linked to the caller's
        // token, so no external tool can wedge the job the way the first CI run did.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(DeadlineSeconds));
        var dct = deadline.Token;

        // A data directory initted by one major cannot be started by a pg_ctl of another; verify the chosen bin
        // dir is internally consistent before we write anything (bounded, kills on overrun like every tool).
        await EnsureMatchingMajorAsync(dct);

        // A fresh cluster with a single trusted superuser; UTF8 so the app's text columns behave as in prod.
        await RunToolAsync("initdb",
            new[] { "-D", _dataDir, "-U", "postgres", "-A", "trust", "-E", "UTF8", "--no-sync" }, dct);

        // Private server: loopback TCP on our free port, unix socket under the temp root (the default socket
        // dir may be root-owned), and durability off -- this cluster is discarded at the end of the test.
        // -l sends the SERVER's stdout/stderr to a log file so nothing spills to the parent console. We also
        // pass captureIo:false here so we do NOT redirect pg_ctl's OWN stdio: on Windows the postmaster
        // pg_ctl forks inherits any inheritable handles pg_ctl has open, including our stdout/stderr pipes,
        // and then holds the WRITE end open for the life of the daemon -- ReadToEndAsync on our side would
        // never observe EOF and the fixture would hang until the DeadlineSeconds cancel fired, even though
        // the server itself was ready in tens of milliseconds. -w still bounds pg_ctl's own readiness wait,
        // and WaitForReadyAsync below verifies the port is really accepting connections before we return.
        var logFile = Path.Combine(_root, "server.log");
        var serverOptions = $"-p {_port} -k \"{_root}\" -c listen_addresses=127.0.0.1 -c fsync=off";
        await RunToolAsync("pg_ctl",
            new[] { "-D", _dataDir, "-l", logFile, "-o", serverOptions, "-w", "-t", "30", "start" },
            dct, captureIo: false);
        _started = true;

        // Belt-and-braces: even though pg_ctl -w waits for readiness, a fast return from pg_ctl on a busy
        // worker does not guarantee TCP is accepting yet. Poll a real connection under the same deadline so
        // callers never see an "OK, now use it" from us on a server that immediately refuses a connect.
        await WaitForReadyAsync(dct);
    }

    /// <summary>
    /// Polls a fresh Npgsql connection to <c>postgres</c> on the private port until it succeeds or
    /// <paramref name="ct"/> fires. Kept in-process (rather than shelling out to <c>pg_isready</c>) so the
    /// readiness probe cannot itself introduce a subprocess pipe hang on Windows.
    /// </summary>
    private async Task WaitForReadyAsync(CancellationToken ct)
    {
        var probeConnectionString =
            $"Host=127.0.0.1;Port={_port};Username=postgres;Database=postgres;Timeout=2";
        while (true)
        {
            try
            {
                await using var conn = new NpgsqlConnection(probeConnectionString);
                await conn.OpenAsync(ct);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_started)
        {
            try
            {
                // Bound teardown too -- a wedged stop must not hang the run any more than a wedged start.
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(DeadlineSeconds));
                await RunToolAsync("pg_ctl",
                    new[] { "-D", _dataDir, "-m", "immediate", "-w", "stop" }, deadline.Token);
            }
            catch
            {
                // Best-effort teardown -- the data directory is removed below regardless.
            }

            _started = false;
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // A leftover temp directory is harmless; never fail a test on cleanup.
        }
    }

    /// <summary>
    /// Runs a bundled tool to completion under <paramref name="ct"/>, returning its stdout. On cancellation
    /// (the overall deadline firing, or the caller cancelling) the process is <b>killed</b> -- including any
    /// children -- and a <see cref="TimeoutException"/> is thrown, so a hung tool can never block the run.
    /// <para>
    /// <paramref name="captureIo"/> defaults to <c>true</c> so exit-code failures surface the tool's stderr in
    /// the exception message (the useful default for initdb/pg_ctl-stop/--version probes). Set it to
    /// <c>false</c> for <c>pg_ctl start</c>, whose forked postmaster inherits any inheritable handles the
    /// parent has open -- including redirected stdio pipes -- and would then hold the WRITE end past pg_ctl's
    /// own exit, wedging <c>ReadToEndAsync</c> until the deadline. In that mode the returned string is empty
    /// and a non-zero exit code throws with the exit code only; the server's own output is captured via
    /// <c>pg_ctl -l</c> instead.
    /// </para>
    /// </summary>
    private async Task<string> RunToolAsync(
        string tool, IReadOnlyList<string> args, CancellationToken ct, bool captureIo = true)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(_binDir, ExeName(tool)),
            RedirectStandardOutput = captureIo,
            RedirectStandardError = captureIo,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"could not start {tool}");
        try
        {
            var stdoutTask = captureIo ? proc.StandardOutput.ReadToEndAsync(ct) : Task.FromResult(string.Empty);
            var stderrTask = captureIo ? proc.StandardError.ReadToEndAsync(ct) : Task.FromResult(string.Empty);
            await proc.WaitForExitAsync(ct);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            if (proc.ExitCode != 0)
            {
                var detail = captureIo
                    ? $": {stderr.Trim()} {stdout.Trim()}".TrimEnd()
                    : " (stdio was inherited; see server.log)";
                throw new InvalidOperationException($"{tool} exited {proc.ExitCode}{detail}");
            }

            return stdout;
        }
        catch (OperationCanceledException)
        {
            TryKill(proc);
            throw new TimeoutException(
                $"{tool} did not complete within {DeadlineSeconds}s and was killed " +
                $"(args: {string.Join(' ', args)})");
        }
    }

    private static void TryKill(Process proc)
    {
        try
        {
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // The process may have exited between the check and the kill; nothing to do.
        }
    }

    /// <summary>
    /// Verifies the chosen bin dir's <c>initdb</c> and <c>pg_ctl</c> report the same PostgreSQL major version.
    /// A cross-version pair (possible when discovery surfaces multiple candidate roots) cannot init-then-start a
    /// cluster and is one of the ways a start can wedge; fail fast and loud instead.
    /// </summary>
    private async Task EnsureMatchingMajorAsync(CancellationToken ct)
    {
        var initdbMajor = ParseMajor(await RunToolAsync("initdb", new[] { "--version" }, ct));
        var pgCtlMajor = ParseMajor(await RunToolAsync("pg_ctl", new[] { "--version" }, ct));
        if (initdbMajor is null || pgCtlMajor is null || initdbMajor != pgCtlMajor)
        {
            throw new InvalidOperationException(
                $"initdb (v{initdbMajor?.ToString() ?? "?"}) and pg_ctl (v{pgCtlMajor?.ToString() ?? "?"}) in " +
                $"'{_binDir}' are not the same PostgreSQL major version; set {OptInEnvVar} against a consistent " +
                "install, or point WISPER_TEST_PG_BIN at one.");
        }
    }

    /// <summary>Extracts the leading major number from a <c>--version</c> line, e.g. "pg_ctl (PostgreSQL) 15.4".</summary>
    private static int? ParseMajor(string versionLine)
    {
        // Take the last whitespace-separated token ("15.4", "16beta1", "15") and read its leading digits.
        var token = versionLine.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (token is null)
        {
            return null;
        }

        var digits = new string(token.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var major) ? major : null;
    }

    /// <summary>Locates a PostgreSQL <c>bin</c> directory containing both server tools, or <c>null</c>.</summary>
    private static string? FindBinDir()
    {
        var overridden = Environment.GetEnvironmentVariable("WISPER_TEST_PG_BIN");
        if (!string.IsNullOrWhiteSpace(overridden) && HasServerTools(overridden))
        {
            return overridden;
        }

        var candidates = new List<string>();

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        candidates.AddRange(path.Split(Path.PathSeparator).Where(d => !string.IsNullOrWhiteSpace(d)));

        // Common per-version install roots (Debian/Ubuntu, RHEL, Homebrew, local builds).
        foreach (var root in new[] { "/usr/lib/postgresql", "/usr/pgsql", "/opt/homebrew/opt", "/usr/local" })
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var sub in Directory.EnumerateDirectories(root))
            {
                candidates.Add(Path.Combine(sub, "bin"));
                candidates.Add(sub);
            }
        }

        return candidates.FirstOrDefault(HasServerTools);
    }

    private static bool HasServerTools(string dir) =>
        File.Exists(Path.Combine(dir, ExeName("initdb"))) &&
        File.Exists(Path.Combine(dir, ExeName("pg_ctl")));

    private static string ExeName(string tool) =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? tool + ".exe" : tool;

    private static int FreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
