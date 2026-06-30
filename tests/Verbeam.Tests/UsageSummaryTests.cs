using Microsoft.Data.Sqlite;
using Verbeam.Core.Models;
using Verbeam.Core.Storage;

namespace Verbeam.Tests;

public sealed class UsageSummaryTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "verbeam-usage-summary-tests-" + Guid.NewGuid());

    private SqliteTranslationEventStore NewStore()
        => new(Path.Combine(_dir, "core.sqlite"));

    private static TranslationEvent Event(string provider, string model, int surface, long input, long output)
        => new(
            Guid.NewGuid().ToString("N"),
            SessionId: "",
            ProfileId: "default",
            TranslationKey: null,
            RequestName: "test",
            SourceText: "src",
            TranslatedText: "dst",
            SourceLanguage: "ja",
            TargetLanguage: "zh-TW",
            Mode: "subtitle",
            Provider: provider,
            GlossaryId: "",
            GlossaryHash: "",
            Engine: "test",
            Model: model,
            LatencyMs: 10,
            CacheHit: false,
            ErrorCode: "0",
            ErrorMessage: "",
            CreatedAt: DateTimeOffset.UtcNow)
        {
            InputTokens = input,
            OutputTokens = output,
            TotalTokens = input + output,
            TokenSource = "test",
            TokenEstimated = true,
            Surface = surface
        };

    [Fact]
    public async Task GetUsageSummary_AggregatesBySurfaceAndProvider()
    {
        var store = NewStore();
        await store.AddEventAsync(Event("llama-cpp", "qwen", TranslationSurface.Region, 100, 50));
        await store.AddEventAsync(Event("llama-cpp", "qwen", TranslationSurface.Region, 200, 80));
        await store.AddEventAsync(Event("api-compatible", "claude-opus-4", TranslationSurface.Text, 40, 20));
        await store.AddEventAsync(Event("llama-cpp", "qwen", TranslationSurface.Ocr, 10, 5));

        var summary = await store.GetUsageSummaryAsync("default", "all", null);

        Assert.Equal(4, summary.TotalRequests);
        Assert.Equal(430 + 60 + 15, summary.TotalTokens);

        // By surface: region(2 reqs, 430 tokens) is largest, then text(60), then ocr(15).
        Assert.Equal(3, summary.BySurface.Count);
        var region = Assert.Single(summary.BySurface, s => s.Surface == "region");
        Assert.Equal(2, region.Requests);
        Assert.Equal(430, region.TotalTokens);
        Assert.Contains(summary.BySurface, s => s.Surface == "text" && s.TotalTokens == 60);
        Assert.Contains(summary.BySurface, s => s.Surface == "ocr" && s.TotalTokens == 15);

        // By provider/model: llama-cpp/qwen aggregates the three region+ocr rows.
        var llama = Assert.Single(summary.ByProvider, p => p.Provider == "llama-cpp" && p.Model == "qwen");
        Assert.Equal(3, llama.Requests);
        Assert.Equal(445, llama.TotalTokens);
    }

    [Fact]
    public async Task GetUsageSummary_UnlabeledSurface_BucketsAsUnknown()
    {
        var store = NewStore();
        // Surface 0 = unknown (e.g. a legacy row or unlabeled path).
        await store.AddEventAsync(Event("llama-cpp", "qwen", TranslationSurface.Unknown, 10, 5));

        var summary = await store.GetUsageSummaryAsync("default", "all", null);

        var unknown = Assert.Single(summary.BySurface);
        Assert.Equal("unknown", unknown.Surface);
        Assert.Equal(15, unknown.TotalTokens);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
