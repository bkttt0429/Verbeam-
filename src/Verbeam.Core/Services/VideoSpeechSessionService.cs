using System.Collections.Concurrent;
using System.Text;
using Verbeam.Core.Models;
using Verbeam.Core.Options;
using Verbeam.Core.Providers;
using Verbeam.Core.Storage;

namespace Verbeam.Core.Services;

public sealed class VideoSpeechSessionService : IDisposable, IAsyncDisposable
{
    private readonly VerbeamOptions _options;
    private readonly VideoMediaSourceRegistry _mediaSources;
    private readonly SpeechProviderRegistry _speechProviders;
    private readonly GlossaryStore _glossaries;
    private static readonly System.Text.Json.JsonSerializerOptions EventJsonOptions =
        new(System.Text.Json.JsonSerializerDefaults.Web);

    private readonly IVideoSpeechSessionStore _sessions;
    private readonly SpeechService _speechService;
    private readonly TranslationService _translationService;
    private readonly VideoSpeechEventBroker _events;
    private readonly string _bufferRootPath;
    private readonly VideoSpeechWindowScheduler _scheduler;
    private readonly ConcurrentDictionary<string, Lazy<Task<VideoSpeechAudioBuffer>>> _inflightDownloads = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, Task> _backgroundWork = new();
    private readonly SemaphoreSlim _downloadSlots;
    private readonly SemaphoreSlim _asrSlots;

    public VideoSpeechSessionService(
        VerbeamOptions options,
        VideoMediaSourceRegistry mediaSources,
        SpeechProviderRegistry speechProviders,
        GlossaryStore glossaries,
        IVideoSpeechSessionStore sessions,
        SpeechService speechService,
        TranslationService translationService,
        VideoSpeechEventBroker events,
        string bufferRootPath)
    {
        _options = options;
        _mediaSources = mediaSources;
        _speechProviders = speechProviders;
        _glossaries = glossaries;
        _sessions = sessions;
        _speechService = speechService;
        _translationService = translationService;
        _events = events;
        _bufferRootPath = bufferRootPath;
        _downloadSlots = new SemaphoreSlim(Math.Max(1, options.Speech.Video.MaxDownloadWorkers));
        _asrSlots = new SemaphoreSlim(Math.Max(1, options.Speech.Video.MaxAsrWorkers));
        _scheduler = new VideoSpeechWindowScheduler(
            Math.Max(1, options.Speech.Video.MaxDownloadWorkers) + Math.Max(1, options.Speech.Video.MaxAsrWorkers),
            ProcessWindowAsync);
    }

    public async Task<VideoSpeechSessionStatus> StartAsync(
        VideoSpeechSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        var sourceUrl = request.SourceUrl?.Trim();
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            throw new ArgumentException("sourceUrl is required.");
        }

        var mediaSource = _mediaSources.GetRequired(sourceUrl);
        var provider = Pick(request.SpeechProvider ?? request.Provider, _options.Speech.DefaultProvider);
        _speechProviders.GetRequired(provider);

        var language = Pick(request.Language, _options.Speech.DefaultLanguage);
        var profile = Pick(request.Profile, "default");
        var metadata = await mediaSource.ResolveAsync(sourceUrl, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var session = new VideoSpeechSessionStatus(
            Guid.NewGuid().ToString("N"),
            "initializing",
            profile,
            Pick(request.SessionId, string.Empty),
            sourceUrl,
            metadata.Platform,
            metadata.VideoId,
            metadata.Title,
            metadata.DurationSeconds,
            language,
            provider,
            CaptionsUsed: false,
            SegmentCount: 0,
            ErrorCode: string.Empty,
            ErrorMessage: string.Empty,
            now,
            now);

        await _sessions.AddSessionAsync(session, request, cancellationToken);
        await EmitAsync(session.Id, "session_created", new { session }, cancellationToken);

        var sessionToken = _scheduler.GetOrCreateSessionToken(session.Id);
        TrackBackground(() => InitializeAsync(session, request, mediaSource, sessionToken));
        return session;
    }

    public Task<VideoSpeechSessionStatus?> GetAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
        => _sessions.GetSessionAsync(sessionId, cancellationToken);

    public Task<IReadOnlyList<VideoSpeechSessionStatus>> ListAsync(
        string profileId,
        int limit,
        CancellationToken cancellationToken = default)
        => _sessions.ListSessionsAsync(profileId, limit, cancellationToken);

    public Task<IReadOnlyList<VideoSpeechSessionEvent>> ListEventsAsync(
        string sessionId,
        long afterSequence,
        int limit,
        CancellationToken cancellationToken = default)
        => _sessions.ListEventsAsync(sessionId, afterSequence, limit, cancellationToken);

    public Task<IReadOnlyList<VideoSpeechCachedSegment>> ListSegmentsAsync(
        string sessionId,
        double startSeconds,
        double endSeconds,
        CancellationToken cancellationToken = default)
        => _sessions.ListSegmentsAsync(sessionId, startSeconds, endSeconds, cancellationToken);

    public async Task<VideoSpeechSessionStatus> UpdatePositionAsync(
        string sessionId,
        VideoSpeechPositionRequest request,
        CancellationToken cancellationToken = default)
    {
        var session = await _sessions.GetSessionAsync(sessionId, cancellationToken)
            ?? throw new InvalidOperationException("Video speech session was not found.");
        await EmitAsync(session.Id, "position", new
        {
            positionSeconds = Math.Max(0, request.PositionSeconds),
            request.Playing,
            request.LookaheadSeconds
        }, cancellationToken);

        if (session.CaptionsUsed || session.Status.Equals("captions_ready", StringComparison.OrdinalIgnoreCase))
        {
            return session;
        }

        await _sessions.ResetStaleRunningWindowsAsync(
            session.Id,
            DateTimeOffset.UtcNow.AddMinutes(-5),
            cancellationToken);

        var tasks = BuildWindowTasks(session, request);
        foreach (var task in tasks)
        {
            if (!await _sessions.TryMarkWindowQueuedAsync(task, cancellationToken))
            {
                continue;
            }

            await EmitAsync(session.Id, "window_queued", new { task }, cancellationToken);
            _scheduler.TryEnqueue(task);
        }

        return session;
    }

    public async Task<bool> CancelAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var session = await _sessions.GetSessionAsync(sessionId, cancellationToken);
        if (session is null)
        {
            return false;
        }

        var canceled = session with
        {
            Status = "canceled",
            ErrorCode = "canceled",
            ErrorMessage = "Video speech session was canceled.",
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await _sessions.UpdateSessionAsync(canceled, cancellationToken);
        await EmitAsync(sessionId, "session_canceled", new { session = canceled }, cancellationToken);
        await FinalizeSessionAsync(sessionId);
        return true;
    }

    private async Task InitializeAsync(
        VideoSpeechSessionStatus session,
        VideoSpeechSessionRequest request,
        IVideoMediaSourceProvider mediaSource,
        CancellationToken cancellationToken)
    {
        try
        {
            var preferCaptions = request.PreferCaptions ?? _options.Speech.PreferCaptions;
            if (preferCaptions)
            {
                await EmitAsync(session.Id, "caption_probe", new { session.SourceUrl, session.Language }, cancellationToken);
                var captions = await mediaSource.TryLoadCaptionsAsync(session.SourceUrl, session.Language, cancellationToken);
                if (captions.Count > 0)
                {
                    var cached = ToCachedSegments(session, captions, "captions", "captions", 0, 0);
                    await _sessions.AddSegmentsAsync(cached, cancellationToken);
                    await PublishTranslationsAsync(session, cached, request, cancellationToken);
                    var readyWithCaptions = session with
                    {
                        Status = "captions_ready",
                        CaptionsUsed = true,
                        SegmentCount = cached.Count,
                        UpdatedAt = DateTimeOffset.UtcNow
                    };
                    await _sessions.UpdateSessionAsync(readyWithCaptions, cancellationToken);
                    // No per-caption segment events: a long video can have
                    // thousands of captions. The frontend fetches /segments
                    // once when it sees captions_ready.
                    await EmitAsync(session.Id, "captions_ready", new { session = readyWithCaptions }, cancellationToken);
                    await FinalizeSessionAsync(session.Id);
                    return;
                }
            }

            var ready = session with { Status = "ready", UpdatedAt = DateTimeOffset.UtcNow };
            await _sessions.UpdateSessionAsync(ready, cancellationToken);
            await EmitAsync(session.Id, "session_ready", new { session = ready }, cancellationToken);
        }
        catch (Exception ex)
        {
            var failed = session with
            {
                Status = "failed",
                ErrorCode = "video_session_init_failed",
                ErrorMessage = ex.Message,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await _sessions.UpdateSessionAsync(failed, CancellationToken.None);
            await EmitAsync(session.Id, "error", new { session.Id, failed.ErrorCode, failed.ErrorMessage }, CancellationToken.None);
            await FinalizeSessionAsync(session.Id);
        }
    }

    private IReadOnlyList<VideoSpeechWindowTask> BuildWindowTasks(
        VideoSpeechSessionStatus session,
        VideoSpeechPositionRequest request)
    {
        var now = DateTimeOffset.UtcNow;
        var sectionSeconds = VideoSpeechWindowGrid.SectionLength(_options.Speech.Video);
        var windowSeconds = VideoSpeechWindowGrid.WindowLength(_options.Speech.Video);
        var position = Math.Max(0, request.PositionSeconds);
        var lookahead = Math.Max(
            0,
            request.LookaheadSeconds ?? windowSeconds * _options.Speech.Video.PrefetchWindows);
        var windows = VideoSpeechWindowGrid.EnumerateWindows(
            position,
            position + lookahead,
            sectionSeconds,
            windowSeconds,
            session.DurationSeconds);

        var values = new List<VideoSpeechWindowTask>(windows.Count);
        var priority = 100;
        foreach (var window in windows)
        {
            values.Add(NewTask(session.Id, window.StartSeconds, window.EndSeconds, priority, now));
            priority = priority == 100 ? 60 : Math.Max(10, priority - 10);
        }

        return values;
    }

    private static VideoSpeechWindowTask NewTask(
        string sessionId,
        double startSeconds,
        double endSeconds,
        int priority,
        DateTimeOffset now)
        => new(
            Guid.NewGuid().ToString("N"),
            sessionId,
            Math.Max(0, startSeconds),
            Math.Max(startSeconds + 1, endSeconds),
            priority,
            "queued",
            string.Empty,
            string.Empty,
            now,
            now);

    private async Task ProcessWindowAsync(
        VideoSpeechWindowScheduler.WindowWorkItem item,
        CancellationToken cancellationToken)
    {
        var task = item.Task;
        var session = await _sessions.GetSessionAsync(task.SessionId, CancellationToken.None);
        if (session is null ||
            session.Status.Equals("canceled", StringComparison.OrdinalIgnoreCase) ||
            session.CaptionsUsed)
        {
            return;
        }

        try
        {
            await _sessions.UpdateWindowTaskStatusAsync(
                task.SessionId, task.StartSeconds, task.EndSeconds, "running", string.Empty, string.Empty, cancellationToken);
            await EmitAsync(session.Id, "window_started", new { task }, cancellationToken);
            var buffer = await EnsureBufferAsync(session, task, cancellationToken);
            byte[] audioBytes;
            var audioMimeType = buffer.AudioMimeType;
            // Padding widens the cut for ASR context without changing the
            // window's grid identity; segments map back from cutStart.
            var padding = Math.Max(0, _options.Speech.Video.WindowPaddingSeconds);
            var cutStart = Math.Max(buffer.StartSeconds, task.StartSeconds - padding);
            var cutEnd = Math.Min(buffer.EndSeconds, task.EndSeconds + padding);
            if (buffer.AudioMimeType.Equals("text/plain", StringComparison.OrdinalIgnoreCase))
            {
                audioBytes = await File.ReadAllBytesAsync(buffer.FilePath, cancellationToken);
                cutStart = task.StartSeconds;
                cutEnd = task.EndSeconds;
            }
            else
            {
                audioBytes = await _speechService.CutAudioFileAsync(
                    buffer.FilePath,
                    cutStart - buffer.StartSeconds,
                    cutEnd - cutStart,
                    cancellationToken);
                audioMimeType = "audio/wav";
            }

            await _asrSlots.WaitAsync(cancellationToken);
            SpeechProviderResult result;
            try
            {
                var provider = _speechProviders.GetRequired(session.Provider);
                var glossary = await _glossaries.GetOptionalAsync(null, cancellationToken);
                result = await provider.TranscribeAsync(
                    new SpeechProviderRequest(
                        audioBytes,
                        audioMimeType,
                        session.Language,
                        $"{session.SourceUrl}#t={task.StartSeconds:F0}-{task.EndSeconds:F0}",
                        glossary.Terms),
                    cancellationToken);
            }
            finally
            {
                _asrSlots.Release();
            }

            var normalized = SpeechService.NormalizeSegments(result.Text, result.Segments, session.Language);
            if (normalized.Count == 1 &&
                normalized[0].StartSeconds <= 0 &&
                normalized[0].EndSeconds <= 0)
            {
                normalized = [normalized[0] with { EndSeconds = cutEnd - cutStart }];
            }

            var absolute = normalized
                .Select(segment => segment with
                {
                    StartSeconds = cutStart + segment.StartSeconds,
                    EndSeconds = cutStart + Math.Max(segment.EndSeconds, segment.StartSeconds),
                    Language = string.IsNullOrWhiteSpace(segment.Language) ? session.Language : segment.Language
                })
                // Keep segments whose midpoint falls inside the window so the
                // padded cut never produces duplicates across adjacent windows.
                .Where(segment =>
                {
                    var midpoint = (segment.StartSeconds + segment.EndSeconds) / 2;
                    return midpoint >= task.StartSeconds && midpoint < task.EndSeconds;
                })
                .ToArray();
            var expanded = SpeechService.ExpandLongSegments(absolute, session.Language);
            var cached = ToCachedSegments(session, expanded, session.Provider, result.Engine, task.StartSeconds, task.EndSeconds);
            await _sessions.AddSegmentsAsync(cached, cancellationToken);

            foreach (var segment in cached)
            {
                await EmitAsync(session.Id, "segment", new { segment }, cancellationToken);
            }

            var request = await _sessions.GetSessionRequestAsync(session.Id, cancellationToken);
            await PublishTranslationsAsync(session, cached, request, cancellationToken);

            var count = await _sessions.CountSegmentsAsync(session.Id, cancellationToken);
            var updated = session with
            {
                Status = "running",
                SegmentCount = count,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await _sessions.UpdateSessionAsync(updated, cancellationToken);
            await _sessions.UpdateWindowTaskStatusAsync(
                task.SessionId, task.StartSeconds, task.EndSeconds, "succeeded", string.Empty, string.Empty, cancellationToken);
            await EmitAsync(session.Id, "window_done", new
            {
                task.Id,
                task.StartSeconds,
                task.EndSeconds,
                segmentCount = cached.Count
            }, cancellationToken);
            await OnWindowCompletedAsync(session.Id, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await _sessions.UpdateWindowTaskStatusAsync(
                task.SessionId, task.StartSeconds, task.EndSeconds, "canceled", "canceled", "Session was canceled.", CancellationToken.None);
        }
        catch (Exception ex)
        {
            var errorCode = ClassifyMediaError(ex.Message);
            if (IsRetryableMediaError(errorCode) && item.Attempt < _options.Speech.Video.MaxMediaRetryAttempts)
            {
                var attempt = item.Attempt + 1;
                await _sessions.UpdateWindowTaskStatusAsync(
                    task.SessionId, task.StartSeconds, task.EndSeconds, "queued", errorCode, ex.Message, CancellationToken.None);
                await EmitAsync(task.SessionId, "window_retry", new
                {
                    task.Id,
                    task.StartSeconds,
                    task.EndSeconds,
                    attempt,
                    errorCode
                }, CancellationToken.None);
                var delay = TimeSpan.FromMilliseconds(
                    Math.Max(100, _options.Speech.Video.MediaRetryBaseDelayMs) * Math.Pow(2, item.Attempt));
                _scheduler.ScheduleRetry(item with { Attempt = attempt }, delay);
                return;
            }

            await _sessions.UpdateWindowTaskStatusAsync(
                task.SessionId, task.StartSeconds, task.EndSeconds, "failed", "window_failed", ex.Message, CancellationToken.None);
            await EmitAsync(task.SessionId, "error", new
            {
                task.Id,
                errorCode = "window_failed",
                errorMessage = ex.Message
            }, CancellationToken.None);
            await OnWindowCompletedAsync(task.SessionId, CancellationToken.None);
        }
    }

    private async Task PublishTranslationsAsync(
        VideoSpeechSessionStatus session,
        IReadOnlyList<VideoSpeechCachedSegment> segments,
        VideoSpeechSessionRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null || !request.Translate || segments.Count == 0)
        {
            return;
        }

        var provider = Pick(request.TranslationProvider, _options.DefaultProvider);
        foreach (var segment in segments)
        {
            var outcome = await _translationService.TranslateAsync(
                new MortTranslateRequest
                {
                    Text = segment.Text,
                    Source = Pick(request.Source, Pick(segment.Language, Pick(request.Language, _options.DefaultSource))),
                    Target = Pick(request.Target, _options.DefaultTarget),
                    Mode = Pick(request.Mode, "subtitle"),
                    Glossary = request.Glossary,
                    Provider = provider,
                    Model = request.Model,
                    Profile = session.ProfileId,
                    SessionId = session.SessionId,
                    AllowSharedMemory = request.AllowSharedMemory,
                    PrincipalId = Pick(request.PrincipalId, "local")
                },
                cancellationToken);

            await EmitAsync(session.Id, "translation", new
            {
                translation = new SpeechTranslatedSegment(
                    segment.Index,
                    segment.StartSeconds,
                    segment.EndSeconds,
                    segment.Text,
                    outcome.Text,
                    outcome.ErrorCode,
                    outcome.ErrorMessage,
                    provider,
                    outcome.Engine,
                    outcome.LatencyMs,
                    outcome.CacheHit,
                    outcome.TokenUsage)
            }, cancellationToken);
        }
    }

    /// <summary>
    /// Advances the session after a window reaches a terminal status: marks the
    /// session completed once the whole duration is covered, or queues the next
    /// uncovered grid window at the lowest priority (backfill).
    /// </summary>
    private async Task OnWindowCompletedAsync(string sessionId, CancellationToken cancellationToken)
    {
        var session = await _sessions.GetSessionAsync(sessionId, CancellationToken.None);
        if (session is null ||
            session.CaptionsUsed ||
            session.DurationSeconds <= 0 ||
            !session.Status.Equals("running", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var video = _options.Speech.Video;
        var sectionSeconds = VideoSpeechWindowGrid.SectionLength(video);
        var windowSeconds = VideoSpeechWindowGrid.WindowLength(video);
        var gridWindows = VideoSpeechWindowGrid.EnumerateWindows(
            0, session.DurationSeconds, sectionSeconds, windowSeconds, session.DurationSeconds);
        var tasks = await _sessions.ListWindowTasksAsync(sessionId, CancellationToken.None);
        var byStart = tasks.ToDictionary(item => item.StartSeconds);

        VideoSpeechWindowGrid.GridWindow? nextCandidate = null;
        var allTerminal = true;
        foreach (var window in gridWindows)
        {
            var status = byStart.TryGetValue(window.StartSeconds, out var existing) ? existing.Status : string.Empty;
            if (status is "succeeded" or "failed")
            {
                continue;
            }

            allTerminal = false;
            if (nextCandidate is null && status is not "running")
            {
                // Missing, queued (possibly stale from a restart), or canceled:
                // all eligible for (re-)queueing.
                nextCandidate = window;
            }
        }

        if (allTerminal)
        {
            if (!tasks.Any(item => item.Status == "succeeded"))
            {
                return;
            }

            var completed = session with
            {
                Status = "completed",
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await _sessions.UpdateSessionAsync(completed, CancellationToken.None);
            await EmitAsync(sessionId, "session_completed", new
            {
                session = completed,
                failedWindows = tasks.Count(item => item.Status == "failed")
            }, CancellationToken.None);
            await FinalizeSessionAsync(sessionId);
            return;
        }

        if (!video.BackfillEnabled || nextCandidate is null)
        {
            return;
        }

        var backfill = NewTask(
            sessionId,
            nextCandidate.Value.StartSeconds,
            nextCandidate.Value.EndSeconds,
            priority: 1,
            DateTimeOffset.UtcNow);
        if (!await _sessions.TryMarkWindowQueuedAsync(backfill, CancellationToken.None))
        {
            return;
        }

        if (_scheduler.TryEnqueue(backfill, isBackfill: true))
        {
            await EmitAsync(sessionId, "window_queued", new { task = backfill, backfill = true }, CancellationToken.None);
        }
    }

    private async Task<VideoSpeechAudioBuffer> EnsureBufferAsync(
        VideoSpeechSessionStatus session,
        VideoSpeechWindowTask task,
        CancellationToken cancellationToken)
    {
        var existing = await TryGetCoveringBufferAsync(session.Id, task, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var video = _options.Speech.Video;
        var sectionSeconds = VideoSpeechWindowGrid.SectionLength(video);
        var sectionIndex = VideoSpeechWindowGrid.SectionIndexOf(task.StartSeconds, sectionSeconds);
        var sectionStart = sectionIndex * sectionSeconds;
        var sectionEnd = sectionStart + sectionSeconds;
        if (session.DurationSeconds > 0)
        {
            sectionEnd = Math.Min(sectionEnd, Math.Max(session.DurationSeconds, sectionStart + 1));
        }

        // The playhead window downloads a short "hot" range for low latency;
        // prefetch and backfill windows download their full section.
        double rangeStart;
        double rangeEnd;
        string kind;
        if (task.Priority >= 100)
        {
            var hotSeconds = Math.Max(15, video.HotSectionSeconds);
            rangeStart = task.StartSeconds;
            rangeEnd = Math.Min(sectionEnd, Math.Max(task.EndSeconds, task.StartSeconds + hotSeconds));
            kind = "hot";
        }
        else
        {
            rangeStart = sectionStart;
            rangeEnd = sectionEnd;
            kind = "section";
        }

        var key = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{session.Id}:{kind}:{rangeStart:F0}:{rangeEnd:F0}");
        var inflight = _inflightDownloads.GetOrAdd(
            key,
            _ => new Lazy<Task<VideoSpeechAudioBuffer>>(
                () => DownloadBufferAsync(session, task, rangeStart, rangeEnd, cancellationToken)));
        try
        {
            return await inflight.Value;
        }
        finally
        {
            _inflightDownloads.TryRemove(key, out _);
        }
    }

    private async Task<VideoSpeechAudioBuffer> DownloadBufferAsync(
        VideoSpeechSessionStatus session,
        VideoSpeechWindowTask task,
        double rangeStart,
        double rangeEnd,
        CancellationToken cancellationToken)
    {
        await _downloadSlots.WaitAsync(cancellationToken);
        try
        {
            // Another download may have completed while we waited for a slot.
            var existing = await TryGetCoveringBufferAsync(session.Id, task, cancellationToken);
            if (existing is not null)
            {
                return existing;
            }

            var bufferId = Guid.NewGuid().ToString("N");
            var now = DateTimeOffset.UtcNow;
            var pending = new VideoSpeechAudioBuffer(
                bufferId,
                session.Id,
                rangeStart,
                rangeEnd,
                string.Empty,
                string.Empty,
                0,
                "running",
                string.Empty,
                string.Empty,
                now,
                now);
            await _sessions.UpsertBufferAsync(pending, cancellationToken);
            await EmitAsync(session.Id, "section_download_started", new { buffer = pending }, cancellationToken);

            try
            {
                var mediaSource = _mediaSources.GetRequired(session.SourceUrl);
                var outputDirectory = Path.Combine(_bufferRootPath, session.Id);
                var section = await mediaSource.DownloadAudioSectionAsync(
                    session.SourceUrl,
                    rangeStart,
                    rangeEnd - rangeStart,
                    outputDirectory,
                    cancellationToken);
                var completed = pending with
                {
                    FilePath = section.FilePath,
                    AudioMimeType = section.AudioMimeType,
                    ByteLength = section.ByteLength,
                    Status = "succeeded",
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                await _sessions.UpsertBufferAsync(completed, cancellationToken);
                await EmitAsync(session.Id, "section_download_done", new { buffer = completed }, cancellationToken);
                await EvictStaleBuffersAsync(session.Id, completed.Id, cancellationToken);
                return completed;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var failed = pending with
                {
                    Status = "failed",
                    ErrorCode = ClassifyMediaError(ex.Message),
                    ErrorMessage = ex.Message,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                await _sessions.UpsertBufferAsync(failed, CancellationToken.None);
                await EmitAsync(session.Id, "media_error", new
                {
                    buffer = failed,
                    retryable = IsRetryableMediaError(failed.ErrorCode)
                }, CancellationToken.None);
                throw;
            }
        }
        finally
        {
            _downloadSlots.Release();
        }
    }

    private async Task<VideoSpeechAudioBuffer?> TryGetCoveringBufferAsync(
        string sessionId,
        VideoSpeechWindowTask task,
        CancellationToken cancellationToken)
    {
        var existing = await _sessions.FindCoveringBufferAsync(sessionId, task.StartSeconds, task.EndSeconds, cancellationToken);
        if (existing is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(existing.FilePath) || !File.Exists(existing.FilePath))
        {
            await _sessions.DeleteBufferAsync(existing.Id, cancellationToken);
            return null;
        }

        // Touch the buffer so LRU eviction keeps recently used sections.
        var touched = existing with { UpdatedAt = DateTimeOffset.UtcNow };
        await _sessions.UpsertBufferAsync(touched, cancellationToken);
        await EmitAsync(sessionId, "buffer_hit", new { buffer = touched }, cancellationToken);
        return touched;
    }

    private async Task EvictStaleBuffersAsync(
        string sessionId,
        string protectedBufferId,
        CancellationToken cancellationToken)
    {
        var max = _options.Speech.Video.MaxBufferedSections;
        if (max <= 0)
        {
            return;
        }

        var buffers = await _sessions.ListBuffersAsync(sessionId, cancellationToken);
        var succeeded = buffers
            .Where(buffer => buffer.Status == "succeeded" && buffer.Id != protectedBufferId)
            .OrderBy(buffer => buffer.UpdatedAt)
            .ToList();
        var excess = succeeded.Count + 1 - max;
        for (var i = 0; i < excess && i < succeeded.Count; i++)
        {
            var buffer = succeeded[i];
            try
            {
                if (File.Exists(buffer.FilePath))
                {
                    File.Delete(buffer.FilePath);
                }
            }
            catch (IOException)
            {
                // Still in use by a running window; it will be reclaimed at
                // session finalization instead.
                continue;
            }

            await _sessions.DeleteBufferAsync(buffer.Id, cancellationToken);
        }
    }

    /// <summary>
    /// Releases per-session resources once the session reaches a terminal
    /// status: cancels in-flight work, removes audio buffers from disk and
    /// store, and trims old events.
    /// </summary>
    private async Task FinalizeSessionAsync(string sessionId)
    {
        _scheduler.CancelSession(sessionId);
        var directory = Path.Combine(_bufferRootPath, sessionId);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }

                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A dying ffmpeg/yt-dlp may still hold a handle on Windows.
                await Task.Delay(500);
            }
        }

        await _sessions.DeleteBuffersAsync(sessionId, CancellationToken.None);
        var keep = _options.Speech.Video.EventRetentionCount;
        if (keep > 0)
        {
            await _sessions.TrimEventsAsync(sessionId, keep, CancellationToken.None);
        }
    }

    /// <summary>
    /// Persists an event (the source of truth) and then pushes it to live SSE
    /// subscribers. The DB write strictly precedes the publish so a published
    /// event can always be re-read from the log.
    /// </summary>
    private async Task EmitAsync(
        string sessionId,
        string type,
        object payload,
        CancellationToken cancellationToken)
    {
        var payloadJson = System.Text.Json.JsonSerializer.Serialize(payload, EventJsonOptions);
        var sequence = await _sessions.AddEventJsonAsync(sessionId, type, payloadJson, cancellationToken);
        _events.Publish(new VideoSpeechSessionEvent(sequence, sessionId, type, payloadJson, DateTimeOffset.UtcNow));
    }

    private static IReadOnlyList<VideoSpeechCachedSegment> ToCachedSegments(
        VideoSpeechSessionStatus session,
        IReadOnlyList<SpeechSegment> segments,
        string provider,
        string engine,
        double windowStartSeconds,
        double windowEndSeconds)
    {
        var now = DateTimeOffset.UtcNow;
        return segments
            .Where(segment => !string.IsNullOrWhiteSpace(segment.Text))
            .Select((segment, index) => new VideoSpeechCachedSegment(
                Guid.NewGuid().ToString("N"),
                session.Id,
                index,
                Math.Max(0, segment.StartSeconds),
                Math.Max(segment.StartSeconds, segment.EndSeconds),
                segment.Text,
                segment.Confidence,
                segment.Speaker,
                string.IsNullOrWhiteSpace(segment.Language) ? session.Language : segment.Language,
                provider,
                engine,
                windowStartSeconds,
                windowEndSeconds,
                now))
            .ToArray();
    }

    private static string ClassifyMediaError(string message)
    {
        if (message.Contains("403", StringComparison.OrdinalIgnoreCase))
        {
            return "media_forbidden";
        }

        if (message.Contains("429", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("412", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("throttl", StringComparison.OrdinalIgnoreCase))
        {
            return "media_rate_limited";
        }

        if (message.Contains("timed out", StringComparison.OrdinalIgnoreCase))
        {
            return "media_timeout";
        }

        return "media_failed";
    }

    private static bool IsRetryableMediaError(string errorCode)
        => errorCode is "media_rate_limited" or "media_timeout" or "media_forbidden";

    private static string Pick(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    /// <summary>
    /// Tracks fire-and-forget work so DisposeAsync can wait for it; otherwise
    /// store writes from a background tail can race host shutdown (and, in
    /// tests, re-open pooled SQLite connections after ClearAllPools).
    /// </summary>
    private void TrackBackground(Func<Task> work)
    {
        var id = Guid.NewGuid();
        _backgroundWork[id] = RunAsync();

        async Task RunAsync()
        {
            await Task.Yield();
            try
            {
                await work();
            }
            catch
            {
                // Background work owns its own error reporting.
            }
            finally
            {
                _backgroundWork.TryRemove(id, out _);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _scheduler.DisposeAsync();
        try
        {
            await Task.WhenAll(_backgroundWork.Values).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // Abandon background work that cannot finish during shutdown.
        }
    }

    public void Dispose()
        => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
