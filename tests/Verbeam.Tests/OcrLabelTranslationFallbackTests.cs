using Verbeam.Core.Services;

namespace Verbeam.Tests;

public sealed class OcrLabelTranslationFallbackTests
{
    [Theory]
    [InlineData("(Library)", "\u7a0b\u5f0f\u5eab")]
    [InlineData("(Executab le)", "\u57f7\u884c\u6a94")]
    [InlineData("Source Program", "\u539f\u7a0b\u5f0f")]
    [InlineData("Object Program", "\u76ee\u7684\u7a0b\u5f0f")]
    [InlineData("Preprocess", "\u9810\u5148\u8655\u7406")]
    [InlineData("(Assemble)", "\u7d44\u8b6f")]
    public void TryTranslate_ReturnsKnownCompilerLabel(string input, string expected)
    {
        Assert.Equal(expected, OcrLabelTranslationFallback.TryTranslate(input));
    }

    [Theory]
    [InlineData("(file.obj)", "file.obj")]
    [InlineData("(file.exe)", "file.exe")]
    [InlineData("( *. lib)", "*.lib")]
    public void TryTranslate_NormalizesFileReferenceLabels(string input, string expected)
    {
        Assert.Equal(expected, OcrLabelTranslationFallback.TryTranslate(input));
    }

    [Fact]
    public void TryTranslate_IgnoresNormalOcrProse()
    {
        Assert.Null(OcrLabelTranslationFallback.TryTranslate("This is a longer OCR sentence."));
    }

    [Theory]
    [InlineData("zh-TW", true)]
    [InlineData("zh-Hant-TW", true)]
    [InlineData("ja", false)]
    [InlineData("en", false)]
    public void SupportsTarget_OnlyEnablesTraditionalChineseFallback(string target, bool expected)
    {
        Assert.Equal(expected, OcrLabelTranslationFallback.SupportsTarget(target));
    }
}
