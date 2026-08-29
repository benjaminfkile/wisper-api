using System.Text.RegularExpressions;
using Wisper.Api.ApiKeys;
using Xunit;

namespace Wisper.Api.Tests.ApiKeys;

/// <summary>
/// Invariants of <see cref="ApiKeyToken"/> (docs/API.md §2, docs/DATA_MODEL.md §3). Mirrors the host
/// agent token: the minted key is <c>wck_live_&lt;64 hex&gt;</c> (256-bit CSPRNG), its hash is a
/// deterministic lowercase-hex SHA-256 for an O(1) indexed lookup, the display prefix is non-secret, and
/// the <c>wck_</c> namespace lets the auth layer tell a key from a JWT.
/// </summary>
public class ApiKeyTokenTests
{
    [Fact]
    public void Issue_mints_a_wck_live_token_with_64_hex_of_entropy()
    {
        var issued = ApiKeyToken.Issue();

        Assert.StartsWith("wck_live_", issued.Token);
        var secret = issued.Token["wck_live_".Length..];
        Assert.Equal(64, secret.Length); // 32 bytes → 64 hex chars (256 bits)
        Assert.Matches(new Regex("^[0-9a-f]{64}$"), secret);
    }

    [Fact]
    public void Issue_prefix_is_the_namespace_plus_first_four_secret_chars_and_is_non_secret()
    {
        var issued = ApiKeyToken.Issue();

        var secret = issued.Token["wck_live_".Length..];
        Assert.Equal("wck_live_" + secret[..4], issued.TokenPrefix);

        // The prefix is a non-secret display fragment -- it must not reveal the full secret.
        Assert.DoesNotContain(secret, issued.TokenPrefix);
    }

    [Fact]
    public void Issue_hash_matches_the_deterministic_hash_of_the_clear_token()
    {
        var issued = ApiKeyToken.Issue();

        Assert.Equal(ApiKeyToken.Hash(issued.Token), issued.TokenHash);
    }

    [Fact]
    public void Hash_is_the_deterministic_lowercase_hex_sha256_of_the_token()
    {
        const string token = "wck_live_0123456789abcdef";
        var expected = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)))
            .ToLowerInvariant();

        var hash = ApiKeyToken.Hash(token);

        Assert.Equal(expected, hash);                       // exact SHA-256, lowercase hex
        Assert.Equal(hash, ApiKeyToken.Hash(token));        // deterministic
        Assert.Equal(64, hash.Length);                      // 32-byte digest → 64 hex
        Assert.Matches(new Regex("^[0-9a-f]{64}$"), hash);  // lowercase hex only
    }

    [Fact]
    public void Distinct_issues_produce_distinct_tokens_and_hashes()
    {
        var a = ApiKeyToken.Issue();
        var b = ApiKeyToken.Issue();

        Assert.NotEqual(a.Token, b.Token);
        Assert.NotEqual(a.TokenHash, b.TokenHash);
    }

    [Theory]
    [InlineData("wck_live_deadbeef", true)]
    [InlineData("wck_test_deadbeef", true)] // any wck_ namespace looks like a key
    [InlineData("eyJhbGciOiJIUzI1NiJ9.payload.sig", false)] // a JWT never starts with wck_
    [InlineData("Bearer wck_live_x", false)] // must be the raw token, not a header value
    [InlineData("", false)]
    [InlineData(null, false)]
    public void LooksLikeApiKey_distinguishes_a_key_from_a_jwt(string? token, bool expected)
    {
        Assert.Equal(expected, ApiKeyToken.LooksLikeApiKey(token));
    }

    [Fact]
    public void A_minted_key_looks_like_an_api_key()
    {
        Assert.True(ApiKeyToken.LooksLikeApiKey(ApiKeyToken.Issue().Token));
    }
}
