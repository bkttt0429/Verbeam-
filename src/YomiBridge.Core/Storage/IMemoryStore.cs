using YomiBridge.Core.Models;

namespace YomiBridge.Core.Storage;

public interface IMemoryStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<MemoryItem> AddOrUpdateAsync(MemoryUpsertRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MemoryItem>> ListAsync(
        string profileId,
        string? memoryKind,
        int limit,
        bool activeOnly,
        CancellationToken cancellationToken = default);

    Task<MemoryItem?> FindExactAsync(
        string profileId,
        string memoryKind,
        string sourceLanguage,
        string targetLanguage,
        string sourceText,
        CancellationToken cancellationToken = default);

    Task RecordUseAsync(IReadOnlyList<string> memoryIds, CancellationToken cancellationToken = default);
}
