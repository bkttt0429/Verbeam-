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

        var builder = new MemoryContextBuilder(store, new VerbeamOptions());

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
    public async Task BuildAsync_HashChangesWhenSelectedMemoryChanges()
    {
        var store = new FakeMemoryStore();
        var updatedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        store.Items.Add(Memory("term-a", "term", "game", "en", "zh-TW", "Star Key", "Star Key TW", updatedAt: updatedAt));
        var builder = new MemoryContextBuilder(store, new VerbeamOptions());
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

    private sealed class FakeMemoryStore : IMemoryStore
    {
        public List<MemoryItem> Items { get; } = [];

        public Task InitializeAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<MemoryItem> AddOrUpdateAsync(MemoryUpsertRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<MemoryItem>> ListAsync(
            string profileId,
            string? memoryKind,
            int limit,
            bool activeOnly,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

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
                .Where(item => item.Confidence >= request.MinimumConfidence)
                .Take(request.Limit)
                .ToArray();

            return Task.FromResult(result);
        }

        public Task<MemoryItem?> FindExactAsync(
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
}
