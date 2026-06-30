using System.Net;
using System.Text;
using Verbeam.Core.Options;
using Verbeam.Core.Services;

namespace Verbeam.Tests;

public sealed class MemoryOidcClientTests
{
    [Fact]
    public async Task StartLoginAsync_UsesDiscoveryAndBuildsPkceAuthorizationUrl()
    {
        using var client = new HttpClient(new AsyncRespondingHandler(request =>
            Task.FromResult(request.RequestUri?.ToString() switch
            {
                "https://issuer.example/.well-known/openid-configuration" => JsonResponse(
                    """{"authorization_endpoint":"https://issuer.example/authorize","token_endpoint":"https://issuer.example/token"}"""),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            })));
        var oidc = CreateClient(client);

        var login = await oidc.StartLoginAsync();

        Assert.NotNull(login);
        var uri = new Uri(login.AuthorizationUrl);
        Assert.Equal("https://issuer.example/authorize", uri.GetLeftPart(UriPartial.Path));
        Assert.Contains("response_type=code", uri.Query);
        Assert.Contains("client_id=verbeam", uri.Query);
        Assert.Contains("redirect_uri=https%3A%2F%2Fverbeam.example%2Fmemory%2Foidc%2Fcallback", uri.Query);
        Assert.Contains("scope=openid%20profile", uri.Query);
        Assert.Contains($"state={login.State}", uri.Query);
        Assert.Contains("code_challenge=", uri.Query);
        Assert.Contains("code_challenge_method=S256", uri.Query);
    }

    [Fact]
    public async Task ExchangeCodeAsync_PostsCodeVerifierAndConsumesStateOnce()
    {
        var tokenRequestBodies = new List<string>();
        using var client = new HttpClient(new AsyncRespondingHandler(async request =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return JsonResponse(
                    """{"authorization_endpoint":"https://issuer.example/authorize","token_endpoint":"https://issuer.example/token"}""");
            }

            tokenRequestBodies.Add(await request.Content!.ReadAsStringAsync());
            return JsonResponse(
                """{"token_type":"Bearer","access_token":"access.jwt","id_token":"id.jwt","refresh_token":"refresh-token","expires_in":3600}""");
        }));
        var oidc = CreateClient(client);
        var login = await oidc.StartLoginAsync();
        Assert.NotNull(login);

        var tokens = await oidc.ExchangeCodeAsync("auth-code", login.State);
        var replay = await oidc.ExchangeCodeAsync("auth-code", login.State);

        Assert.NotNull(tokens);
        Assert.Equal("access.jwt", tokens.AccessToken);
        Assert.Equal("id.jwt", tokens.IdToken);
        Assert.Equal("refresh-token", tokens.RefreshToken);
        Assert.Null(replay);
        var body = Assert.Single(tokenRequestBodies);
        Assert.Contains("grant_type=authorization_code", body);
        Assert.Contains("client_id=verbeam", body);
        Assert.Contains("client_secret=client-secret", body);
        Assert.Contains("code=auth-code", body);
        Assert.Contains("redirect_uri=https%3A%2F%2Fverbeam.example%2Fmemory%2Foidc%2Fcallback", body);
        Assert.Contains("code_verifier=", body);
    }

    [Fact]
    public async Task RefreshAsync_PostsRefreshTokenToTokenEndpoint()
    {
        var tokenRequestBodies = new List<string>();
        using var client = new HttpClient(new AsyncRespondingHandler(async request =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return JsonResponse(
                    """{"authorization_endpoint":"https://issuer.example/authorize","token_endpoint":"https://issuer.example/token"}""");
            }

            tokenRequestBodies.Add(await request.Content!.ReadAsStringAsync());
            return JsonResponse(
                """{"token_type":"Bearer","access_token":"new-access.jwt","id_token":"new-id.jwt","expires_in":1800}""");
        }));
        var oidc = CreateClient(client);

        var tokens = await oidc.RefreshAsync("refresh-token");

        Assert.NotNull(tokens);
        Assert.Equal("new-access.jwt", tokens.AccessToken);
        Assert.Equal("new-id.jwt", tokens.IdToken);
        var body = Assert.Single(tokenRequestBodies);
        Assert.Contains("grant_type=refresh_token", body);
        Assert.Contains("client_id=verbeam", body);
        Assert.Contains("refresh_token=refresh-token", body);
    }

    [Fact]
    public async Task StartLoginAsync_RejectsInsecureRedirectUri()
    {
        using var client = new HttpClient(new AsyncRespondingHandler(_ =>
            Task.FromResult(JsonResponse(
                """{"authorization_endpoint":"https://issuer.example/authorize","token_endpoint":"https://issuer.example/token"}"""))));
        var oidc = new MemoryOidcClient(
            new MemoryOidcOptions
            {
                Enabled = true,
                DiscoveryUrl = "https://issuer.example/.well-known/openid-configuration",
                ClientId = "verbeam",
                RedirectUri = "http://verbeam.example/memory/oidc/callback"
            },
            client);

        var login = await oidc.StartLoginAsync();

        Assert.Null(login);
    }

    private static MemoryOidcClient CreateClient(HttpClient httpClient)
        => new(
            new MemoryOidcOptions
            {
                Enabled = true,
                DiscoveryUrl = "https://issuer.example/.well-known/openid-configuration",
                ClientId = "verbeam",
                ClientSecret = "client-secret",
                RedirectUri = "https://verbeam.example/memory/oidc/callback",
                Scopes = ["openid", "profile"]
            },
            httpClient);

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class AsyncRespondingHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => respond(request);
    }
}
