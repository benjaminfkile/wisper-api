using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Wisper.Api.Auth;
using Wisper.Api.Tests.TestSupport;
using Xunit;

namespace Wisper.Api.Tests.Auth;

/// <summary>
/// Unit tests for <see cref="JwksSigningKeyProvider"/>: parses a JWKS document served over HTTP
/// (stubbed), caches within the TTL, and refreshes after it (docs/API.md §2). The real
/// <see cref="CognitoJwtValidator"/> is driven through it end-to-end to prove a JWKS-backed
/// key validates a matching token.
/// </summary>
public class JwksSigningKeyProviderTests
{
    private const string Issuer = "https://cognito-idp.us-east-1.amazonaws.com/us-east-1_TESTPOOL";

    private static (JwksSigningKeyProvider Provider, StubHandler Handler) Build(
        string jwksBody, CognitoAuthOptions options, TimeProvider clock)
    {
        var handler = new StubHandler(jwksBody);
        var provider = new JwksSigningKeyProvider(
            new StubHttpClientFactory(handler),
            new StaticOptionsMonitor<CognitoAuthOptions>(options),
            clock,
            NullLogger<JwksSigningKeyProvider>.Instance);
        return (provider, handler);
    }

    [Fact]
    public async Task Fetches_and_parses_the_signing_key()
    {
        using var signer = new TestJwtSigner();
        var (provider, _) = Build(
            signer.Jwks(),
            new CognitoAuthOptions { Issuer = Issuer },
            new FakeTimeProvider(DateTimeOffset.UnixEpoch));

        var keys = await provider.GetSigningKeysAsync();

        Assert.Single(keys);
    }

    [Fact]
    public async Task Caches_within_ttl_then_refreshes_after_expiry()
    {
        using var signer = new TestJwtSigner();
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var (provider, handler) = Build(
            signer.Jwks(),
            new CognitoAuthOptions { Issuer = Issuer, JwksCacheMinutes = 10 },
            clock);

        await provider.GetSigningKeysAsync();
        await provider.GetSigningKeysAsync();
        Assert.Equal(1, handler.Calls); // second call served from cache

        clock.Advance(TimeSpan.FromMinutes(11));
        await provider.GetSigningKeysAsync();
        Assert.Equal(2, handler.Calls); // TTL elapsed → refetch
    }

    [Fact]
    public async Task Throws_when_issuer_unconfigured()
    {
        var (provider, _) = Build(
            "{\"keys\":[]}", new CognitoAuthOptions(), new FakeTimeProvider(DateTimeOffset.UnixEpoch));

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetSigningKeysAsync());
    }

    [Fact]
    public async Task Throws_when_document_has_no_signing_keys()
    {
        var (provider, _) = Build(
            "{\"keys\":[]}",
            new CognitoAuthOptions { Issuer = Issuer },
            new FakeTimeProvider(DateTimeOffset.UnixEpoch));

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetSigningKeysAsync());
    }

    [Fact]
    public async Task Jwks_backed_key_validates_a_matching_token_end_to_end()
    {
        using var signer = new TestJwtSigner();
        var options = new CognitoAuthOptions { Issuer = Issuer };
        var (provider, _) = Build(signer.Jwks(), options, new FakeTimeProvider(DateTimeOffset.UnixEpoch));
        var validator = new CognitoJwtValidator(
            provider,
            new StaticOptionsMonitor<CognitoAuthOptions>(options),
            NullLogger<CognitoJwtValidator>.Instance);
        var token = signer.CreateToken(Issuer, subject: "sub-e2e");

        var result = await validator.ValidateAsync(token);

        Assert.True(result.Succeeded);
        Assert.Equal("sub-e2e", result.Principal!.GetSubject());
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _body;

        public StubHandler(string body) => _body = body;

        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body),
            });
        }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public StubHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }
}
