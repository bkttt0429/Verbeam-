using Verbeam.Core.Models;
using Verbeam.Core.Services;
using Verbeam.Core.Storage;

namespace Verbeam.Tests;

public sealed class TranslationCachingTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), "verbeam-caching-tests-" + Guid.NewGuid());

    [Theory]
    [InlineData("こんにちは。", "こんにちは 。")]
    [InlineData("hello  world", "hello world")]
    [InlineData("ＨＰ 95", "HP 95")]
    [InlineData(" trim me ", "trim me")]
    [InlineData("zero​width", "zerowidth")]
    public void NormalizeText_CollapsesJitterVariants(string left, string right)
    {
        Assert.Equal(TranslationCacheKey.NormalizeText(left), TranslationCacheKey.NormalizeText(right));
    }

    [Fact]
    public void NormalizeText_KeepsContentDifferencesApart()
    {
        Assert.NotEqual(TranslationCacheKey.NormalizeText("HP 95"), TranslationCacheKey.NormalizeText("HP 94"));
        Assert.NotEqual(TranslationCacheKey.NormalizeText("Compile"), TranslationCacheKey.NormalizeText("compile"));
    }

    [Fact]
    public void CacheKey_JitterVariantsShareOneKey()
    {
        var a = TranslationCacheKey.Create("こんにちは。", "ja", "zh-TW", "m", "p", "model", "1", "g");
        var b = TranslationCacheKey.Create("こんにちは 。", "ja", "zh-TW", "m", "p", "model", "1", "g");
        var c = TranslationCacheKey.Create("こんばんは。", "ja", "zh-TW", "m", "p", "model", "1", "g");

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public async Task MemoryFrontedTranslationCache_ServesFromMemoryAndEvicts()
    {
        var inner = new CountingCache();
        var cache = new MemoryFrontedTranslationCache(inner, capacity: 2);

        await cache.SetAsync(MakeEntry("k1"));
        await cache.SetAsync(MakeEntry("k2"));

        Assert.NotNull(await cache.GetAsync("k1"));
        Assert.Equal(0, inner.GetCalls);

        // k3 evicts the least recently used (k2, because k1 was just touched).
        await cache.SetAsync(MakeEntry("k3"));
        Assert.NotNull(await cache.GetAsync("k2"));
        Assert.Equal(1, inner.GetCalls);
    }

    [Fact]
    public async Task GlossaryStore_ReloadsWhenFileChanges()
    {
        var directory = Path.Combine(_tempDirectory, "glossaries");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "live.json");
        await File.WriteAllTextAsync(path, """{"A": "1"}""");

        var store = new GlossaryStore(directory);
        var first = await store.GetOptionalAsync("live");
        var second = await store.GetOptionalAsync("live");
        Assert.Same(first, second);

        await File.WriteAllTextAsync(path, """{"A": "1", "B": "2"}""");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));
        var third = await store.GetOptionalAsync("live");
        Assert.Equal(2, third.Terms.Count);
    }

    [Theory]
    [InlineData("DragonSlayer99: 販賣中", "DragonSlayer99: ", "販賣中")]
    [InlineData("[abc] : x", "[abc] : ", "x")]
    [InlineData("【商人】：今日特賣", "【商人】：", "今日特賣")]
    [InlineData("龍騎士99：跟我來", "龍騎士99：", "跟我來")]
    [InlineData("[DragonSlayer99] 收購龍鱗", "[DragonSlayer99] ", "收購龍鱗")]
    [InlineData("ふつうの字幕です", null, null)]
    [InlineData("has space before: colon", null, null)]
    public void RealtimeTemplateCache_SplitsChatPrefix(string text, string? expectedPrefix, string? expectedBody)
    {
        var matched = RealtimeTemplateCache.TrySplitChatPrefix(text, out var prefix, out var body);

        if (expectedPrefix is null)
        {
            Assert.False(matched);
        }
        else
        {
            Assert.True(matched);
            Assert.Equal(expectedPrefix, prefix);
            Assert.Equal(expectedBody, body);
        }
    }

    [Fact]
    public void RealtimeTemplateCache_LearnsAndSubstitutesNumbers()
    {
        var cache = new RealtimeTemplateCache();
        const string scope = "scope";

        Assert.Null(cache.TryApply("賣 +9屠龍刀 5000萬", scope));
        // Digit runs survive translation verbatim (9, 5000): template accepted.
        Assert.True(cache.TryLearn("賣 +9屠龍刀 5000萬", "Selling +9 Dragon Blade for 5000万", scope));

        // Same shape, new numbers: substituted, never stale.
        Assert.Equal("Selling +10 Dragon Blade for 4800万", cache.TryApply("賣 +10屠龍刀 4800萬", scope));
        // Original numbers still substitute correctly too.
        Assert.Equal("Selling +9 Dragon Blade for 5000万", cache.TryApply("賣 +9屠龍刀 5000萬", scope));
    }

    [Fact]
    public void RealtimeTemplateCache_RejectsRewrittenNumbers()
    {
        var cache = new RealtimeTemplateCache();
        const string scope = "scope";

        // Model rewrote 5000萬 as 50M: digit sequences differ, so no template.
        Assert.False(cache.TryLearn("賣 屠龍刀 5000萬", "Selling Dragon Blade 50M", scope));
        // Reordered numbers also refuse to templatize.
        Assert.False(cache.TryLearn("3 把 7 元", "7 for 3", scope));
        // No numbers at all: nothing to templatize.
        Assert.False(cache.TryLearn("こんにちは", "你好", scope));
    }

    [Fact]
    public void RealtimeTemplateCache_ScopesAndSlotCountsAreRespected()
    {
        var cache = new RealtimeTemplateCache();
        Assert.True(cache.TryLearn("HP 95", "HP 95", "scope-a"));

        Assert.Equal("HP 41", cache.TryApply("HP 41", "scope-a"));
        Assert.Null(cache.TryApply("HP 41", "scope-b"));
        Assert.Null(cache.TryApply("HP 4 1", "scope-a"));
    }

    [Fact]
    public async Task TranslationEventBatcher_FlushesOnDispose()
    {
        var store = new RecordingEventStore();
        var batcher = new TranslationEventBatcher((_, _) => Task.FromResult<ITranslationEventStore>(store));

        for (var index = 0; index < 5; index++)
        {
            batcher.Enqueue(MakeEvent($"e{index}"));
        }

        await batcher.DisposeAsync();
        Assert.Equal(5, store.Events.Count);
        Assert.True(store.BatchCalls >= 1);
    }

    [Fact]
    public async Task TranslationEventBatcher_FlushesWhenBatchFills()
    {
        var store = new RecordingEventStore();
        await using var batcher = new TranslationEventBatcher((_, _) => Task.FromResult<ITranslationEventStore>(store));

        for (var index = 0; index < 40; index++)
        {
            batcher.Enqueue(MakeEvent($"e{index}"));
        }

        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (store.Events.Count < 32 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.True(store.Events.Count >= 32, $"expected a full batch to flush, saw {store.Events.Count}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    private static CachedTranslation MakeEntry(string key)
        => new(key, "s", "t", "ja", "zh-TW", "m", "p", "e", "model", "1", "g", 1, DateTimeOffset.UtcNow);

    private static TranslationEvent MakeEvent(string id)
        => new(id, "session", "default", null, "name", "s", "t", "ja", "zh-TW", "m", "p", "g", "gh", "e", "model", 1, false, "0", "", DateTimeOffset.UtcNow);

    private sealed class CountingCache : ITranslationCache
    {
        private readonly Dictionary<string, CachedTranslation> _store = new();

        public int GetCalls { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<CachedTranslation?> GetAsync(string key, CancellationToken cancellationToken = default)
        {
            GetCalls++;
            return Task.FromResult(_store.TryGetValue(key, out var value) ? value : null);
        }

        public Task SetAsync(CachedTranslation entry, CancellationToken cancellationToken = default)
        {
            _store[entry.Key] = entry;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingEventStore : ITranslationEventStore
    {
        private readonly object _gate = new();

        public List<TranslationEvent> Events { get; } = [];

        public int BatchCalls { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task AddEventAsync(TranslationEvent entry, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                Events.Add(entry);
            }

            return Task.CompletedTask;
        }

        public Task AddEventsAsync(IReadOnlyList<TranslationEvent> entries, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                BatchCalls++;
                Events.AddRange(entries);
            }

            return Task.CompletedTask;
        }

        public Task<TranslationEvent?> GetEventAsync(string id, CancellationToken cancellationToken = default)
            => Task.FromResult<TranslationEvent?>(null);

        public Task<IReadOnlyList<TranslationEvent>> ListEventsAsync(string profileId, int limit, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<TranslationEvent>>([]);

        public Task<IReadOnlyList<TranslationEvent>> ListRecentContextAsync(string profileId, string sessionId, string sourceLanguage, string targetLanguage, string mode, string excludeSourceText, int limit, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<TranslationEvent>>([]);

        public Task<IReadOnlyList<TranslationEvent>> ListSessionSuccessEventsAsync(string profileId, string sessionId, string sourceLanguage, string targetLanguage, string mode, int limit, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<TranslationEvent>>([]);
    }
}
