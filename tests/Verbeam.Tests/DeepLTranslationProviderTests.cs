using System.Net;
using System.Text.Json;
using Verbeam.Core.Models;
using Verbeam.Core.Options;
using Verbeam.Core.Providers;

namespace Verbeam.Tests;

public sealed class DeepLTranslationProviderTests
{
    [Fact]
    public async Task TranslateAsync_UsesFreeEndpointAndTraditionalChineseTarget()
    {
        using var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"translations":[{"detected_source_language":"JA","text":"你好"}]}""",
                System.Text.Encoding.UTF8,
                "application/json")
        });
        using var client = new HttpClient(handler);
        var provider = new DeepLTranslationProvider(
            client,
            new DeepLOptions { ApiKey = "test-key:fx" });

        var result = await provider.TranslateAsync(
            CreateRequest("こんにちは", "ja", "zh-TW", "default"),
            CancellationToken.None);

        Assert.Equal("你好", result.Text);
        Assert.Equal("deepl:default", result.Engine);
        Assert.Equal("https://api-free.deepl.com/v2/translate", handler.RequestUri?.ToString());
        Assert.Equal("DeepL-Auth-Key test-key:fx", handler.Authorization);

        using var document = JsonDocument.Parse(handler.RequestBody!);
        var root = document.RootElement;
        Assert.Equal("こんにちは", root.GetProperty("text")[0].GetString());
        Assert.Equal("JA", root.GetProperty("source_lang").GetString());
        Assert.Equal("ZH-HANT", root.GetProperty("target_lang").GetString());
        Assert.False(root.TryGetProperty("model_type", out _));
    }

    [Fact]
    public async Task TranslateAsync_OmitsAutoSourceAndSendsModelType()
    {
        using var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"translations":[{"detected_source_language":"JA","text":"Hello"}]}""",
                System.Text.Encoding.UTF8,
                "application/json")
        });
        using var client = new HttpClient(handler);
        var provider = new DeepLTranslationProvider(
            client,
            new DeepLOptions { ApiKey = "test-key" });

        var result = await provider.TranslateAsync(
            CreateRequest("こんにちは", "auto", "en", "quality_optimized"),
            CancellationToken.None);

        Assert.Equal("Hello", result.Text);
        Assert.Equal("deepl:quality_optimized", result.Engine);
        Assert.Equal("https://api.deepl.com/v2/translate", handler.RequestUri?.ToString());

        using var document = JsonDocument.Parse(handler.RequestBody!);
        var root = document.RootElement;
        Assert.Equal("EN-US", root.GetProperty("target_lang").GetString());
        Assert.Equal("quality_optimized", root.GetProperty("model_type").GetString());
        Assert.False(root.TryGetProperty("source_lang", out _));
    }

    [Fact]
    public async Task TranslateAsync_ThrowsWhenApiKeyIsMissing()
    {
        using var client = new HttpClient(new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        var provider = new DeepLTranslationProvider(client, new DeepLOptions());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.TranslateAsync(
                CreateRequest("こんにちは", "ja", "zh-TW", "default"),
                CancellationToken.None));

        Assert.Contains("DeepL API key", error.Message);
    }

    private static ProviderTranslationRequest CreateRequest(
        string text,
        string source,
        string target,
        string model)
        => new(
            text,
            source,
            target,
            "game_dialogue",
            model,
            new PromptPreset
            {
                Id = "test",
                Name = "Test",
                SystemPrompt = "Translate.",
                UserTemplate = "{{text}}"
            },
            new Dictionary<string, string>(),
            string.Empty);

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            _respond = respond;
        }

        public Uri? RequestUri { get; private set; }
        public string? Authorization { get; private set; }
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            Authorization = request.Headers.TryGetValues("Authorization", out var values)
                ? Assert.Single(values)
                : null;
            RequestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return _respond(request);
        }
    }
}
