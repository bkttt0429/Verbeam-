using Verbeam.Core.Services;

namespace Verbeam.Tests;

public sealed class DocumentSegmentationTests
{
    // ---- Markdown ----

    [Fact]
    public void Markdown_MergesParagraphLines_AndPreservesBlankLines()
    {
        const string input = "# Title\n\nLine one\nLine two\n\nTail";
        var segments = MarkdownSegmenter.Segment(input);

        var translatable = segments.Where(s => s.Translate).Select(s => s.Text).ToArray();
        Assert.Equal(new[] { "# Title", "Line one\nLine two", "Tail" }, translatable);
    }

    [Fact]
    public void Markdown_FencedCodeBlock_IsVerbatimAndNotTranslated()
    {
        const string input = "before\n\n```python\nx = 1\nprint(x)\n```\n\nafter";
        var segments = MarkdownSegmenter.Segment(input);

        var code = Assert.Single(segments, s => !s.Translate && s.Text.Contains("x = 1"));
        Assert.Equal("```python\nx = 1\nprint(x)\n```", code.Text);
        Assert.DoesNotContain(segments.Where(s => s.Translate), s => s.Text.Contains("x = 1"));
    }

    [Fact]
    public void Markdown_RoundTripsByJoiningOnNewline()
    {
        const string input = "# Title\n\nLine one\nLine two\n\n```\ncode\n```\n";
        var segments = MarkdownSegmenter.Segment(input);

        Assert.Equal(input, string.Join("\n", segments.Select(s => s.Text)));
    }

    [Fact]
    public void Markdown_NormalizesCrlf()
    {
        var segments = MarkdownSegmenter.Segment("a\r\nb");
        Assert.Equal("a\nb", Assert.Single(segments, s => s.Translate).Text);
    }

    [Fact]
    public void Markdown_NoMerge_EmitsOneSegmentPerLine()
    {
        const string input = "Line one\nLine two";
        var segments = MarkdownSegmenter.Segment(input, mergeParagraphs: false);

        Assert.Equal(new[] { "Line one", "Line two" }, segments.Where(s => s.Translate).Select(s => s.Text));
    }

    // ---- HTML ----

    [Fact]
    public void Html_TranslatesTextNodes_NotTags()
    {
        var segments = HtmlTextSegmenter.Segment("<p class=\"x\">Hello</p>");

        Assert.Equal("Hello", Assert.Single(segments, s => s.Translate).Text);
        Assert.Contains(segments, s => !s.Translate && s.Text == "<p class=\"x\">");
        Assert.Contains(segments, s => !s.Translate && s.Text == "</p>");
    }

    [Fact]
    public void Html_SkipsScriptAndStyleContent()
    {
        const string input = "<style>.a{color:red}</style><p>Hi</p><script>var a=1<2;</script>";
        var segments = HtmlTextSegmenter.Segment(input);

        Assert.Equal(new[] { "Hi" }, segments.Where(s => s.Translate).Select(s => s.Text));
        Assert.Contains(segments, s => !s.Translate && s.Text == "<style>.a{color:red}</style>");
        Assert.Contains(segments, s => !s.Translate && s.Text == "<script>var a=1<2;</script>");
    }

    [Fact]
    public void Html_SkipsTitleAndComments()
    {
        const string input = "<title>Page</title><!-- note --><p>Body</p>";
        var segments = HtmlTextSegmenter.Segment(input);

        Assert.Equal(new[] { "Body" }, segments.Where(s => s.Translate).Select(s => s.Text));
        Assert.Contains(segments, s => !s.Translate && s.Text == "<title>Page</title>");
        Assert.Contains(segments, s => !s.Translate && s.Text == "<!-- note -->");
    }

    [Fact]
    public void Html_DecodesEntitiesInTranslatableText()
    {
        var segments = HtmlTextSegmenter.Segment("<p>Tom &amp; Jerry &lt;3</p>");
        Assert.Equal("Tom & Jerry <3", Assert.Single(segments, s => s.Translate).Text);
    }

    [Fact]
    public void Html_LeavesWhitespaceOnlyTextVerbatim()
    {
        var segments = HtmlTextSegmenter.Segment("<p>\n  </p>");
        Assert.DoesNotContain(segments, s => s.Translate);
        Assert.Contains(segments, s => !s.Translate && s.Text == "\n  ");
    }
}
