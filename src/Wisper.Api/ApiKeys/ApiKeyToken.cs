using System.Security.Cryptography;

namespace Wisper.Api.ApiKeys;

/// <summary>
/// Issues and hashes consumer <b>API keys</b> (docs/API.md §2, docs/DATA_MODEL.md §3). A key is a
/// long-lived, high-entropy machine bearer shown to the user <b>once</b> at mint; only its hash and a
/// short non-secret prefix are stored. This mirrors <see cref="Wisper.Api.Hosts.HostAgentToken"/>
/// byte-for-byte in approach: because the key carries full CSPRNG entropy, a plain <see cref="SHA256"/>
/// digest is the right at-rest form -- deterministic (so a presented key resolves to its row by an
/// indexed hash lookup) and preimage-resistant, which a salted password hash (argon2/bcrypt) -- designed
/// for low-entropy secrets and non-deterministic by design -- could not provide for an O(1) lookup.
/// <para>
/// The <see cref="Namespace"/> also lets the auth layer cheaply distinguish a key from a JWT: keys start
/// with <c>wck_</c>; JWTs never do (see <see cref="LooksLikeApiKey"/>).
/// </para>
/// </summary>
public static class ApiKeyToken
{
    /// <summary>The token namespace -- the discriminator the auth layer uses to tell a key from a JWT.</summary>
    public const string Namespace = "wck_";

    /// <summary>The token prefix (namespace + environment marker); the secret follows.</summary>
    public const string Prefix = "wck_live_";

    /// <summary>Bytes of CSPRNG entropy behind the secret portion (256 bits).</summary>
    private const int SecretBytes = 32;

    /// <summary>Characters of the secret carried into the non-secret display prefix.</summary>
    private const int PrefixSecretChars = 4;

    /// <summary>A freshly minted key: the clear secret (shown once), its display prefix, and its at-rest hash.</summary>
    public readonly record struct Issued(string Token, string TokenPrefix, string TokenHash);

    /// <summary>
    /// Mints a new API key. Returns the clear <see cref="Issued.Token"/> to hand to the user exactly
    /// once, the non-secret <see cref="Issued.TokenPrefix"/> to persist for identification/listing UX, and
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

    /// <summary>
    /// Whether <paramref name="token"/> looks like an API key rather than a JWT -- the cheap discriminator
    /// the auth layer uses before doing a hashed lookup. Keys carry the <see cref="Namespace"/> prefix;
    /// JWTs (three base64url segments) never do. This is a shape check only, not authentication.
    /// </summary>
    public static bool LooksLikeApiKey(string? token) =>
        token is not null && token.StartsWith(Namespace, StringComparison.Ordinal);
}
