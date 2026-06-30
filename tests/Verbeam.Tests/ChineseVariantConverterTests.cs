using Verbeam.Core.Services;

namespace Verbeam.Tests;

public sealed class ChineseVariantConverterTests
{
    private readonly ChineseVariantConverter _cc = ChineseVariantConverter.Shared;

    // Golden pairs generated from python `opencc` s2tw — the source of truth.
    // Includes the tricky one-to-many characters (发/干/里/后/台/制/了...) that
    // require the phrase dictionary, not just char-level mapping.
    [Theory]
    [InlineData("头发", "頭髮")]
    [InlineData("干净", "乾淨")]
    [InlineData("干燥", "乾燥")]
    [InlineData("里面", "裡面")]
    [InlineData("这里", "這裡")]
    [InlineData("皇后", "皇后")]
    [InlineData("以后", "以後")]
    [InlineData("台湾", "臺灣")]
    [InlineData("软件", "軟件")]
    [InlineData("复杂", "複雜")]
    [InlineData("范围", "範圍")]
    [InlineData("面条", "麵條")]
    [InlineData("历史", "歷史")]
    [InlineData("钟表", "鐘錶")]
    [InlineData("系统", "系統")]
    [InlineData("了解", "瞭解")]
    [InlineData("云计算", "雲計算")]
    [InlineData("奥德赛", "奧德賽")]
    [InlineData("制作", "製作")]
    [InlineData("雷雨交加的夜晚，高耸的山顶上耸立着一座复古的建筑。", "雷雨交加的夜晚，高聳的山頂上聳立著一座復古的建築。")]
    [InlineData("因为它是西方文学的源头，影响了在它之后出现的所有作品。", "因為它是西方文學的源頭，影響了在它之後出現的所有作品。")]
    [InlineData("笼罩在金色的光辉中，有着六片金色羽翼的天使。", "籠罩在金色的光輝中，有著六片金色羽翼的天使。")]
    [InlineData("他能感应到周围的环境，一开始只有几厘米，现在已经到了10米。", "他能感應到周圍的環境，一開始只有幾釐米，現在已經到了10米。")]
    public void ToTraditionalTaiwan_MatchesOpenCc(string src, string expected)
        => Assert.Equal(expected, _cc.ToTraditionalTaiwan(src));

    [Theory]
    [InlineData("這個時間還沒到", "這個時間還沒到")] // already Traditional -> no-op
    [InlineData("精神病院", "精神病院")]              // variant-neutral -> no-op
    [InlineData("大家好世界今天的天", "大家好世界今天的天")] // neutral -> no-op
    [InlineData("Hello 世界 123", "Hello 世界 123")]  // mixed/ASCII preserved
    [InlineData("", "")]
    public void ToTraditionalTaiwan_NoOpOnNonSimplified(string src, string expected)
        => Assert.Equal(expected, _cc.ToTraditionalTaiwan(src));

    [Fact]
    public void ToTraditionalTaiwan_LeavesNoConvertibleSimplified()
    {
        // The whole point: output must contain no Simplified characters.
        var src = "雷雨交加的夜晚他来到这该死的精神病院敲门发现门开了";
        var outp = _cc.ToTraditionalTaiwan(src);
        // re-converting an already-Traditional string is a no-op
        Assert.Equal(outp, _cc.ToTraditionalTaiwan(outp));
        Assert.DoesNotContain("来", outp);
        Assert.DoesNotContain("这", outp);
        Assert.DoesNotContain("门", outp);
        Assert.DoesNotContain("发", outp);
    }

    [Fact]
    public void ToSimplified_RoundTripsCommonText()
    {
        Assert.Equal("头发", _cc.ToSimplified("頭髮"));
        Assert.Equal("这里面", _cc.ToSimplified("這裡面"));
    }
}
