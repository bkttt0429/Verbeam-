using System.Text.Json;
using Verbeam.Core.Models;
using Verbeam.Core.Options;
using Verbeam.Core.Services;
using Verbeam.Core.Storage;
using Microsoft.Data.Sqlite;

namespace Verbeam.Tests;

[Collection(NonParallelTestCollection.Name)]
public sealed class MemoryMaintenanceServiceTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(),
        "verbeam-memory-maintenance-tests-" + Guid.NewGuid());

    [Fact]
    public async Task MaintainTranslationCandidatesAsync_CreatesLocalGeneratedCandidateFromRepeatedStableEvents()
    {
        Directory.CreateDirectory(_tempDirectory);
        var databasePath = Path.Combine(_tempDirectory, "memory.sqlite");
        var eventStore = new SqliteTranslationEventStore(databasePath);
        var memoryStore = new SqliteMemoryStore(databasePath);
        await eventStore.InitializeAsync();
        await memoryStore.InitializeAsync();

        for (var index = 0; index < 3; index++)
        {
            await eventStore.AddEventAsync(Event(index, "Stable line", "穩定譯文"));
        }

        var service = new MemoryMaintenanceService(
            eventStore,
            memoryStore,
            new VerbeamOptions
            {
                Memory =
                {
                    AutoTranslationCandidateEventThreshold = 3,
                    AutoTranslationCandidateMaxEvents = 10,
                    AutoTranslationCandidateConfidence = 0.42
                }
            });

        var candidates = await service.MaintainTranslationCandidatesAsync(
            "auto-profile",
            "session-a",
            "en",
            "zh-TW",
            "game_dialogue");

        var candidate = Assert.Single(candidates);
        Assert.Equal("Stable line", candidate.SourceText);
        Assert.Equal("穩定譯文", candidate.TargetText);
        Assert.Equal(RagSecurityPolicy.LocalGenerated, candidate.TrustLevel);
        Assert.Equal(0.42, candidate.Confidence, precision: 3);
        Assert.Equal("memory-maintenance-v1", candidate.CreatedBy);
        using var metadata = JsonDocument.Parse(candidate.MetadataJson);
        Assert.Equal("auto-translation-memory", metadata.RootElement.GetProperty("created_from").GetString());
        Assert.Equal("candidate", metadata.RootElement.GetProperty("review_status").GetString());
        Assert.Equal("memory-maintenance-v1", metadata.RootElement.GetProperty("extractor").GetString());
        Assert.Equal(3, metadata.RootElement.GetProperty("observation_count").GetInt32());
        Assert.Equal(3, metadata.RootElement.GetProperty("source_event_ids").GetArrayLength());

        var trustedExact = await memoryStore.FindExactAsync(
            "auto-profile",
            "translation",
            "en",
            "zh-TW",
            "Stable line");
        Assert.Null(trustedExact);
    }

    [Fact]
    public async Task DrainQueuedJobsAsync_CompletesDurableTranslationCandidateJob()
    {
        Directory.CreateDirectory(_tempDirectory);
        var databasePath = Path.Combine(_tempDirectory, "memory-queue.sqlite");
        var eventStore = new SqliteTranslationEventStore(databasePath);
        var memoryStore = new SqliteMemoryStore(databasePath);
        var jobStore = new SqliteMemoryMaintenanceJobStore(databasePath);
        await eventStore.InitializeAsync();
        await memoryStore.InitializeAsync();
        await jobStore.InitializeAsync();

        for (var index = 0; index < 3; index++)
        {
            await eventStore.AddEventAsync(Event(index, "Queued stable line", "Queued stable target"));
        }

        var service = new MemoryMaintenanceService(
            eventStore,
            memoryStore,
            ocrMemoryStore: null,
            new VerbeamOptions
            {
                Memory =
                {
                    AutoTranslationCandidateEventThreshold = 3,
                    AutoTranslationCandidateMaxEvents = 10
                }
            },
            embeddingProvider: null,
            jobStore);

        var ids = await service.EnqueueMaintenanceJobsAsync(
            "auto-profile",
            "session-a",
            "en",
            "zh-TW",
            "game_dialogue",
            extractCandidates: true,
            maintainEmbeddings: false);

        var id = Assert.Single(ids);
        var pending = await service.ListQueuedJobsAsync("auto-profile", "pending");
        Assert.Equal(id, Assert.Single(pending).Id);

        var drained = await service.DrainQueuedJobsAsync(limit: 5);
        Assert.Equal(1, drained.ClaimedCount);
        Assert.Equal(1, drained.CompletedCount);
        Assert.Equal(0, drained.FailedCount);

        var completed = await service.ListQueuedJobsAsync("auto-profile", "completed");
        var job = Assert.Single(completed);
        Assert.Equal(id, job.Id);
        Assert.Equal(MemoryMaintenanceService.TranslationCandidatesJobKind, job.JobKind);
        Assert.Equal(1, job.Attempts);
        Assert.NotNull(job.CompletedAt);

        var candidates = await memoryStore.ListAsync(
            "auto-profile",
            "translation",
            limit: 20,
            activeOnly: false,
            sourceLanguage: "en",
            targetLanguage: "zh-TW",
            query: "Queued stable line");
        var candidate = Assert.Single(candidates, item => item.SourceText == "Queued stable line");
        Assert.Equal(RagSecurityPolicy.LocalGenerated, candidate.TrustLevel);

        var trustedExact = await memoryStore.FindExactAsync(
            "auto-profile",
            "translation",
            "en",
            "zh-TW",
            "Queued stable line");
        Assert.Null(trustedExact);
    }

    [Fact]
    public async Task MaintainTranslationCandidatesAsync_MergesDuplicateCandidateAndRaisesConfidence()
    {
        Directory.CreateDirectory(_tempDirectory);
        var databasePath = Path.Combine(_tempDirectory, "memory.sqlite");
        var eventStore = new SqliteTranslationEventStore(databasePath);
        var memoryStore = new SqliteMemoryStore(databasePath);
        await eventStore.InitializeAsync();
        await memoryStore.InitializeAsync();

        for (var index = 0; index < 3; index++)
        {
            await eventStore.AddEventAsync(Event(index, "Stable merge line", "Stable merge target"));
        }

        var service = new MemoryMaintenanceService(
            eventStore,
            memoryStore,
            new VerbeamOptions
            {
                Memory =
                {
                    AutoTranslationCandidateEventThreshold = 3,
                    AutoTranslationCandidateMaxEvents = 10,
                    AutoTranslationCandidateConfidence = 0.40
                }
            });

        var firstRun = await service.MaintainTranslationCandidatesAsync(
            "auto-profile",
            "session-a",
            "en",
            "zh-TW",
            "game_dialogue");
        var first = Assert.Single(firstRun);
        Assert.Equal(0.40, first.Confidence, precision: 3);

        await eventStore.AddEventAsync(Event(3, "Stable merge line ", "Stable merge target"));
        await eventStore.AddEventAsync(Event(4, "Stable merge line", "Stable merge target"));

        var secondRun = await service.MaintainTranslationCandidatesAsync(
            "auto-profile",
            "session-a",
            "en",
            "zh-TW",
            "game_dialogue");
        var second = Assert.Single(secondRun);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal("Stable merge line", second.SourceText);
        Assert.Equal("Stable merge target", second.TargetText);
        Assert.True(second.Confidence > first.Confidence);
        Assert.Equal(0.50, second.Confidence, precision: 3);

        using var metadata = JsonDocument.Parse(second.MetadataJson);
        Assert.Equal(5, metadata.RootElement.GetProperty("observation_count").GetInt32());
        Assert.Equal(5, metadata.RootElement.GetProperty("source_event_ids").GetArrayLength());

        var listed = await memoryStore.ListAsync(
            "auto-profile",
            "translation",
            limit: 20,
            activeOnly: false,
            sourceLanguage: "en",
            targetLanguage: "zh-TW",
            query: "Stable merge line");
        var matching = listed
            .Where(item => item.SourceText == "Stable merge line")
            .ToArray();
        var stored = Assert.Single(matching);
        Assert.Equal(first.Id, stored.Id);
    }

    [Fact]
    public async Task MaintainTranslationCandidatesAsync_DecaysGeneratedConflictWithoutOverwriting()
    {
        Directory.CreateDirectory(_tempDirectory);
        var databasePath = Path.Combine(_tempDirectory, "memory.sqlite");
        var eventStore = new SqliteTranslationEventStore(databasePath);
        var memoryStore = new SqliteMemoryStore(databasePath);
        await eventStore.InitializeAsync();
        await memoryStore.InitializeAsync();

        for (var index = 0; index < 3; index++)
        {
            await eventStore.AddEventAsync(Event(index, "Ambiguous line", "Generated target A"));
        }

        var service = new MemoryMaintenanceService(
            eventStore,
            memoryStore,
            new VerbeamOptions
            {
                Memory =
                {
                    AutoTranslationCandidateEventThreshold = 3,
                    AutoTranslationCandidateMaxEvents = 3,
                    AutoTranslationCandidateConfidence = 0.60
                }
            });

        var firstRun = await service.MaintainTranslationCandidatesAsync(
            "auto-profile",
            "session-a",
            "en",
            "zh-TW",
            "game_dialogue");
        var first = Assert.Single(firstRun);
        Assert.Equal("Generated target A", first.TargetText);
        Assert.Equal(0.60, first.Confidence, precision: 3);

        for (var index = 3; index < 6; index++)
        {
            await eventStore.AddEventAsync(Event(index, "Ambiguous line", "Generated target B"));
        }

        var secondRun = await service.MaintainTranslationCandidatesAsync(
            "auto-profile",
            "session-a",
            "en",
            "zh-TW",
            "game_dialogue");

        Assert.Empty(secondRun);
        var stored = await memoryStore.FindByKeyAsync(
            "auto-profile",
            "translation",
            "en",
            "zh-TW",
            "Ambiguous line");
        Assert.NotNull(stored);
        Assert.Equal(first.Id, stored.Id);
        Assert.Equal("Generated target A", stored.TargetText);
        Assert.Equal(0.30, stored.Confidence, precision: 3);
    }

    [Fact]
    public async Task MaintainOcrCorrectionCandidatesAsync_CreatesLocalGeneratedCandidateAfterRepeatedUses()
    {
        Directory.CreateDirectory(_tempDirectory);
        var databasePath = Path.Combine(_tempDirectory, "memory.sqlite");
        var eventStore = new SqliteTranslationEventStore(databasePath);
        var memoryStore = new SqliteMemoryStore(databasePath);
        var ocrStore = new SqliteOcrMemoryStore(databasePath);
        await eventStore.InitializeAsync();
        await memoryStore.InitializeAsync();
        await ocrStore.InitializeAsync();

        var correction = await ocrStore.AddOrUpdateCorrectionAsync(new OcrCorrectionRequest
        {
            Profile = "auto-profile",
            Language = "en",
            WrongText = "5tar Key",
            CorrectedText = "Star Key",
            Note = "manual OCR correction",
            Source = "user"
        });
        await ocrStore.RecordCorrectionUseAsync([correction.Id]);
        await ocrStore.RecordCorrectionUseAsync([correction.Id]);

        var service = new MemoryMaintenanceService(
            eventStore,
            memoryStore,
            ocrStore,
            new VerbeamOptions
            {
                Memory =
                {
                    AutoOcrCorrectionCandidateUseThreshold = 2,
                    AutoTranslationCandidateConfidence = 0.42
                }
            });

        var candidates = await service.MaintainOcrCorrectionCandidatesAsync(
            "auto-profile",
            "en",
            "zh-TW",
            "ocr-event-a",
            [new AppliedOcrCorrection(correction.Id, "5tar Key", "Star Key")]);

        var candidate = Assert.Single(candidates);
        Assert.Equal("ocr_correction", candidate.MemoryKind);
        Assert.Equal("5tar Key", candidate.SourceText);
        Assert.Equal("Star Key", candidate.TargetText);
        Assert.Equal(RagSecurityPolicy.LocalGenerated, candidate.TrustLevel);
        Assert.Equal(0.42, candidate.Confidence, precision: 3);
        Assert.Equal(-25, candidate.Priority);
        Assert.Equal("events://ocr/ocr-event-a", candidate.SourceUri);

        using var metadata = JsonDocument.Parse(candidate.MetadataJson);
        Assert.Equal("auto-ocr-correction-memory", metadata.RootElement.GetProperty("created_from").GetString());
        Assert.Equal("candidate", metadata.RootElement.GetProperty("review_status").GetString());
        Assert.Equal("ocr_events", metadata.RootElement.GetProperty("source_table").GetString());
        Assert.Equal(2, metadata.RootElement.GetProperty("observation_count").GetInt32());
        var sourceEventIds = metadata.RootElement.GetProperty("source_event_ids");
        Assert.Equal("ocr-event-a", Assert.Single(sourceEventIds.EnumerateArray()).GetString());

        var trustedExact = await memoryStore.FindExactAsync(
            "auto-profile",
            "ocr_correction",
            "en",
            "zh-TW",
            "5tar Key");
        Assert.Null(trustedExact);
    }

    [Fact]
    public async Task MaintainTranslationCandidatesAsync_CreatesTermCandidateFromRepeatedRetainedTerms()
    {
        Directory.CreateDirectory(_tempDirectory);
        var databasePath = Path.Combine(_tempDirectory, "memory.sqlite");
        var eventStore = new SqliteTranslationEventStore(databasePath);
        var memoryStore = new SqliteMemoryStore(databasePath);
        await eventStore.InitializeAsync();
        await memoryStore.InitializeAsync();

        await eventStore.AddEventAsync(Event(0, "Star Key opens the north door", "translated Star Key north"));
        await eventStore.AddEventAsync(Event(1, "Use Star Key near the altar", "translated Star Key altar"));
        await eventStore.AddEventAsync(Event(2, "Star Key glows again", "translated Star Key glows"));

        var service = new MemoryMaintenanceService(
            eventStore,
            memoryStore,
            new VerbeamOptions
            {
                Memory =
                {
                    AutoTranslationCandidateEventThreshold = 3,
                    AutoTranslationCandidateMaxEvents = 10,
                    AutoTranslationCandidateConfidence = 0.42
                }
            });

        var candidates = await service.MaintainTranslationCandidatesAsync(
            "auto-profile",
            "session-a",
            "en",
            "zh-TW",
            "game_dialogue");

        var candidate = Assert.Single(candidates);
        Assert.Equal("term", candidate.MemoryKind);
        Assert.Equal("Star Key", candidate.SourceText);
        Assert.Equal("Star Key", candidate.TargetText);
        Assert.Equal(RagSecurityPolicy.LocalGenerated, candidate.TrustLevel);
        Assert.Equal(0.42, candidate.Confidence, precision: 3);
        using var metadata = JsonDocument.Parse(candidate.MetadataJson);
        Assert.Equal("auto-term-memory", metadata.RootElement.GetProperty("created_from").GetString());
        Assert.Equal("candidate", metadata.RootElement.GetProperty("review_status").GetString());
        Assert.Equal(3, metadata.RootElement.GetProperty("observation_count").GetInt32());

        var trustedExact = await memoryStore.FindExactAsync(
            "auto-profile",
            "term",
            "en",
            "zh-TW",
            "Star Key");
        Assert.Null(trustedExact);
    }

    [Fact]
    public async Task MaintainTranslationCandidatesAsync_DoesNotOverwriteTrustedTermConflict()
    {
        Directory.CreateDirectory(_tempDirectory);
        var databasePath = Path.Combine(_tempDirectory, "memory.sqlite");
        var eventStore = new SqliteTranslationEventStore(databasePath);
        var memoryStore = new SqliteMemoryStore(databasePath);
        await eventStore.InitializeAsync();
        await memoryStore.InitializeAsync();

        var trusted = await memoryStore.AddOrUpdateAsync(new MemoryUpsertRequest
        {
            Profile = "auto-profile",
            MemoryKind = "term",
            Source = "en",
            Target = "zh-TW",
            SourceText = "Star Key",
            TargetText = "Verified Star Key",
            TrustLevel = RagSecurityPolicy.UserVerified
        });

        await eventStore.AddEventAsync(Event(0, "Star Key opens the north door", "translated Star Key north"));
        await eventStore.AddEventAsync(Event(1, "Use Star Key near the altar", "translated Star Key altar"));
        await eventStore.AddEventAsync(Event(2, "Star Key glows again", "translated Star Key glows"));

        var service = new MemoryMaintenanceService(
            eventStore,
            memoryStore,
            new VerbeamOptions
            {
                Memory =
                {
                    AutoTranslationCandidateEventThreshold = 3,
                    AutoTranslationCandidateMaxEvents = 10
                }
            });

        var candidates = await service.MaintainTranslationCandidatesAsync(
            "auto-profile",
            "session-a",
            "en",
            "zh-TW",
            "game_dialogue");

        Assert.Empty(candidates);
        var exact = await memoryStore.FindExactAsync(
            "auto-profile",
            "term",
            "en",
            "zh-TW",
            "Star Key");
        Assert.NotNull(exact);
        Assert.Equal(trusted.Id, exact.Id);
        Assert.Equal("Verified Star Key", exact.TargetText);
    }

    [Fact]
    public async Task MaintainTranslationCandidatesAsync_DoesNotOverwriteTrustedConflict()
    {
        Directory.CreateDirectory(_tempDirectory);
        var databasePath = Path.Combine(_tempDirectory, "memory.sqlite");
        var eventStore = new SqliteTranslationEventStore(databasePath);
        var memoryStore = new SqliteMemoryStore(databasePath);
        await eventStore.InitializeAsync();
        await memoryStore.InitializeAsync();

        var trusted = await memoryStore.AddOrUpdateAsync(new MemoryUpsertRequest
        {
            Profile = "auto-profile",
            MemoryKind = "translation",
            Source = "en",
            Target = "zh-TW",
            SourceText = "Conflict line",
            TargetText = "本機可信譯文",
            TrustLevel = RagSecurityPolicy.UserVerified
        });

        for (var index = 0; index < 3; index++)
        {
            await eventStore.AddEventAsync(Event(index, "Conflict line", "外部候選譯文"));
        }

        var service = new MemoryMaintenanceService(
            eventStore,
            memoryStore,
            new VerbeamOptions
            {
                Memory =
                {
                    AutoTranslationCandidateEventThreshold = 3,
                    AutoTranslationCandidateMaxEvents = 10
                }
            });

        var candidates = await service.MaintainTranslationCandidatesAsync(
            "auto-profile",
            "session-a",
            "en",
            "zh-TW",
            "game_dialogue");

        Assert.Empty(candidates);
        var exact = await memoryStore.FindExactAsync(
            "auto-profile",
            "translation",
            "en",
            "zh-TW",
            "Conflict line");
        Assert.NotNull(exact);
        Assert.Equal(trusted.Id, exact.Id);
        Assert.Equal("本機可信譯文", exact.TargetText);
    }

    [Fact]
    public async Task MaintainEmbeddingsAsync_CreatesVectorsForTrustedActiveScopedMemoryOnly()
    {
        Directory.CreateDirectory(_tempDirectory);
        var databasePath = Path.Combine(_tempDirectory, "memory.sqlite");
        var eventStore = new SqliteTranslationEventStore(databasePath);
        var memoryStore = new SqliteMemoryStore(databasePath);
        await eventStore.InitializeAsync();
        await memoryStore.InitializeAsync();
        var provider = new HashEmbeddingProvider(16);
        var service = new MemoryMaintenanceService(
            eventStore,
            memoryStore,
            ocrMemoryStore: null,
            options: new VerbeamOptions
            {
                Memory =
                {
                    SemanticRetrievalEnabled = true,
                    EmbeddingMaintenanceBatchSize = 10
                }
            },
            embeddingProvider: provider);

        var trusted = await memoryStore.AddOrUpdateAsync(Memory("embedding-profile", "Star Key", "Star Key TW"));
        var generated = await memoryStore.AddOrUpdateAsync(Memory(
            "embedding-profile",
            "Generated Term",
            "Generated Term TW",
            trustLevel: RagSecurityPolicy.LocalGenerated));
        var shared = await memoryStore.AddOrUpdateAsync(Memory(
            "embedding-profile",
            "Shared Term",
            "Shared Term TW",
            visibility: "shared"));
        var otherProfile = await memoryStore.AddOrUpdateAsync(Memory("other-profile", "Other Key", "Other Key TW"));
        var inactive = await memoryStore.AddOrUpdateAsync(Memory("embedding-profile", "Inactive Key", "Inactive Key TW"));
        await memoryStore.UpdateAsync(inactive.Id, new MemoryUpdateRequest { IsActive = false });

        var result = await service.MaintainEmbeddingsAsync(
            "embedding-profile",
            "en",
            "zh-TW");

        Assert.Equal(1, result.CandidateCount);
        Assert.Equal(1, result.CreatedCount);
        Assert.Equal(0, result.UpdatedCount);
        Assert.Equal(0, result.CurrentCount);
        Assert.Equal(0, result.SkippedCount);

        var embeddings = await memoryStore.ListEmbeddingsAsync(
            [trusted.Id, generated.Id, shared.Id, otherProfile.Id, inactive.Id],
            provider.Model);
        var embedding = Assert.Single(embeddings);
        Assert.Equal(trusted.Id, embedding.MemoryId);
        Assert.Equal(provider.Dimensions, embedding.Dimensions);
        Assert.Equal(MemoryEmbeddingText.CreateContentHash(trusted), embedding.ContentHash);
    }

    [Fact]
    public async Task MaintainEmbeddingsAsync_UpdatesStaleVectorAndKeepsCurrentVector()
    {
        Directory.CreateDirectory(_tempDirectory);
        var databasePath = Path.Combine(_tempDirectory, "memory.sqlite");
        var eventStore = new SqliteTranslationEventStore(databasePath);
        var memoryStore = new SqliteMemoryStore(databasePath);
        await eventStore.InitializeAsync();
        await memoryStore.InitializeAsync();
        var provider = new HashEmbeddingProvider(16);
        var service = new MemoryMaintenanceService(
            eventStore,
            memoryStore,
            ocrMemoryStore: null,
            options: new VerbeamOptions
            {
                Memory =
                {
                    SemanticRetrievalEnabled = true,
                    EmbeddingMaintenanceBatchSize = 10
                }
            },
            embeddingProvider: provider);
        var current = await memoryStore.AddOrUpdateAsync(Memory("embedding-profile", "Current Key", "Current Key TW"));
        var stale = await memoryStore.AddOrUpdateAsync(Memory("embedding-profile", "Stale Key", "Stale Key TW"));
        await memoryStore.UpsertEmbeddingAsync(new MemoryEmbedding(
            current.Id,
            provider.Model,
            provider.Dimensions,
            await provider.EmbedAsync(MemoryEmbeddingText.CreateText(current)),
            MemoryEmbeddingText.CreateContentHash(current),
            DateTimeOffset.Parse("2026-01-01T00:00:00Z")));
        await memoryStore.UpsertEmbeddingAsync(new MemoryEmbedding(
            stale.Id,
            provider.Model,
            provider.Dimensions,
            await provider.EmbedAsync("old stale text"),
            "old-content-hash",
            DateTimeOffset.Parse("2026-01-01T00:00:00Z")));

        var result = await service.MaintainEmbeddingsAsync(
            "embedding-profile",
            "en",
            "zh-TW");

        Assert.Equal(2, result.CandidateCount);
        Assert.Equal(0, result.CreatedCount);
        Assert.Equal(1, result.UpdatedCount);
        Assert.Equal(1, result.CurrentCount);
        Assert.Equal(0, result.SkippedCount);

        var embeddings = await memoryStore.ListEmbeddingsAsync([current.Id, stale.Id], provider.Model);
        Assert.Contains(embeddings, item =>
            item.MemoryId == current.Id &&
            item.ContentHash == MemoryEmbeddingText.CreateContentHash(current));
        Assert.Contains(embeddings, item =>
            item.MemoryId == stale.Id &&
            item.ContentHash == MemoryEmbeddingText.CreateContentHash(stale));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    private static TranslationEvent Event(int index, string sourceText, string translatedText)
        => new(
            "event-" + index,
            "session-a",
            "auto-profile",
            null,
            "request-" + index,
            sourceText,
            translatedText,
            "en",
            "zh-TW",
            "game_dialogue",
            "mock",
            string.Empty,
            string.Empty,
            "mock",
            "mock",
            1,
            false,
            "0",
            string.Empty,
            DateTimeOffset.Parse("2026-01-01T00:00:00Z").AddSeconds(index));

    private static MemoryUpsertRequest Memory(
        string profile,
        string sourceText,
        string targetText,
        string trustLevel = RagSecurityPolicy.UserVerified,
        string visibility = "profile")
        => new()
        {
            Profile = profile,
            MemoryKind = "term",
            Source = "en",
            Target = "zh-TW",
            SourceText = sourceText,
            TargetText = targetText,
            TrustLevel = trustLevel,
            Visibility = visibility
        };
}
