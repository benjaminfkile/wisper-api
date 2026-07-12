using System.Security.Cryptography;

namespace Wisper.Api.Hosts;

/// <summary>
/// Issues and hashes host <b>agent tokens</b> (docs/API.md §6, docs/TUNNEL.md §3, §13). A token is a
/// high-entropy secret shown to the host owner <b>once</b> at registration/rotation; only its hash and a
/// short non-secret prefix are stored (docs/DATA_MODEL.md §4). Because the token carries full CSPRNG
/// entropy, a plain <see cref="SHA256"/> digest is the right at-rest form: it is deterministic (so a
/// presented token resolves to its host by an indexed hash lookup, docs/TUNNEL.md §13) and preimage-resistant,
/// which a salted password hash (argon2/bcrypt) — designed for low-entropy secrets and non-deterministic by
/// design — could not provide for an O(1) lookup.
/// </summary>
public static class HostAgentToken
{
    /// <summary>The token namespace/prefix (mirrors the docs/API.md §6 example, <c>wht_live_…</c>).</summary>
    public const string Prefix = "wht_live_";

    /// <summary>Bytes of CSPRNG entropy behind the secret portion (256 bits).</summary>
    private const int SecretBytes = 32;

    /// <summary>Characters of the secret carried into the non-secret display prefix.</summary>
    private const int PrefixSecretChars = 4;

    /// <summary>A freshly issued token: the clear secret (shown once), its display prefix, and its at-rest hash.</summary>
    public readonly record struct Issued(string Token, string TokenPrefix, string TokenHash);

    /// <summary>
    /// Mints a new agent token. Returns the clear <see cref="Issued.Token"/> to hand to the owner exactly
    /// once, the non-secret <see cref="Issued.TokenPrefix"/> to persist for identification/rotation UX, and
    /// the <see cref="Issued.TokenHash"/> to persist as the only at-rest form of the secret.
    /// </summary>
    public static Issued Issue()
    {
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(SecretBytes)).ToLowerInvariant();
        var token = Prefix + secret;
        var tokenPrefix = Prefix + secret[..PrefixSecretChars];
        return new Issued(token, tokenPrefix, Hash(token));
    }

    /// <summary>The deterministic at-rest hash of <paramref name="token"/> (lowercase hex SHA-256).</summary>
    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}
