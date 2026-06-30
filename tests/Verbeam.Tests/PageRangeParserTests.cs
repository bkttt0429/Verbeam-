using Verbeam.Core.Services;

namespace Verbeam.Tests;

public sealed class PageRangeParserTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("auto")]
    [InlineData("ALL")]
    public void Parse_BlankOrAuto_SelectsEveryPage(string? spec)
    {
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, PageRangeParser.Parse(spec, 5));
    }

    [Fact]
    public void Parse_SingleNumber_IsOneBased()
    {
        Assert.Equal(new[] { 2 }, PageRangeParser.Parse("3", 5));
    }

    [Fact]
    public void Parse_Range_IsInclusiveAndZeroBased()
    {
        Assert.Equal(new[] { 0, 1, 2 }, PageRangeParser.Parse("1-3", 5));
    }

    [Fact]
    public void Parse_MixedListAndRange_ClampsToPageCount()
    {
        // 1-based "1,3,8-12" over a 10-page file => indices 0,2,7,8,9 (12 clamped away).
        Assert.Equal(new[] { 0, 2, 7, 8, 9 }, PageRangeParser.Parse("1,3,8-12", 10));
    }

    [Fact]
    public void Parse_UnorderedAndDuplicate_IsSortedAndDeduped()
    {
        Assert.Equal(new[] { 0, 1, 2 }, PageRangeParser.Parse("3,1,2,2,1", 5));
    }

    [Fact]
    public void Parse_ReversedRange_IsNormalized()
    {
        Assert.Equal(new[] { 0, 1, 2 }, PageRangeParser.Parse("3-1", 5));
    }

    [Fact]
    public void Parse_OpenEndedRanges_ResolveToBounds()
    {
        Assert.Equal(new[] { 2, 3, 4 }, PageRangeParser.Parse("3-", 5));
        Assert.Equal(new[] { 0, 1 }, PageRangeParser.Parse("-2", 5));
    }

    [Fact]
    public void Parse_AllTokensUnparseable_FallsBackToEveryPage()
    {
        Assert.Equal(new[] { 0, 1, 2 }, PageRangeParser.Parse("abc", 3));
    }

    [Fact]
    public void Parse_GarbageMixedWithValid_KeepsOnlyValid()
    {
        Assert.Equal(new[] { 1 }, PageRangeParser.Parse("2,abc", 5));
    }

    [Fact]
    public void Parse_ValidButFullyOutOfRange_ReturnsEmpty()
    {
        Assert.Empty(PageRangeParser.Parse("999", 3));
    }

    [Fact]
    public void Parse_ZeroPageCount_ReturnsEmpty()
    {
        Assert.Empty(PageRangeParser.Parse("1-3", 0));
    }
}
