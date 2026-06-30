using System.Text;
using System.Text.RegularExpressions;
using Verbeam.Core.Models;
using Verbeam.Core.Options;
using Verbeam.Core.Storage;

namespace Verbeam.Core.Services;

public sealed class MemoryMaintenanceService
{
    private const int MaxSourceCharacters = 500;
    private const int MaxTargetCharacters = 1000;
    private const int MaxTermCharacters = 80;
    private const int MaxMaintenanceJobAttempts = 3;
    public const string TranslationCandidatesJobKind = "translation_candidates";
    public const string EmbeddingPrewarmJobKind = "embedding_prewarm";
    private const string ExtractorName = "memory-maintenance-v1";
    private static readonly string[] EmbeddableMemoryKinds =
    [
        "term",
        "ocr_correction",
        "style",
        "translation",
        "scene_summary"
    ];

    private static readonly string[] EmbeddableTrustLevels =
    [
        RagSecurityPolicy.UserVerified,
        RagSecurityPolicy.TrustedImport
    ];

    private static readonly Regex RetainedTitleTermRegex = new(
        @"\b(?:[A-Z][A-Za-z0-9]{2,})(?:\s+[A-Z][A-Za-z0-9]{1,}){1,3}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly ITranslationEventStore _eventStore;
    private readonly IMemoryStore _memoryStore;
    private readonly IOcrMemoryStore? _ocrMemoryStore;
    private readonly IEmbeddingProvider? _embeddingProvider;
    private readonly IMemoryMaintenanceJobStore? _jobStore;
    private readonly MemoryOptions _options;

    public MemoryMaintenanceService(
        ITranslationEventStore eventStore,
        IMemoryStore memoryStore,
        VerbeamOptions options)
        : this(eventStore, memoryStore, ocrMemoryStore: null, options, embeddingProvider: null)
    {
    }

    public MemoryMaintenanceService(
        ITranslationEventStore eventStore,
        IMemoryStore memoryStore,
        IOcrMemoryStore? ocrMemoryStore,
        VerbeamOptions options,
        IEmbeddingProvider? embeddingProvider = null,
        IMemoryMaintenanceJobStore? jobStore = null)
    {
        _eventStore = eventStore;
        _memoryStore = memoryStore;
        _ocrMemoryStore = ocrMemoryStore;
        _embeddingProvider = embeddingProvider;
        _jobStore = jobStore;
        _options = options.Memory;
    }

    public bool HasDurableQueue => _jobStore is not null;

    public async Task<IReadOnlyList<string>> EnqueueMaintenanceJobsAsync(
        string profileId,
        string sessionId,
        string sourceLanguage,
        string targetLanguage,
        string mode,
        bool extractCandidates,
        bool maintainEmbeddings,
        CancellationToken cancellationToken = default)
    {
        if (_jobStore is null)
        {
            return [];
        }

        var ids = new List<string>();
        if (extractCandidates && !string.IsNullOrWhiteSpace(sessionId))
        {
            ids.Add(await _jobStore.EnqueueAsync(
                TranslationCandidatesJobKind,
                profileId,
                sessionId,
                sourceLanguage,
                targetLanguage,
                mode,
                cancellationToken));
        }

        if (maintainEmbeddings)
        {
            ids.Add(await _jobStore.EnqueueAsync(
                EmbeddingPrewarmJobKind,
                profileId,
                sessionId,
                sourceLanguage,
                targetLanguage,
                mode,
                cancellationToken));
        }

        return ids;
    }

    public async Task<IReadOnlyList<MemoryMaintenanceJob>> ListQueuedJobsAsync(
        string? profileId = null,
        string? status = null,
        int limit = 100,
        CancellationToken cancellationToken = default)
        => _jobStore is null
            ? []
            : await _jobStore.ListAsync(profileId, status, limit, cancellationToken);

    public async Task<MemoryMaintenanceDrainResult> DrainQueuedJobsAsync(
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        if (_jobStore is null)
        {
            return new MemoryMaintenanceDrainResult(0, 0, 0);
        }

        var jobs = await _jobStore.ClaimAsync(
            Math.Clamp(limit ?? 10, 1, 50),
            TimeSpan.FromMinutes(10),
            cancellationToken);
        var completed = 0;
        var failed = 0;
        foreach (var job in jobs)
        {
            try
            {
                await RunQueuedJobAsync(job, cancellationToken);
                await _jobStore.CompleteAsync(job.Id, cancellationToken);
                completed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await _jobStore.FailAsync(job.Id, ex.Message, MaxMaintenanceJobAttempts, cancellationToken);
                failed++;
            }
        }

        return new MemoryMaintenanceDrainResult(jobs.Count, completed, failed);
    }

    public async Task<IReadOnlyList<MemoryItem>> MaintainTranslationCandidatesAsync(
        string profileId,
        string sessionId,
        string sourceLanguage,
        string targetLanguage,
        string mode,
        CancellationToken cancellationToken = default)
    {
        if (!_options.AutoExtractionEnabled || string.IsNullOrWhiteSpace(sessionId))
        {
            return [];
        }

        var threshold = Math.Clamp(_options.AutoTranslationCandidateEventThreshold, 2, 100);
        var maxEvents = Math.Clamp(_options.AutoTranslationCandidateMaxEvents, threshold, 500);
        var events = await _eventStore.ListSessionSuccessEventsAsync(
            profileId,
            sessionId,
            sourceLanguage,
            targetLanguage,
            mode,
            maxEvents,
            cancellationToken);

        var selected = events
            .Where(item => IsExtractableEvent(item, profileId, sessionId, sourceLanguage, targetLanguage, mode))
            .OrderBy(item => item.CreatedAt)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
        if (selected.Length < threshold)
        {
            return [];
        }

        var maintained = new List<MemoryItem>();
        foreach (var candidate in BuildTranslationCandidates(selected, threshold).Concat(BuildTermCandidates(selected, threshold)))
        {
            if (await HandleBlockingMemoryAsync(
                    profileId,
                    sourceLanguage,
                    targetLanguage,
                    candidate,
                    cancellationToken))
            {
                continue;
            }

            maintained.Add(await _memoryStore.AddOrUpdateAsync(
                new AutoExtractedMemoryUpsert
                {
                    Profile = profileId,
                    MemoryKind = candidate.MemoryKind,
                    Source = sourceLanguage,
                    Target = targetLanguage,
                    SourceText = candidate.SourceText,
                    TargetText = candidate.TargetText,
                    Note = candidate.Note,
                    Priority = candidate.Priority,
                    Confidence = ConfidenceFor(candidate.ObservationCount, threshold),
                    Origin = "auto-extracted",
                    TrustLevel = RagSecurityPolicy.LocalGenerated,
                    SourceUri = candidate.SourceUri,
                    CreatedBy = ExtractorName,
                    Visibility = "profile",
                    AcknowledgeSecurityFlags = true,
                    SourceEventIds = candidate.SourceEventIds,
                    ObservationCount = candidate.ObservationCount,
                    CreatedFrom = candidate.CreatedFrom,
                    SourceTable = candidate.SourceTable,
                    Extractor = ExtractorName
                },
                cancellationToken));
        }

        return maintained;
    }

    public async Task<IReadOnlyList<MemoryItem>> MaintainOcrCorrectionCandidatesAsync(
        string profileId,
        string sourceLanguage,
        string targetLanguage,
        string ocrEventId,
        IReadOnlyList<AppliedOcrCorrection> appliedCorrections,
        CancellationToken cancellationToken = default)
    {
        if (!_options.AutoExtractionEnabled ||
            _ocrMemoryStore is null ||
            string.IsNullOrWhiteSpace(ocrEventId) ||
            appliedCorrections.Count == 0)
        {
            return [];
        }

        var threshold = Math.Clamp(_options.AutoOcrCorrectionCandidateUseThreshold, 2, 100);
        var appliedIds = appliedCorrections
            .Select(item => item.CorrectionId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (appliedIds.Count == 0)
        {
            return [];
        }

        var corrections = await _ocrMemoryStore.ListCorrectionsAsync(
            profileId,
            sourceLanguage,
            limit: 500,
            activeOnly: true,
            cancellationToken);

        var maintained = new List<MemoryItem>();
        foreach (var correction in corrections.Where(item => appliedIds.Contains(item.Id)))
        {
            if (correction.UseCount < threshold ||
                !IsBoundedText(correction.WrongText, MaxTermCharacters) ||
                !IsBoundedText(correction.CorrectedText, MaxTermCharacters) ||
                string.Equals(NormalizeKey(correction.WrongText), NormalizeKey(correction.CorrectedText), StringComparison.Ordinal))
            {
                continue;
            }

            var candidate = new AutoMemoryCandidate(
                "ocr_correction",
                correction.WrongText,
                correction.CorrectedText,
                [ocrEventId],
                correction.UseCount,
                "ocr_events",
                $"events://ocr/{ocrEventId}",
                "auto-ocr-correction-memory",
                $"Auto OCR correction candidate after {correction.UseCount} applied correction uses.",
                -25);
            if (await HandleBlockingMemoryAsync(
                    profileId,
                    sourceLanguage,
                    targetLanguage,
                    candidate,
                    cancellationToken))
            {
                continue;
            }

            maintained.Add(await _memoryStore.AddOrUpdateAsync(
                new AutoExtractedMemoryUpsert
                {
                    Profile = profileId,
                    MemoryKind = candidate.MemoryKind,
                    Source = sourceLanguage,
                    Target = targetLanguage,
                    SourceText = candidate.SourceText,
                    TargetText = candidate.TargetText,
                    Note = candidate.Note,
                    Priority = candidate.Priority,
                    Confidence = ConfidenceFor(candidate.ObservationCount, threshold),
                    Origin = "auto-extracted",
                    TrustLevel = RagSecurityPolicy.LocalGenerated,
                    SourceUri = candidate.SourceUri,
                    CreatedBy = ExtractorName,
                    Visibility = "profile",
                    AcknowledgeSecurityFlags = true,
                    SourceEventIds = candidate.SourceEventIds,
                    ObservationCount = candidate.ObservationCount,
                    CreatedFrom = candidate.CreatedFrom,
                    SourceTable = candidate.SourceTable,
                    Extractor = ExtractorName
                },
                cancellationToken));
        }

        return maintained;
    }

    public async Task<MemoryEmbeddingMaintenanceResult> MaintainEmbeddingsAsync(
        string profileId,
        string sourceLanguage,
        string targetLanguage,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        if (!_options.SemanticRetrievalEnabled ||
            _embeddingProvider is null ||
            string.IsNullOrWhiteSpace(profileId) ||
            string.IsNullOrWhiteSpace(sourceLanguage) ||
            string.IsNullOrWhiteSpace(targetLanguage))
        {
            return new MemoryEmbeddingMaintenanceResult(
                profileId,
                sourceLanguage,
                targetLanguage,
                _embeddingProvider?.Model ?? string.Empty,
                CandidateCount: 0,
                CreatedCount: 0,
                UpdatedCount: 0,
                CurrentCount: 0,
                SkippedCount: 0);
        }

        var maxItems = Math.Clamp(limit ?? _options.EmbeddingMaintenanceBatchSize, 1, 500);
        var candidates = await ListEmbeddingCandidatesAsync(
            profileId,
            sourceLanguage,
            targetLanguage,
            maxItems,
            cancellationToken);
        if (candidates.Count == 0)
        {
            return new MemoryEmbeddingMaintenanceResult(
                profileId,
                sourceLanguage,
                targetLanguage,
                _embeddingProvider.Model,
                CandidateCount: 0,
                CreatedCount: 0,
                UpdatedCount: 0,
                CurrentCount: 0,
                SkippedCount: 0);
        }

        var existing = await _memoryStore.ListEmbeddingsAsync(
            candidates.Select(item => item.Id).ToArray(),
            _embeddingProvider.Model,
            cancellationToken);
        var existingById = existing.ToDictionary(item => item.MemoryId, StringComparer.Ordinal);
        var created = 0;
        var updated = 0;
        var current = 0;
        var skipped = 0;

        foreach (var item in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var contentHash = MemoryEmbeddingText.CreateContentHash(item);
            if (existingById.TryGetValue(item.Id, out var embedding) &&
                embedding.ContentHash == contentHash &&
                embedding.Dimensions == _embeddingProvider.Dimensions)
            {
                current++;
                continue;
            }

            var vector = await _embeddingProvider.EmbedAsync(
                MemoryEmbeddingText.CreateText(item),
                cancellationToken);
            if (vector.Length != _embeddingProvider.Dimensions)
            {
                skipped++;
                continue;
            }

            await _memoryStore.UpsertEmbeddingAsync(
                new MemoryEmbedding(
                    item.Id,
                    _embeddingProvider.Model,
                    _embeddingProvider.Dimensions,
                    vector,
                    contentHash,
                    DateTimeOffset.UtcNow),
                cancellationToken);

            if (embedding is null)
            {
                created++;
            }
            else
            {
                updated++;
            }
        }

        return new MemoryEmbeddingMaintenanceResult(
            profileId,
            sourceLanguage,
            targetLanguage,
            _embeddingProvider.Model,
            candidates.Count,
            created,
            updated,
            current,
            skipped);
    }

    private async Task RunQueuedJobAsync(
        MemoryMaintenanceJob job,
        CancellationToken cancellationToken)
    {
        if (string.Equals(job.JobKind, TranslationCandidatesJobKind, StringComparison.Ordinal))
        {
            await MaintainTranslationCandidatesAsync(
                job.ProfileId,
                job.SessionId,
                job.SourceLanguage,
                job.TargetLanguage,
                job.Mode,
                cancellationToken);
            return;
        }

        if (string.Equals(job.JobKind, EmbeddingPrewarmJobKind, StringComparison.Ordinal))
        {
            await MaintainEmbeddingsAsync(
                job.ProfileId,
                job.SourceLanguage,
                job.TargetLanguage,
                cancellationToken: cancellationToken);
            return;
        }

        throw new InvalidOperationException($"Unknown memory maintenance job kind '{job.JobKind}'.");
    }

    private static IReadOnlyList<AutoMemoryCandidate> BuildTranslationCandidates(
        IReadOnlyList<TranslationEvent> events,
        int threshold)
    {
        var candidates = new List<AutoMemoryCandidate>();
        foreach (var sourceGroup in events.GroupBy(item => NormalizeKey(item.SourceText), StringComparer.Ordinal))
        {
            var stableTargetGroups = sourceGroup
                .GroupBy(item => NormalizeKey(item.TranslatedText), StringComparer.Ordinal)
                .Where(group => group.Count() >= threshold)
                .Select(group => group
                    .OrderBy(item => item.CreatedAt)
                    .ThenBy(item => item.Id, StringComparer.Ordinal)
                    .ToArray())
                .ToArray();
            if (stableTargetGroups.Length != 1)
            {
                continue;
            }

            var selected = stableTargetGroups[0];
            candidates.Add(new AutoMemoryCandidate(
                "translation",
                selected[^1].SourceText.Trim(),
                selected[^1].TranslatedText.Trim(),
                selected.Select(item => item.Id).ToArray(),
                selected.Length,
                "translation_events",
                $"events://translation/{selected[0].Id}..{selected[^1].Id}",
                "auto-translation-memory",
                $"Auto candidate from {selected.Length} repeated successful translations.",
                -50));
        }

        return candidates;
    }

    private static IReadOnlyList<AutoMemoryCandidate> BuildTermCandidates(
        IReadOnlyList<TranslationEvent> events,
        int threshold)
    {
        var observations = new Dictionary<string, List<TranslationEvent>>(StringComparer.Ordinal);
        foreach (var item in events)
        {
            foreach (var term in ExtractRetainedTerms(item.SourceText, item.TranslatedText).Distinct(StringComparer.Ordinal))
            {
                if (!observations.TryGetValue(term, out var termEvents))
                {
                    termEvents = [];
                    observations[term] = termEvents;
                }

                termEvents.Add(item);
            }
        }

        return observations
            .Where(item => item.Value.Count >= threshold)
            .Select(item =>
            {
                var termEvents = item.Value
                    .OrderBy(entry => entry.CreatedAt)
                    .ThenBy(entry => entry.Id, StringComparer.Ordinal)
                    .ToArray();
                return new AutoMemoryCandidate(
                    "term",
                    item.Key,
                    item.Key,
                    termEvents.Select(entry => entry.Id).ToArray(),
                    termEvents.Length,
                    "translation_events",
                    $"events://translation/{termEvents[0].Id}..{termEvents[^1].Id}",
                    "auto-term-memory",
                    $"Auto term candidate from {termEvents.Length} retained occurrences.",
                    -25);
            })
            .ToArray();
    }

    private async Task<IReadOnlyList<MemoryItem>> ListEmbeddingCandidatesAsync(
        string profileId,
        string sourceLanguage,
        string targetLanguage,
        int maxItems,
        CancellationToken cancellationToken)
    {
        var candidates = new List<MemoryItem>();
        foreach (var memoryKind in EmbeddableMemoryKinds)
        {
            foreach (var trustLevel in EmbeddableTrustLevels)
            {
                var remaining = maxItems - candidates.Count;
                if (remaining <= 0)
                {
                    return candidates;
                }

                var items = await _memoryStore.ListAsync(
                    profileId,
                    memoryKind,
                    remaining,
                    activeOnly: true,
                    trustLevel: trustLevel,
                    sourceLanguage: sourceLanguage,
                    targetLanguage: targetLanguage,
                    cancellationToken: cancellationToken);
                candidates.AddRange(items.Where(item => _options.SharedMemoryEnabled || item.Visibility != "shared"));
            }
        }

        return candidates;
    }

    private async Task<bool> HandleBlockingMemoryAsync(
        string profileId,
        string sourceLanguage,
        string targetLanguage,
        AutoMemoryCandidate candidate,
        CancellationToken cancellationToken)
    {
        var matches = await _memoryStore.ListAsync(
            profileId,
            candidate.MemoryKind,
            limit: 50,
            activeOnly: false,
            sourceLanguage: sourceLanguage,
            targetLanguage: targetLanguage,
            query: candidate.SourceText,
            cancellationToken: cancellationToken);

        var exactMatches = matches
            .Where(item => string.Equals(NormalizeKey(item.SourceText), candidate.SourceKey, StringComparison.Ordinal))
            .ToArray();
        if (exactMatches.Any(item => item.TrustLevel is RagSecurityPolicy.UserVerified or RagSecurityPolicy.TrustedImport))
        {
            return true;
        }

        var activeConflicts = exactMatches
            .Where(item =>
                item.IsActive &&
                !string.Equals(NormalizeKey(item.TargetText), candidate.TargetKey, StringComparison.Ordinal))
            .ToArray();
        foreach (var conflict in activeConflicts.Where(item => item.TrustLevel == RagSecurityPolicy.LocalGenerated))
        {
            await DecayGeneratedConflictAsync(conflict, cancellationToken);
        }

        return activeConflicts.Any(item =>
            item.IsActive &&
            !string.Equals(NormalizeKey(item.TargetText), candidate.TargetKey, StringComparison.Ordinal));
    }

    private async Task DecayGeneratedConflictAsync(
        MemoryItem conflict,
        CancellationToken cancellationToken)
    {
        var decayedConfidence = Math.Clamp(conflict.Confidence * 0.5, 0.05, 0.8);
        if (decayedConfidence >= conflict.Confidence)
        {
            return;
        }

        await _memoryStore.UpdateAsync(
            conflict.Id,
            new MemoryUpdateRequest { Confidence = decayedConfidence },
            cancellationToken);
    }

    private static bool IsExtractableEvent(
        TranslationEvent item,
        string profileId,
        string sessionId,
        string sourceLanguage,
        string targetLanguage,
        string mode)
        => string.Equals(item.ProfileId, profileId, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(item.SessionId, sessionId, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(item.SourceLanguage, sourceLanguage, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(item.TargetLanguage, targetLanguage, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(item.Mode, mode, StringComparison.OrdinalIgnoreCase) &&
           item.ErrorCode == "0" &&
           !item.Engine.StartsWith("memory:", StringComparison.OrdinalIgnoreCase) &&
           IsBoundedText(item.SourceText, MaxSourceCharacters) &&
           IsBoundedText(item.TranslatedText, MaxTargetCharacters);

    private static bool IsBoundedText(string value, int maxCharacters)
    {
        var trimmed = value.Trim();
        return trimmed.Length >= 2 && trimmed.Length <= maxCharacters;
    }

    private static IEnumerable<string> ExtractRetainedTerms(string sourceText, string translatedText)
    {
        foreach (var match in RetainedTitleTermRegex.Matches(sourceText).Cast<Match>())
        {
            foreach (var term in ExpandTitleTerm(match.Value))
            {
                if (term.Length > MaxTermCharacters ||
                    !translatedText.Contains(term, StringComparison.Ordinal))
                {
                    continue;
                }

                yield return term;
            }
        }
    }

    private static IEnumerable<string> ExpandTitleTerm(string value)
    {
        var words = value
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeKey)
            .Where(word => word.Length > 0)
            .ToArray();
        for (var start = 0; start < words.Length - 1; start++)
        {
            var maxLength = Math.Min(4, words.Length - start);
            for (var length = 2; length <= maxLength; length++)
            {
                yield return string.Join(' ', words.Skip(start).Take(length));
            }
        }
    }

    private double ConfidenceFor(int observationCount, int threshold)
    {
        var configured = Math.Clamp(_options.AutoTranslationCandidateConfidence, 0.05, 0.8);
        var baseConfidence = observationCount >= threshold ? configured : Math.Min(configured, 0.3);
        var extra = Math.Max(0, observationCount - threshold) * 0.05;
        return Math.Clamp(baseConfidence + Math.Min(extra, 0.2), 0.05, 0.8);
    }

    private static string NormalizeKey(string text)
        => text.Normalize(NormalizationForm.FormKC).Trim();

    private sealed record AutoMemoryCandidate(
        string MemoryKind,
        string SourceText,
        string TargetText,
        IReadOnlyList<string> SourceEventIds,
        int ObservationCount,
        string SourceTable,
        string SourceUri,
        string CreatedFrom,
        string Note,
        int Priority)
    {
        public string SourceKey { get; } = NormalizeKey(SourceText);
        public string TargetKey { get; } = NormalizeKey(TargetText);
    }
}
