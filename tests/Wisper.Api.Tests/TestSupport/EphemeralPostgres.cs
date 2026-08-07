using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Wisper.Api.Tests.TestSupport;

/// <summary>
/// A throwaway PostgreSQL cluster for the few tests that must run against the REAL SQL stores rather than the
/// in-memory doubles (task #540: the ledger <c>lease_id</c> FK ordering is only observable against Postgres —
/// the in-memory ledger enforces no FK, which is exactly why the unit suite never caught the bug). It
/// <c>initdb</c>'s a fresh data directory under the temp folder, starts a private server on a free loopback
/// TCP port with <c>trust</c> auth, and tears both down on dispose.
/// <para>
/// When the PostgreSQL <b>server</b> binaries are not installed the harness is unavailable and
/// <see cref="TryStartAsync"/> returns <c>null</c> so the caller skips the regression — the unit suite stays
/// green on a machine with no Postgres (Grunt), while the regression actually runs wherever the binaries
/// exist. Set <c>WISPER_TEST_PG_BIN</c> to point at a <c>bin</c> directory to override discovery.
/// </para>
/// </summary>
public sealed class EphemeralPostgres : IAsyncDisposable
{
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
    /// Stands up a throwaway server, or returns <c>null</c> when the PostgreSQL server binaries
    /// (<c>initdb</c>/<c>pg_ctl</c>) are not available — the signal for callers to skip. When the binaries
    /// <i>are</i> present a startup failure throws, so a genuinely broken server is not silently skipped.
    /// </summary>
    public static async Task<EphemeralPostgres?> TryStartAsync(CancellationToken ct = default)
    {
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

    private async Task StartAsync(CancellationToken ct)
    {
        // A fresh cluster with a single trusted superuser; UTF8 so the app's text columns behave as in prod.
        await RunToolAsync("initdb",
            new[] { "-D", _dataDir, "-U", "postgres", "-A", "trust", "-E", "UTF8", "--no-sync" }, ct);

        // Private server: loopback TCP on our free port, unix socket under the temp root (the default socket
        // dir may be root-owned), and durability off — this cluster is discarded at the end of the test.
        var serverOptions = $"-p {_port} -k \"{_root}\" -c listen_addresses=127.0.0.1 -c fsync=off";
        await RunToolAsync("pg_ctl",
            new[] { "-D", _dataDir, "-o", serverOptions, "-w", "-t", "60", "start" }, ct);
        _started = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_started)
        {
            try
            {
                await RunToolAsync("pg_ctl",
                    new[] { "-D", _dataDir, "-m", "immediate", "-w", "stop" }, CancellationToken.None);
            }
            catch
            {
                // Best-effort teardown — the data directory is removed below regardless.
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

    private async Task RunToolAsync(string tool, IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(_binDir, ExeName(tool)),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"could not start {tool}");
        var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{tool} exited {proc.ExitCode}: {stderr.Trim()} {stdout.Trim()}".Trim());
        }
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
