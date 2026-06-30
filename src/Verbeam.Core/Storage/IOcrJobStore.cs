using Verbeam.Core.Models;

namespace Verbeam.Core.Storage;

public interface IOcrJobStore
{
    Task AddJobAsync(
        OcrJobStatus job,
        OcrJobRequest request,
        CancellationToken cancellationToken = default);

    Task<OcrJobStatus?> GetJobAsync(
        string jobId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OcrJobStatus>> ListJobsAsync(
        string profileId,
        int limit,
        CancellationToken cancellationToken = default);

    Task UpdateJobAsync(
        OcrJobStatus job,
        CancellationToken cancellationToken = default);

    Task<long> AddEventAsync(
        string jobId,
        string type,
        object payload,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OcrJobEvent>> ListEventsAsync(
        string jobId,
        long afterSequence,
        int limit,
        CancellationToken cancellationToken = default);
}
