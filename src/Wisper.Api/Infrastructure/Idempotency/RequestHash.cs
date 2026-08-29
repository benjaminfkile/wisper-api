using System.Security.Cryptography;
using System.Text;

namespace Wisper.Api.Infrastructure.Idempotency;

/// <summary>
/// Hashes a request body so an <c>Idempotency-Key</c> replay can tell a <b>same-body</b> retry (replay the
/// stored response) from a <b>different-body</b> reuse (409 conflict) -- docs/API.md §9, docs/DATA_MODEL.md
/// §10. A stable SHA-256 hex digest of the raw bytes; storing the digest (not the body) keeps the
/// idempotency row small and leaks nothing about the payload.
/// </summary>
public static class RequestHash
{
    /// <summary>The lowercase SHA-256 hex digest of <paramref name="body"/>.</summary>
    public static string Compute(ReadOnlySpan<byte> body)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(body, hash);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>The lowercase SHA-256 hex digest of <paramref name="body"/> (UTF-8 encoded).</summary>
    public static string Compute(string body) => Compute(Encoding.UTF8.GetBytes(body));
}
