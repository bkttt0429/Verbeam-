using System.Collections.Concurrent;
using System.Security.Cryptography;
using Verbeam.Core.Models;
using Verbeam.Core.Options;
using Verbeam.Core.Storage;

namespace Verbeam.Core.Services;

public sealed class OcrJobService
{
    private readonly VerbeamOptions _options;
    private readonly OcrService _ocrService;
    private readonly IOcrMemoryStore _ocrEvents;
    private readonly IOcrJobStore _jobs;
    private readonly OcrRoutingService _routing;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeJobs = new(StringComparer.OrdinalIgnoreCase);

    public OcrJobService(
        VerbeamOptions options,
        OcrService ocrService,
        IOcrMemoryStore ocrEvents,
        IOcrJobStore jobs,
        OcrRoutingService routing)
    {
        _options = options;
        _ocrService = ocrService;
        _ocrEvents = ocrEvents;
        _jobs = jobs;
        _routing = routing;
    }

    public async Task<OcrJobStatus> StartAsync(
        OcrJobRequest request,
        CancellationToken cancellationToken = default)
    {
        var image = DecodeImageForJob(request.ImageBase64, request.ImageMimeType, _options.Ocr.MaxImageBytes);
        var decision = _routing.ResolveDecision(request.OcrProvider ?? request.Provider, request.ContentType, request.Preference);
        var provider = decision.Provider;
        var language = Pick(request.Language, _options.Ocr.DefaultLanguage);
        var profile = Pick(request.Profile, "default");
        var sessionId = Pick(request.SessionId, string.Empty);
        var estimatedDurationMs = await EstimateDurationMsAsync(profile, provider, decision.ExpectedLatencyMs, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var job = new OcrJobStatus(
            Guid.NewGuid().ToString("N"),
            "queued",
            profile,
            sessionId,
            image.Hash,
            image.MimeType,
            language,
            provider,
            string.Empty,
            BlockCount: 0,
            Progress: 0,
            ResultEventId: string.Empty,
            CacheHit: false,
            ErrorCode: string.Empty,
            ErrorMessage: string.Empty,
            now,
            StartedAt: null,
            CompletedAt: null,
            now)
        {
            Stage = OcrJobStages.Queued,
            EstimatedDurationMs = estimatedDurationMs
        };

        var cts = new CancellationTokenSource();
        _activeJobs[job.Id] = cts;
        try
        {
            await _jobs.AddJobAsync(job, request, cancellationToken);
            await _jobs.AddEventAsync(job.Id, "job_queued", new { job }, cancellationToken);
        }
        catch
        {
            if (_activeJobs.TryRemove(job.Id, out var failedCts))
            {
                failedCts.Dispose();
            }

            throw;
        }

        _ = Task.Run(() => RunJobAsync(job, request, cts.Token), CancellationToken.None);

        return job;
    }

    private async Task<long> EstimateDurationMsAsync(
        string profileId,
        string provider,
        int expectedLatencyMs,
        CancellationToken cancellationToken)
    {
        try
        {
            var events = await _ocrEvents.ListEventsAsync(profileId, 50, cancellationToken);
            var latencies = events
                .Where(item => item.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase) && item.LatencyMs > 0)
                .Take(5)
                .Select(item => item.LatencyMs)
                .OrderBy(value => value)
                .ToArray();
            if (latencies.Length > 0)
            {
                return latencies[latencies.Length / 2];
            }
        }
        catch
        {
            // Estimation must never block or fail a job; fall back to the profile expectation.
        }

        return expectedLatencyMs;
    }

    public async Task<OcrJobStatus?> GetAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        var job = await _jobs.GetJobAsync(jobId, cancellationToken);
        if (job is null || IsTerminal(job.Status) || _activeJobs.ContainsKey(job.Id))
        {
            return job;
        }

        // No worker owns this job (e.g. the server restarted mid-run). Give a grace
        // period for the StartAsync race, then fail it so clients stop waiting.
        if (DateTimeOffset.UtcNow - job.UpdatedAt < TimeSpan.FromSeconds(30))
        {
            return job;
        }

        var orphaned = job with
        {
            Status = "failed",
            ErrorCode = "ocr_job_orphaned",
            ErrorMessage = "OCR job was abandoned (server restarted while the job was running).",
            CompletedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await _jobs.UpdateJobAsync(orphaned, cancellationToken);
        await _jobs.AddEventAsync(orphaned.Id, "error", new { orphaned.Id, errorCode = orphaned.ErrorCode, errorMessage = orphaned.ErrorMessage }, cancellationToken);
        return orphaned;
    }

    private static bool IsTerminal(string status)
        => status is "succeeded" or "failed" or "canceled";

    public Task<IReadOnlyList<OcrJobStatus>> ListAsync(
        string profileId,
        int limit,
        CancellationToken cancellationToken = default)
        => _jobs.ListJobsAsync(profileId, limit, cancellationToken);

    public Task<IReadOnlyList<OcrJobEvent>> ListEventsAsync(
        string jobId,
        long afterSequence,
        int limit,
        CancellationToken cancellationToken = default)
        => _jobs.ListEventsAsync(jobId, afterSequence, limit, cancellationToken);

    public async Task<OcrJobResult?> GetResultAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        var job = await GetAsync(jobId, cancellationToken);
        if (job is null)
        {
            return null;
        }

        var result = string.IsNullOrWhiteSpace(job.ResultEventId)
            ? null
            : await _ocrEvents.GetEventAsync(job.ResultEventId, cancellationToken);
        return new OcrJobResult(job, result);
    }

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
        OcrJobStatus initialJob,
        OcrJobRequest request,
        CancellationToken cancellationToken)
    {
        var job = initialJob;
        try
        {
            job = await UpdateJobAsync(
                job with
                {
                    Status = "running",
                    Stage = OcrJobStages.Preparing,
                    StartedAt = DateTimeOffset.UtcNow,
                    Progress = StageProgress(OcrJobStages.Preparing),
                    UpdatedAt = DateTimeOffset.UtcNow
                },
                cancellationToken);
            await _jobs.AddEventAsync(job.Id, "job_started", new { job }, cancellationToken);

            var response = await _ocrService.RecognizeAsync(
                ToOcrRequest(request),
                async (stage, stageCancellationToken) =>
                {
                    job = await UpdateJobAsync(
                        job with
                        {
                            Stage = stage,
                            Progress = StageProgress(stage),
                            UpdatedAt = DateTimeOffset.UtcNow
                        },
                        stageCancellationToken);
                    await _jobs.AddEventAsync(
                        job.Id,
                        "stage",
                        new { jobId = job.Id, stage, progress = job.Progress, estimatedDurationMs = job.EstimatedDurationMs },
                        stageCancellationToken);
                },
                cancellationToken);
            await _jobs.AddEventAsync(job.Id, "result", new
            {
                response.EventId,
                response.Provider,
                response.Engine,
                response.Language,
                blockCount = response.Blocks.Count,
                response.LatencyMs,
                response.CacheHit
            }, cancellationToken);

            var completedJob = job with
            {
                Status = "succeeded",
                Stage = OcrJobStages.Done,
                Provider = response.Provider,
                Engine = response.Engine,
                BlockCount = response.Blocks.Count,
                Progress = 1,
                ResultEventId = response.EventId,
                CacheHit = response.CacheHit,
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
                ErrorMessage = "OCR job was canceled.",
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
                ErrorCode = "ocr_job_failed",
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

    private async Task<OcrJobStatus> UpdateJobAsync(
        OcrJobStatus job,
        CancellationToken cancellationToken)
    {
        await _jobs.UpdateJobAsync(job, cancellationToken);
        return job;
    }

    private static double StageProgress(string stage)
        => stage switch
        {
            OcrJobStages.Queued => 0,
            OcrJobStages.Preparing => 0.05,
            OcrJobStages.Recognizing => 0.10,
            OcrJobStages.Assembling => 0.92,
            OcrJobStages.Done => 1,
            _ => 0.05
        };

    private static OcrRequest ToOcrRequest(OcrJobRequest request)
        => new()
        {
            ImageBase64 = request.ImageBase64,
            ImageMimeType = request.ImageMimeType,
            Provider = request.OcrProvider ?? request.Provider,
            ContentType = request.ContentType,
            Preference = request.Preference,
            Language = request.Language,
            LanguageHint = request.LanguageHint,
            AllowedLanguages = request.AllowedLanguages,
            Profile = request.Profile,
            SessionId = request.SessionId,
            NormalizeWhitespace = request.NormalizeWhitespace,
            PreprocessingPreset = request.PreprocessingPreset
        };

    private static DecodedJobImage DecodeImageForJob(
        string? imageBase64,
        string? imageMimeType,
        int maxImageBytes)
    {
        if (string.IsNullOrWhiteSpace(imageBase64))
        {
            throw new ArgumentException("imageBase64 is required.");
        }

        var payload = imageBase64.Trim();
        var mimeType = Pick(imageMimeType, "application/octet-stream");
        if (payload.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var commaIndex = payload.IndexOf(',');
            if (commaIndex < 0)
            {
                throw new ArgumentException("imageBase64 data URI is missing a comma separator.");
            }

            var metadata = payload[5..commaIndex];
            var semicolonIndex = metadata.IndexOf(';');
            if (semicolonIndex > 0)
            {
                mimeType = metadata[..semicolonIndex];
            }
            else if (!string.IsNullOrWhiteSpace(metadata))
            {
                mimeType = metadata;
            }

            payload = payload[(commaIndex + 1)..];
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(payload);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("imageBase64 must be valid base64.", ex);
        }

        if (bytes.Length == 0)
        {
            throw new ArgumentException("imageBase64 decoded to an empty payload.");
        }

        if (bytes.Length > maxImageBytes)
        {
            throw new ArgumentException($"image payload is too large. Max size is {maxImageBytes} bytes.");
        }

        return new DecodedJobImage(
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            mimeType);
    }

    private static string Pick(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private sealed record DecodedJobImage(string Hash, string MimeType);
}
