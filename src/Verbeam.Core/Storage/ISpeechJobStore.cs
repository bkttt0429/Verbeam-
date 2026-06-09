using Verbeam.Core.Models;

namespace Verbeam.Core.Storage;

public interface ISpeechJobStore
{
    Task AddJobAsync(
        SpeechJobStatus job,
        SpeechJobRequest request,
        CancellationToken cancellationToken = default);

    Task<SpeechJobStatus?> GetJobAsync(
        string jobId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SpeechJobStatus>> ListJobsAsync(
        string profileId,
        int limit,
        CancellationToken cancellationToken = default);

    Task UpdateJobAsync(
        SpeechJobStatus job,
        CancellationToken cancellationToken = default);

    Task<long> AddEventAsync(
        string jobId,
        string type,
        object payload,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SpeechJobEvent>> ListEventsAsync(
        string jobId,
        long afterSequence,
        int limit,
        CancellationToken cancellationToken = default);
}
