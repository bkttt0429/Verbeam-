using System.Text.Json;
using YomiBridge.Core.Models;
using YomiBridge.Core.Services;

namespace YomiBridge.Tests;

public sealed class StoreTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), "yomibridge-store-tests-" + Guid.NewGuid());

    [Fact]
    public async Task PromptPresetStore_LoadsPresetSummaries()
    {
        var presetDirectory = Path.Combine(_tempDirectory, "presets");
        Directory.CreateDirectory(presetDirectory);

        var preset = new PromptPreset
        {
            Id = "test_mode",
            Name = "Test Mode",
            Description = "For tests",
            Version = "42",
            SystemPrompt = "system",
            UserTemplate = "{TEXT}"
        };

        await File.WriteAllTextAsync(
            Path.Combine(presetDirectory, "test_mode.json"),
            JsonSerializer.Serialize(preset, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        var store = new PromptPresetStore(presetDirectory);
        var summaries = await store.ListAsync();
        var loaded = await store.GetRequiredAsync("test_mode");

        Assert.Single(summaries);
        Assert.Equal("42", summaries[0].Version);
        Assert.Equal("system", loaded.SystemPrompt);
    }

    [Fact]
    public async Task GlossaryStore_LoadsSimpleTermMap()
    {
        var glossaryDirectory = Path.Combine(_tempDirectory, "glossaries");
        Directory.CreateDirectory(glossaryDirectory);

        await File.WriteAllTextAsync(
            Path.Combine(glossaryDirectory, "sample.json"),
            """
            {
              "Hero": "Brave One",
              "Potion": "Recovery Item"
            }
            """);

        var store = new GlossaryStore(glossaryDirectory);
        var summaries = await store.ListAsync();
        var glossary = await store.GetOptionalAsync("sample");

        Assert.Single(summaries);
        Assert.Equal(2, glossary.Terms.Count);
        Assert.False(string.IsNullOrWhiteSpace(glossary.Hash));
        Assert.Equal("Brave One", glossary.Terms["Hero"]);
    }

    [Fact]
    public void TimedTextService_ParsesVttSegments()
    {
        var segments = TimedTextService.ParseVtt(
            """
            WEBVTT

            00:00:01.000 --> 00:00:02.500
            hello

            00:00:03.000 --> 00:00:04.000
            world
            """,
            "en");

        Assert.Equal(2, segments.Count);
        Assert.Equal(1, segments[0].StartSeconds);
        Assert.Equal(2.5, segments[0].EndSeconds);
        Assert.Equal("hello", segments[0].Text);
        Assert.Equal("world", segments[1].Text);
    }

    [Fact]
    public void TimedTextService_NormalizesYouTubeRollingCaptions()
    {
        var segments = TimedTextService.ParseVtt(
            """
            WEBVTT
            Kind: captions
            Language: en

            00:00:00.960 --> 00:00:02.869 align:start position:0%
             
            Okay.<00:00:01.199><c> Are</c><00:00:01.360><c> we</c><00:00:01.520><c> streaming</c><00:00:01.920><c> now?</c>

            00:00:02.869 --> 00:00:02.879 align:start position:0%
            Okay. Are we streaming now?
             

            00:00:02.879 --> 00:00:04.630 align:start position:0%
            Okay. Are we streaming now?
            &gt;&gt; I<00:00:03.120><c> think</c><00:00:03.280><c> we</c><00:00:03.520><c> might</c><00:00:03.679><c> be</c><00:00:03.840><c> streaming.</c><00:00:04.400><c> You</c><00:00:04.560><c> want</c>

            00:00:04.640 --> 00:00:06.070 align:start position:0%
            &gt;&gt; I think we might be streaming. You want
            to<00:00:04.799><c> double</c><00:00:05.040><c> check</c><00:00:05.120><c> that,</c><00:00:05.440><c> Victor?</c>
            """,
            "en");

        Assert.Equal(3, segments.Count);
        Assert.Equal("Okay. Are we streaming now?", segments[0].Text);
        Assert.Equal(">> I think we might be streaming. You want", segments[1].Text);
        Assert.Equal("to double check that, Victor?", segments[2].Text);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }
}
