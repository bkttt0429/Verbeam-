using Verbeam.Core.Models;

namespace Verbeam.Core.Storage;

public interface IMemoryStore : IInitializableStore
{
    Task<MemoryItem> AddOrUpdateAsync(MemoryUpsertRequest request, CancellationToken cancellationToken = default);

    Task<MemoryItem?> GetAsync(string id, CancellationToken cancellationToken = default);

    Task<MemoryItem?> UpdateTrustAsync(
        string id,
        MemoryTrustUpdateRequest request,
        CancellationToken cancellationToken = default);

    Task<MemoryItem?> UpdateAsync(
        string id,
        MemoryUpdateRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MemoryItem>> ListAsync(
        string profileId,
        string? memoryKind,
        int limit,
        bool activeOnly,
        string? trustLevel = null,
        string? sourceLanguage = null,
        string? targetLanguage = null,
        string? visibility = null,
        string? query = null,
        CancellationToken cancellationToken = default);

    Task<int> CountAsync(
        string profileId,
        bool activeOnly,
        string? trustLevel = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MemoryItem>> SearchAsync(
        MemorySearchRequest request,
        CancellationToken cancellationToken = default);

    Task UpsertEmbeddingAsync(
        MemoryEmbedding embedding,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MemoryEmbedding>> ListEmbeddingsAsync(
        IReadOnlyList<string> memoryIds,
        string embeddingModel,
        CancellationToken cancellationToken = default);

    Task<MemoryItem?> FindExactAsync(
        string profileId,
        string memoryKind,
        string sourceLanguage,
        string targetLanguage,
        string sourceText,
        CancellationToken cancellationToken = default,
        bool includeShared = false);

    Task<MemoryItem?> FindByKeyAsync(
        string profileId,
        string memoryKind,
        string sourceLanguage,
        string targetLanguage,
        string sourceText,
        CancellationToken cancellationToken = default);

    Task RecordUseAsync(IReadOnlyList<string> memoryIds, CancellationToken cancellationToken = default);
}
