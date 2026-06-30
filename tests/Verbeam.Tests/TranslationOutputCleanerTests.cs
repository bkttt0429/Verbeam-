using Verbeam.Core.Services;

namespace Verbeam.Tests;

public sealed class TranslationOutputCleanerTests
{
    [Fact]
    public void Clean_StripsPromptEchoBeforeTargetLabel()
    {
        var output = string.Join('\n',
            "\u8853\u8a9e\u8868\uff1a",
            "(none)",
            "",
            "\u65e5\u6587\uff1a",
            "\u539f\u7a0b\u5f0f (Source Program)",
            "",
            "\u7e41\u4e2d\u8b6f\u6587\uff1a",
            "\u539f\u59cb\u7a0b\u5f0f");

        var cleaned = TranslationOutputCleaner.Clean(output);

        Assert.Equal("\u539f\u59cb\u7a0b\u5f0f", cleaned);
    }

    [Fact]
    public void Clean_RemovesLeadingGlossaryEchoWhenModelStopsEarly()
    {
        var output = string.Join('\n',
            "\u7a0b\u5f0f\u8868\uff1a",
            "(none)",
            "",
            "\u65e5\u6587\uff1a",
            "\u539f\u7a0b\u5f0f");

        var cleaned = TranslationOutputCleaner.Clean(output);

        Assert.Equal("\u539f\u7a0b\u5f0f", cleaned);
    }

    [Fact]
    public void Clean_RemovesOcrPromptTemplateArtifacts()
    {
        var output = string.Join('\n',
            "\u53f0\u8a9e: \u3008\u7a0b\u5f0f\u3009",
            "\u7e41\u9ad4: \u7576\u7a0b\u5f0f",
            "\u53f0\u8a9e",
            "\u6587\u4ef6.c",
            "\u6587\u4ef6\u7684\u540d\u7a31\u662f<<<TEXT>>>.",
            "\uff08\u8853\u8a9e\u8868\uff09",
            "\uff08\u65e5\u6587\uff09",
            "\uff08\u539f\u6587\uff09",
            "\uff08\u8b6f\u6587\uff09",
            "\u9023\u7d50(\u9023\u63a5)");

        var cleaned = TranslationOutputCleaner.Clean(output);

        Assert.Equal(
            string.Join('\n', "\u7576\u7a0b\u5f0f", "\u6587\u4ef6.c", "\u9023\u7d50(\u9023\u63a5)"),
            cleaned);
    }
}
