using System.Diagnostics;
using Verbeam.Core.Models;
using Verbeam.Core.Options;
using Verbeam.Core.Services;
using Verbeam.Core.Storage;
using Microsoft.Data.Sqlite;

namespace Verbeam.Tests;

public sealed class MemoryRetrievalPerformanceTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(),
        "verbeam-memory-perf-tests-" + Guid.NewGuid());

    [Fact]
    public async Task BuildDebugAsync_ReportsLatencyForBoundedSqliteCandidateSet()
    {
        Directory.CreateDirectory(_tempDirectory);
        var store = new SqliteMemoryStore(Path.Combine(_tempDirectory, "memory.sqlite"));
        await store.InitializeAsync();

        const int rowCount = 250;
        const int candidateLimit = 200;
        for (var index = 0; index < rowCount; index++)
        {
            await store.AddOrUpdateAsync(new MemoryUpsertRequest
            {
                Profile = "perf-profile",
                MemoryKind = "term",
                Source = "en",
                Target = "zh-TW",
                SourceText = $"PerfTerm{index:000}",
                TargetText = $"PerfTerm{index:000} TW",
                Priority = index % 25,
                Confidence = 0.95
            });
        }

        var options = new VerbeamOptions
        {
            Memory =
            {
                CandidateLimit = candidateLimit,
                MaxRecentLines = 0
            }
        };
        var builder = new MemoryContextBuilder(store, options);
        var request = new MemoryContextRequest(
            "perf-profile",
            string.Empty,
            "en",
            "zh-TW",
            "game_dialogue",
            "Use PerfTerm005, PerfTerm125, and PerfTerm249 in this line.");

        var stopwatch = Stopwatch.StartNew();
        var debug = await builder.BuildDebugAsync(request);
        stopwatch.Stop();

        Assert.Equal(candidateLimit, debug.CandidateCount);
        Assert.True(debug.RetrievalElapsedMs >= 0);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
        Assert.InRange(debug.SelectedMemoryCount, 1, 3);
        Assert.True(debug.ContextCharacterCount > 0);
        Assert.True(debug.ContextCharacterCount <= options.Memory.MaxContextCharacters);
    }

    [Fact]
    public async Task BuildDebugAsync_ReportsLatencyForPrewarmedSemanticCandidateSet()
    {
        Directory.CreateDirectory(_tempDirectory);
        var store = new SqliteMemoryStore(Path.Combine(_tempDirectory, "memory.sqlite"));
        await store.InitializeAsync();

        const int rowCount = 400;
        const int candidateLimit = 400;
        for (var index = 0; index < rowCount; index++)
        {
            await store.AddOrUpdateAsync(new MemoryUpsertRequest
            {
                Profile = "semantic-perf-profile",
                MemoryKind = "term",
                Source = "en",
                Target = "zh-TW",
                SourceText = $"PerfSemanticTerm{index:000}",
                TargetText = $"PerfSemanticTerm{index:000} TW",
                Priority = index % 25,
                Confidence = 0.95
            });
        }

        var options = new VerbeamOptions
        {
            Memory =
            {
                CandidateLimit = candidateLimit,
                SemanticRetrievalEnabled = true,
                SemanticCandidateLimit = candidateLimit,
                SemanticTimeoutMs = 1000,
                EmbeddingMaintenanceBatchSize = candidateLimit,
                MaxRecentLines = 0
            }
        };
        var provider = new CountingEmbeddingProvider(new HashEmbeddingProvider(options.Memory.EmbeddingDimensions));
        var maintenance = new MemoryMaintenanceService(
            NullTranslationEventStore.Instance,
            store,
            ocrMemoryStore: null,
            options,
            provider);
        var maintained = await maintenance.MaintainEmbeddingsAsync(
            "semantic-perf-profile",
            "en",
            "zh-TW");
        Assert.Equal(rowCount, maintained.CandidateCount);
        Assert.Equal(rowCount, maintained.CreatedCount);

        provider.ResetCallCount();
        var builder = new MemoryContextBuilder(store, options, provider);
        var request = new MemoryContextRequest(
            "semantic-perf-profile",
            string.Empty,
            "en",
            "zh-TW",
            "game_dialogue",
            "Use PerfSemanticTerm005 and PerfSemanticTerm325 in this line.");

        var stopwatch = Stopwatch.StartNew();
        var debug = await builder.BuildDebugAsync(request);
        stopwatch.Stop();

        Assert.Equal(candidateLimit, debug.CandidateCount);
        Assert.True(debug.RetrievalElapsedMs >= 0);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Equal(1, provider.CallCount);
        Assert.InRange(debug.SelectedMemoryCount, 1, options.Memory.MaxTerms);
        Assert.True(debug.ContextCharacterCount > 0);
        Assert.True(debug.ContextCharacterCount <= options.Memory.MaxContextCharacters);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    private sealed class CountingEmbeddingProvider(IEmbeddingProvider inner) : IEmbeddingProvider
    {
        private int _callCount;

        public string Model => inner.Model;
        public int Dimensions => inner.Dimensions;
        public int CallCount => _callCount;

        public async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            return await inner.EmbedAsync(text, cancellationToken);
        }

        public void ResetCallCount()
            => Interlocked.Exchange(ref _callCount, 0);
    }
}
