using Verbeam.Core.Services;

namespace Verbeam.Tests;

public sealed class RealtimeContextWindowTests
{
    private const string Scope = "jazh-TWgame_dialoguemockmocksession-1";

    [Fact]
    public void BuildContext_ReturnsNullWhenEmpty()
    {
        var window = new RealtimeContextWindow();

        Assert.Null(window.BuildContext(Scope));
        Assert.Null(window.BuildContext(string.Empty));
    }

    [Fact]
    public void BuildContext_RendersPairsOldestFirst()
    {
        var window = new RealtimeContextWindow();
        window.Append(Scope, "おはよう", "早安");
        window.Append(Scope, "元気？", "你好嗎？");

        var context = window.BuildContext(Scope);

        Assert.NotNull(context);
        var lines = context!.Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.Equal("おはよう => 早安", lines[1]);
        Assert.Equal("元気？ => 你好嗎？", lines[2]);
    }

    [Fact]
    public void BuildContext_ExcludesTheLineBeingTranslated()
    {
        var window = new RealtimeContextWindow();
        window.Append(Scope, "おはよう", "早安");

        Assert.Null(window.BuildContext(Scope, "おはよう"));
        Assert.Null(window.BuildContext(Scope, " おはよう "));
    }

    [Fact]
    public void Append_RepeatedSourceMovesToMostRecentWithoutDuplicating()
    {
        var window = new RealtimeContextWindow();
        window.Append(Scope, "A", "甲");
        window.Append(Scope, "B", "乙");
        // Same subtitle re-confirmed across frames: replaces, never floods.
        window.Append(Scope, "A", "甲2");

        var context = window.BuildContext(Scope);

        Assert.NotNull(context);
        var lines = context!.Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.Equal("B => 乙", lines[1]);
        Assert.Equal("A => 甲2", lines[2]);
    }

    [Fact]
    public void Append_EvictsOldestBeyondCapacity()
    {
        var window = new RealtimeContextWindow();
        for (var i = 0; i < RealtimeContextWindow.MaxPairsPerScope + 2; i++)
        {
            window.Append(Scope, $"line {i}", $"譯 {i}");
        }

        var context = window.BuildContext(Scope);

        Assert.NotNull(context);
        Assert.DoesNotContain("line 0 ", context);
        Assert.DoesNotContain("line 1 ", context);
        Assert.Contains("line 2 => 譯 2", context);
        Assert.Contains($"line {RealtimeContextWindow.MaxPairsPerScope + 1} => 譯 {RealtimeContextWindow.MaxPairsPerScope + 1}", context);
    }

    [Fact]
    public void Append_IsolatesScopes()
    {
        var window = new RealtimeContextWindow();
        window.Append(Scope, "おはよう", "早安");

        Assert.Null(window.BuildContext(Scope + "-other"));
    }

    [Theory]
    [InlineData("", "譯文")]
    [InlineData("原文", "")]
    [InlineData("   ", "譯文")]
    public void Append_SkipsBlankPairs(string source, string translated)
    {
        var window = new RealtimeContextWindow();
        window.Append(Scope, source, translated);

        Assert.Null(window.BuildContext(Scope));
    }

    [Fact]
    public void Append_SkipsOverlongLines()
    {
        var window = new RealtimeContextWindow();
        window.Append(Scope, new string('あ', 300), "短譯文");

        Assert.Null(window.BuildContext(Scope));
    }

    [Fact]
    public void BuildContext_FlattensEmbeddedNewlines()
    {
        var window = new RealtimeContextWindow();
        window.Append(Scope, "二\n行", "兩\r\n行");

        var context = window.BuildContext(Scope);

        Assert.NotNull(context);
        Assert.Equal(2, context!.Split('\n').Length);
        Assert.Contains("二 行 => 兩 行", context);
    }
}
