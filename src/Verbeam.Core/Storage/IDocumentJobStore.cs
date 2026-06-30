using Verbeam.Core.Models;

namespace Verbeam.Core.Storage;

public interface IDocumentJobStore
{
    Task AddJobAsync(
        DocumentJobStatus job,
        DocumentJobRequest request,
        CancellationToken cancellationToken = default);

    Task<DocumentJobStatus?> GetJobAsync(
        string jobId,
        CancellationToken cancellationToken = default);

    Task<DocumentJobRequest?> GetRequestAsync(
        string jobId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DocumentJobStatus>> ListJobsAsync(
        string profileId,
        int limit,
        CancellationToken cancellationToken = default);

    Task UpdateJobAsync(
        DocumentJobStatus job,
        CancellationToken cancellationToken = default);

    Task<long> AddEventAsync(
        string jobId,
        string type,
        object payload,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DocumentJobEvent>> ListEventsAsync(
        string jobId,
        long afterSequence,
        int limit,
        CancellationToken cancellationToken = default);
}
