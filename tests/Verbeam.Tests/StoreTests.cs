using System.Text.Json;
using Microsoft.Data.Sqlite;
using Verbeam.Core.Models;
using Verbeam.Core.Services;
using Verbeam.Core.Storage;

namespace Verbeam.Tests;

public sealed class StoreTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), "verbeam-store-tests-" + Guid.NewGuid());

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

    [Theory]
    [InlineData("（Compile)", "compile")]
    [InlineData("(compile)", "compile")]
    [InlineData(" Compile ", "compile")]
    [InlineData("ＣＯＭＰＩＬＥ", "compile")]
    [InlineData("「Source  Program」", "source program")]
    [InlineData("Source　Program", "source program")]
    [InlineData("（）", "")]
    [InlineData("", "")]
    public void GlossaryStore_NormalizeTerm_StripsWrappersAndWidthAndCase(string input, string expected)
    {
        Assert.Equal(expected, GlossaryStore.NormalizeTerm(input));
    }

    [Theory]
    [InlineData("Executab le", "executable")]
    [InlineData("Source  Program", "sourceprogram")]
    public void GlossaryStore_NormalizeTermCompact_RemovesOcrSplitSpaces(string input, string expected)
    {
        Assert.Equal(expected, GlossaryStore.NormalizeTermCompact(input));
    }

    [Fact]
    public async Task GlossaryStore_BuildsNormalizedTermLookup()
    {
        var glossaryDirectory = Path.Combine(_tempDirectory, "glossaries-normalized");
        Directory.CreateDirectory(glossaryDirectory);

        await File.WriteAllTextAsync(
            Path.Combine(glossaryDirectory, "terms.json"),
            """
            {
              "Compile": "編譯",
              "Source Program": "原程式"
            }
            """);

        var store = new GlossaryStore(glossaryDirectory);
        var glossary = await store.GetOptionalAsync("terms");

        Assert.Equal("編譯", glossary.NormalizedTerms[GlossaryStore.NormalizeTerm("（Compile)")]);
        Assert.Equal("原程式", glossary.NormalizedTerms[GlossaryStore.NormalizeTerm("source  program")]);
        // Mixed blocks that already carry the translated prefix must NOT match.
        Assert.False(glossary.NormalizedTerms.ContainsKey(GlossaryStore.NormalizeTerm("編譯 (Compile)")));

        var empty = await store.GetOptionalAsync(null);
        Assert.Empty(empty.NormalizedTerms);
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

    [Fact]
    public async Task SqliteDocumentJobStore_RoundTripsJobRequestAndEvents()
    {
        var databasePath = Path.Combine(_tempDirectory, "document-jobs.sqlite");
        var store = new SqliteDocumentJobStore(databasePath);
        await store.InitializeAsync();

        var createdAt = DateTimeOffset.UtcNow;
        var request = new DocumentJobRequest
        {
            InputPath = Path.Combine(_tempDirectory, "input.txt"),
            OriginalFileName = "input.txt",
            ContentType = "text/plain",
            SourceKind = "text",
            Source = "ja",
            Target = "zh-TW",
            TranslationProvider = "ollama",
            Profile = "default",
            SessionId = "session-1",
            AllowSharedMemory = true
        };
        var artifact = new DocumentJobArtifact(
            "artifact-1",
            "translated",
            "translated.txt",
            "text/plain",
            Path.Combine(_tempDirectory, "translated.txt"),
            12,
            createdAt);
        var warning = new DocumentJobWarning("sample_warning", "sample warning", "unit:1");
        var job = new DocumentJobStatus(
            "job-1",
            "queued",
            "default",
            "session-1",
            "text",
            "input.txt",
            "text/plain",
            "abc123",
            DocumentJobStages.Queued,
            TotalUnits: null,
            CompletedUnits: 0,
            Progress: 0,
            ArtifactCount: 1,
            WarningCount: 1,
            ErrorCode: string.Empty,
            ErrorMessage: string.Empty,
            createdAt,
            StartedAt: null,
            CompletedAt: null,
            createdAt)
        {
            Artifacts = [artifact],
            Warnings = [warning]
        };

        await store.AddJobAsync(job, request);
        await store.AddEventAsync(job.Id, "job_queued", new { job.Id });

        var loaded = await store.GetJobAsync(job.Id);
        var loadedRequest = await store.GetRequestAsync(job.Id);
        var events = await store.ListEventsAsync(job.Id, afterSequence: 0, limit: 10);

        Assert.NotNull(loaded);
        Assert.Equal("text", loaded.SourceKind);
        Assert.Single(loaded.Artifacts);
        Assert.Single(loaded.Warnings);
        Assert.NotNull(loadedRequest);
        Assert.Equal("zh-TW", loadedRequest.Target);
        Assert.True(loadedRequest.AllowSharedMemory);
        Assert.Single(events);
        Assert.Equal("job_queued", events[0].Type);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }
}
