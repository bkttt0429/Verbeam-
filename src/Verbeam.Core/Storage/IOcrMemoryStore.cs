using Verbeam.Core.Models;

namespace Verbeam.Core.Storage;

public interface IOcrMemoryStore
{
    Task<OcrCachedResult?> GetCachedResultAsync(
        string key,
        CancellationToken cancellationToken = default);

    Task SetCachedResultAsync(
        OcrCachedResult entry,
        CancellationToken cancellationToken = default);

    Task AddEventAsync(OcrEvent entry, CancellationToken cancellationToken = default);

    Task<OcrEvent?> GetEventAsync(
        string eventId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OcrEvent>> ListEventsAsync(
        string profileId,
        int limit,
        CancellationToken cancellationToken = default);

    Task AddSmokeResultAsync(
        OcrSmokeTestRecord entry,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OcrSmokeTestRecord>> ListSmokeResultsAsync(
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

    Task<OcrCorrection?> UpdateCorrectionAsync(
        string correctionId,
        OcrCorrectionUpdateRequest request,
        CancellationToken cancellationToken = default);

    Task RecordCorrectionUseAsync(
        IReadOnlyList<string> correctionIds,
        CancellationToken cancellationToken = default);
}
