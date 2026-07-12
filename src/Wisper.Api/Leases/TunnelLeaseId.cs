using System.Diagnostics.CodeAnalysis;

namespace Wisper.Api.Leases;

/// <summary>
/// The external/tunnel form of a lease id — the <c>lease_&lt;32-hex&gt;</c> token clients see in
/// <c>docs/API.md §5</c> and the agent sees on the tunnel (docs/TUNNEL.md §1, Wisper owns the id space).
/// It is a lossless rendering of the lease's <see cref="System.Guid"/> primary key, so the same value
/// addresses the DB row (<c>GET /v1/leases/:id</c>) and the running container over the tunnel
/// (<c>lease.release</c>). The <see cref="Wisper.Api.Tunnel.TunnelRelay"/> mints ids in exactly this
/// shape, so a relay-issued id round-trips back to its Guid here.
/// </summary>
public static class TunnelLeaseId
{
    private const string Prefix = "lease_";

    /// <summary>Renders <paramref name="id"/> as its <c>lease_&lt;32-hex&gt;</c> external token.</summary>
    public static string Format(Guid id) => Prefix + id.ToString("N");

    /// <summary>
    /// Parses an external <c>lease_&lt;32-hex&gt;</c> token back to its <see cref="System.Guid"/>. Returns
    /// <c>false</c> for anything that isn't a well-formed lease token, so the caller can 404 rather than
    /// leak the id shape (docs/API.md §3).
    /// </summary>
    public static bool TryParse([NotNullWhen(true)] string? value, out Guid id)
    {
        id = Guid.Empty;
        if (string.IsNullOrEmpty(value) || !value.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        return Guid.TryParseExact(value.AsSpan(Prefix.Length), "N", out id);
    }
}
