using System.Text.Json;
using Verbeam.Core.Models;
using Verbeam.Core.Services;
using Verbeam.Core.Storage;
using Microsoft.Data.Sqlite;

namespace Verbeam.Tests;

public sealed class MemoryStoreTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(),
        "verbeam-memory-store-tests-" + Guid.NewGuid());

    [Fact]
    public async Task LocalGeneratedMemoryIsMarkedAsCandidateAndExcludedFromTrustedRetrieval()
    {
        Directory.CreateDirectory(_tempDirectory);
        var store = new SqliteMemoryStore(Path.Combine(_tempDirectory, "memory.sqlite"));
        await store.InitializeAsync();

        var memory = await store.AddOrUpdateAsync(new MemoryUpsertRequest
        {
            Profile = "candidate-profile",
            MemoryKind = "term",
            Source = "en",
            Target = "zh-TW",
            SourceText = "Candidate Term",
            TargetText = "Candidate Term TW",
            Origin = RagSecurityPolicy.LocalGenerated,
            Confidence = 0.7
        });

        Assert.Equal(RagSecurityPolicy.LocalGenerated, memory.TrustLevel);
        using var metadata = JsonDocument.Parse(memory.MetadataJson);
        Assert.Equal("local_generated", metadata.RootElement.GetProperty("origin").GetString());
        Assert.Equal("candidate", metadata.RootElement.GetProperty("review_status").GetString());

        var trusted = await store.SearchAsync(new MemorySearchRequest(
            "candidate-profile",
            "en",
            "zh-TW",
            ["term"],
            Limit: 10,
            TrustedOnly: true));
        Assert.Empty(trusted);

        var all = await store.SearchAsync(new MemorySearchRequest(
            "candidate-profile",
            "en",
            "zh-TW",
            ["term"],
            Limit: 10,
            TrustedOnly: false));
        Assert.Equal(memory.Id, Assert.Single(all).Id);
    }

    [Fact]
    public async Task MemoryEmbeddings_RoundTripFloatVector()
    {
        Directory.CreateDirectory(_tempDirectory);
        var store = new SqliteMemoryStore(Path.Combine(_tempDirectory, "memory.sqlite"));
        await store.InitializeAsync();

        var memory = await store.AddOrUpdateAsync(new MemoryUpsertRequest
        {
            Profile = "embedding-profile",
            MemoryKind = "term",
            Source = "en",
            Target = "zh-TW",
            SourceText = "Final gate seal",
            TargetText = "Final Gate Seal TW"
        });
        var createdAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

        await store.UpsertEmbeddingAsync(new MemoryEmbedding(
            memory.Id,
            "test-model",
            3,
            [0.25f, -0.5f, 1f],
            "content-hash-a",
            createdAt));

        var embeddings = await store.ListEmbeddingsAsync([memory.Id], "test-model");

        var embedding = Assert.Single(embeddings);
        Assert.Equal(memory.Id, embedding.MemoryId);
        Assert.Equal("test-model", embedding.EmbeddingModel);
        Assert.Equal(3, embedding.Dimensions);
        Assert.Equal([0.25f, -0.5f, 1f], embedding.Vector);
        Assert.Equal("content-hash-a", embedding.ContentHash);
        Assert.Equal(createdAt, embedding.CreatedAt);
    }

    [Fact]
    public async Task SharedMemory_IsExcludedFromTrustedRuntimeRetrievalUnlessExplicitlyIncluded()
    {
        Directory.CreateDirectory(_tempDirectory);
        var store = new SqliteMemoryStore(Path.Combine(_tempDirectory, "memory.sqlite"));
        await store.InitializeAsync();

        var shared = await store.AddOrUpdateAsync(new MemoryUpsertRequest
        {
            Profile = "shared-profile",
            MemoryKind = "translation",
            Source = "en",
            Target = "zh-TW",
            SourceText = "shared exact",
            TargetText = "SHARED EXACT",
            TrustLevel = RagSecurityPolicy.UserVerified,
            Visibility = "shared"
        });

        var hiddenSearch = await store.SearchAsync(new MemorySearchRequest(
            "shared-profile",
            "en",
            "zh-TW",
            ["translation"],
            Limit: 10,
            TrustedOnly: true));
        Assert.Empty(hiddenSearch);

        var visibleSearch = await store.SearchAsync(new MemorySearchRequest(
            "shared-profile",
            "en",
            "zh-TW",
            ["translation"],
            Limit: 10,
            TrustedOnly: true,
            IncludeShared: true));
        Assert.Equal(shared.Id, Assert.Single(visibleSearch).Id);

        var hiddenExact = await store.FindExactAsync(
            "shared-profile",
            "translation",
            "en",
            "zh-TW",
            "shared exact");
        Assert.Null(hiddenExact);

        var visibleExact = await store.FindExactAsync(
            "shared-profile",
            "translation",
            "en",
            "zh-TW",
            "shared exact",
            includeShared: true);
        Assert.NotNull(visibleExact);
        Assert.Equal(shared.Id, visibleExact.Id);
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
