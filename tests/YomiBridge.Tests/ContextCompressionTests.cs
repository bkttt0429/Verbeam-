using YomiBridge.Core.Options;
using YomiBridge.Core.Services;

namespace YomiBridge.Tests;

public sealed class ContextCompressionTests
{
    [Fact]
    public void Compress_ReturnsEmptyForBlankContext()
    {
        var service = new ContextCompressionService(new ContextCompressionOptions());

        var result = service.Compress([null, "", "   "]);

        Assert.Equal(string.Empty, result.Text);
        Assert.Equal(string.Empty, result.Hash);
        Assert.False(result.IsCompressed);
    }

    [Fact]
    public void Compress_KeepsShortContextAsIs()
    {
        var service = new ContextCompressionService(new ContextCompressionOptions
        {
            MaxCharacters = 100
        });

        var result = service.Compress(["Mina calls the device Star Key."]);

        Assert.Equal("Mina calls the device Star Key.", result.Text);
        Assert.False(result.IsCompressed);
        Assert.False(string.IsNullOrWhiteSpace(result.Hash));
    }

    [Fact]
    public void Compress_LongContextKeepsBeginningAndEnd()
    {
        var service = new ContextCompressionService(new ContextCompressionOptions
        {
            MaxCharacters = 120,
            HeadCharacters = 50,
            TailCharacters = 50
        });
        var context = string.Join(
            " ",
            "Chapter one establishes that Mina calls the device Star Key.",
            new string('x', 300),
            "The latest scene says she is angry but still formal.");

        var result = service.Compress([context]);

        Assert.True(result.IsCompressed);
        Assert.True(result.Text.Length <= 120);
        Assert.Contains("Chapter one", result.Text);
        Assert.Contains("still formal", result.Text);
        Assert.Contains("compressed", result.Text);
    }

    [Fact]
    public void TranslationCacheKey_WithoutContextMatchesLegacyKey()
    {
        var legacy = TranslationCacheKey.Create(
            "hello",
            "en",
            "zh-TW",
            "web_article",
            "mock",
            "mock",
            "1",
            "glossary");
        var explicitEmptyContext = TranslationCacheKey.Create(
            "hello",
            "en",
            "zh-TW",
            "web_article",
            "mock",
            "mock",
            "1",
            "glossary",
            "");

        Assert.Equal(legacy, explicitEmptyContext);
    }

    [Fact]
    public void TranslationCacheKey_WithContextChangesKey()
    {
        var withoutContext = TranslationCacheKey.Create(
            "hello",
            "en",
            "zh-TW",
            "web_article",
            "mock",
            "mock",
            "1",
            "glossary");
        var withContext = TranslationCacheKey.Create(
            "hello",
            "en",
            "zh-TW",
            "web_article",
            "mock",
            "mock",
            "1",
            "glossary",
            "context-hash");

        Assert.NotEqual(withoutContext, withContext);
    }
}
