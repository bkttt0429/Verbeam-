using Verbeam.Core.Models;

namespace Verbeam.Core.Storage;

public interface IVideoSpeechSessionStore
{
    Task AddSessionAsync(
        VideoSpeechSessionStatus session,
        VideoSpeechSessionRequest request,
        CancellationToken cancellationToken = default);

    Task<VideoSpeechSessionStatus?> GetSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default);

    Task<VideoSpeechSessionRequest?> GetSessionRequestAsync(
        string sessionId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<VideoSpeechSessionStatus>> ListSessionsAsync(
        string profileId,
        int limit,
        CancellationToken cancellationToken = default);

    Task UpdateSessionAsync(
        VideoSpeechSessionStatus session,
        CancellationToken cancellationToken = default);

    Task<long> AddEventAsync(
        string sessionId,
        string type,
        object payload,
        CancellationToken cancellationToken = default);

    /// <summary>Adds an event whose payload is already serialized JSON and returns its sequence.</summary>
    Task<long> AddEventJsonAsync(
        string sessionId,
        string type,
        string payloadJson,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<VideoSpeechSessionEvent>> ListEventsAsync(
        string sessionId,
        long afterSequence,
        int limit,
        CancellationToken cancellationToken = default);

    Task UpsertBufferAsync(
        VideoSpeechAudioBuffer buffer,
        CancellationToken cancellationToken = default);

    Task<VideoSpeechAudioBuffer?> FindCoveringBufferAsync(
        string sessionId,
        double startSeconds,
        double endSeconds,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<VideoSpeechAudioBuffer>> ListBuffersAsync(
        string sessionId,
        CancellationToken cancellationToken = default);

    Task DeleteBufferAsync(
        string bufferId,
        CancellationToken cancellationToken = default);

    Task DeleteBuffersAsync(
        string sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes all but the most recent <paramref name="keepLast"/> events of a session.</summary>
    Task TrimEventsAsync(
        string sessionId,
        int keepLast,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts the task as queued, or re-queues an existing row unless it is
    /// already succeeded or running. Returns false when nothing changed.
    /// </summary>
    Task<bool> TryMarkWindowQueuedAsync(
        VideoSpeechWindowTask task,
        CancellationToken cancellationToken = default);

    Task UpdateWindowTaskStatusAsync(
        string sessionId,
        double startSeconds,
        double endSeconds,
        string status,
        string errorCode,
        string errorMessage,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<VideoSpeechWindowTask>> ListWindowTasksAsync(
        string sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resets window tasks stuck in running (e.g. after a process restart)
    /// back to queued when they have not been updated since the cutoff.
    /// </summary>
    Task ResetStaleRunningWindowsAsync(
        string sessionId,
        DateTimeOffset cutoff,
        CancellationToken cancellationToken = default);

    Task AddSegmentsAsync(
        IReadOnlyList<VideoSpeechCachedSegment> segments,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<VideoSpeechCachedSegment>> ListSegmentsAsync(
        string sessionId,
        double startSeconds,
        double endSeconds,
        CancellationToken cancellationToken = default);

    Task<int> CountSegmentsAsync(
        string sessionId,
        CancellationToken cancellationToken = default);
}
