using Verbeam.Core.Services;

namespace Verbeam.Tests;

public sealed class TextChunkerTests
{
    [Fact]
    public void Split_ShortText_ReturnsSingleSegment()
    {
        var segments = TextChunker.Split("hello world", 800);

        var only = Assert.Single(segments);
        Assert.Equal("hello world", only.Content);
        Assert.Equal(string.Empty, only.Separator);
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("para one\n\npara two\n\n\npara three trailing")]
    [InlineData("ながい文章。これは二つ目。\n\n別の段落です！最後の文？")]
    [InlineData("line a\nline b\n\nline c")]
    public void Split_Rejoined_ReproducesOriginalExactly(string text)
    {
        var segments = TextChunker.Split(text, 8);

        var rejoined = string.Concat(segments.Select(s => s.Content + s.Separator));
        Assert.Equal(text, rejoined);
    }

    [Fact]
    public void Split_LongText_KeepsContentRunsWithinLimitWhereBoundariesAllow()
    {
        // Three sentences, each <= limit; they must break into separate chunks.
        var text = "これは一つ目の文です。これは二つ目の文です。これは三つ目の文です。";

        var segments = TextChunker.Split(text, 12);

        Assert.True(segments.Count > 1);
        Assert.All(segments, s => Assert.True(s.Content.Length <= 12, $"chunk too long: {s.Content}"));
        Assert.Equal(text, string.Concat(segments.Select(s => s.Content + s.Separator)));
    }

    [Fact]
    public void Split_ParagraphBoundary_SeparatorCarriesBlankLines()
    {
        var text = "first paragraph\n\nsecond paragraph";

        var segments = TextChunker.Split(text, 10);

        // The blank-line break is preserved verbatim as a separator, never translated.
        Assert.Contains(segments, s => s.Separator.Contains("\n\n"));
        Assert.Equal(text, string.Concat(segments.Select(s => s.Content + s.Separator)));
    }

    [Fact]
    public void Split_RunawaySentence_HardSplitsAtCharacterLimit()
    {
        // No sentence punctuation: a single 30-char run must still be cut to <= limit.
        var text = new string('あ', 30);

        var segments = TextChunker.Split(text, 10);

        Assert.Equal(3, segments.Count);
        Assert.All(segments, s => Assert.True(s.Content.Length <= 10));
        Assert.Equal(text, string.Concat(segments.Select(s => s.Content + s.Separator)));
    }

    [Fact]
    public void Split_NonPositiveLimit_ReturnsSingleSegment()
    {
        var segments = TextChunker.Split("anything at all", 0);

        Assert.Single(segments);
    }
}
