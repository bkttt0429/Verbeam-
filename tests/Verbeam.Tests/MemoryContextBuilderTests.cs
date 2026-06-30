using System.Diagnostics;
using System.Text;
using Verbeam.Core.Models;
using Verbeam.Core.Options;
using Verbeam.Core.Services;
using Verbeam.Core.Storage;

namespace Verbeam.Tests;

public sealed class MemoryContextBuilderTests
{
    [Fact]
    public async Task BuildAsync_RendersOnlyRelevantTrustedMemory()
    {
        var store = new FakeMemoryStore();
        store.Items.Add(Memory("term-a", "term", "game", "en", "zh-TW", "Star Key", "Star Key TW", priority: 100));
        store.Items.Add(Memory("ocr-a", "ocr_correction", "game", "en", "zh-TW", "5tar Key", "Star Key", priority: 90));
        store.Items.Add(Memory("term-b", "term", "game", "en", "zh-TW", "Moon Key", "Moon Key TW", priority: 100));
        store.Items.Add(Memory("term-c", "term", "other", "en", "zh-TW", "Star Key", "Other Profile", priority: 100));
        store.Items.Add(Memory("term-d", "term", "game", "en", "zh-TW", "Star Key", "Untrusted", trustLevel: RagSecurityPolicy.UntrustedImport));

        var events = new FakeTranslationEventStore();
        var builder = new MemoryContextBuilder(store, events, new VerbeamOptions());

        var context = await builder.BuildAsync(
            new MemoryContextRequest(
                "game",
                "session-a",
                "en",
                "zh-TW",
                "game_dialogue",
                "Use Star Key after the 5tar Key OCR glitch."));

        Assert.Contains("RAG_CONTEXT_BEGIN", context.Text);
        Assert.Contains("Star Key => Star Key TW", context.Text);
        Assert.Contains("5tar Key => Star Key", context.Text);
        Assert.DoesNotContain("Moon Key TW", context.Text);
        Assert.DoesNotContain("Other Profile", context.Text);
        Assert.DoesNotContain("Untrusted", context.Text);
        Assert.Contains("term-a", context.MemoryIds);
        Assert.Contains("ocr-a", context.MemoryIds);
        Assert.False(string.IsNullOrWhiteSpace(context.Hash));
    }

    [Fact]
    public async Task BuildAsync_UsesUserVerifiedMemoryInsteadOfLocalGeneratedCandidates()
    {
        var store = new FakeMemoryStore();
        store.Items.Add(Memory(
            "term-auto",
            "term",
            "game",
            "en",
            "zh-TW",
            "Star Key",
            "Auto Star Key",
            priority: 1000,
            trustLevel: RagSecurityPolicy.LocalGenerated));
        store.Items.Add(Memory(
            "term-user",
            "term",
            "game",
            "en",
            "zh-TW",
            "Star Key",
            "User Star Key",
            priority: 0,
            trustLevel: RagSecurityPolicy.UserVerified));
        var events = new FakeTranslationEventStore();
        var builder = new MemoryContextBuilder(store, events, new VerbeamOptions());

        var context = await builder.BuildAsync(
            new MemoryContextRequest("game", "session-a", "en", "zh-TW", "game_dialogue", "Use Star Key."));

        Assert.Contains("User Star Key", context.Text);
        Assert.DoesNotContain("Auto Star Key", context.Text);
        Assert.Equal(["term-user"], context.MemoryIds);
    }

    [Fact]
    public async Task BuildAsync_SemanticRetrievalAddsRelatedMemoryBelowExactUserMemory()
    {
        var store = new FakeMemoryStore();
        store.Items.Add(Memory("term-exact", "term", "game", "en", "zh-TW", "Star Key", "Star Key TW"));
        store.Items.Add(Memory("term-semantic", "term", "game", "en", "zh-TW", "Final gate seal", "Final Gate Seal TW"));
        var events = new FakeTranslationEventStore();
        var options = new VerbeamOptions
        {
            Memory =
            {
                SemanticRetrievalEnabled = true,
                SemanticMinimumSimilarity = 0.9,
                MaxTerms = 2,
                MaxRecentLines = 0
            }
        };
        var builder = new MemoryContextBuilder(store, events, options, new KeywordEmbeddingProvider());

        var debug = await builder.BuildDebugAsync(
            new MemoryContextRequest(
                "game",
                "session-a",
                "en",
                "zh-TW",
                "game_dialogue",
                "Use Star Key at the final boss door."));

        Assert.Equal(["term-exact", "term-semantic"], debug.Items.Select(item => item.Id));
        Assert.Equal("source_contains_term", debug.Items[0].Reason);
        Assert.Equal("semantic_match", debug.Items[1].Reason);
        Assert.True(debug.Items[0].Score > debug.Items[1].Score);
        Assert.Contains("Final gate seal => Final Gate Seal TW", debug.RenderedContext);
    }

    [Fact]
    public async Task BuildAsync_SemanticTimeoutFallsBackToLexicalMemory()
    {
        var store = new FakeMemoryStore();
        store.Items.Add(Memory("term-exact", "term", "game", "en", "zh-TW", "Star Key", "Star Key TW"));
        store.Items.Add(Memory("term-semantic", "term", "game", "en", "zh-TW", "Final gate seal", "Final Gate Seal TW"));
        var events = new FakeTranslationEventStore();
        var options = new VerbeamOptions
        {
            Memory =
            {
                SemanticRetrievalEnabled = true,
                SemanticTimeoutMs = 5,
                MaxTerms = 2,
                MaxRecentLines = 0
            }
        };
        var builder = new MemoryContextBuilder(store, events, options, new DelayedEmbeddingProvider(TimeSpan.FromSeconds(5)));

        var stopwatch = Stopwatch.StartNew();
        var context = await builder.BuildAsync(
            new MemoryContextRequest(
                "game",
                "session-a",
                "en",
                "zh-TW",
                "game_dialogue",
                "Use Star Key at the final boss door."));
        stopwatch.Stop();

        Assert.Equal(["term-exact"], context.MemoryIds);
        Assert.Contains("Star Key => Star Key TW", context.Text);
        Assert.DoesNotContain("Final Gate Seal TW", context.Text);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task BuildDebugAsync_ReturnsSameHashWithScoresAndReasons()
    {
        var store = new FakeMemoryStore();
        store.Items.Add(Memory("term-a", "term", "game", "en", "zh-TW", "Star Key", "Star Key TW", priority: 100));
        var events = new FakeTranslationEventStore();
        var builder = new MemoryContextBuilder(store, events, new VerbeamOptions());
        var request = new MemoryContextRequest("game", "session-a", "en", "zh-TW", "game_dialogue", "Use Star Key.");

        var context = await builder.BuildAsync(request);
        var debug = await builder.BuildDebugAsync(request);

        Assert.Equal(context.Hash, debug.ContextHash);
        Assert.Equal(context.Text, debug.RenderedContext);
        Assert.Equal(context.Text.Length, debug.ContextCharacterCount);
        Assert.Equal(context.Snippets.Count, debug.SelectedMemoryCount);
        Assert.Equal(context.RecentEventIds.Count, debug.SelectedRecentEventCount);
        Assert.Equal("memory-context-v1", debug.PolicyVersion);
        Assert.Equal(1, debug.CandidateCount);
        Assert.True(debug.RetrievalElapsedMs >= 0);
        var item = Assert.Single(debug.Items);
        Assert.Equal("term-a", item.Id);
        Assert.Equal("source_contains_term", item.Reason);
        Assert.True(item.Score > 0);
    }

    [Fact]
    public async Task BuildAsync_RendersRecentContextFromSameSessionOnly()
    {
        var store = new FakeMemoryStore();
        var events = new FakeTranslationEventStore();
        events.Events.Add(Event("recent-a", "game", "session-a", "en", "zh-TW", "game_dialogue", "First line", "First translation", 1));
        events.Events.Add(Event("recent-b", "game", "session-a", "en", "zh-TW", "game_dialogue", "Second line", "Second translation", 2));
        events.Events.Add(Event("same-source", "game", "session-a", "en", "zh-TW", "game_dialogue", "Current line", "Current translation", 3));
        events.Events.Add(Event("other-session", "game", "session-b", "en", "zh-TW", "game_dialogue", "Other session", "Other session translation", 4));
        events.Events.Add(Event("other-profile", "other", "session-a", "en", "zh-TW", "game_dialogue", "Other profile", "Other profile translation", 5));
        events.Events.Add(Event("failed", "game", "session-a", "en", "zh-TW", "game_dialogue", "Failed line", "", 6, errorCode: "1"));
        var builder = new MemoryContextBuilder(store, events, new VerbeamOptions());

        var context = await builder.BuildAsync(
            new MemoryContextRequest("game", "session-a", "en", "zh-TW", "game_dialogue", "Current line"));

        Assert.Contains("Recent context:", context.Text);
        Assert.Contains("First line", context.Text);
        Assert.Contains("Second translation", context.Text);
        Assert.DoesNotContain("Current translation", context.Text);
        Assert.DoesNotContain("Other session translation", context.Text);
        Assert.DoesNotContain("Other profile translation", context.Text);
        Assert.DoesNotContain("Failed line", context.Text);
        Assert.Equal(["recent-a", "recent-b"], context.RecentEventIds);
        Assert.False(string.IsNullOrWhiteSpace(context.Hash));
    }

    [Fact]
    public async Task BuildAsync_RecentContextRespectsLineLimit()
    {
        var store = new FakeMemoryStore();
        var events = new FakeTranslationEventStore();
        events.Events.Add(Event("recent-a", "game", "session-a", "en", "zh-TW", "game_dialogue", "First line", "First translation", 1));
        events.Events.Add(Event("recent-b", "game", "session-a", "en", "zh-TW", "game_dialogue", "Second line", "Second translation", 2));
        events.Events.Add(Event("recent-c", "game", "session-a", "en", "zh-TW", "game_dialogue", "Third line", "Third translation", 3));
        var options = new VerbeamOptions
        {
            Memory = { MaxRecentLines = 2 }
        };
        var builder = new MemoryContextBuilder(store, events, options);

        var context = await builder.BuildAsync(
            new MemoryContextRequest("game", "session-a", "en", "zh-TW", "game_dialogue", "Current line"));

        Assert.Equal(["recent-b", "recent-c"], context.RecentEventIds);
        Assert.DoesNotContain("First translation", context.Text);
        Assert.Contains("Second translation", context.Text);
        Assert.Contains("Third translation", context.Text);
    }

    [Fact]
    public async Task BuildAsync_RendersSceneSummaryAndIncludesItInHash()
    {
        var store = new FakeMemoryStore();
        var events = new FakeTranslationEventStore();
        var summaries = new FakeSceneSummaryStore();
        summaries.Summaries.Add(Summary(
            "summary-a",
            "game",
            "session-a",
            "The party is trapped in the tower.",
            "recent-a",
            "recent-b",
            1));
        var builder = new MemoryContextBuilder(store, events, summaries, new VerbeamOptions());
        var request = new MemoryContextRequest("game", "session-a", "en", "zh-TW", "game_dialogue", "Current line");

        var first = await builder.BuildAsync(request);

        summaries.Summaries.Clear();
        summaries.Summaries.Add(Summary(
            "summary-a",
            "game",
            "session-a",
            "The party escaped the tower.",
            "recent-a",
            "recent-b",
            2));
        var second = await builder.BuildAsync(request);

        Assert.Contains("Scene summary:", first.Text);
        Assert.Contains("trapped in the tower", first.Text);
        Assert.Equal(["summary-a"], first.SceneSummaryIds);
        Assert.NotEqual(first.Hash, second.Hash);
        Assert.Contains("escaped the tower", second.Text);
    }

    [Fact]
    public async Task BuildAsync_RecentContextHashChangesWhenRecentEventChanges()
    {
        var store = new FakeMemoryStore();
        var events = new FakeTranslationEventStore();
        events.Events.Add(Event("recent-a", "game", "session-a", "en", "zh-TW", "game_dialogue", "First line", "First translation", 1));
        var builder = new MemoryContextBuilder(store, events, new VerbeamOptions());
        var request = new MemoryContextRequest("game", "session-a", "en", "zh-TW", "game_dialogue", "Current line");

        var first = await builder.BuildAsync(request);

        events.Events.Add(Event("recent-b", "game", "session-a", "en", "zh-TW", "game_dialogue", "Second line", "Second translation", 2));
        var second = await builder.BuildAsync(request);

        Assert.NotEqual(first.Hash, second.Hash);
        Assert.Equal(["recent-a", "recent-b"], second.RecentEventIds);
    }

    [Fact]
    public async Task BuildAsync_HashChangesWhenSelectedMemoryChanges()
    {
        var store = new FakeMemoryStore();
        var updatedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        store.Items.Add(Memory("term-a", "term", "game", "en", "zh-TW", "Star Key", "Star Key TW", updatedAt: updatedAt));
        var events = new FakeTranslationEventStore();
        var builder = new MemoryContextBuilder(store, events, new VerbeamOptions());
        var request = new MemoryContextRequest("game", "session-a", "en", "zh-TW", "game_dialogue", "Use Star Key.");

        var first = await builder.BuildAsync(request);

        store.Items.Clear();
        store.Items.Add(Memory("term-a", "term", "game", "en", "zh-TW", "Star Key", "Astral Key TW", updatedAt: updatedAt.AddMinutes(1)));
        var second = await builder.BuildAsync(request);

        Assert.NotEqual(first.Hash, second.Hash);
        Assert.Contains("Astral Key TW", second.Text);
    }

    private static MemoryItem Memory(
        string id,
        string memoryKind,
        string profileId,
        string source,
        string target,
        string sourceText,
        string targetText,
        int priority = 0,
        double confidence = 1.0,
        string trustLevel = RagSecurityPolicy.UserVerified,
        DateTimeOffset? updatedAt = null)
    {
        var timestamp = updatedAt ?? DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        return new MemoryItem(
            id,
            profileId,
            memoryKind,
            source,
            target,
            sourceText,
            targetText,
            string.Empty,
            priority,
            confidence,
            "[]",
            "{}",
            trustLevel,
            string.Empty,
            RagSecurityPolicy.ComputeSourceHash(sourceText, targetText),
            string.Empty,
            string.Empty,
            "[]",
            "normal",
            "profile",
            IsActive: true,
            timestamp,
            timestamp,
            LastUsedAt: null,
            UseCount: 0);
    }

    private static TranslationEvent Event(
        string id,
        string profileId,
        string sessionId,
        string source,
        string target,
        string mode,
        string sourceText,
        string translatedText,
        int minutes,
        string errorCode = "0")
        => new(
            id,
            sessionId,
            profileId,
            "translation-key-" + id,
            id,
            sourceText,
            translatedText,
            source,
            target,
            mode,
            "mock",
            string.Empty,
            string.Empty,
            "mock",
            "mock",
            1,
            false,
            errorCode,
            string.Empty,
            DateTimeOffset.Parse("2026-01-01T00:00:00Z").AddMinutes(minutes));

    private static SceneSummary Summary(
        string id,
        string profileId,
        string sessionId,
        string summaryText,
        string startEventId,
        string endEventId,
        int minutes)
        => new(
            id,
            sessionId,
            profileId,
            summaryText,
            startEventId,
            endEventId,
            DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            DateTimeOffset.Parse("2026-01-01T00:00:00Z").AddMinutes(minutes));

    private sealed class KeywordEmbeddingProvider : IEmbeddingProvider
    {
        public string Model => "test-keyword-v1";

        public int Dimensions => 3;

        public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var normalized = text.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
            if (normalized.Contains("final", StringComparison.Ordinal) ||
                normalized.Contains("boss", StringComparison.Ordinal) ||
                normalized.Contains("gate", StringComparison.Ordinal) ||
                normalized.Contains("door", StringComparison.Ordinal) ||
                normalized.Contains("seal", StringComparison.Ordinal))
            {
                return Task.FromResult(new[] { 1f, 0f, 0f });
            }

            if (normalized.Contains("star", StringComparison.Ordinal) ||
                normalized.Contains("key", StringComparison.Ordinal))
            {
                return Task.FromResult(new[] { 0f, 1f, 0f });
            }

            return Task.FromResult(new[] { 0f, 0f, 1f });
        }
    }

    private sealed class DelayedEmbeddingProvider : IEmbeddingProvider
    {
        private readonly TimeSpan _delay;

        public DelayedEmbeddingProvider(TimeSpan delay)
        {
            _delay = delay;
        }

        public string Model => "test-delayed-v1";

        public int Dimensions => 3;

        public async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
        {
            await Task.Delay(_delay, cancellationToken);
            return [0f, 1f, 0f];
        }
    }

    private sealed class FakeMemoryStore : IMemoryStore
    {
        public List<MemoryItem> Items { get; } = [];
        public List<MemoryEmbedding> Embeddings { get; } = [];

        public Task InitializeAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<MemoryItem> AddOrUpdateAsync(MemoryUpsertRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<MemoryItem?> GetAsync(string id, CancellationToken cancellationToken = default)
            => Task.FromResult(Items.FirstOrDefault(item => item.Id == id));

        public Task<MemoryItem?> UpdateTrustAsync(
            string id,
            MemoryTrustUpdateRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<MemoryItem?> UpdateAsync(
            string id,
            MemoryUpdateRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<MemoryItem>> ListAsync(
            string profileId,
            string? memoryKind,
            int limit,
            bool activeOnly,
            string? trustLevel = null,
            string? sourceLanguage = null,
            string? targetLanguage = null,
            string? visibility = null,
            string? query = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<int> CountAsync(
            string profileId,
            bool activeOnly,
            string? trustLevel = null,
            CancellationToken cancellationToken = default)
        {
            var query = Items
                .Where(item => item.ProfileId == profileId)
                .Where(item => !activeOnly || item.IsActive);
            if (!string.IsNullOrWhiteSpace(trustLevel))
            {
                query = query.Where(item => item.TrustLevel == trustLevel);
            }

            return Task.FromResult(query.Count());
        }

        public Task<IReadOnlyList<MemoryItem>> SearchAsync(
            MemorySearchRequest request,
            CancellationToken cancellationToken = default)
        {
            var kinds = request.MemoryKinds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            IReadOnlyList<MemoryItem> result = Items
                .Where(item => item.ProfileId == request.ProfileId)
                .Where(item => item.SourceLanguage == request.SourceLanguage)
                .Where(item => item.TargetLanguage == request.TargetLanguage)
                .Where(item => kinds.Contains(item.MemoryKind))
                .Where(item => !request.ActiveOnly || item.IsActive)
                .Where(item => !request.TrustedOnly || item.TrustLevel is RagSecurityPolicy.UserVerified or RagSecurityPolicy.TrustedImport)
                .Where(item => request.IncludeShared || item.Visibility != "shared")
                .Where(item => item.Confidence >= request.MinimumConfidence)
                .Take(request.Limit)
                .ToArray();

            return Task.FromResult(result);
        }

        public Task UpsertEmbeddingAsync(
            MemoryEmbedding embedding,
            CancellationToken cancellationToken = default)
        {
            Embeddings.RemoveAll(item =>
                string.Equals(item.MemoryId, embedding.MemoryId, StringComparison.Ordinal) &&
                string.Equals(item.EmbeddingModel, embedding.EmbeddingModel, StringComparison.Ordinal));
            Embeddings.Add(embedding);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<MemoryEmbedding>> ListEmbeddingsAsync(
            IReadOnlyList<string> memoryIds,
            string embeddingModel,
            CancellationToken cancellationToken = default)
        {
            var ids = memoryIds.ToHashSet(StringComparer.Ordinal);
            IReadOnlyList<MemoryEmbedding> result = Embeddings
                .Where(item => ids.Contains(item.MemoryId))
                .Where(item => string.Equals(item.EmbeddingModel, embeddingModel, StringComparison.Ordinal))
                .ToArray();

            return Task.FromResult(result);
        }

        public Task<MemoryItem?> FindExactAsync(
            string profileId,
            string memoryKind,
            string sourceLanguage,
            string targetLanguage,
            string sourceText,
            CancellationToken cancellationToken = default,
            bool includeShared = false)
            => Task.FromResult<MemoryItem?>(null);

        public Task<MemoryItem?> FindByKeyAsync(
            string profileId,
            string memoryKind,
            string sourceLanguage,
            string targetLanguage,
            string sourceText,
            CancellationToken cancellationToken = default)
            => Task.FromResult<MemoryItem?>(null);

        public Task RecordUseAsync(IReadOnlyList<string> memoryIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class FakeTranslationEventStore : ITranslationEventStore
    {
        public List<TranslationEvent> Events { get; } = [];

        public Task InitializeAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task AddEventAsync(TranslationEvent entry, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<TranslationEvent?> GetEventAsync(string id, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<TranslationEvent>> ListEventsAsync(
            string profileId,
            int limit,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<TranslationEvent>> ListRecentContextAsync(
            string profileId,
            string sessionId,
            string sourceLanguage,
            string targetLanguage,
            string mode,
            string excludeSourceText,
            int limit,
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<TranslationEvent> result = Events
                .Where(item => item.ProfileId == profileId)
                .Where(item => item.SessionId == sessionId)
                .Where(item => item.SourceLanguage == sourceLanguage)
                .Where(item => item.TargetLanguage == targetLanguage)
                .Where(item => item.Mode == mode)
                .Where(item => item.ErrorCode == "0")
                .Where(item => !string.IsNullOrWhiteSpace(item.TranslatedText))
                .Where(item => item.SourceText != excludeSourceText)
                .OrderByDescending(item => item.CreatedAt)
                .Take(limit)
                .ToArray();

            return Task.FromResult(result);
        }

        public Task<IReadOnlyList<TranslationEvent>> ListSessionSuccessEventsAsync(
            string profileId,
            string sessionId,
            string sourceLanguage,
            string targetLanguage,
            string mode,
            int limit,
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<TranslationEvent> result = Events
                .Where(item => item.ProfileId == profileId)
                .Where(item => item.SessionId == sessionId)
                .Where(item => item.SourceLanguage == sourceLanguage)
                .Where(item => item.TargetLanguage == targetLanguage)
                .Where(item => item.Mode == mode)
                .Where(item => item.ErrorCode == "0")
                .Where(item => !string.IsNullOrWhiteSpace(item.TranslatedText))
                .OrderByDescending(item => item.CreatedAt)
                .Take(limit)
                .OrderBy(item => item.CreatedAt)
                .ToArray();

            return Task.FromResult(result);
        }

    }

    private sealed class FakeSceneSummaryStore : ISceneSummaryStore
    {
        public List<SceneSummary> Summaries { get; } = [];

        public Task InitializeAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<SceneSummary> AddOrUpdateAsync(
            SceneSummaryUpsertRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SceneSummary?> GetLatestAsync(
            string profileId,
            string sessionId,
            CancellationToken cancellationToken = default)
        {
            var summary = Summaries
                .Where(item => item.ProfileId == profileId)
                .Where(item => item.SessionId == sessionId)
                .OrderByDescending(item => item.UpdatedAt)
                .FirstOrDefault();

            return Task.FromResult(summary);
        }

        public Task<IReadOnlyList<SceneSummary>> ListAsync(
            string profileId,
            string? sessionId,
            int limit,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
