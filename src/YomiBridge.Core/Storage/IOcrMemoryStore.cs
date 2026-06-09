using YomiBridge.Core.Models;

namespace YomiBridge.Core.Storage;

public interface IOcrMemoryStore
{
    Task AddEventAsync(OcrEvent entry, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OcrEvent>> ListEventsAsync(
        string profileId,
        int limit,
        CancellationToken cancellationToken = default);

    Task<OcrCorrection> AddOrUpdateCorrectionAsync(
        OcrCorrectionRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OcrCorrection>> ListCorrectionsAsync(
        string profileId,
        string language,
        int limit,
        bool activeOnly,
        CancellationToken cancellationToken = default);

    Task RecordCorrectionUseAsync(
        IReadOnlyList<string> correctionIds,
        CancellationToken cancellationToken = default);
}
