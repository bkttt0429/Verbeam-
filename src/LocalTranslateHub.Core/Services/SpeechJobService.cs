using System.Collections.Concurrent;
using System.Text;
using LocalTranslateHub.Core.Models;
using LocalTranslateHub.Core.Options;
using LocalTranslateHub.Core.Providers;
using LocalTranslateHub.Core.Storage;

namespace LocalTranslateHub.Core.Services;

public sealed class SpeechJobService
{
    private readonly LocalTranslateHubOptions _options;
    private readonly SpeechService _speechService;
    private readonly SpeechProviderRegistry _providers;
    private readonly GlossaryStore _glossaries;
    private readonly ISpeechEventStore _speechEvents;
    private readonly ISpeechJobStore _jobs;
    private readonly TranslationService _translationService;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeJobs = new(StringComparer.OrdinalIgnoreCase);

    public SpeechJobService(
        LocalTranslateHubOptions options,
        SpeechService speechService,
        SpeechProviderRegistry providers,
        GlossaryStore glossaries,
        ISpeechEventStore speechEvents,
        ISpeechJobStore jobs,
        TranslationService translationService)
    {
        _options = options;
        _speechService = speechService;
        _providers = providers;
        _glossaries = glossaries;
        _speechEvents = speechEvents;
        _jobs = jobs;
        _translationService = translationService;
    }

    public async Task<SpeechJobStatus> StartAsync(
        SpeechJobRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);

        var provider = Pick(request.SpeechProvider ?? request.Provider, _options.Speech.DefaultProvider);
        var language = Pick(request.Language, _options.Speech.DefaultLanguage);
        var profile = Pick(request.Profile, "default");
        var sessionId = Pick(request.SessionId, string.Empty);
        var sourceUri = request.SourceUrl?.Trim() ?? string.Empty;
        var now = DateTimeOffset.UtcNow;
        var job = new SpeechJobStatus(
            Guid.NewGuid().ToString("N"),
            "queued",
            profile,
            sessionId,
            string.IsNullOrWhiteSpace(sourceUri) ? "upload" : "url",
            sourceUri,
            language,
            provider,
            string.Empty,
            CaptionsUsed: false,
            SegmentCount: 0,
            Progress: 0,
            ResultEventId: string.Empty,
            ErrorCode: string.Empty,
            ErrorMessage: string.Empty,
            now,
            StartedAt: null,
            CompletedAt: null,
            now);

        await _jobs.AddJobAsync(job, request, cancellationToken);
        await _jobs.AddEventAsync(job.Id, "job_queued", new { job }, cancellationToken);

        var cts = new CancellationTokenSource();
        _activeJobs[job.Id] = cts;
        _ = Task.Run(() => RunJobAsync(job, request, cts.Token), CancellationToken.None);

        return job;
    }

    public Task<SpeechJobStatus?> GetAsync(
        string jobId,
        CancellationToken cancellationToken = default)
        => _jobs.GetJobAsync(jobId, cancellationToken);

    public Task<IReadOnlyList<SpeechJobStatus>> ListAsync(
        string profileId,
        int limit,
        CancellationToken cancellationToken = default)
        => _jobs.ListJobsAsync(profileId, limit, cancellationToken);

    public Task<IReadOnlyList<SpeechJobEvent>> ListEventsAsync(
        string jobId,
        long afterSequence,
        int limit,
        CancellationToken cancellationToken = default)
        => _jobs.ListEventsAsync(jobId, afterSequence, limit, cancellationToken);

    public async Task<bool> CancelAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        if (!_activeJobs.TryGetValue(jobId, out var cts))
        {
            return false;
        }

        await _jobs.AddEventAsync(jobId, "cancel_requested", new { jobId }, cancellationToken);
        cts.Cancel();
        return true;
    }

    private async Task RunJobAsync(
        SpeechJobStatus initialJob,
        SpeechJobRequest request,
        CancellationToken cancellationToken)
    {
        var job = initialJob;
        try
        {
            job = await UpdateJobAsync(
                job with
                {
                    Status = "running",
                    StartedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                },
                cancellationToken);
            await _jobs.AddEventAsync(job.Id, "job_started", new { job }, cancellationToken);

            if (!string.IsNullOrWhiteSpace(request.SourceUrl) &&
                SpeechService.IsYouTubeUrl(request.SourceUrl))
            {
                job = await RunYouTubeJobAsync(job, request, cancellationToken);
            }
            else
            {
                job = await RunSynchronousJobAsync(job, request, cancellationToken);
            }

            var completedJob = job with
            {
                Status = "succeeded",
                Progress = 1,
                CompletedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await _jobs.AddEventAsync(completedJob.Id, "job_done", new { job = completedJob }, cancellationToken);
            job = await UpdateJobAsync(completedJob, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            job = job with
            {
                Status = "canceled",
                ErrorCode = "canceled",
                ErrorMessage = "ASR job was canceled.",
                CompletedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await _jobs.UpdateJobAsync(job, CancellationToken.None);
            await _jobs.AddEventAsync(job.Id, "job_canceled", new { job }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            job = job with
            {
                Status = "failed",
                ErrorCode = "asr_job_failed",
                ErrorMessage = ex.Message,
                CompletedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await _jobs.UpdateJobAsync(job, CancellationToken.None);
            await _jobs.AddEventAsync(job.Id, "error", new { job.Id, errorCode = job.ErrorCode, errorMessage = job.ErrorMessage }, CancellationToken.None);
        }
        finally
        {
            if (_activeJobs.TryRemove(job.Id, out var cts))
            {
                cts.Dispose();
            }
        }
    }

    private async Task<SpeechJobStatus> RunSynchronousJobAsync(
        SpeechJobStatus job,
        SpeechJobRequest request,
        CancellationToken cancellationToken)
    {
        var response = await _speechService.RecognizeAsync(ToSpeechRequest(request), cancellationToken);
        await PublishSegmentsAsync(job.Id, response.Segments, request, cancellationToken);

        return await UpdateJobAsync(
            job with
            {
                SourceKind = response.SourceKind,
                SourceUri = response.SourceUri,
                Provider = response.Provider,
                Engine = response.Engine,
                CaptionsUsed = response.CaptionsUsed,
                SegmentCount = response.Segments.Count,
                Progress = 1,
                ResultEventId = response.EventId,
                UpdatedAt = DateTimeOffset.UtcNow
            },
            cancellationToken);
    }

    private async Task<SpeechJobStatus> RunYouTubeJobAsync(
        SpeechJobStatus job,
        SpeechJobRequest request,
        CancellationToken cancellationToken)
    {
        var sourceUrl = request.SourceUrl?.Trim()
            ?? throw new ArgumentException("sourceUrl is required.");
        var preferCaptions = request.PreferCaptions ?? _options.Speech.PreferCaptions;
        if (preferCaptions)
        {
            var captionSegments = await _speechService.TryLoadYouTubeCaptionsAsync(
                sourceUrl,
                job.Language,
                cancellationToken);
            if (captionSegments.Count > 0)
            {
                await PublishSegmentsAsync(job.Id, captionSegments, request, cancellationToken);
                var response = await StoreSpeechEventAsync(
                    job,
                    "youtube-captions",
                    sourceUrl,
                    "text/vtt",
                    "youtube-captions",
                    "yt-dlp:captions",
                    captionSegments,
                    captionsUsed: true,
                    latencyMs: 0,
                    cancellationToken);
                return await UpdateJobAsync(
                    job with
                    {
                        SourceKind = response.SourceKind,
                        SourceUri = response.SourceUri,
                        Provider = response.Provider,
                        Engine = response.Engine,
                        CaptionsUsed = response.CaptionsUsed,
                        SegmentCount = response.Segments.Count,
                        Progress = 1,
                        ResultEventId = response.EventId,
                        UpdatedAt = DateTimeOffset.UtcNow
                    },
                    cancellationToken);
            }
        }

        await _jobs.AddEventAsync(job.Id, "media_started", new
        {
            sourceUrl,
            audioFormat = _options.Speech.YouTube.AudioFormat,
            chunkSeconds = _options.Speech.YouTube.AudioChunkSeconds
        }, cancellationToken);

        var provider = _providers.GetRequired(job.Provider);
        var glossary = await _glossaries.GetOptionalAsync(request.Glossary, cancellationToken);
        var chunkSeconds = Math.Max(30, _options.Speech.YouTube.AudioChunkSeconds);
        var segments = new List<SpeechSegment>();
        var engines = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var started = DateTimeOffset.UtcNow;

        await foreach (var chunk in _speechService.DownloadYouTubeAudioChunksAsync(sourceUrl, chunkSeconds, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _jobs.AddEventAsync(job.Id, "chunk_started", new
            {
                chunk.Index,
                chunk.StartSeconds,
                byteLength = chunk.AudioBytes.Length
            }, cancellationToken);

            var providerRequest = new SpeechProviderRequest(
                chunk.AudioBytes,
                "audio/wav",
                job.Language,
                $"{sourceUrl}#chunk={chunk.Index}",
                glossary.Terms);
            var result = await provider.TranscribeAsync(providerRequest, cancellationToken);
            engines.Add(result.Engine);

            var chunkSegments = SpeechService.NormalizeSegments(result.Text, result.Segments, job.Language);
            if (chunkSegments.Count == 1 &&
                chunkSegments[0].StartSeconds <= 0 &&
                chunkSegments[0].EndSeconds <= 0)
            {
                chunkSegments = [chunkSegments[0] with
                {
                    EndSeconds = SpeechService.EstimateWavDurationSeconds(chunk.AudioBytes, chunkSeconds)
                }];
            }

            foreach (var segment in chunkSegments)
            {
                segments.Add(segment with
                {
                    Index = segments.Count,
                    StartSeconds = chunk.StartSeconds + segment.StartSeconds,
                    EndSeconds = chunk.StartSeconds + Math.Max(segment.EndSeconds, segment.StartSeconds),
                    Language = string.IsNullOrWhiteSpace(segment.Language) ? job.Language : segment.Language
                });
            }

            var expanded = SpeechService.ExpandLongSegments(segments, job.Language);
            var newSegments = expanded.Skip(job.SegmentCount).ToArray();
            await PublishSegmentsAsync(job.Id, newSegments, request, cancellationToken);

            job = await UpdateJobAsync(
                job with
                {
                    SourceKind = "youtube-audio-chunks",
                    SourceUri = sourceUrl,
                    Engine = engines.Count == 0 ? $"{provider.Descriptor.Name}:chunks" : $"{string.Join("+", engines.Order(StringComparer.OrdinalIgnoreCase))}:chunks",
                    SegmentCount = expanded.Count,
                    Progress = Math.Min(0.95, 0.1 + ((chunk.Index + 1) * 0.05)),
                    UpdatedAt = DateTimeOffset.UtcNow
                },
                cancellationToken);

            await _jobs.AddEventAsync(job.Id, "checkpoint", new
            {
                chunk.Index,
                segmentCount = job.SegmentCount,
                job.Progress
            }, cancellationToken);
        }

        var finalSegments = SpeechService.ExpandLongSegments(segments, job.Language);
        if (finalSegments.Count == 0)
        {
            throw new InvalidOperationException("YouTube audio was downloaded, but ASR produced no speech segments.");
        }

        var finalEngine = engines.Count == 0
            ? $"{provider.Descriptor.Name}:chunks"
            : $"{string.Join("+", engines.Order(StringComparer.OrdinalIgnoreCase))}:chunks";
        var speech = await StoreSpeechEventAsync(
            job,
            "youtube-audio-chunks",
            sourceUrl,
            "audio/wav",
            provider.Descriptor.Name,
            finalEngine,
            finalSegments,
            captionsUsed: false,
            latencyMs: (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds,
            cancellationToken);

        return await UpdateJobAsync(
            job with
            {
                SourceKind = speech.SourceKind,
                SourceUri = speech.SourceUri,
                Provider = speech.Provider,
                Engine = speech.Engine,
                CaptionsUsed = speech.CaptionsUsed,
                SegmentCount = speech.Segments.Count,
                Progress = 1,
                ResultEventId = speech.EventId,
                UpdatedAt = DateTimeOffset.UtcNow
            },
            cancellationToken);
    }

    private async Task PublishSegmentsAsync(
        string jobId,
        IReadOnlyList<SpeechSegment> segments,
        SpeechJobRequest request,
        CancellationToken cancellationToken)
    {
        foreach (var segment in segments)
        {
            await _jobs.AddEventAsync(jobId, "segment", new { segment }, cancellationToken);
            if (request.Translate)
            {
                var outcome = await _translationService.TranslateAsync(
                    new MortTranslateRequest
                    {
                        Text = segment.Text,
                        Source = Pick(request.Source, Pick(request.Language, _options.DefaultSource)),
                        Target = Pick(request.Target, _options.DefaultTarget),
                        Mode = Pick(request.Mode, "subtitle"),
                        Glossary = request.Glossary,
                        Provider = Pick(request.TranslationProvider, _options.DefaultProvider),
                        Model = request.Model,
                        Profile = request.Profile,
                        SessionId = request.SessionId
                    },
                    cancellationToken);

                await _jobs.AddEventAsync(jobId, "translation", new
                {
                    translation = new SpeechTranslatedSegment(
                        segment.Index,
                        segment.StartSeconds,
                        segment.EndSeconds,
                        segment.Text,
                        outcome.Text,
                        outcome.ErrorCode,
                        outcome.ErrorMessage,
                        Pick(request.TranslationProvider, _options.DefaultProvider),
                        outcome.Engine,
                        outcome.LatencyMs,
                        outcome.CacheHit)
                }, cancellationToken);
            }
        }
    }

    private async Task<SpeechResponse> StoreSpeechEventAsync(
        SpeechJobStatus job,
        string sourceKind,
        string sourceUri,
        string audioMimeType,
        string provider,
        string engine,
        IReadOnlyList<SpeechSegment> segments,
        bool captionsUsed,
        long latencyMs,
        CancellationToken cancellationToken)
    {
        var text = SpeechService.JoinSegmentText(segments);
        var eventId = Guid.NewGuid().ToString("N");
        var audioHash = SpeechService.ComputeSha256(Encoding.UTF8.GetBytes(sourceUri));
        await _speechEvents.AddEventAsync(
            new SpeechEvent(
                eventId,
                job.ProfileId,
                job.SessionId,
                sourceKind,
                sourceUri,
                audioHash,
                audioMimeType,
                job.Language,
                provider,
                engine,
                text,
                segments,
                captionsUsed,
                latencyMs,
                DateTimeOffset.UtcNow),
            cancellationToken);

        return new SpeechResponse(
            eventId,
            text,
            segments,
            provider,
            engine,
            job.Language,
            sourceKind,
            sourceUri,
            audioMimeType,
            captionsUsed,
            latencyMs);
    }

    private async Task<SpeechJobStatus> UpdateJobAsync(
        SpeechJobStatus job,
        CancellationToken cancellationToken)
    {
        await _jobs.UpdateJobAsync(job, cancellationToken);
        return job;
    }

    private static SpeechRequest ToSpeechRequest(SpeechJobRequest request)
        => new()
        {
            AudioBase64 = request.AudioBase64,
            AudioMimeType = request.AudioMimeType,
            SourceUrl = request.SourceUrl,
            Provider = request.SpeechProvider ?? request.Provider,
            Language = request.Language,
            Profile = request.Profile,
            SessionId = request.SessionId,
            Glossary = request.Glossary,
            PreferCaptions = request.PreferCaptions
        };

    private static void ValidateRequest(SpeechJobRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.AudioBase64) &&
            string.IsNullOrWhiteSpace(request.SourceUrl))
        {
            throw new ArgumentException("audioBase64 or sourceUrl is required.");
        }
    }

    private static string Pick(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
