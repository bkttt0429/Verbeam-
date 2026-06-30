using System.Net;
using System.Text;
using Verbeam.Core.Options;
using Verbeam.Core.Services;

namespace Verbeam.Tests;

public sealed class MemoryBearerJwtKeyStoreTests
{
    [Fact]
    public async Task GetJwksJsonAsync_FetchesRemoteJwksUrlAndCachesResponse()
    {
        const string jwksJson = """{"keys":[{"kty":"RSA","kid":"remote-key","use":"sig","n":"abc","e":"AQAB"}]}""";
        var requestCount = 0;
        using var client = new HttpClient(new RespondingHandler(request =>
        {
            requestCount++;
            Assert.Equal("https://issuer.example/keys", request.RequestUri?.ToString());
            return JsonResponse(jwksJson);
        }));
        var store = new MemoryBearerJwtKeyStore(
            new MemoryBearerJwtOptions
            {
                JwksUrl = "https://issuer.example/keys",
                JwksRefreshSeconds = 300
            },
            Directory.GetCurrentDirectory(),
            client);

        var first = await store.GetJwksJsonAsync();
        var second = await store.GetJwksJsonAsync();

        Assert.Equal(jwksJson, first);
        Assert.Equal(jwksJson, second);
        Assert.Equal(1, requestCount);
    }

    [Fact]
    public async Task GetJwksJsonAsync_UsesOidcDiscoveryJwksUriAndCachesResponse()
    {
        const string discoveryJson = """{"issuer":"https://issuer.example","jwks_uri":"https://issuer.example/keys"}""";
        const string jwksJson = """{"keys":[{"kty":"RSA","kid":"oidc-key","use":"sig","n":"abc","e":"AQAB"}]}""";
        var requestCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        using var client = new HttpClient(new RespondingHandler(request =>
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;
            requestCounts[url] = requestCounts.GetValueOrDefault(url) + 1;
            return url switch
            {
                "https://issuer.example/.well-known/openid-configuration" => JsonResponse(discoveryJson),
                "https://issuer.example/keys" => JsonResponse(jwksJson),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        }));
        var store = new MemoryBearerJwtKeyStore(
            new MemoryBearerJwtOptions
            {
                OidcDiscoveryUrl = "https://issuer.example/.well-known/openid-configuration",
                JwksRefreshSeconds = 300
            },
            Directory.GetCurrentDirectory(),
            client);

        var first = await store.GetJwksJsonAsync();
        var second = await store.GetJwksJsonAsync();

        Assert.Equal(jwksJson, first);
        Assert.Equal(jwksJson, second);
        Assert.Equal(1, requestCounts["https://issuer.example/.well-known/openid-configuration"]);
        Assert.Equal(1, requestCounts["https://issuer.example/keys"]);
    }

    [Fact]
    public async Task GetJwksJsonAsync_RejectsNonHttpsRemoteUrls()
    {
        var requestCount = 0;
        using var client = new HttpClient(new RespondingHandler(_ =>
        {
            requestCount++;
            return JsonResponse("""{"keys":[]}""");
        }));
        var store = new MemoryBearerJwtKeyStore(
            new MemoryBearerJwtOptions
            {
                JwksUrl = "http://issuer.example/keys"
            },
            Directory.GetCurrentDirectory(),
            client);

        var jwks = await store.GetJwksJsonAsync();

        Assert.Null(jwks);
        Assert.Equal(0, requestCount);
    }

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class RespondingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
