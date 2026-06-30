using Verbeam.Core.Models;
using Verbeam.Core.Options;
using Verbeam.Core.Services;

namespace Verbeam.Tests;

public sealed class RecurringTextSuppressorTests
{
    private static VerbeamOptions Options(int windowFrames = 10, double minAgeSeconds = 5)
        => new()
        {
            Ocr =
            {
                RealtimeAutoSuppress =
                {
                    WindowFrames = windowFrames,
                    MinAgeSeconds = minAgeSeconds
                }
            }
        };

    // Mutually dissimilar lines: templated strings ("字幕第13句" / "字幕第14句")
    // would fuzzy-merge into one ever-present cluster by design, which is the
    // watermark behavior — test subtitles must not look like that.
    private static readonly string[] SubtitlePool =
    [
        "今天天气真好",
        "他要开始战斗了",
        "故事终于结束了",
        "其他玩家便忍不住",
        "原来是深藏不露的高手",
        "这场比赛太精彩",
        "大家都笑了起来",
        "没想到结局如此反转",
        "主角获得了新的力量",
        "反派露出真面目",
        "观众席爆发出欢呼",
        "下一集会更好看"
    ];

    private static string Subtitle(int frame) => SubtitlePool[frame % SubtitlePool.Length];

    private static RecurringTextSuppressor.Result Frame(
        RecurringTextSuppressor suppressor,
        string sessionId,
        string imageHash,
        params string[] lines)
    {
        var text = string.Join("\n", lines);
        var blocks = lines.Select(line => new OcrTextBlock(line, 0.9, null)).ToArray();
        var document = new OcrDocumentResult
        {
            Pages =
            [
                new OcrPageResult
                {
                    PageIndex = 0,
                    Blocks = lines
                        .Select((line, index) => new OcrBlock { Id = $"p0-b{index}", Type = OcrBlockTypes.Text, Text = line })
                        .ToArray()
                }
            ]
        };
        return suppressor.Process(sessionId, imageHash, text, blocks, document);
    }

    [Fact]
    public void WatermarkVariants_FuzzyClusterIsFlaggedAndSuppressed()
    {
        // Per-frame OCR misreads of the same channel watermark; consecutive
        // variants chain together via fuzzy matching even though the first and
        // third differ in two characters.
        string[] watermark = ["麦亮常带你看漫画", "麦兜常带你看漫画", "支兜常带你看漫画"];
        string[] subtitles = ["今天天气真好", "他要开始战斗了", "故事终于结束了"];

        var now = DateTimeOffset.UtcNow;
        var suppressor = new RecurringTextSuppressor(Options(), () => now);

        RecurringTextSuppressor.Result? first = null;
        RecurringTextSuppressor.Result? last = null;
        for (var i = 0; i < 15; i++)
        {
            // Subtitle dwells 5 frames (~typical); watermark present every frame.
            last = Frame(suppressor, "session", $"hash-{i}", watermark[i % 3], subtitles[i / 5]);
            first ??= last;
            now = now.AddSeconds(1);
        }

        Assert.Empty(first!.SuppressedText);
        Assert.Contains("带你看漫画", first.Text);

        Assert.NotEmpty(last!.SuppressedText);
        Assert.Equal(subtitles[2], last.Text);
        var block = Assert.Single(last.Blocks);
        Assert.Equal(subtitles[2], block.Text);
        var documentBlock = Assert.Single(last.Document!.Pages[0].Blocks);
        Assert.Equal(subtitles[2], documentBlock.Text);
    }

    [Fact]
    public void CollapsedFlatText_WatermarkRemovedAsSubstring()
    {
        // Realtime flat text is whitespace-normalized into a single line; the
        // flagged block text must still disappear from it as a substring.
        const string watermark = "麦兜常带你看漫画";
        var now = DateTimeOffset.UtcNow;
        var suppressor = new RecurringTextSuppressor(Options(), () => now);

        RecurringTextSuppressor.Result? last = null;
        for (var i = 0; i < 15; i++)
        {
            var subtitle = Subtitle(i);
            var blocks = new[]
            {
                new OcrTextBlock(watermark, 0.9, null),
                new OcrTextBlock(subtitle, 0.9, null)
            };
            last = suppressor.Process("session", $"hash-{i}", $"{watermark} {subtitle}", blocks, null);
            now = now.AddSeconds(1);
        }

        Assert.NotEmpty(last!.SuppressedText);
        Assert.Equal(Subtitle(14), last.Text);
        var block = Assert.Single(last.Blocks);
        Assert.Equal(Subtitle(14), block.Text);
    }

    [Fact]
    public void RotatingSubtitles_AreNeverSuppressed()
    {
        string[] subtitles =
        [
            "今天天气真好",
            "他要开始战斗了",
            "故事终于结束了",
            "其他玩家便忍不住将注意力",
            "原来他是深藏不露的高手",
            "这场比赛太精彩了"
        ];

        var now = DateTimeOffset.UtcNow;
        var suppressor = new RecurringTextSuppressor(Options(), () => now);

        for (var i = 0; i < 30; i++)
        {
            var subtitle = subtitles[i / 5 % subtitles.Length];
            var result = Frame(suppressor, "session", $"hash-{i}", subtitle);
            Assert.Empty(result.SuppressedText);
            Assert.Equal(subtitle, result.Text);
            now = now.AddSeconds(1);
        }
    }

    [Fact]
    public void PausedVideo_IdenticalFrames_NeverAccumulateEvidence()
    {
        var now = DateTimeOffset.UtcNow;
        var suppressor = new RecurringTextSuppressor(Options(), () => now);

        // Identical image hash for 40 "seconds": the frame counter must not
        // advance, so even a watermark line stays untouched (and so does the
        // frozen subtitle).
        RecurringTextSuppressor.Result? last = null;
        for (var i = 0; i < 40; i++)
        {
            last = Frame(suppressor, "session", "frozen-hash", "这句字幕停住了", "麦兜常带你看漫画");
            now = now.AddSeconds(1);
        }

        Assert.Empty(last!.SuppressedText);
        Assert.Contains("麦兜常带你看漫画", last.Text);
    }

    [Fact]
    public void FlaggedCluster_IsForgottenAfterAbsence()
    {
        const string watermark = "麦兜常带你看漫画";
        var now = DateTimeOffset.UtcNow;
        var suppressor = new RecurringTextSuppressor(Options(), () => now);

        var frame = 0;
        for (; frame < 12; frame++)
        {
            Frame(suppressor, "session", $"hash-{frame}", watermark, Subtitle(frame));
            now = now.AddSeconds(1);
        }

        // Watermark gone for longer than ClusterExpireSeconds (60s default).
        for (; frame < 85; frame++)
        {
            var result = Frame(suppressor, "session", $"hash-{frame}", Subtitle(frame));
            Assert.Equal(Subtitle(frame), result.Text);
            now = now.AddSeconds(1);
        }

        // The cluster expired, so a reappearance must re-earn the flag instead
        // of being suppressed from old evidence.
        var back = Frame(suppressor, "session", $"hash-{frame}", watermark, "字幕又来了");
        Assert.Empty(back.SuppressedText);
        Assert.Contains(watermark, back.Text);
    }

    [Fact]
    public void Sessions_TrackIndependently()
    {
        const string watermark = "麦兜常带你看漫画";
        var now = DateTimeOffset.UtcNow;
        var suppressor = new RecurringTextSuppressor(Options(), () => now);

        for (var i = 0; i < 15; i++)
        {
            Frame(suppressor, "session-a", $"hash-{i}", watermark, Subtitle(i));
            now = now.AddSeconds(1);
        }

        // A brand-new session has no evidence yet; the same watermark line must
        // survive its first frame there.
        var fresh = Frame(suppressor, "session-b", "hash-0", watermark, "新场景字幕");
        Assert.Empty(fresh.SuppressedText);
        Assert.Contains(watermark, fresh.Text);
    }
}
