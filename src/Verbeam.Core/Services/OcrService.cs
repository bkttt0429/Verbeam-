using System.Buffers.Binary;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Verbeam.Core.Models;
using Verbeam.Core.Options;
using Verbeam.Core.Providers;
using Verbeam.Core.Storage;

namespace Verbeam.Core.Services;

public delegate Task OcrStageCallback(string stage, CancellationToken cancellationToken);

public sealed class OcrService
{
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly TimeSpan RealtimeCorrectionsTtl = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RealtimeExactFrameCacheTtl = TimeSpan.FromMilliseconds(1500);
    private const int RealtimeExactFrameCacheMaxEntries = 48;
    private static readonly string[] LatinSubtitleWords =
    [
        "I", "A", "AM", "AN", "AND", "ARE", "AS", "AT", "BE", "CAN", "DO", "FOR",
        "FROM", "GO", "HAVE", "HE", "HER", "HERE", "HIS", "IN", "IS", "IT", "ME",
        "MY", "NO", "NOT", "OF", "ON", "ONE", "OR", "OUR", "OUT", "RISING", "STAR",
        "THE", "THEM", "THEN", "THERE", "THEY", "THIS", "TO", "UP", "WE", "WHAT",
        "WHEN", "WHERE", "WHO", "WILL", "WITH", "YOU", "YOUR"
    ];

    private readonly VerbeamOptions _options;
    private readonly OcrProviderRegistry _providers;
    private readonly IOcrMemoryStore _memoryStore;
    private readonly OcrRoutingService _routing;
    private readonly OcrConcurrencyLimiter _concurrencyLimiter;
    private readonly RecurringTextSuppressor _recurringTextSuppressor;
    private readonly object _correctionsCacheLock = new();
    private readonly Dictionary<(string ProfileId, string Language), (DateTimeOffset FetchedAt, IReadOnlyList<OcrCorrection> Items)> _correctionsCache = new();
    private readonly object _realtimeExactFrameCacheLock = new();
    private readonly Dictionary<string, (DateTimeOffset FetchedAt, OcrResponse Response)> _realtimeExactFrameCache = new();

    public OcrService(
        VerbeamOptions options,
        OcrProviderRegistry providers,
        IOcrMemoryStore memoryStore,
        OcrRoutingService routing,
        OcrConcurrencyLimiter concurrencyLimiter,
        RecurringTextSuppressor? recurringTextSuppressor = null)
    {
        _options = options;
        _providers = providers;
        _memoryStore = memoryStore;
        _routing = routing;
        _concurrencyLimiter = concurrencyLimiter;
        _recurringTextSuppressor = recurringTextSuppressor ?? new RecurringTextSuppressor(options);
    }

    public Task<OcrResponse> RecognizeAsync(
        OcrRequest request,
        CancellationToken cancellationToken = default)
        => RecognizeAsync(request, onStage: null, cancellationToken);

    public async Task<OcrResponse> RecognizeAsync(
        OcrRequest request,
        OcrStageCallback? onStage,
        CancellationToken cancellationToken = default)
    {
        async Task ReportStageAsync(string stage)
        {
            if (onStage is not null)
            {
                await onStage(stage, cancellationToken);
            }
        }

        await ReportStageAsync(OcrJobStages.Preparing);
        var providerName = _routing.ResolveProviderName(request.Provider, request.ContentType, request.Preference, request.Language);
        var provider = _providers.GetRequired(providerName);
        var requestedLanguage = LanguageRegistry.Normalize(Pick(request.Language, _options.Ocr.DefaultLanguage));
        var isAuto = requestedLanguage == LanguageRegistry.Auto;
        var languageHint = LanguageRegistry.Normalize(request.LanguageHint);
        var allowedLanguages = LanguageRegistry.ResolveAllowedLanguages(
            request.AllowedLanguages is { Count: > 0 }
                ? request.AllowedLanguages
                : _options.Ocr.AutoDetection.DefaultAllowedLanguages);
        var seedLanguage = isAuto
            ? languageHint != LanguageRegistry.Auto ? languageHint : SeedLanguageFor(provider.Descriptor, allowedLanguages)
            : requestedLanguage;
        var profileId = Pick(request.Profile, "default");
        var sessionId = Pick(request.SessionId, string.Empty);
        var normalizeWhitespace = request.NormalizeWhitespace ?? _options.Ocr.NormalizeWhitespace;
        var preprocessingPreset = NormalizePreprocessingPreset(request.PreprocessingPreset, _options.Ocr.Preprocessing);
        var realtime = request.Realtime == true;
        var decoded = DecodeImage(request.ImageBase64, request.ImageMimeType, _options.Ocr.MaxImageBytes);
        var imageHash = ComputeSha256(decoded.ImageBytes);
        var corrections = await ListCorrectionsForLanguagesAsync(
            profileId,
            isAuto ? allowedLanguages : [requestedLanguage],
            realtime,
            cancellationToken);
        var correctionHash = ComputeCorrectionHash(corrections);
        var engineModelVersion = ResolveEngineModelVersion(provider.Descriptor.Name, _options);
        var languageKeyComponent = isAuto
            ? $"auto|hint:{languageHint}|allowed:{string.Join(",", allowedLanguages)}"
            : requestedLanguage;
        var refinement = _options.Ocr.Refinement;
        var refineEligible = !realtime &&
            refinement.Enabled &&
            !string.Equals(preprocessingPreset, refinement.RerunPreset, StringComparison.OrdinalIgnoreCase) &&
            (request.Refine ?? (
                string.Equals(request.Preference?.Trim(), "high_accuracy", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(request.ContentType?.Trim(), "document", StringComparison.OrdinalIgnoreCase)));
        var refinementKey = refineEligible
            ? FormattableString.Invariant($"refine:v1:{refinement.TriggerConfidence}:{refinement.MinGain}:{refinement.MaxBlocks}:{refinement.PaddingRatio}:{refinement.RerunPreset}")
            : "refine:off";
        refinementKey += "|" + BuildShadowRepairCacheKey(_options.Ocr.ShadowRepair);
        var cacheKey = BuildOcrCacheKey(
            imageHash,
            decoded.ImageMimeType,
            provider.Descriptor.Name,
            languageKeyComponent,
            normalizeWhitespace,
            preprocessingPreset,
            correctionHash,
            engineModelVersion,
            refinementKey);

        var cached = realtime
            ? null
            : await _memoryStore.GetCachedResultAsync(cacheKey, cancellationToken);
        if (cached is not null)
        {
            if (cached.AppliedCorrections.Count > 0)
            {
                await _memoryStore.RecordCorrectionUseAsync(
                    cached.AppliedCorrections.Select(item => item.CorrectionId).ToArray(),
                    cancellationToken);
            }

            var cachedEventId = realtime ? string.Empty : Guid.NewGuid().ToString("N");
            var cachedDocument = EnsureDocumentPageSize(cached.Document, decoded.Width, decoded.Height);
            var cachedLanguage = Pick(cached.Language, seedLanguage);
            var cachedProvider = Pick(cached.Provider, provider.Descriptor.Name);
            if (!realtime)
            {
                await _memoryStore.AddEventAsync(
                    new OcrEvent(
                        cachedEventId,
                        profileId,
                        sessionId,
                        imageHash,
                        decoded.ImageMimeType,
                        cachedLanguage,
                        cachedProvider,
                        cached.Engine,
                        cached.RawText,
                        cached.CorrectedText,
                        cached.Blocks,
                        cached.AppliedCorrections,
                        0,
                        DateTimeOffset.UtcNow,
                        cachedDocument),
                    cancellationToken);
            }

            return ApplyRecurringTextSuppression(request, realtime, sessionId, imageHash, new OcrResponse(
                cachedEventId,
                cached.CorrectedText,
                cached.RawText,
                cached.Blocks,
                cached.AppliedCorrections,
                cachedProvider,
                cached.Engine,
                cachedLanguage,
                decoded.ImageMimeType,
                0,
                cachedDocument,
                CacheHit: true)
            {
                RequestedLanguage = requestedLanguage,
                ResolvedOcrLanguage = Pick(cached.Detection.ResolvedOcrLanguage, cachedLanguage),
                DetectedLanguage = cached.Detection.DetectedLanguage,
                LanguageConfidence = cached.Detection.LanguageConfidence,
                LanguageCandidates = cached.Detection.Candidates
            });
        }

        if (realtime && TryGetRealtimeExactFrameCache(cacheKey, out var realtimeCached))
        {
            return ApplyRecurringTextSuppression(
                request,
                realtime,
                sessionId,
                imageHash,
                realtimeCached with
                {
                    LatencyMs = 0,
                    CacheHit = true
                });
        }

        var stopwatch = Stopwatch.StartNew();
        var serviceWaitStopwatch = Stopwatch.StartNew();
        using var concurrencyLease = await _concurrencyLimiter.WaitAsync(provider.Descriptor.Name, cancellationToken);
        var serviceWaitMs = serviceWaitStopwatch.ElapsedMilliseconds;
        await ReportStageAsync(OcrJobStages.Recognizing);
        var shadowRepairFirstRun = await TryRealtimeShadowRepairFirstAsync(
            provider,
            decoded,
            seedLanguage,
            normalizeWhitespace,
            realtime,
            cancellationToken);
        OcrRunOutcome bestRun;
        if (shadowRepairFirstRun is not null)
        {
            provider = shadowRepairFirstRun.Value.Provider;
            bestRun = shadowRepairFirstRun.Value.Run;
        }
        else
        {
            bestRun = await RunRecognitionAsync(
                provider,
                decoded,
                seedLanguage,
                normalizeWhitespace,
                preprocessingPreset,
                cancellationToken,
                realtime,
                sessionId);

            // Empty-result fallback: when the primary engine reads NO text (rapidocr-net's detector
            // misses low-contrast / vertical text and returns zero blocks), retry once with OneOCR,
            // which handles those cases. Realtime frames are included only when
            // FallbackToOneOcrOnEmptyRealtime is set (region live OCR); the ~220 ms cost then only
            // hits the failing frames. OneOCR-as-primary and an unavailable OneOCR are no-ops.
            var suppressOneOcrEmptyFallback = ShouldSuppressOneOcrEmptyFallback(
                request.Provider,
                provider.Descriptor.Name,
                realtime);
            if (bestRun.RawBlocks.Length == 0 &&
                !suppressOneOcrEmptyFallback &&
                (!realtime || _options.Ocr.FallbackToOneOcrOnEmptyRealtime) &&
                _options.Ocr.FallbackToOneOcrOnEmpty &&
                !provider.Descriptor.Name.Equals("oneocr", StringComparison.OrdinalIgnoreCase) &&
                _providers.Contains("oneocr"))
            {
                var oneOcrProvider = _providers.GetRequired("oneocr");
                try
                {
                    var fallbackRun = await RunRecognitionAsync(
                        oneOcrProvider,
                        decoded,
                        seedLanguage,
                        normalizeWhitespace,
                        preprocessingPreset,
                        cancellationToken);
                    if (fallbackRun.RawBlocks.Length > 0)
                    {
                        bestRun = fallbackRun;
                        provider = oneOcrProvider; // record the engine that actually read the text
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
                {
                    // OneOCR not usable here (non-Windows, missing dll/model); keep the empty result.
                }
            }

            var shadowRepairRun = await TryShadowRepairFallbackAsync(
                provider,
                decoded,
                bestRun,
                seedLanguage,
                normalizeWhitespace,
                realtime,
                cancellationToken);
            if (shadowRepairRun is not null)
            {
                provider = shadowRepairRun.Value.Provider;
                bestRun = shadowRepairRun.Value.Run;
            }
        }

        var rapidV5VerticalFallbackRun = await TryRapidOcrV6JapaneseVerticalFallbackAsync(
            provider,
            decoded,
            bestRun,
            seedLanguage,
            normalizeWhitespace,
            preprocessingPreset,
            realtime,
            sessionId,
            cancellationToken);
        if (rapidV5VerticalFallbackRun is not null)
        {
            provider = rapidV5VerticalFallbackRun.Value.Provider;
            bestRun = rapidV5VerticalFallbackRun.Value.Run;
        }

        // Low-confidence fallback: re-run OCR with the top candidate languages and keep
        // the best-scoring result. Skipped for realtime frames (latency budget) and for
        // language-agnostic engines, where every pass would return the same output.
        if (isAuto &&
            !realtime &&
            !provider.Descriptor.IsLanguageAgnostic &&
            bestRun.Detection.Confidence < _options.Ocr.AutoDetection.RerunThreshold)
        {
            var attempted = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { bestRun.AttemptedLanguage };
            var rerunCandidates = bestRun.Detection.Candidates
                .Select(candidate => candidate.Language)
                .Where(candidate => allowedLanguages.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                .Concat(allowedLanguages)
                .Where(candidate => attempted.Add(candidate))
                .Take(Math.Max(0, _options.Ocr.AutoDetection.MaxRerunCandidates))
                .ToArray();

            foreach (var candidate in rerunCandidates)
            {
                OcrRunOutcome rerun;
                try
                {
                    rerun = await RunRecognitionAsync(
                        provider,
                        decoded,
                        candidate,
                        normalizeWhitespace,
                        preprocessingPreset,
                        cancellationToken);
                }
                catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
                {
                    // A candidate language the engine cannot serve (e.g. missing Windows
                    // language pack) must not fail the request; the first pass stands.
                    continue;
                }

                if (rerun.Score > bestRun.Score)
                {
                    bestRun = rerun;
                }
            }
        }

        // Multi-pass block refinement: old scans often drop or misread text in a
        // single full-image pass while a cropped text-line re-run reads the same
        // block correctly. Re-run low-confidence / anchor-without-CJK blocks on
        // their cropped region and keep the better reading.
        if (refineEligible)
        {
            bestRun = await RefineSuspectBlocksAsync(provider, decoded, bestRun, normalizeWhitespace, cancellationToken);
        }

        stopwatch.Stop();
        await ReportStageAsync(OcrJobStages.Assembling);

        var result = bestRun.Result;
        var rawText = bestRun.RawText;
        var correctionResult = ApplyCorrections(bestRun.RawText, bestRun.RawBlocks, bestRun.RawDocument, corrections);
        if (correctionResult.AppliedCorrections.Count > 0)
        {
            await _memoryStore.RecordCorrectionUseAsync(
                correctionResult.AppliedCorrections.Select(item => item.CorrectionId).ToArray(),
                cancellationToken);
        }

        var (annotatedBlocks, annotatedDocument, detection) = AnnotateLanguages(
            correctionResult.Blocks,
            correctionResult.Document);
        var resolvedLanguage = provider.Descriptor.IsLanguageAgnostic
            ? Pick(detection.DetectedLanguage, bestRun.AttemptedLanguage)
            : bestRun.AttemptedLanguage;
        var detectionRecord = new OcrLanguageDetection(
            requestedLanguage,
            resolvedLanguage,
            detection.DetectedLanguage,
            detection.Confidence,
            detection.Candidates);

        var eventId = realtime ? string.Empty : Guid.NewGuid().ToString("N");
        if (!realtime)
        {
            // Realtime subtitle frames have effectively unique hashes, so the event log and
            // result cache would only accumulate dead rows for them; skip both writes.
            await _memoryStore.AddEventAsync(
                new OcrEvent(
                    eventId,
                    profileId,
                    sessionId,
                    imageHash,
                    decoded.ImageMimeType,
                    resolvedLanguage,
                    provider.Descriptor.Name,
                    result.Engine,
                    rawText,
                    correctionResult.Text,
                    annotatedBlocks,
                    correctionResult.AppliedCorrections,
                    stopwatch.ElapsedMilliseconds,
                    DateTimeOffset.UtcNow,
                    annotatedDocument),
                cancellationToken);

            var now = DateTimeOffset.UtcNow;
            await _memoryStore.SetCachedResultAsync(
                new OcrCachedResult(
                    cacheKey,
                    imageHash,
                    decoded.ImageMimeType,
                    provider.Descriptor.Name,
                    result.Engine,
                    engineModelVersion,
                    resolvedLanguage,
                    normalizeWhitespace,
                    correctionHash,
                    rawText,
                    correctionResult.Text,
                    annotatedBlocks,
                    correctionResult.AppliedCorrections,
                    annotatedDocument,
                    stopwatch.ElapsedMilliseconds,
                    now,
                    now,
                    UseCount: 0)
                {
                    Detection = detectionRecord
                },
                cancellationToken);
        }

        var response = new OcrResponse(
            eventId,
            correctionResult.Text,
            rawText,
            annotatedBlocks,
            correctionResult.AppliedCorrections,
            provider.Descriptor.Name,
            result.Engine,
            resolvedLanguage,
            decoded.ImageMimeType,
            stopwatch.ElapsedMilliseconds,
            annotatedDocument,
            CacheHit: false)
        {
            RequestedLanguage = requestedLanguage,
            ResolvedOcrLanguage = resolvedLanguage,
            DetectedLanguage = detection.DetectedLanguage,
            LanguageConfidence = detection.Confidence,
            LanguageCandidates = detection.Candidates,
            Timing = (result.Timing ?? new OcrProviderTiming()) with
            {
                ServiceWaitMs = serviceWaitMs
            }
        };

        if (realtime)
        {
            StoreRealtimeExactFrameCache(cacheKey, response);
        }

        return ApplyRecurringTextSuppression(request, realtime, sessionId, imageHash, response);
    }

    private bool TryGetRealtimeExactFrameCache(string cacheKey, out OcrResponse response)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_realtimeExactFrameCacheLock)
        {
            if (_realtimeExactFrameCache.TryGetValue(cacheKey, out var entry))
            {
                if (now - entry.FetchedAt <= RealtimeExactFrameCacheTtl)
                {
                    response = entry.Response;
                    return true;
                }

                _realtimeExactFrameCache.Remove(cacheKey);
            }

            if (_realtimeExactFrameCache.Count > RealtimeExactFrameCacheMaxEntries)
            {
                TrimRealtimeExactFrameCache(now);
            }
        }

        response = default!;
        return false;
    }

    private void StoreRealtimeExactFrameCache(string cacheKey, OcrResponse response)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_realtimeExactFrameCacheLock)
        {
            _realtimeExactFrameCache[cacheKey] = (now, response);
            if (_realtimeExactFrameCache.Count > RealtimeExactFrameCacheMaxEntries)
            {
                TrimRealtimeExactFrameCache(now);
            }
        }
    }

    private void TrimRealtimeExactFrameCache(DateTimeOffset now)
    {
        foreach (var key in _realtimeExactFrameCache
            .Where(item => now - item.Value.FetchedAt > RealtimeExactFrameCacheTtl)
            .Select(item => item.Key)
            .ToArray())
        {
            _realtimeExactFrameCache.Remove(key);
        }

        if (_realtimeExactFrameCache.Count <= RealtimeExactFrameCacheMaxEntries)
        {
            return;
        }

        foreach (var key in _realtimeExactFrameCache
            .OrderBy(item => item.Value.FetchedAt)
            .Take(_realtimeExactFrameCache.Count - RealtimeExactFrameCacheMaxEntries)
            .Select(item => item.Key)
            .ToArray())
        {
            _realtimeExactFrameCache.Remove(key);
        }
    }

    /// <summary>
    /// Realtime sessions only: drops auto-detected recurring text (watermarks)
    /// from the response. Runs after cache reads/writes on purpose — the cache
    /// stays unfiltered because flagging is temporal, per-session state.
    /// </summary>
    private OcrResponse ApplyRecurringTextSuppression(
        OcrRequest request,
        bool realtime,
        string sessionId,
        string imageHash,
        OcrResponse response)
    {
        // Every response flows through here, so stamp the canonical image hash once.
        // The block workbench keys per-block annotations on it.
        response = response with { ImageHash = imageHash };

        var enabled = request.AutoSuppressRecurringText ?? _options.Ocr.RealtimeAutoSuppress.Enabled;
        if (!realtime || !enabled || sessionId.Length == 0)
        {
            return response;
        }

        var result = _recurringTextSuppressor.Process(
            sessionId,
            imageHash,
            response.Text,
            response.Blocks,
            response.Document);
        if (result.SuppressedText.Count == 0)
        {
            return response;
        }

        return response with
        {
            Text = result.Text,
            Blocks = result.Blocks,
            Document = result.Document,
            SuppressedText = result.SuppressedText
        };
    }

    private async Task<IReadOnlyList<OcrCorrection>> ListCorrectionsAsync(
        string profileId,
        string language,
        bool realtime,
        CancellationToken cancellationToken)
    {
        // Realtime subtitle loops hit this several times per second with identical
        // arguments; a short TTL cache spares one SQLite query per frame while staying
        // fresh enough for correction edits. Non-realtime calls always read the store.
        if (realtime)
        {
            lock (_correctionsCacheLock)
            {
                if (_correctionsCache.TryGetValue((profileId, language), out var entry) &&
                    DateTimeOffset.UtcNow - entry.FetchedAt < RealtimeCorrectionsTtl)
                {
                    return entry.Items;
                }
            }
        }

        var corrections = await _memoryStore.ListCorrectionsAsync(
            profileId,
            language,
            limit: 200,
            activeOnly: true,
            cancellationToken);

        lock (_correctionsCacheLock)
        {
            _correctionsCache[(profileId, language)] = (DateTimeOffset.UtcNow, corrections);
        }

        return corrections;
    }

    /// <summary>
    /// Unions the correction lists of every language the request may resolve to.
    /// Each language is queried under both its canonical tag and its translation-side
    /// code so corrections stored before canonicalization (e.g. language "ja") keep working.
    /// </summary>
    private async Task<IReadOnlyList<OcrCorrection>> ListCorrectionsForLanguagesAsync(
        string profileId,
        IReadOnlyList<string> languages,
        bool realtime,
        CancellationToken cancellationToken)
    {
        var keys = languages
            .SelectMany(language => new[]
            {
                LanguageRegistry.Normalize(language),
                LanguageRegistry.ToTranslationCode(language)
            })
            .Where(value => !string.IsNullOrWhiteSpace(value) && value != LanguageRegistry.Auto)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (keys.Length == 0)
        {
            return [];
        }

        var merged = new List<OcrCorrection>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys)
        {
            foreach (var correction in await ListCorrectionsAsync(profileId, key, realtime, cancellationToken))
            {
                if (seen.Add(correction.Id))
                {
                    merged.Add(correction);
                }
            }
        }

        return merged
            .OrderByDescending(correction => correction.Priority)
            .ThenByDescending(correction => correction.Confidence)
            .ThenByDescending(correction => correction.WrongText.Length)
            .ToArray();
    }

    private sealed record OcrRunOutcome(
        string AttemptedLanguage,
        OcrProviderResult Result,
        OcrTextBlock[] RawBlocks,
        string RawText,
        OcrDocumentResult RawDocument,
        ScriptDetectionResult Detection,
        double Score);

    private static bool ShouldSuppressOneOcrEmptyFallback(
        string? requestedProvider,
        string resolvedProvider,
        bool realtime)
    {
        if (!realtime ||
            string.IsNullOrWhiteSpace(requestedProvider) ||
            string.IsNullOrWhiteSpace(resolvedProvider))
        {
            return false;
        }

        return requestedProvider.Trim().Equals(resolvedProvider, StringComparison.OrdinalIgnoreCase) &&
            resolvedProvider.StartsWith("rapidocr-net", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<(IOcrProvider Provider, OcrRunOutcome Run)?> TryRapidOcrV6JapaneseVerticalFallbackAsync(
        IOcrProvider provider,
        DecodedOcrImage decoded,
        OcrRunOutcome primaryRun,
        string language,
        bool normalizeWhitespace,
        string preprocessingPreset,
        bool realtime,
        string sessionId,
        CancellationToken cancellationToken)
    {
        if (realtime ||
            !provider.Descriptor.Name.Equals("rapidocr-net-v6", StringComparison.OrdinalIgnoreCase) ||
            !_providers.Contains("rapidocr-net") ||
            !LooksLikeJapaneseVerticalMixedScriptConfusion(primaryRun, language))
        {
            return null;
        }

        var fallbackProvider = _providers.GetRequired("rapidocr-net");
        OcrRunOutcome fallbackRun;
        try
        {
            fallbackRun = await RunRecognitionAsync(
                fallbackProvider,
                decoded,
                language,
                normalizeWhitespace,
                preprocessingPreset,
                cancellationToken,
                realtime: false,
                sessionKey: string.IsNullOrWhiteSpace(sessionId)
                    ? "rapidocr-v5-vertical-fallback"
                    : sessionId + ":rapidocr-v5-vertical-fallback");
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            return null;
        }

        var primaryQuality = JapaneseVerticalFallbackQuality(primaryRun);
        var fallbackQuality = JapaneseVerticalFallbackQuality(fallbackRun);
        return fallbackQuality >= primaryQuality + 1.5
            ? (fallbackProvider, fallbackRun)
            : null;
    }

    private static bool LooksLikeJapaneseVerticalMixedScriptConfusion(OcrRunOutcome run, string language)
    {
        if (run.RawBlocks.Length < 2 ||
            (!IsJapaneseOcrLanguage(language) && CountJapaneseKana(run.RawText) == 0))
        {
            return false;
        }

        var tallBlocks = run.RawBlocks
            .Where(block => block.BoundingBox is { } box && box.Height >= Math.Max(24, box.Width * 1.35))
            .ToArray();
        if (tallBlocks.Length < 2 || CountJapaneseKana(run.RawText) == 0)
        {
            return false;
        }

        var hasKanaBlock = tallBlocks.Any(block => CountJapaneseKana(block.Text) > 0);
        var hasShortIdeographOnlyBlock = tallBlocks.Any(block =>
            CountJapaneseKana(block.Text) == 0 &&
            CountCjkIdeographCharacters(block.Text) > 0 &&
            CountMeaningfulCharacters(block.Text) <= 3);
        return hasKanaBlock && hasShortIdeographOnlyBlock;
    }

    private static double JapaneseVerticalFallbackQuality(OcrRunOutcome run)
    {
        if (!HasOcrText(run))
        {
            return 0;
        }

        var text = run.RawText;
        var japanese = CountJapaneseCharacters(text);
        var kana = CountJapaneseKana(text);
        var latin = CountLatinLetters(text);
        var replacements = text.Count(character => character == '\uFFFD');
        var averageConfidence = AverageBlockConfidence(run.RawBlocks);
        var tallBlockBonus = run.RawBlocks.Count(block =>
            block.BoundingBox is { } box && box.Height >= Math.Max(24, box.Width * 1.35));
        return japanese +
            (kana * 3.0) +
            (averageConfidence * 2.0) +
            Math.Min(tallBlockBonus, 4) -
            (latin * 0.5) -
            (replacements * 4.0);
    }

    private async Task<OcrRunOutcome> RunRecognitionAsync(
        IOcrProvider provider,
        DecodedOcrImage decoded,
        string language,
        bool normalizeWhitespace,
        string preprocessingPreset,
        CancellationToken cancellationToken,
        bool realtime = false,
        string sessionKey = "")
    {
        var providerRequest = new OcrProviderRequest(
            decoded.ImageBytes,
            decoded.ImageMimeType,
            language,
            normalizeWhitespace,
            preprocessingPreset,
            realtime,
            sessionKey);
        var result = await provider.RecognizeAsync(providerRequest, cancellationToken);
        var buildStopwatch = Stopwatch.StartNew();
        var outcome = BuildRunOutcome(language, result, normalizeWhitespace, decoded.Width, decoded.Height);
        var buildMs = buildStopwatch.ElapsedMilliseconds;
        return outcome with
        {
            Result = outcome.Result with
            {
                Timing = (outcome.Result.Timing ?? new OcrProviderTiming()) with
                {
                    ServiceBuildMs = buildMs
                }
            }
        };
    }

    private static OcrRunOutcome BuildRunOutcome(
        string language,
        OcrProviderResult result,
        bool normalizeWhitespace,
        int? imageWidth,
        int? imageHeight)
    {
        var normalizedBlocks = result.Blocks
            .Select(block => block with
            {
                Text = normalizeWhitespace ? NormalizeWhitespace(block.Text) : block.Text
            })
            .ToArray();
        var rawBlocks = normalizedBlocks
            .Where(block => HasMeaningfulOcrContent(block.Text))
            .ToArray();

        var providerText = result.Text;
        if (normalizeWhitespace && !string.IsNullOrWhiteSpace(providerText))
        {
            providerText = NormalizeWhitespace(providerText);
        }

        var rawText = rawBlocks.Length > 0
            ? string.Join(Environment.NewLine, rawBlocks.Select(block => block.Text).Where(value => !string.IsNullOrWhiteSpace(value)))
            : result.Blocks.Count == 0 && HasMeaningfulOcrContent(providerText)
                ? providerText
                : string.Empty;

        if (normalizeWhitespace && !string.IsNullOrWhiteSpace(rawText))
        {
            rawText = NormalizeWhitespace(rawText);
        }

        if (rawBlocks.Length == 0 && !string.IsNullOrWhiteSpace(rawText))
        {
            rawBlocks = [new OcrTextBlock(rawText, 1.0, null)];
        }

        var rawDocument = result.Document is null
            ? BuildTextDocument(rawBlocks, result.Engine, imageWidth, imageHeight)
            : PrepareProviderDocument(result.Document, rawBlocks, result.Engine, normalizeWhitespace, imageWidth, imageHeight);
        var sanitizedResult = result with
        {
            Text = rawText,
            Blocks = rawBlocks,
            Document = rawDocument
        };

        var detection = UnicodeScriptDetector.Aggregate(
            rawBlocks
                .Select(block =>
                {
                    var blockDetection = UnicodeScriptDetector.Detect(block.Text);
                    return (blockDetection, blockDetection.EffectiveCharCount * Math.Clamp(block.Confidence, 0.05, 1.0));
                })
                .ToArray());
        var score = OcrLanguageScorer.Score(language, rawBlocks, detection);

        return new OcrRunOutcome(language, sanitizedResult, rawBlocks, rawText, rawDocument, detection, score);
    }

    private async Task<(IOcrProvider Provider, OcrRunOutcome Run)?> TryRealtimeShadowRepairFirstAsync(
        IOcrProvider provider,
        DecodedOcrImage decoded,
        string language,
        bool normalizeWhitespace,
        bool realtime,
        CancellationToken cancellationToken)
    {
        var options = _options.Ocr.ShadowRepair;
        if (!ShouldAttemptRealtimeShadowRepairBeforePrimary(decoded, options, realtime, language))
        {
            return null;
        }

        var repairProviders = BuildShadowRepairProviders(provider, options, realtime: true).ToArray();
        var directBest = await TryProviderNativeShadowRepairAsync(
            repairProviders,
            decoded,
            options,
            language,
            normalizeWhitespace,
            realtime,
            requireBrightRealtimeCandidate: true,
            cancellationToken);
        if (directBest is not null)
        {
            return directBest;
        }

        if (!LooksLikeBrightRealtimeRepairCandidate(decoded, options))
        {
            return null;
        }

        var candidates = BuildShadowRepairCandidates(
            decoded,
            options,
            includeFullCandidate: false,
            scaleOverride: EffectiveShadowRepairScale(options, realtime: true),
            realtime: true);
        if (candidates.Count == 0)
        {
            return null;
        }

        var minQuality = Math.Max(8.0, options.MinQualityGain);
        var bestQuality = 0.0;
        (IOcrProvider Provider, OcrRunOutcome Run)? best = null;

        foreach (var repairProvider in repairProviders)
        {
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                OcrRunOutcome repairedRun;
                try
                {
                    repairedRun = await RunRecognitionAsync(
                        repairProvider,
                        candidate.Decoded,
                        language,
                        normalizeWhitespace,
                        preprocessingPreset: "none",
                        cancellationToken,
                        realtime: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or ArgumentException)
                {
                    continue;
                }

                if (!HasOcrText(repairedRun))
                {
                    continue;
                }

                var mappedRun = ApplyLatinShadowTextCleanup(MapShadowRepairRun(repairedRun, candidate), language);
                var repairedQuality = ShadowRepairQuality(mappedRun, language);
                if (repairedQuality > bestQuality)
                {
                    bestQuality = repairedQuality;
                    best = (repairProvider, mappedRun);
                }
            }
        }

        return best is not null && bestQuality >= minQuality
            ? best
            : null;
    }

    private async Task<(IOcrProvider Provider, OcrRunOutcome Run)?> TryProviderNativeShadowRepairAsync(
        IReadOnlyList<IOcrProvider> repairProviders,
        DecodedOcrImage decoded,
        OcrShadowRepairOptions options,
        string language,
        bool normalizeWhitespace,
        bool realtime,
        bool requireBrightRealtimeCandidate,
        CancellationToken cancellationToken)
    {
        var width = decoded.Width.GetValueOrDefault();
        var height = decoded.Height.GetValueOrDefault();
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        var crop = GetShadowRepairMainCrop(width, height, options, realtime);
        var requestedScale = Math.Max(1.0, EffectiveShadowRepairScale(options, realtime));
        var originalPixels = crop.Width * (double)crop.Height;
        var maxPixels = Math.Max(originalPixels, options.MaxRepairedPixels);
        var scale = Math.Min(requestedScale, Math.Sqrt(maxPixels / originalPixels));
        var minQuality = realtime ? Math.Max(8.0, options.MinQualityGain) : 0.0;
        var bestQuality = minQuality;
        (IOcrProvider Provider, OcrRunOutcome Run)? best = null;

        foreach (var repairProvider in repairProviders)
        {
            if (repairProvider is not IShadowRepairOcrProvider nativeRepairProvider)
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();

            OcrShadowRepairProviderResult? providerResult;
            try
            {
                providerResult = await nativeRepairProvider.RecognizeShadowRepairAsync(
                    new OcrShadowRepairProviderRequest(
                        decoded.ImageBytes,
                        decoded.ImageMimeType,
                        language,
                        normalizeWhitespace,
                        realtime,
                        string.Empty,
                        crop.X,
                        crop.Y,
                        crop.Width,
                        crop.Height,
                        scale,
                        "main-clahe",
                        requireBrightRealtimeCandidate),
                    cancellationToken);
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or ArgumentException)
            {
                continue;
            }

            if (providerResult is null)
            {
                continue;
            }

            var run = BuildRunOutcome(
                language,
                providerResult.Result,
                normalizeWhitespace,
                providerResult.Width,
                providerResult.Height);
            if (!HasOcrText(run))
            {
                continue;
            }

            var candidate = new ShadowRepairCandidate(
                providerResult.CandidateName,
                new DecodedOcrImage([], "image/png", providerResult.Width, providerResult.Height),
                providerResult.Scale,
                providerResult.OffsetX,
                providerResult.OffsetY,
                providerResult.OriginalWidth,
                providerResult.OriginalHeight);
            var mappedRun = ApplyLatinShadowTextCleanup(MapShadowRepairRun(run, candidate), language);
            var quality = ShadowRepairQuality(mappedRun, language);
            if (quality > bestQuality)
            {
                bestQuality = quality;
                best = (repairProvider, mappedRun);
            }
        }

        return best;
    }

    private async Task<(IOcrProvider Provider, OcrRunOutcome Run)?> TryShadowRepairFallbackAsync(
        IOcrProvider provider,
        DecodedOcrImage decoded,
        OcrRunOutcome currentRun,
        string language,
        bool normalizeWhitespace,
        bool realtime,
        CancellationToken cancellationToken)
    {
        var options = _options.Ocr.ShadowRepair;
        if (!ShouldAttemptShadowRepairFallback(decoded, currentRun, options, realtime, language))
        {
            return null;
        }

        var candidates = BuildShadowRepairCandidates(
            decoded,
            options,
            includeFullCandidate: !realtime,
            scaleOverride: EffectiveShadowRepairScale(options, realtime),
            realtime: realtime);
        if (candidates.Count == 0)
        {
            return null;
        }

        var currentQuality = ShadowRepairQuality(currentRun, language);
        var bestQuality = currentQuality;
        (IOcrProvider Provider, OcrRunOutcome Run)? best = null;

        foreach (var repairProvider in BuildShadowRepairProviders(provider, options, realtime))
        {
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                OcrRunOutcome repairedRun;
                try
                {
                    repairedRun = await RunRecognitionAsync(
                        repairProvider,
                        candidate.Decoded,
                        language,
                        normalizeWhitespace,
                        preprocessingPreset: "none",
                        cancellationToken,
                        realtime);
                }
                catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or ArgumentException)
                {
                    // A repair pass is opportunistic. If a provider cannot serve the
                    // synthetic PNG, keep the original OCR result.
                    continue;
                }

                if (!HasOcrText(repairedRun))
                {
                    continue;
                }

                var mappedRun = ApplyLatinShadowTextCleanup(MapShadowRepairRun(repairedRun, candidate), language);
                if (WouldDowngradeJapaneseShadowRepair(currentRun, mappedRun, language))
                {
                    continue;
                }

                var repairedQuality = ShadowRepairQuality(mappedRun, language);
                if (repairedQuality > bestQuality)
                {
                    bestQuality = repairedQuality;
                    best = (repairProvider, mappedRun);
                }
            }
        }

        if (best is null)
        {
            return null;
        }

        if (!HasOcrText(currentRun) ||
            bestQuality >= currentQuality + Math.Max(0, options.MinQualityGain))
        {
            return best;
        }

        return null;
    }

    private IEnumerable<IOcrProvider> BuildShadowRepairProviders(
        IOcrProvider primaryProvider,
        OcrShadowRepairOptions options,
        bool realtime)
    {
        var names = realtime
            ? new[]
            {
                options.RealtimePreferredProvider,
                primaryProvider.Descriptor.Name
            }
            : new[]
            {
                options.PreferredProvider,
                primaryProvider.Descriptor.Name,
                "oneocr"
            };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawName in names)
        {
            var name = rawName?.Trim();
            if (string.IsNullOrWhiteSpace(name) || !seen.Add(name) || !_providers.Contains(name))
            {
                continue;
            }

            yield return _providers.GetRequired(name);
        }
    }

    private static bool ShouldAttemptShadowRepairFallback(
        DecodedOcrImage decoded,
        OcrRunOutcome currentRun,
        OcrShadowRepairOptions options,
        bool realtime,
        string language)
    {
        if (!options.Enabled || options.Scale < 2 || (realtime && !options.RealtimeEnabled))
        {
            return false;
        }

        if (currentRun.Result.Engine.Contains("sparse-glyph-rescue", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!HasOcrText(currentRun))
        {
            return true;
        }

        var width = decoded.Width.GetValueOrDefault();
        var height = decoded.Height.GetValueOrDefault();
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        if (realtime)
        {
            return IsShadowRepairSubtitleShape(width, height, options) &&
                CountCjkCharacters(currentRun.RawText) == 0 &&
                IsLatinRepairLanguage(language, currentRun) &&
                LooksLikeFragmentedLatinRealtimeText(currentRun);
        }

        if (IsShadowRepairSubtitleShape(width, height, options))
        {
            return true;
        }

        return height <= options.MaxOriginalHeight &&
            AverageBlockConfidence(currentRun.RawBlocks) < options.TriggerAverageConfidence;
    }

    private static bool ShouldAttemptRealtimeShadowRepairBeforePrimary(
        DecodedOcrImage decoded,
        OcrShadowRepairOptions options,
        bool realtime,
        string language)
    {
        if (!realtime ||
            !options.Enabled ||
            !options.RealtimeEnabled ||
            EffectiveShadowRepairScale(options, realtime: true) < 2 ||
            !IsLatinOcrLanguage(language))
        {
            return false;
        }

        var width = decoded.Width.GetValueOrDefault();
        var height = decoded.Height.GetValueOrDefault();
        return IsShadowRepairSubtitleShape(width, height, options);
    }

    private static bool IsShadowRepairSubtitleShape(int width, int height, OcrShadowRepairOptions options)
        => width > 0 &&
           height > 0 &&
           height <= options.MaxOriginalHeight &&
           width / (double)height >= options.MinAspectRatio;

    private static double EffectiveShadowRepairScale(OcrShadowRepairOptions options, bool realtime)
        => realtime && options.RealtimeScale > 0
            ? options.RealtimeScale
            : options.Scale;

    private static IReadOnlyList<ShadowRepairCandidate> BuildShadowRepairCandidates(
        DecodedOcrImage decoded,
        OcrShadowRepairOptions options,
        bool includeFullCandidate = true,
        double? scaleOverride = null,
        bool realtime = false)
    {
        using var sourceStream = new MemoryStream(decoded.ImageBytes, writable: false);
        Bitmap source;
        try
        {
            source = new Bitmap(sourceStream);
        }
        catch (Exception)
        {
            // Formats GDI+ cannot decode simply skip this optional fallback.
            return [];
        }

        using (source)
        {
            var candidates = new List<ShadowRepairCandidate>();
            if (IsShadowRepairSubtitleShape(source.Width, source.Height, options))
            {
                TryAddShadowRepairCandidate(
                    candidates,
                    "main-clahe",
                    source,
                    GetShadowRepairMainCrop(source.Width, source.Height, options, realtime),
                    options,
                    scaleOverride);
            }

            if (includeFullCandidate)
            {
                TryAddShadowRepairCandidate(
                    candidates,
                    "full-clahe",
                    source,
                    new Rectangle(0, 0, source.Width, source.Height),
                    options,
                    scaleOverride);
            }

            return candidates;
        }
    }

    private static Rectangle GetShadowRepairMainCrop(
        int width,
        int height,
        OcrShadowRepairOptions options,
        bool realtime = false)
    {
        var topRatio = Math.Clamp(
            realtime ? options.RealtimeCropTopRatio : options.CropTopRatio,
            0,
            0.95);
        var bottomRatio = Math.Clamp(
            realtime ? options.RealtimeCropBottomRatio : options.CropBottomRatio,
            topRatio + 0.01,
            1.0);
        var top = Math.Clamp((int)Math.Round(height * topRatio), 0, Math.Max(0, height - 8));
        var bottom = Math.Clamp((int)Math.Round(height * bottomRatio), top + 8, height);
        return new Rectangle(0, top, width, bottom - top);
    }

    private static bool LooksLikeBrightRealtimeRepairCandidate(
        DecodedOcrImage decoded,
        OcrShadowRepairOptions options)
    {
        using var sourceStream = new MemoryStream(decoded.ImageBytes, writable: false);
        Bitmap source;
        try
        {
            source = new Bitmap(sourceStream);
        }
        catch (Exception)
        {
            return false;
        }

        using (source)
        {
            if (!IsShadowRepairSubtitleShape(source.Width, source.Height, options))
            {
                return false;
            }

            var crop = GetShadowRepairMainCrop(source.Width, source.Height, options, realtime: true);
            using var normalized = new Bitmap(crop.Width, crop.Height, PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(normalized))
            {
                graphics.Clear(Color.White);
                graphics.CompositingQuality = CompositingQuality.HighSpeed;
                graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                graphics.PixelOffsetMode = PixelOffsetMode.HighSpeed;
                graphics.SmoothingMode = SmoothingMode.HighSpeed;
                graphics.DrawImage(
                    source,
                    new Rectangle(0, 0, crop.Width, crop.Height),
                    crop,
                    GraphicsUnit.Pixel);
            }

            var rect = new Rectangle(0, 0, normalized.Width, normalized.Height);
            var bitmapData = normalized.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                var stride = bitmapData.Stride;
                var rowBytes = Math.Abs(stride);
                var buffer = new byte[rowBytes * normalized.Height];
                Marshal.Copy(bitmapData.Scan0, buffer, 0, buffer.Length);

                var stepX = Math.Max(1, normalized.Width / 550);
                var stepY = Math.Max(1, normalized.Height / 90);
                var samples = 0;
                var bright235 = 0;
                var bright245 = 0;
                var dark90 = 0;
                var sum = 0L;

                for (var y = 0; y < normalized.Height; y += stepY)
                {
                    var sourceRow = stride >= 0
                        ? y * rowBytes
                        : (normalized.Height - 1 - y) * rowBytes;
                    for (var x = 0; x < normalized.Width; x += stepX)
                    {
                        var index = sourceRow + (x * 3);
                        var blue = buffer[index];
                        var green = buffer[index + 1];
                        var red = buffer[index + 2];
                        var gray = (int)Math.Round((0.299 * red) + (0.587 * green) + (0.114 * blue));

                        samples++;
                        sum += gray;
                        if (gray >= 235)
                        {
                            bright235++;
                        }

                        if (gray >= 245)
                        {
                            bright245++;
                        }

                        if (gray <= 90)
                        {
                            dark90++;
                        }
                    }
                }

                if (samples == 0)
                {
                    return false;
                }

                var mean = sum / (double)samples;
                var bright235Ratio = bright235 / (double)samples;
                var bright245Ratio = bright245 / (double)samples;
                var dark90Ratio = dark90 / (double)samples;
                return mean >= 145 &&
                    bright235Ratio >= 0.45 &&
                    bright245Ratio >= 0.18 &&
                    dark90Ratio >= 0.02;
            }
            finally
            {
                normalized.UnlockBits(bitmapData);
            }
        }
    }

    private static void TryAddShadowRepairCandidate(
        List<ShadowRepairCandidate> candidates,
        string name,
        Bitmap source,
        Rectangle crop,
        OcrShadowRepairOptions options,
        double? scaleOverride = null)
    {
        if (crop.Width < 8 || crop.Height < 8)
        {
            return;
        }

        var requestedScale = Math.Max(1.0, scaleOverride ?? options.Scale);
        var originalPixels = crop.Width * (double)crop.Height;
        var maxPixels = Math.Max(originalPixels, options.MaxRepairedPixels);
        var scale = Math.Min(requestedScale, Math.Sqrt(maxPixels / originalPixels));
        scale = Math.Max(1.0, scale);

        byte[] bytes;
        int width;
        int height;
        try
        {
            (bytes, width, height) = RenderClaheCandidateToPng(source, crop, scale);
        }
        catch (Exception)
        {
            return;
        }

        candidates.Add(new ShadowRepairCandidate(
            name,
            new DecodedOcrImage(bytes, "image/png", width, height),
            scale,
            crop.X,
            crop.Y,
            source.Width,
            source.Height));
    }

    private static (byte[] Bytes, int Width, int Height) RenderClaheCandidateToPng(
        Bitmap source,
        Rectangle crop,
        double scale)
    {
        var width = Math.Max(1, (int)Math.Round(crop.Width * scale));
        var height = Math.Max(1, (int)Math.Round(crop.Height * scale));
        using var scaled = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(scaled))
        {
            graphics.Clear(Color.White);
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.SmoothingMode = SmoothingMode.HighQuality;
            graphics.DrawImage(
                source,
                new Rectangle(0, 0, width, height),
                crop,
                GraphicsUnit.Pixel);
        }

        var gray = ExtractGrayscale(scaled);
        var enhanced = ApplyClahe(gray, width, height);
        return (EncodeGrayscalePng(enhanced, width, height), width, height);
    }

    private static byte[] ExtractGrayscale(Bitmap bitmap)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var bitmapData = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var stride = bitmapData.Stride;
            var rowBytes = Math.Abs(stride);
            var buffer = new byte[rowBytes * bitmap.Height];
            Marshal.Copy(bitmapData.Scan0, buffer, 0, buffer.Length);

            var gray = new byte[bitmap.Width * bitmap.Height];
            for (var y = 0; y < bitmap.Height; y++)
            {
                var sourceRow = stride >= 0
                    ? y * rowBytes
                    : (bitmap.Height - 1 - y) * rowBytes;
                var outputRow = y * bitmap.Width;
                for (var x = 0; x < bitmap.Width; x++)
                {
                    var index = sourceRow + (x * 3);
                    var blue = buffer[index];
                    var green = buffer[index + 1];
                    var red = buffer[index + 2];
                    gray[outputRow + x] = (byte)Math.Clamp(
                        (int)Math.Round((0.299 * red) + (0.587 * green) + (0.114 * blue)),
                        0,
                        255);
                }
            }

            return gray;
        }
        finally
        {
            bitmap.UnlockBits(bitmapData);
        }
    }

    private static byte[] ApplyClahe(byte[] gray, int width, int height)
    {
        const int bins = 256;
        const double clipLimit = 2.4;

        var tileColumns = Math.Clamp(width / 64, 1, 8);
        var tileRows = Math.Clamp(height / 32, 1, 8);
        var luts = new byte[tileColumns * tileRows][];

        for (var tileY = 0; tileY < tileRows; tileY++)
        {
            var y0 = tileY * height / tileRows;
            var y1 = (tileY + 1) * height / tileRows;
            for (var tileX = 0; tileX < tileColumns; tileX++)
            {
                var x0 = tileX * width / tileColumns;
                var x1 = (tileX + 1) * width / tileColumns;
                var histogram = new int[bins];
                for (var y = y0; y < y1; y++)
                {
                    var row = y * width;
                    for (var x = x0; x < x1; x++)
                    {
                        histogram[gray[row + x]]++;
                    }
                }

                var tileArea = Math.Max(1, (x1 - x0) * (y1 - y0));
                var clipThreshold = Math.Max(1, (int)Math.Round(clipLimit * tileArea / bins));
                var clipped = 0;
                for (var index = 0; index < bins; index++)
                {
                    if (histogram[index] <= clipThreshold)
                    {
                        continue;
                    }

                    clipped += histogram[index] - clipThreshold;
                    histogram[index] = clipThreshold;
                }

                var redistribute = clipped / bins;
                var remainder = clipped % bins;
                for (var index = 0; index < bins; index++)
                {
                    histogram[index] += redistribute;
                    if (index < remainder)
                    {
                        histogram[index]++;
                    }
                }

                var lut = new byte[bins];
                var cumulative = 0;
                for (var index = 0; index < bins; index++)
                {
                    cumulative += histogram[index];
                    lut[index] = (byte)Math.Clamp((int)Math.Round(cumulative * 255.0 / tileArea), 0, 255);
                }

                luts[(tileY * tileColumns) + tileX] = lut;
            }
        }

        var output = new byte[gray.Length];
        for (var y = 0; y < height; y++)
        {
            var tileYFloat = ((y + 0.5) * tileRows / height) - 0.5;
            var y0 = Math.Clamp((int)Math.Floor(tileYFloat), 0, tileRows - 1);
            var y1 = Math.Min(y0 + 1, tileRows - 1);
            var yWeight = Math.Clamp(tileYFloat - y0, 0, 1);

            for (var x = 0; x < width; x++)
            {
                var tileXFloat = ((x + 0.5) * tileColumns / width) - 0.5;
                var x0 = Math.Clamp((int)Math.Floor(tileXFloat), 0, tileColumns - 1);
                var x1 = Math.Min(x0 + 1, tileColumns - 1);
                var xWeight = Math.Clamp(tileXFloat - x0, 0, 1);
                var value = gray[(y * width) + x];

                var topLeft = luts[(y0 * tileColumns) + x0][value];
                var topRight = luts[(y0 * tileColumns) + x1][value];
                var bottomLeft = luts[(y1 * tileColumns) + x0][value];
                var bottomRight = luts[(y1 * tileColumns) + x1][value];
                var top = topLeft + ((topRight - topLeft) * xWeight);
                var bottom = bottomLeft + ((bottomRight - bottomLeft) * xWeight);
                output[(y * width) + x] = (byte)Math.Clamp(
                    (int)Math.Round(top + ((bottom - top) * yWeight)),
                    0,
                    255);
            }
        }

        return output;
    }

    private static byte[] EncodeGrayscalePng(byte[] gray, int width, int height)
    {
        using var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        var rect = new Rectangle(0, 0, width, height);
        var bitmapData = bitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            var stride = bitmapData.Stride;
            var rowBytes = Math.Abs(stride);
            var buffer = new byte[rowBytes * height];
            for (var y = 0; y < height; y++)
            {
                var targetRow = stride >= 0
                    ? y * rowBytes
                    : (height - 1 - y) * rowBytes;
                var sourceRow = y * width;
                for (var x = 0; x < width; x++)
                {
                    var value = gray[sourceRow + x];
                    var index = targetRow + (x * 3);
                    buffer[index] = value;
                    buffer[index + 1] = value;
                    buffer[index + 2] = value;
                }
            }

            Marshal.Copy(buffer, 0, bitmapData.Scan0, buffer.Length);
        }
        finally
        {
            bitmap.UnlockBits(bitmapData);
        }

        using var output = new MemoryStream();
        bitmap.Save(output, ImageFormat.Png);
        return output.ToArray();
    }

    private static OcrRunOutcome MapShadowRepairRun(
        OcrRunOutcome run,
        ShadowRepairCandidate candidate)
    {
        var suffix = "shadow-repair:" + candidate.Name;
        var engine = AppendEngineSuffix(run.Result.Engine, suffix);
        var mappedBlocks = run.RawBlocks
            .Select(block => MapShadowRepairTextBlock(block, candidate))
            .ToArray();
        var mappedDocument = MapShadowRepairDocument(run.RawDocument, candidate, suffix);
        var result = run.Result with
        {
            Text = run.RawText,
            Blocks = run.Result.Blocks
                .Select(block => MapShadowRepairTextBlock(block, candidate))
                .ToArray(),
            Engine = engine,
            Document = mappedDocument
        };

        return run with
        {
            Result = result,
            RawBlocks = mappedBlocks,
            RawDocument = mappedDocument
        };
    }

    private static OcrRunOutcome ApplyLatinShadowTextCleanup(OcrRunOutcome run, string language)
    {
        if (!IsLatinOcrLanguage(language) || !HasOcrText(run) || CountCjkCharacters(run.RawText) > 0)
        {
            return run;
        }

        var orderedSingleLineBlocks = TryOrderLatinSingleLineBlocks(run.RawBlocks);
        var textForCleanup = orderedSingleLineBlocks is null
            ? run.RawText
            : NormalizeWhitespace(string.Join(' ', orderedSingleLineBlocks.Select(block => block.Text)));
        var cleanedText = CleanupCompactLatinSubtitle(textForCleanup);
        if (orderedSingleLineBlocks is null &&
            string.Equals(cleanedText, run.RawText, StringComparison.Ordinal))
        {
            return run;
        }

        var blocks = run.RawBlocks;
        if (orderedSingleLineBlocks is not null)
        {
            blocks = [MergeTextBlocks(orderedSingleLineBlocks, cleanedText)];
        }
        else if (blocks.Length == 1)
        {
            blocks = [blocks[0] with { Text = cleanedText }];
        }

        var resultBlocks = run.Result.Blocks;
        if (orderedSingleLineBlocks is not null)
        {
            var orderedResultBlocks = TryOrderLatinSingleLineBlocks(resultBlocks);
            resultBlocks = [MergeTextBlocks(orderedResultBlocks ?? orderedSingleLineBlocks, cleanedText)];
        }
        else if (resultBlocks.Count == 1)
        {
            resultBlocks = [resultBlocks[0] with { Text = cleanedText }];
        }

        var document = blocks.Length == 1 && blocks[0].BoundingBox is not null
            ? ReplaceSingleDocumentTextBlock(run.RawDocument, blocks[0], cleanedText)
            : blocks.Length == 1
                ? ReplaceSingleDocumentText(run.RawDocument, cleanedText)
            : run.RawDocument;
        var result = run.Result with
        {
            Text = cleanedText,
            Blocks = resultBlocks,
            Document = document
        };
        var detection = UnicodeScriptDetector.Aggregate(
            blocks
                .Select(block =>
                {
                    var blockDetection = UnicodeScriptDetector.Detect(block.Text);
                    return (blockDetection, blockDetection.EffectiveCharCount * Math.Clamp(block.Confidence, 0.05, 1.0));
                })
                .ToArray());

        return run with
        {
            Result = result,
            RawBlocks = blocks,
            RawText = cleanedText,
            RawDocument = document,
            Detection = detection,
            Score = OcrLanguageScorer.Score(language, blocks, detection)
        };
    }

    private static string CleanupCompactLatinSubtitle(string text)
    {
        var normalized = NormalizeWhitespace(text.Replace('|', ' '));
        var forceUppercase = ShouldUppercaseLatinSubtitle(normalized);
        var builder = new StringBuilder(normalized.Length + 12);
        var tokenStart = -1;

        void FlushToken(int end)
        {
            if (tokenStart < 0)
            {
                return;
            }

            var token = normalized[tokenStart..end];
            builder.Append(SegmentCompactLatinToken(token));
            tokenStart = -1;
        }

        for (var index = 0; index < normalized.Length; index++)
        {
            var character = normalized[index];
            if (IsAsciiLetterOrDigit(character))
            {
                if (tokenStart < 0)
                {
                    tokenStart = index;
                }
            }
            else
            {
                FlushToken(index);
                builder.Append(character);
            }
        }

        FlushToken(normalized.Length);
        var cleaned = NormalizeWhitespace(builder.ToString());
        if (forceUppercase)
        {
            cleaned = cleaned.ToUpperInvariant();
        }

        cleaned = Regex.Replace(cleaned, @"\bTHE\s+(?:IE|NE|OE|0NE|ONE)[.,]?\s+OUR\b", "THE ONE, OUR", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"\bTHE ONE\s*[.,]?\s+OUR\b", "THE ONE, OUR", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"\bRISING\s+TAR\b", "RISING STAR", RegexOptions.IgnoreCase);
        return cleaned;
    }

    private static OcrTextBlock[]? TryOrderLatinSingleLineBlocks(IReadOnlyList<OcrTextBlock> blocks)
    {
        if (blocks.Count < 2 ||
            blocks.Any(block => block.BoundingBox is null || CountCjkCharacters(block.Text) > 0) ||
            !blocks.Any(block => block.Text.Any(IsAsciiLetterOrDigit)))
        {
            return null;
        }

        var boxes = blocks.Select(block => block.BoundingBox!).ToArray();
        var averageHeight = boxes.Average(box => Math.Max(1, box.Height));
        var centerSpread = boxes.Max(box => box.Y + (box.Height / 2.0)) -
            boxes.Min(box => box.Y + (box.Height / 2.0));
        var left = boxes.Min(box => box.X);
        var right = boxes.Max(box => box.X + box.Width);
        var horizontalSpan = right - left;
        if (centerSpread > Math.Max(18.0, averageHeight * 0.9) ||
            horizontalSpan / Math.Max(1.0, averageHeight) < 4.0)
        {
            return null;
        }

        return blocks
            .OrderBy(block => block.BoundingBox!.X)
            .ThenBy(block => block.BoundingBox!.Y)
            .ToArray();
    }

    private static OcrTextBlock MergeTextBlocks(IReadOnlyList<OcrTextBlock> blocks, string text)
    {
        var boxes = blocks
            .Select(block => block.BoundingBox)
            .Where(box => box is not null)
            .Select(box => box!)
            .ToArray();
        OcrBoundingBox? mergedBox = null;
        if (boxes.Length > 0)
        {
            var left = boxes.Min(box => box.X);
            var top = boxes.Min(box => box.Y);
            var right = boxes.Max(box => box.X + box.Width);
            var bottom = boxes.Max(box => box.Y + box.Height);
            mergedBox = new OcrBoundingBox(left, top, right - left, bottom - top);
        }

        var confidenceWeight = blocks.Sum(block => Math.Max(1, block.Text.Length));
        var confidence = confidenceWeight <= 0
            ? blocks.Average(block => block.Confidence)
            : blocks.Sum(block => block.Confidence * Math.Max(1, block.Text.Length)) / confidenceWeight;
        var first = blocks.First();
        return new OcrTextBlock(text, Math.Clamp(confidence, 0, 1), mergedBox)
        {
            DetectedLanguage = first.DetectedLanguage,
            Script = first.Script
        };
    }

    private static bool ShouldUppercaseLatinSubtitle(string text)
    {
        var uppercase = 0;
        var lowercase = 0;
        foreach (var character in text)
        {
            if (character is >= 'A' and <= 'Z')
            {
                uppercase++;
            }
            else if (character is >= 'a' and <= 'z')
            {
                lowercase++;
            }
        }

        return uppercase >= 4 && uppercase >= lowercase * 2;
    }

    private static string SegmentCompactLatinToken(string token)
    {
        if (token.Length < 8 || token.Any(character => !IsAsciiLetterOrDigit(character)))
        {
            return token;
        }

        var upper = token.ToUpperInvariant();
        var segmented = TrySegmentLatinWords(upper);
        return segmented is null ? token : string.Join(' ', segmented);
    }

    private static string[]? TrySegmentLatinWords(string token)
    {
        var best = new (int Cost, int Words, string[] Parts)?[token.Length + 1];
        best[0] = (0, 0, []);

        for (var index = 0; index < token.Length; index++)
        {
            var state = best[index];
            if (state is null)
            {
                continue;
            }

            foreach (var word in LatinSubtitleWords)
            {
                var remaining = token.Length - index;
                var minLength = Math.Max(1, word.Length - 1);
                var maxLength = Math.Min(remaining, word.Length + 1);
                for (var length = minLength; length <= maxLength; length++)
                {
                    if (index + length > token.Length)
                    {
                        continue;
                    }

                    var slice = token.Substring(index, length);
                    var cost = LatinWordMatchCost(slice, word);
                    if (cost is null)
                    {
                        continue;
                    }

                    var next = index + length;
                    var candidate = (
                        Cost: state.Value.Cost + cost.Value,
                        Words: state.Value.Words + 1,
                        Parts: state.Value.Parts.Append(word).ToArray());
                    var existing = best[next];
                    if (existing is null ||
                        candidate.Cost < existing.Value.Cost ||
                        (candidate.Cost == existing.Value.Cost && candidate.Words < existing.Value.Words))
                    {
                        best[next] = candidate;
                    }
                }
            }
        }

        var final = best[token.Length];
        if (final is null ||
            final.Value.Words < 2 ||
            final.Value.Cost > (token.Length >= 10 ? 2 : 1))
        {
            return null;
        }

        return final.Value.Parts;
    }

    private static int? LatinWordMatchCost(string slice, string word)
    {
        if (string.Equals(slice, word, StringComparison.Ordinal))
        {
            return 0;
        }

        if (word.Length < 3 || Math.Abs(slice.Length - word.Length) > 1)
        {
            return null;
        }

        var distance = LevenshteinDistanceAtMostOne(slice, word);
        return distance <= 1 ? distance : null;
    }

    private static int LevenshteinDistanceAtMostOne(string left, string right)
    {
        if (Math.Abs(left.Length - right.Length) > 1)
        {
            return 2;
        }

        var mismatches = 0;
        var i = 0;
        var j = 0;
        while (i < left.Length && j < right.Length)
        {
            if (left[i] == right[j])
            {
                i++;
                j++;
                continue;
            }

            mismatches++;
            if (mismatches > 1)
            {
                return mismatches;
            }

            if (left.Length == right.Length)
            {
                i++;
                j++;
            }
            else if (left.Length > right.Length)
            {
                i++;
            }
            else
            {
                j++;
            }
        }

        if (i < left.Length || j < right.Length)
        {
            mismatches++;
        }

        return mismatches;
    }

    private static bool IsAsciiLetterOrDigit(char character)
        => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9';

    private static OcrDocumentResult ReplaceSingleDocumentText(OcrDocumentResult document, string text)
        => document with
        {
            Pages = document.Pages.Count == 1
                ? [document.Pages[0] with { Blocks = ReplaceSingleDocumentBlockText(document.Pages[0].Blocks, text) }]
                : document.Pages
        };

    private static OcrDocumentResult ReplaceSingleDocumentTextBlock(
        OcrDocumentResult document,
        OcrTextBlock block,
        string text)
        => document with
        {
            Pages = document.Pages.Count == 1
                ? [document.Pages[0] with { Blocks = ReplaceSingleDocumentBlock(document.Pages[0].Blocks, block, text) }]
                : document.Pages
        };

    private static IReadOnlyList<OcrBlock> ReplaceSingleDocumentBlockText(IReadOnlyList<OcrBlock> blocks, string text)
    {
        if (blocks.Count != 1)
        {
            return blocks;
        }

        return [blocks[0] with { Text = text }];
    }

    private static IReadOnlyList<OcrBlock> ReplaceSingleDocumentBlock(
        IReadOnlyList<OcrBlock> blocks,
        OcrTextBlock textBlock,
        string text)
    {
        if (blocks.Count == 0)
        {
            return blocks;
        }

        var first = blocks
            .OrderBy(block => block.ReadingOrder)
            .First();
        return
        [
            first with
            {
                Text = text,
                Confidence = textBlock.Confidence,
                BoundingBox = textBlock.BoundingBox,
                Polygon = ToPolygon(textBlock.BoundingBox),
                ReadingOrder = 0,
                DetectedLanguage = textBlock.DetectedLanguage,
                Script = textBlock.Script
            }
        ];
    }

    private static OcrTextBlock MapShadowRepairTextBlock(
        OcrTextBlock block,
        ShadowRepairCandidate candidate)
        => block with
        {
            BoundingBox = MapShadowRepairBox(block.BoundingBox, candidate)
        };

    private static OcrDocumentResult MapShadowRepairDocument(
        OcrDocumentResult document,
        ShadowRepairCandidate candidate,
        string engineSuffix)
        => document with
        {
            Pages = document.Pages
                .Select(page => page with
                {
                    Width = candidate.OriginalWidth,
                    Height = candidate.OriginalHeight,
                    ImageWidth = page.ImageWidth.HasValue ? candidate.OriginalWidth : page.ImageWidth,
                    ImageHeight = page.ImageHeight.HasValue ? candidate.OriginalHeight : page.ImageHeight,
                    Blocks = page.Blocks
                        .Select(block => MapShadowRepairBlock(block, candidate, engineSuffix))
                        .ToArray()
                })
                .ToArray()
        };

    private static OcrBlock MapShadowRepairBlock(
        OcrBlock block,
        ShadowRepairCandidate candidate,
        string engineSuffix)
    {
        var mappedBox = MapShadowRepairBox(block.BoundingBox, candidate);
        var polygon = block.Polygon.Count > 0
            ? block.Polygon.Select(point => MapShadowRepairPoint(point, candidate)).ToArray()
            : ToPolygon(mappedBox);

        return block with
        {
            BoundingBox = mappedBox,
            Polygon = polygon,
            Engine = AppendEngineSuffix(block.Engine, engineSuffix),
            Children = block.Children
                .Select(child => MapShadowRepairBlock(child, candidate, engineSuffix))
                .ToArray(),
            Table = block.Table is null ? null : MapShadowRepairTable(block.Table, candidate)
        };
    }

    private static OcrTableBlock MapShadowRepairTable(
        OcrTableBlock table,
        ShadowRepairCandidate candidate)
        => table with
        {
            Cells = table.Cells
                .Select(cell => cell with
                {
                    BoundingBox = MapShadowRepairBox(cell.BoundingBox, candidate),
                    Polygon = cell.Polygon
                        .Select(point => MapShadowRepairPoint(point, candidate))
                        .ToArray()
                })
                .ToArray()
        };

    private static OcrBoundingBox? MapShadowRepairBox(
        OcrBoundingBox? box,
        ShadowRepairCandidate candidate)
    {
        if (box is null)
        {
            return null;
        }

        var x = candidate.OffsetX + (int)Math.Round(box.X / candidate.Scale);
        var y = candidate.OffsetY + (int)Math.Round(box.Y / candidate.Scale);
        var width = Math.Max(1, (int)Math.Round(box.Width / candidate.Scale));
        var height = Math.Max(1, (int)Math.Round(box.Height / candidate.Scale));
        return ClampBox(x, y, width, height, candidate.OriginalWidth, candidate.OriginalHeight);
    }

    private static OcrPoint MapShadowRepairPoint(
        OcrPoint point,
        ShadowRepairCandidate candidate)
        => new(
            Math.Clamp(candidate.OffsetX + (point.X / candidate.Scale), 0, Math.Max(0, candidate.OriginalWidth)),
            Math.Clamp(candidate.OffsetY + (point.Y / candidate.Scale), 0, Math.Max(0, candidate.OriginalHeight)));

    private static OcrBoundingBox ClampBox(int x, int y, int width, int height, int maxWidth, int maxHeight)
    {
        if (maxWidth <= 0 || maxHeight <= 0)
        {
            return new OcrBoundingBox(x, y, Math.Max(1, width), Math.Max(1, height));
        }

        x = Math.Clamp(x, 0, Math.Max(0, maxWidth - 1));
        y = Math.Clamp(y, 0, Math.Max(0, maxHeight - 1));
        width = Math.Clamp(width, 1, Math.Max(1, maxWidth - x));
        height = Math.Clamp(height, 1, Math.Max(1, maxHeight - y));
        return new OcrBoundingBox(x, y, width, height);
    }

    private static bool HasOcrText(OcrRunOutcome run)
        => !string.IsNullOrWhiteSpace(run.RawText) ||
           run.RawBlocks.Any(block => !string.IsNullOrWhiteSpace(block.Text));

    private static bool HasMeaningfulOcrContent(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (var character in text)
        {
            if (char.IsLetterOrDigit(character))
            {
                return true;
            }
        }

        return false;
    }

    private static double AverageBlockConfidence(IReadOnlyList<OcrTextBlock> blocks)
    {
        var confidenceBlocks = blocks
            .Where(block => !string.IsNullOrWhiteSpace(block.Text))
            .Select(block => Math.Clamp(block.Confidence, 0, 1))
            .ToArray();
        return confidenceBlocks.Length == 0 ? 0 : confidenceBlocks.Average();
    }

    private static double ShadowRepairQuality(OcrRunOutcome run, string language)
    {
        var text = run.RawText;
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        var meaningful = 0;
        var spaces = 0;
        var punctuation = 0;
        var replacements = 0;
        foreach (var character in text)
        {
            if (char.IsLetterOrDigit(character))
            {
                meaningful++;
            }
            else if (char.IsWhiteSpace(character))
            {
                spaces++;
            }
            else if (character == '\uFFFD')
            {
                replacements++;
            }
            else if (char.IsPunctuation(character) || char.IsSymbol(character))
            {
                punctuation++;
            }
        }

        var tokenCount = CountMeaningfulTokens(text);
        var averageConfidence = AverageBlockConfidence(run.RawBlocks);
        var languagePenalty = IsLatinOcrLanguage(language)
            ? CountCjkCharacters(text) * 1.3
            : 0;
        var tinyStrayPenalty = CountTinyStrayBlocks(run.RawBlocks) * 4.0;
        return meaningful +
            (Math.Min(tokenCount, 16) * 1.2) +
            (Math.Min(spaces, 16) * 0.15) +
            (averageConfidence * 6) -
            (punctuation * 0.35) -
            (replacements * 3.0) -
            languagePenalty -
            tinyStrayPenalty;
    }

    private static int CountMeaningfulTokens(string text)
    {
        var count = 0;
        var inToken = false;
        foreach (var character in text)
        {
            if (char.IsLetterOrDigit(character))
            {
                if (!inToken)
                {
                    count++;
                    inToken = true;
                }
            }
            else
            {
                inToken = false;
            }
        }

        return count;
    }

    private static bool IsLatinOcrLanguage(string language)
    {
        var canonical = LanguageRegistry.Normalize(language);
        return string.Equals(canonical, LanguageRegistry.English, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsJapaneseOcrLanguage(string language)
    {
        var canonical = LanguageRegistry.Normalize(language);
        return string.Equals(canonical, "ja", StringComparison.OrdinalIgnoreCase);
    }

    private static bool WouldDowngradeJapaneseShadowRepair(
        OcrRunOutcome currentRun,
        OcrRunOutcome repairedRun,
        string language)
    {
        if (!IsJapaneseOcrLanguage(language) && CountJapaneseKana(currentRun.RawText) == 0)
        {
            return false;
        }

        var currentJapanese = CountJapaneseCharacters(currentRun.RawText);
        if (currentJapanese < 4)
        {
            return false;
        }

        var repairedJapanese = CountJapaneseCharacters(repairedRun.RawText);
        if (repairedJapanese < currentJapanese)
        {
            return true;
        }

        var currentKana = CountJapaneseKana(currentRun.RawText);
        var repairedKana = CountJapaneseKana(repairedRun.RawText);
        if (currentKana >= 2 && repairedKana < currentKana)
        {
            return true;
        }

        var currentHiragana = CountJapaneseHiragana(currentRun.RawText);
        var repairedHiragana = CountJapaneseHiragana(repairedRun.RawText);
        return currentHiragana >= 2 && repairedHiragana < currentHiragana;
    }

    private static bool IsLatinRepairLanguage(string language, OcrRunOutcome run)
        => IsLatinOcrLanguage(language) ||
           string.Equals(run.Detection.DetectedLanguage, LanguageRegistry.English, StringComparison.OrdinalIgnoreCase) ||
           run.Detection.Script.Contains("Latn", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeFragmentedLatinRealtimeText(OcrRunOutcome run)
    {
        if (!HasOcrText(run))
        {
            return true;
        }

        var text = run.RawText;
        if (run.RawBlocks.Length != 1)
        {
            return true;
        }

        if (CountCjkCharacters(text) > 0)
        {
            return false;
        }

        foreach (var token in SplitAsciiTokens(text))
        {
            if (token.Length >= 9 && token.All(character => character is >= 'A' and <= 'Z'))
            {
                return true;
            }
        }

        return AverageBlockConfidence(run.RawBlocks) < 0.68;
    }

    private static IEnumerable<string> SplitAsciiTokens(string text)
    {
        var start = -1;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            var isAsciiLetter = character is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
            if (isAsciiLetter)
            {
                if (start < 0)
                {
                    start = index;
                }
            }
            else if (start >= 0)
            {
                yield return text[start..index];
                start = -1;
            }
        }

        if (start >= 0)
        {
            yield return text[start..];
        }
    }

    private static int CountCjkCharacters(string text)
    {
        var count = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var value = rune.Value;
            if ((value >= 0x3040 && value <= 0x30FF) ||
                (value >= 0x3400 && value <= 0x4DBF) ||
                (value >= 0x4E00 && value <= 0x9FFF) ||
                (value >= 0xAC00 && value <= 0xD7AF))
            {
                count++;
            }
        }

        return count;
    }

    private static int CountJapaneseCharacters(string text)
    {
        var count = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var value = rune.Value;
            if ((value >= 0x3040 && value <= 0x30FF) ||
                (value >= 0x31F0 && value <= 0x31FF) ||
                (value >= 0xFF66 && value <= 0xFF9F) ||
                (value >= 0x3400 && value <= 0x4DBF) ||
                (value >= 0x4E00 && value <= 0x9FFF) ||
                (value >= 0xF900 && value <= 0xFAFF))
            {
                count++;
            }
        }

        return count;
    }

    private static int CountJapaneseKana(string text)
    {
        var count = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var value = rune.Value;
            if ((value >= 0x3040 && value <= 0x30FF) ||
                (value >= 0x31F0 && value <= 0x31FF) ||
                (value >= 0xFF66 && value <= 0xFF9F))
            {
                count++;
            }
        }

        return count;
    }

    private static int CountJapaneseHiragana(string text)
    {
        var count = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var value = rune.Value;
            if (value >= 0x3040 && value <= 0x309F)
            {
                count++;
            }
        }

        return count;
    }

    private static int CountCjkIdeographCharacters(string text)
    {
        var count = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var value = rune.Value;
            if ((value >= 0x3400 && value <= 0x4DBF) ||
                (value >= 0x4E00 && value <= 0x9FFF) ||
                (value >= 0xF900 && value <= 0xFAFF))
            {
                count++;
            }
        }

        return count;
    }

    private static int CountLatinLetters(string text)
    {
        var count = 0;
        foreach (var character in text)
        {
            if ((character >= 'A' && character <= 'Z') ||
                (character >= 'a' && character <= 'z'))
            {
                count++;
            }
        }

        return count;
    }

    private static int CountTinyStrayBlocks(IReadOnlyList<OcrTextBlock> blocks)
        => blocks.Count(block =>
            CountMeaningfulCharacters(block.Text) <= 1 &&
            block.BoundingBox is { Width: <= 40, Height: <= 20 });

    private static int CountMeaningfulCharacters(string text)
    {
        var count = 0;
        foreach (var character in text)
        {
            if (char.IsLetterOrDigit(character))
            {
                count++;
            }
        }

        return count;
    }

    private static string AppendEngineSuffix(string engine, string suffix)
    {
        if (string.IsNullOrWhiteSpace(engine))
        {
            return suffix;
        }

        return engine.Contains("+" + suffix, StringComparison.OrdinalIgnoreCase)
            ? engine
            : engine + "+" + suffix;
    }

    private static string SeedLanguageFor(OcrProviderDescriptor descriptor, IReadOnlyList<string> allowedLanguages)
    {
        if (descriptor.SupportedLanguages.Count > 0)
        {
            var supported = allowedLanguages.FirstOrDefault(language =>
                descriptor.SupportedLanguages.Contains(language, StringComparer.OrdinalIgnoreCase));
            if (supported is not null)
            {
                return supported;
            }
        }

        return allowedLanguages.Count > 0
            ? allowedLanguages[0]
            : LanguageRegistry.Normalize(descriptor.DefaultLanguage);
    }

    private async Task<OcrRunOutcome> RefineSuspectBlocksAsync(
        IOcrProvider provider,
        DecodedOcrImage decoded,
        OcrRunOutcome run,
        bool normalizeWhitespace,
        CancellationToken cancellationToken)
    {
        var options = _options.Ocr.Refinement;
        var suspects = OcrBlockRefinement.FindSuspectBlocks(run.RawDocument, options);
        if (suspects.Count == 0)
        {
            return run;
        }

        System.Drawing.Bitmap source;
        var sourceStream = new MemoryStream(decoded.ImageBytes, writable: false);
        try
        {
            source = new System.Drawing.Bitmap(sourceStream);
        }
        catch (Exception)
        {
            // Formats GDI+ cannot decode (e.g. webp) simply skip refinement.
            sourceStream.Dispose();
            return run;
        }

        var document = run.RawDocument;
        var changed = false;
        using (sourceStream)
        using (source)
        {
            foreach (var suspect in suspects)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = FindPageContainingBlock(document, suspect.Id);
                if (page is null)
                {
                    continue;
                }

                var pageSize = OcrBlockRefinement.InferPageSize(page);
                if (pageSize is null)
                {
                    continue;
                }

                var cropRect = OcrBlockRefinement.ComputeCropRectangle(
                    suspect.BoundingBox,
                    pageSize.Value.Width,
                    pageSize.Value.Height,
                    source.Width,
                    source.Height,
                    options.PaddingRatio);
                if (cropRect is null)
                {
                    continue;
                }

                byte[] cropBytes;
                try
                {
                    cropBytes = CropToPng(source, cropRect);
                }
                catch (Exception)
                {
                    continue;
                }

                OcrRunOutcome rerun;
                try
                {
                    rerun = await RunRecognitionAsync(
                        provider,
                        new DecodedOcrImage(cropBytes, "image/png", cropRect.Width, cropRect.Height),
                        run.AttemptedLanguage,
                        normalizeWhitespace,
                        options.RerunPreset,
                        cancellationToken);
                }
                catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or ArgumentException)
                {
                    // A failing crop pass must not fail the request; the original
                    // block reading stands (same tolerance as the language reruns).
                    continue;
                }

                var refinedText = rerun.RawText.Trim();
                if (refinedText.Length == 0)
                {
                    continue;
                }

                var refinedConfidence = rerun.RawBlocks.Length > 0
                    ? rerun.RawBlocks.Max(block => block.Confidence)
                    : 0;
                if (!OcrBlockRefinement.ShouldAcceptRefinement(
                    suspect.Text,
                    suspect.Confidence,
                    refinedText,
                    refinedConfidence,
                    options))
                {
                    continue;
                }

                document = OcrBlockRefinement.ApplyRefinement(
                    document,
                    suspect.Id,
                    refinedText,
                    Math.Max(suspect.Confidence, refinedConfidence));
                changed = true;
            }
        }

        if (!changed)
        {
            return run;
        }

        var rawBlocks = FlattenDocumentTextBlocks(document);
        var rawText = string.Join(
            Environment.NewLine,
            rawBlocks.Select(block => block.Text).Where(value => !string.IsNullOrWhiteSpace(value)));
        if (normalizeWhitespace)
        {
            rawText = NormalizeWhitespace(rawText);
        }

        return run with { RawDocument = document, RawBlocks = rawBlocks, RawText = rawText };
    }

    private static byte[] CropToPng(System.Drawing.Bitmap source, OcrBoundingBox rect)
    {
        var bounds = new System.Drawing.Rectangle(rect.X, rect.Y, rect.Width, rect.Height);
        using var crop = source.Clone(bounds, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using var output = new MemoryStream();
        crop.Save(output, System.Drawing.Imaging.ImageFormat.Png);
        return output.ToArray();
    }

    private static OcrPageResult? FindPageContainingBlock(OcrDocumentResult document, string blockId)
        => document.Pages.FirstOrDefault(page => ContainsBlock(page.Blocks, blockId));

    private static bool ContainsBlock(IReadOnlyList<OcrBlock> blocks, string blockId)
        => blocks.Any(block =>
            string.Equals(block.Id, blockId, StringComparison.Ordinal) ||
            ContainsBlock(block.Children, blockId));

    private static OcrTextBlock[] FlattenDocumentTextBlocks(OcrDocumentResult document)
    {
        var blocks = new List<OcrTextBlock>();
        foreach (var page in document.Pages)
        {
            FlattenTextBlocks(page.Blocks, blocks);
        }

        return blocks.ToArray();
    }

    private static void FlattenTextBlocks(IReadOnlyList<OcrBlock> blocks, List<OcrTextBlock> output)
    {
        foreach (var block in blocks.OrderBy(item => item.ReadingOrder))
        {
            if (!string.IsNullOrWhiteSpace(block.Text))
            {
                output.Add(new OcrTextBlock(block.Text, block.Confidence, block.BoundingBox)
                {
                    DetectedLanguage = block.DetectedLanguage,
                    Script = block.Script
                });
            }

            FlattenTextBlocks(block.Children, output);
        }
    }

    private static (OcrTextBlock[] Blocks, OcrDocumentResult Document, ScriptDetectionResult Aggregate) AnnotateLanguages(
        IReadOnlyList<OcrTextBlock> blocks,
        OcrDocumentResult document)
    {
        var detections = blocks
            .Select(block => UnicodeScriptDetector.Detect(block.Text))
            .ToArray();
        var aggregate = UnicodeScriptDetector.Aggregate(
            detections
                .Zip(blocks, (detection, block) =>
                    (detection, detection.EffectiveCharCount * Math.Clamp(block.Confidence, 0.05, 1.0)))
                .ToArray());

        var annotatedBlocks = blocks
            .Zip(detections, (block, detection) => block with
            {
                DetectedLanguage = PickBlockLanguage(detection, aggregate),
                Script = detection.Script
            })
            .ToArray();
        var annotatedDocument = document with
        {
            Pages = document.Pages
                .Select(page => page with
                {
                    Blocks = page.Blocks
                        .Select(block => AnnotateDocumentBlock(block, aggregate))
                        .ToArray()
                })
                .ToArray()
        };

        return (annotatedBlocks, annotatedDocument, aggregate);
    }

    private static string PickBlockLanguage(ScriptDetectionResult detection, ScriptDetectionResult aggregate)
        // Very short fragments ("OK", a lone kanji) carry too little signal; inherit
        // the document-level language instead of misclassifying them.
        => detection.EffectiveCharCount < 4 || string.IsNullOrEmpty(detection.DetectedLanguage)
            ? aggregate.DetectedLanguage
            : detection.DetectedLanguage;

    private static OcrBlock AnnotateDocumentBlock(OcrBlock block, ScriptDetectionResult aggregate)
    {
        var detection = UnicodeScriptDetector.Detect(block.Text);
        var table = block.Table is null
            ? null
            : block.Table with
            {
                Cells = block.Table.Cells
                    .Select(cell => cell with
                    {
                        DetectedLanguage = PickBlockLanguage(UnicodeScriptDetector.Detect(cell.Text), aggregate)
                    })
                    .ToArray()
            };

        return block with
        {
            DetectedLanguage = PickBlockLanguage(detection, aggregate),
            Script = detection.Script,
            Children = block.Children
                .Select(child => AnnotateDocumentBlock(child, aggregate))
                .ToArray(),
            Table = table
        };
    }

    private static DecodedOcrImage DecodeImage(string? imageBase64, string? imageMimeType, int maxImageBytes)
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

        var size = TryReadImageSize(bytes, mimeType);
        return new DecodedOcrImage(bytes, mimeType, size?.Width, size?.Height);
    }

    private static ImageSize? TryReadImageSize(byte[] bytes, string mimeType)
        => TryReadPngSize(bytes)
            ?? TryReadJpegSize(bytes)
            ?? TryReadGifSize(bytes)
            ?? TryReadBmpSize(bytes)
            ?? TryReadWebpSize(bytes)
            ?? TryReadImageSizeFromMime(bytes, mimeType);

    private static ImageSize? TryReadImageSizeFromMime(byte[] bytes, string mimeType)
        => mimeType.ToLowerInvariant() switch
        {
            "image/png" => TryReadPngSize(bytes),
            "image/jpeg" or "image/jpg" => TryReadJpegSize(bytes),
            "image/gif" => TryReadGifSize(bytes),
            "image/bmp" => TryReadBmpSize(bytes),
            "image/webp" => TryReadWebpSize(bytes),
            _ => null
        };

    private static ImageSize? TryReadPngSize(byte[] bytes)
    {
        ReadOnlySpan<byte> signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
        if (bytes.Length < 24 || !bytes.AsSpan(0, 8).SequenceEqual(signature))
        {
            return null;
        }

        var width = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4));
        var height = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4));
        return width > 0 && height > 0 ? new ImageSize(width, height) : null;
    }

    private static ImageSize? TryReadGifSize(byte[] bytes)
    {
        if (bytes.Length < 10 ||
            !(bytes.AsSpan(0, 6).SequenceEqual("GIF87a"u8) || bytes.AsSpan(0, 6).SequenceEqual("GIF89a"u8)))
        {
            return null;
        }

        var width = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6, 2));
        var height = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(8, 2));
        return width > 0 && height > 0 ? new ImageSize(width, height) : null;
    }

    private static ImageSize? TryReadBmpSize(byte[] bytes)
    {
        if (bytes.Length < 26 || bytes[0] != (byte)'B' || bytes[1] != (byte)'M')
        {
            return null;
        }

        var width = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(18, 4));
        var height = Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(22, 4)));
        return width > 0 && height > 0 ? new ImageSize(width, height) : null;
    }

    private static ImageSize? TryReadJpegSize(byte[] bytes)
    {
        if (bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8)
        {
            return null;
        }

        var offset = 2;
        while (offset + 3 < bytes.Length)
        {
            if (bytes[offset] != 0xFF)
            {
                offset++;
                continue;
            }

            while (offset < bytes.Length && bytes[offset] == 0xFF)
            {
                offset++;
            }

            if (offset >= bytes.Length)
            {
                break;
            }

            var marker = bytes[offset++];
            if (marker is 0xD9 or 0xDA)
            {
                break;
            }

            if (marker is 0x01 || marker is >= 0xD0 and <= 0xD7)
            {
                continue;
            }

            if (offset + 2 > bytes.Length)
            {
                break;
            }

            var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset, 2));
            if (segmentLength < 2 || offset + segmentLength > bytes.Length)
            {
                break;
            }

            if (IsJpegStartOfFrameMarker(marker) && segmentLength >= 7)
            {
                var height = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset + 3, 2));
                var width = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset + 5, 2));
                return width > 0 && height > 0 ? new ImageSize(width, height) : null;
            }

            offset += segmentLength;
        }

        return null;
    }

    private static bool IsJpegStartOfFrameMarker(byte marker)
        => marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC;

    private static ImageSize? TryReadWebpSize(byte[] bytes)
    {
        if (bytes.Length < 16 ||
            !bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) ||
            !bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8))
        {
            return null;
        }

        var chunk = bytes.AsSpan(12, 4);
        if (chunk.SequenceEqual("VP8X"u8) && bytes.Length >= 30)
        {
            var width = 1 + ReadUInt24LittleEndian(bytes.AsSpan(24, 3));
            var height = 1 + ReadUInt24LittleEndian(bytes.AsSpan(27, 3));
            return width > 0 && height > 0 ? new ImageSize(width, height) : null;
        }

        if (chunk.SequenceEqual("VP8 "u8) && bytes.Length >= 30)
        {
            if (bytes[23] != 0x9D || bytes[24] != 0x01 || bytes[25] != 0x2A)
            {
                return null;
            }

            var width = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(26, 2)) & 0x3FFF;
            var height = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(28, 2)) & 0x3FFF;
            return width > 0 && height > 0 ? new ImageSize(width, height) : null;
        }

        if (chunk.SequenceEqual("VP8L"u8) && bytes.Length >= 25 && bytes[20] == 0x2F)
        {
            var b1 = bytes[21];
            var b2 = bytes[22];
            var b3 = bytes[23];
            var b4 = bytes[24];
            var width = 1 + b1 + ((b2 & 0x3F) << 8);
            var height = 1 + ((b2 & 0xC0) >> 6) + (b3 << 2) + ((b4 & 0x0F) << 10);
            return width > 0 && height > 0 ? new ImageSize(width, height) : null;
        }

        return null;
    }

    private static int ReadUInt24LittleEndian(ReadOnlySpan<byte> bytes)
        => bytes[0] | (bytes[1] << 8) | (bytes[2] << 16);

    private static string Pick(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string NormalizeWhitespace(string text)
        => WhitespaceRegex.Replace(text.Trim(), " ");

    private static int? PositiveOrFallback(int? value, int? fallback)
        => value.GetValueOrDefault() > 0
            ? value
            : fallback.GetValueOrDefault() > 0 ? fallback : null;

    private static OcrDocumentResult EnsureDocumentPageSize(
        OcrDocumentResult document,
        int? imageWidth,
        int? imageHeight)
    {
        if (imageWidth.GetValueOrDefault() <= 0 && imageHeight.GetValueOrDefault() <= 0)
        {
            return document;
        }

        var changed = false;
        var pages = document.Pages
            .Select(page =>
            {
                var width = PositiveOrFallback(page.Width, imageWidth);
                var height = PositiveOrFallback(page.Height, imageHeight);
                if (width == page.Width && height == page.Height)
                {
                    return page;
                }

                changed = true;
                return page with { Width = width, Height = height };
            })
            .ToArray();

        return changed ? document with { Pages = pages } : document;
    }

    private static OcrDocumentResult PrepareProviderDocument(
        OcrDocumentResult document,
        IReadOnlyList<OcrTextBlock> fallbackBlocks,
        string engine,
        bool normalizeWhitespace,
        int? imageWidth,
        int? imageHeight)
    {
        if (document.Pages.Count == 0 || document.Pages.All(page => page.Blocks.Count == 0))
        {
            return BuildTextDocument(fallbackBlocks, engine, imageWidth, imageHeight);
        }

        var pages = document.Pages
            .Select((page, pageIndex) => page with
            {
                PageIndex = page.PageIndex < 0 ? pageIndex : page.PageIndex,
                Width = PositiveOrFallback(page.Width, imageWidth),
                Height = PositiveOrFallback(page.Height, imageHeight),
                Blocks = page.Blocks
                    .Select((block, blockIndex) => NormalizeBlock(block, engine, blockIndex, normalizeWhitespace))
                    .Where(HasMeaningfulDocumentBlock)
                    .ToArray()
            })
            .ToArray();

        return document with
        {
            Version = string.IsNullOrWhiteSpace(document.Version) ? "ocr-ir-v1" : document.Version,
            Pages = pages
        };
    }

    private static OcrBlock NormalizeBlock(
        OcrBlock block,
        string engine,
        int readingOrder,
        bool normalizeWhitespace)
    {
        var text = normalizeWhitespace && !string.IsNullOrWhiteSpace(block.Text)
            ? NormalizeWhitespace(block.Text)
            : block.Text;
        var type = string.IsNullOrWhiteSpace(block.Type) ? OcrBlockTypes.Unknown : block.Type;

        return block with
        {
            Id = string.IsNullOrWhiteSpace(block.Id) ? $"b{readingOrder}" : block.Id,
            Type = type,
            Text = text,
            ReadingOrder = block.ReadingOrder < 0 ? readingOrder : block.ReadingOrder,
            Engine = string.IsNullOrWhiteSpace(block.Engine) ? engine : block.Engine,
            ShouldTranslate = ShouldTranslateBlockType(type) && block.ShouldTranslate,
            Children = block.Children
                .Select((child, index) => NormalizeBlock(child, engine, index, normalizeWhitespace))
                .Where(HasMeaningfulDocumentBlock)
                .ToArray(),
            Table = block.Table is null ? null : NormalizeTable(block.Table, normalizeWhitespace)
        };
    }

    private static bool HasMeaningfulDocumentBlock(OcrBlock block)
    {
        if (HasMeaningfulOcrContent(block.Text) ||
            HasMeaningfulOcrContent(block.SourceText) ||
            block.Children.Any(HasMeaningfulDocumentBlock) ||
            block.Table?.Cells.Any(cell =>
                HasMeaningfulOcrContent(cell.Text) ||
                HasMeaningfulOcrContent(cell.SourceText)) == true ||
            block.Formula is not null && (
                HasMeaningfulOcrContent(block.Formula.Latex) ||
                HasMeaningfulOcrContent(block.Formula.SourceText)))
        {
            return true;
        }

        return !ShouldTranslateBlockType(block.Type) &&
            !string.Equals(block.Type, OcrBlockTypes.Text, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(block.Type, OcrBlockTypes.Unknown, StringComparison.OrdinalIgnoreCase);
    }

    private static OcrTableBlock NormalizeTable(OcrTableBlock table, bool normalizeWhitespace)
        => table with
        {
            Cells = table.Cells
                .Select(cell => cell with
                {
                    Text = normalizeWhitespace && !string.IsNullOrWhiteSpace(cell.Text)
                        ? NormalizeWhitespace(cell.Text)
                        : cell.Text
                })
                .ToArray()
        };

    private static OcrDocumentResult BuildTextDocument(
        IReadOnlyList<OcrTextBlock> blocks,
        string engine,
        int? imageWidth,
        int? imageHeight)
        => new()
        {
            Pages =
            [
                new OcrPageResult
                {
                    PageIndex = 0,
                    Width = PositiveOrFallback(null, imageWidth),
                    Height = PositiveOrFallback(null, imageHeight),
                    Blocks = blocks
                        .Select((block, index) => new OcrBlock
                        {
                            Id = $"p0-b{index}",
                            Type = OcrBlockTypes.Text,
                            Text = block.Text,
                            Confidence = block.Confidence,
                            BoundingBox = block.BoundingBox,
                            Polygon = ToPolygon(block.BoundingBox),
                            ReadingOrder = index,
                            Engine = engine,
                            ShouldTranslate = true
                        })
                        .ToArray()
                }
            ]
        };

    private static IReadOnlyList<OcrPoint> ToPolygon(OcrBoundingBox? box)
    {
        if (box is null)
        {
            return Array.Empty<OcrPoint>();
        }

        return
        [
            new OcrPoint(box.X, box.Y),
            new OcrPoint(box.X + box.Width, box.Y),
            new OcrPoint(box.X + box.Width, box.Y + box.Height),
            new OcrPoint(box.X, box.Y + box.Height)
        ];
    }

    private static CorrectionResult ApplyCorrections(
        string text,
        IReadOnlyList<OcrTextBlock> blocks,
        OcrDocumentResult document,
        IReadOnlyList<OcrCorrection> corrections)
    {
        if (corrections.Count == 0)
        {
            return new CorrectionResult(text, blocks, document, []);
        }

        var correctedText = text;
        var correctedBlocks = blocks.ToArray();
        var correctedDocument = document;
        var applied = new List<AppliedOcrCorrection>();

        foreach (var correction in corrections)
        {
            if (string.IsNullOrEmpty(correction.WrongText) ||
                string.Equals(correction.WrongText, correction.CorrectedText, StringComparison.Ordinal))
            {
                continue;
            }

            var textResult = ReplaceCorrectionText(correctedText, correction);
            var nextText = textResult.Text;
            var blockChanged = false;
            var documentResult = ReplaceInDocument(correctedDocument, correction);
            for (var index = 0; index < correctedBlocks.Length; index++)
            {
                var block = correctedBlocks[index];
                var blockResult = ReplaceCorrectionText(block.Text, correction);
                if (blockResult.Changed)
                {
                    correctedBlocks[index] = block with { Text = blockResult.Text };
                    blockChanged = true;
                }
            }

            if (textResult.Changed || blockChanged || documentResult.Changed)
            {
                correctedText = nextText;
                correctedDocument = documentResult.Document;
                applied.Add(new AppliedOcrCorrection(
                    correction.Id,
                    correction.WrongText,
                    correction.CorrectedText));
            }
        }

        return new CorrectionResult(correctedText, correctedBlocks, correctedDocument, applied);
    }

    private static DocumentReplacementResult ReplaceInDocument(
        OcrDocumentResult document,
        OcrCorrection correction)
    {
        var changed = false;
        var pages = document.Pages
            .Select(page =>
            {
                var blocks = page.Blocks
                    .Select(block =>
                    {
                        var result = ReplaceInBlock(block, correction);
                        changed |= result.Changed;
                        return result.Block;
                    })
                    .ToArray();
                return page with { Blocks = blocks };
            })
            .ToArray();

        return new DocumentReplacementResult(document with { Pages = pages }, changed);
    }

    private static BlockReplacementResult ReplaceInBlock(
        OcrBlock block,
        OcrCorrection correction)
    {
        var changed = false;
        var shouldReplaceBlockText = block.ShouldTranslate && !string.Equals(block.Type, OcrBlockTypes.Formula, StringComparison.OrdinalIgnoreCase);
        var text = block.Text;
        if (shouldReplaceBlockText)
        {
            var textResult = ReplaceCorrectionText(text, correction);
            changed = textResult.Changed;
            text = textResult.Text;
        }

        var children = block.Children
            .Select(child =>
            {
                var result = ReplaceInBlock(child, correction);
                changed |= result.Changed;
                return result.Block;
            })
            .ToArray();
        var tableResult = block.Table is null
            ? new TableReplacementResult(null, Changed: false)
            : ReplaceInTable(block.Table, correction);
        changed |= tableResult.Changed;

        return new BlockReplacementResult(
            block with
            {
                Text = text,
                Children = children,
                Table = tableResult.Table
            },
            changed);
    }

    private static TableReplacementResult ReplaceInTable(
        OcrTableBlock table,
        OcrCorrection correction)
    {
        var changed = false;
        var cells = table.Cells
            .Select(cell =>
            {
                if (!cell.ShouldTranslate)
                {
                    return cell;
                }

                var textResult = ReplaceCorrectionText(cell.Text, correction);
                if (textResult.Changed)
                {
                    changed = true;
                    return cell with { Text = textResult.Text };
                }

                return cell;
            })
            .ToArray();

        return new TableReplacementResult(table with { Cells = cells }, changed);
    }

    private static TextReplacementResult ReplaceCorrectionText(string text, OcrCorrection correction)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new TextReplacementResult(text, Changed: false);
        }

        var exact = text.Replace(correction.WrongText, correction.CorrectedText, StringComparison.Ordinal);
        if (!string.Equals(exact, text, StringComparison.Ordinal))
        {
            return new TextReplacementResult(exact, Changed: true);
        }

        var match = FindFuzzyCorrectionMatch(text, correction);
        if (match is null)
        {
            return new TextReplacementResult(text, Changed: false);
        }

        var next = text[..match.Start] + correction.CorrectedText + text[(match.Start + match.Length)..];
        return string.Equals(next, text, StringComparison.Ordinal)
            ? new TextReplacementResult(text, Changed: false)
            : new TextReplacementResult(next, Changed: true);
    }

    private static FuzzyCorrectionMatch? FindFuzzyCorrectionMatch(string text, OcrCorrection correction)
    {
        var targets = BuildFuzzyTargets(correction).ToArray();
        foreach (var target in targets)
        {
            var match = FindFuzzyCorrectionMatchForTarget(text, correction, target, allowEditDistance: false);
            if (match is not null)
            {
                return match;
            }
        }

        foreach (var target in targets)
        {
            var match = FindFuzzyCorrectionMatchForTarget(text, correction, target, allowEditDistance: true);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private static FuzzyCorrectionMatch? FindFuzzyCorrectionMatchForTarget(
        string text,
        OcrCorrection correction,
        FuzzyCorrectionTarget target,
        bool allowEditDistance)
    {
        var maxDistance = target.Normalized.Length <= 5 ? 1 : 2;
        var minLength = Math.Max(1, target.SourceLength - maxDistance);
        var maxLength = Math.Min(text.Length, target.SourceLength + maxDistance);
        FuzzyCorrectionMatch? bestMatch = null;
        var bestScore = int.MaxValue;

        for (var start = 0; start < text.Length; start++)
        {
            var remaining = text.Length - start;
            for (var length = minLength; length <= maxLength && length <= remaining; length++)
            {
                var candidate = text.Substring(start, length);
                if (string.Equals(candidate, correction.CorrectedText, StringComparison.Ordinal))
                {
                    continue;
                }

                var normalizedCandidate = NormalizeOcrFuzzyText(candidate);
                if (normalizedCandidate.Length < 3)
                {
                    continue;
                }

                if (string.Equals(normalizedCandidate, target.Normalized, StringComparison.Ordinal) ||
                    (allowEditDistance && IsEditDistanceWithin(normalizedCandidate, target.Normalized, maxDistance)))
                {
                    var ignoredCharacters = Math.Max(0, candidate.Length - normalizedCandidate.Length);
                    var score = ignoredCharacters * 10 + Math.Abs(candidate.Length - target.SourceLength);
                    if (bestMatch is null || score < bestScore)
                    {
                        bestMatch = new FuzzyCorrectionMatch(start, length);
                        bestScore = score;
                    }
                }
            }
        }

        return bestMatch;
    }

    private static IEnumerable<FuzzyCorrectionTarget> BuildFuzzyTargets(OcrCorrection correction)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in new[] { correction.WrongText, correction.CorrectedText })
        {
            var normalized = NormalizeOcrFuzzyText(value);
            if (normalized.Length < 3 || !seen.Add(normalized))
            {
                continue;
            }

            yield return new FuzzyCorrectionTarget(normalized, value.Length);
        }
    }

    private static string NormalizeOcrFuzzyText(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            if (IsIgnoredOcrFuzzyCharacter(character))
            {
                continue;
            }

            builder.Append(char.ToLowerInvariant(NormalizeOcrConfusable(character)));
        }

        return builder.ToString();
    }

    private static bool IsIgnoredOcrFuzzyCharacter(char character)
    {
        if (char.IsWhiteSpace(character))
        {
            return true;
        }

        return char.GetUnicodeCategory(character) switch
        {
            UnicodeCategory.ConnectorPunctuation or
            UnicodeCategory.DashPunctuation or
            UnicodeCategory.OpenPunctuation or
            UnicodeCategory.ClosePunctuation or
            UnicodeCategory.InitialQuotePunctuation or
            UnicodeCategory.FinalQuotePunctuation or
            UnicodeCategory.OtherPunctuation => true,
            _ => false
        };
    }

    private static char NormalizeOcrConfusable(char character)
        => character switch
        {
            'ぺ' or 'へ' => 'べ',
            'ペ' or 'ヘ' => 'ベ',
            'ロ' => '口',
            'カ' => '力',
            _ => character
        };

    private static bool IsEditDistanceWithin(string left, string right, int maxDistance)
    {
        if (Math.Abs(left.Length - right.Length) > maxDistance)
        {
            return false;
        }

        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var index = 0; index <= right.Length; index++)
        {
            previous[index] = index;
        }

        for (var leftIndex = 1; leftIndex <= left.Length; leftIndex++)
        {
            current[0] = leftIndex;
            var rowMinimum = current[0];
            for (var rightIndex = 1; rightIndex <= right.Length; rightIndex++)
            {
                var substitutionCost = left[leftIndex - 1] == right[rightIndex - 1] ? 0 : 1;
                current[rightIndex] = Math.Min(
                    Math.Min(current[rightIndex - 1] + 1, previous[rightIndex] + 1),
                    previous[rightIndex - 1] + substitutionCost);
                rowMinimum = Math.Min(rowMinimum, current[rightIndex]);
            }

            if (rowMinimum > maxDistance)
            {
                return false;
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length] <= maxDistance;
    }

    private static bool ShouldTranslateBlockType(string type)
        => !string.Equals(type, OcrBlockTypes.Formula, StringComparison.OrdinalIgnoreCase) &&
           !string.Equals(type, OcrBlockTypes.Code, StringComparison.OrdinalIgnoreCase) &&
           !string.Equals(type, OcrBlockTypes.Figure, StringComparison.OrdinalIgnoreCase);

    private static string BuildOcrCacheKey(
        string imageHash,
        string imageMimeType,
        string provider,
        string language,
        bool normalizeWhitespace,
        string preprocessingPreset,
        string correctionHash,
        string engineModelVersion,
        string refinementKey)
        // v3: language component carries the normalized canonical tag, or for auto requests
        // the hint + allowed-language set; bumping the version retires keys written when
        // language was a raw UI string ("ja", "zh-tw", ...).
        => "ocr:v4:" + ComputeSha256(string.Join(
            "\n",
            imageHash,
            imageMimeType.ToLowerInvariant(),
            provider.ToLowerInvariant(),
            language.ToLowerInvariant(),
            normalizeWhitespace ? "normalize" : "preserve",
            preprocessingPreset,
            correctionHash,
            engineModelVersion,
            refinementKey));

    private static string BuildShadowRepairCacheKey(OcrShadowRepairOptions options)
        => options.Enabled
            ? FormattableString.Invariant(
                $"shadow:v5:{options.RealtimeEnabled}:{options.PreferredProvider}:{options.RealtimePreferredProvider}:{options.Scale}:{options.RealtimeScale}:{options.MaxRepairedPixels}:{options.MinAspectRatio}:{options.MaxOriginalHeight}:{options.CropTopRatio}:{options.CropBottomRatio}:{options.RealtimeCropTopRatio}:{options.RealtimeCropBottomRatio}:{options.TriggerAverageConfidence}:{options.MinQualityGain}")
            : "shadow:off";

    private static string NormalizePreprocessingPreset(string? value, OcrPreprocessingOptions options)
    {
        var preset = Pick(value, options.DefaultPreset).ToLowerInvariant();
        if (options.AllowedPresets.Any(allowed => string.Equals(allowed, preset, StringComparison.OrdinalIgnoreCase)))
        {
            return preset;
        }

        throw new ArgumentException($"Unknown OCR preprocessing preset '{preset}'.");
    }

    private static string ComputeCorrectionHash(IReadOnlyList<OcrCorrection> corrections)
    {
        if (corrections.Count == 0)
        {
            return "none";
        }

        var material = string.Join(
            "\n",
            corrections
                .OrderBy(correction => correction.Id, StringComparer.Ordinal)
                .Select(correction => string.Join(
                    "\t",
                    correction.Id,
                    correction.WrongText,
                    correction.CorrectedText,
                    correction.Priority.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    correction.Confidence.ToString("R", System.Globalization.CultureInfo.InvariantCulture))));
        return ComputeSha256(material);
    }

    private static string ResolveEngineModelVersion(string provider, VerbeamOptions options)
        => provider.ToLowerInvariant() switch
        {
            "mock" => "mock-v1",
            "external" => "external:" + ComputeSha256($"{options.Ocr.External.FileName}\n{options.Ocr.External.Arguments}")[..12],
            "tesseract" => "tesseract-tsv-lines-v2",
            "easyocr" => "easyocr",
            "rapidocr-ppocrv5" => "rapidocr-ppocrv5-boxes-v2",
            "rapidocr-net" => "rapidocr-net-ppocrv5-art-vertical+sparse-glyph-v2",
            "rapidocr-net-v6" => "rapidocr-net-ppocrv6-medium-det-small-rec+sparse-glyph-v1+vertical-v5-fallback-v1",
            "paddleocr" => "paddleocr-boxes-v2",
            "pix2text" => "pix2text",
            "pp-structure-v3" => "PP-StructureV3",
            "paddleocr-vl" => "PaddleOCR-VL-1.6",
            "dots-ocr" => "rednote-hilab/dots.ocr",
            _ => provider
        };

    private static string ComputeSha256(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string ComputeSha256(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private sealed record DecodedOcrImage(byte[] ImageBytes, string ImageMimeType, int? Width, int? Height);

    private sealed record ShadowRepairCandidate(
        string Name,
        DecodedOcrImage Decoded,
        double Scale,
        int OffsetX,
        int OffsetY,
        int OriginalWidth,
        int OriginalHeight);

    private sealed record ImageSize(int Width, int Height);

    private sealed record CorrectionResult(
        string Text,
        IReadOnlyList<OcrTextBlock> Blocks,
        OcrDocumentResult Document,
        IReadOnlyList<AppliedOcrCorrection> AppliedCorrections);

    private sealed record TextReplacementResult(string Text, bool Changed);

    private sealed record FuzzyCorrectionTarget(string Normalized, int SourceLength);

    private sealed record FuzzyCorrectionMatch(int Start, int Length);

    private sealed record DocumentReplacementResult(OcrDocumentResult Document, bool Changed);

    private sealed record BlockReplacementResult(OcrBlock Block, bool Changed);

    private sealed record TableReplacementResult(OcrTableBlock? Table, bool Changed);
}
