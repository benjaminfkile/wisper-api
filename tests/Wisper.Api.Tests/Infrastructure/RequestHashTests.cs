using Wisper.Api.Infrastructure.Idempotency;
using Xunit;

namespace Wisper.Api.Tests.Infrastructure;

/// <summary>
/// Unit tests for <see cref="RequestHash"/> -- the body digest that lets an <c>Idempotency-Key</c> replay
/// distinguish a same-body retry from a different-body reuse (docs/API.md §9). The digest is stable for
/// equal bodies and differs for unequal ones.
/// </summary>
public class RequestHashTests
{
    [Fact]
    public void Equal_bodies_hash_equal()
    {
        Assert.Equal(RequestHash.Compute("""{"amount_cents":1000}"""),
                     RequestHash.Compute("""{"amount_cents":1000}"""));
    }

    [Fact]
    public void Different_bodies_hash_differently()
    {
        Assert.NotEqual(RequestHash.Compute("""{"amount_cents":1000}"""),
                        RequestHash.Compute("""{"amount_cents":2000}"""));
    }

    [Fact]
    public void Digest_is_lowercase_hex_of_sha256_length()
    {
        var hash = RequestHash.Compute("hello");

        Assert.Equal(64, hash.Length);   // 32 bytes → 64 hex chars
        Assert.Equal(hash, hash.ToLowerInvariant());
    }
}
