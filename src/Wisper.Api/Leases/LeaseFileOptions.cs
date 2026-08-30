namespace Wisper.Api.Leases;

/// <summary>
/// Operational caps for the lease-files feature (docs/API.md §5, docs/TUNNEL.md §5, §10). Bound from
/// configuration section <see cref="SectionName"/>. Defaults match the pinned contract.
/// </summary>
public sealed class LeaseFileOptions
{
    /// <summary>Configuration section these options bind from.</summary>
    public const string SectionName = "Leases";

    /// <summary>Maximum number of files allowed on a <c>POST /v1/leases</c> body. Default 16.</summary>
    public int MaxFileCount { get; set; } = 16;

    /// <summary>Maximum total decoded bytes across all files on a <c>POST /v1/leases</c> body. Default 1 MiB.</summary>
    public int MaxFileTotalBytes { get; set; } = 1 * 1024 * 1024;

    /// <summary>Maximum bytes a single <c>GET /v1/leases/:id/files</c> download may transfer. Default 16 MiB.</summary>
    public long MaxDownloadBytes { get; set; } = 16 * 1024 * 1024;
}
