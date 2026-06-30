using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Verbeam.Core.Models;
using Verbeam.Core.Options;
using Verbeam.Core.Providers;
using Verbeam.Core.Storage;

namespace Verbeam.Core.Services;

public sealed class TranslationService : IDisposable, IAsyncDisposable
{
    private readonly VerbeamOptions _options;
    private readonly TranslationProviderRegistry _providers;
    private readonly PromptPresetStore _presets;
    private readonly GlossaryStore _glossaries;
    private readonly GameScopedStores _gameStores;
    private readonly GameScopedServices _gameServices;
    private readonly ContextCompressionService _contextCompression;
    private readonly TranslationEventBatcher? _eventBatcher;
    private readonly RealtimeTemplateCache? _templateCache;
    private readonly RealtimeContextWindow? _contextWindow;
    private readonly object _memoryMaintenanceTasksGate = new();
    private readonly List<Task> _memoryMaintenanceTasks = [];

    public TranslationService(
        VerbeamOptions options,
        TranslationProviderRegistry providers,
        PromptPresetStore presets,
        GlossaryStore glossaries,
        GameScopedStores gameStores,
        GameScopedServices gameServices,
        ContextCompressionService contextCompression,
        TranslationEventBatcher? eventBatcher = null,
        RealtimeTemplateCache? templateCache = null,
        RealtimeContextWindow? contextWindow = null)
    {
        _eventBatcher = eventBatcher;
        _templateCache = templateCache;
        _contextWindow = contextWindow;
        _options = options;
        _providers = providers;
        _presets = presets;
        _glossaries = glossaries;
        _gameStores = gameStores;
        _gameServices = gameServices;
        _contextCompression = contextCompression;
    }

    public void Dispose()
    {
        DrainMemoryMaintenanceTasksAsync().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        await DrainMemoryMaintenanceTasksAsync();
    }

    public async Task<TranslationOutcome> TranslateAsync(
        MortTranslateRequest request,
        CancellationToken cancellationToken = default)
        => await TranslateAsync(request, onDelta: null, cancellationToken);

    /// <summary>
    /// As <see cref="TranslateAsync(MortTranslateRequest, CancellationToken)"/>, but when
    /// <paramref name="onDelta"/> is provided AND the request falls through to the LLM,
    /// each generated token is streamed via the callback before the final outcome returns.
    /// Instant resolvers (cache, OpenCC, glossary, template, same-language) do not stream —
    /// they have nothing incremental to emit.
    /// </summary>
    public async Task<TranslationOutcome> TranslateAsync(
        MortTranslateRequest request,
        Func<string, Task>? onDelta,
        CancellationToken cancellationToken = default)
    {
        // Realtime chat lines: pass the leading "ID:" token through verbatim (small
        // models tend to mangle player IDs) and translate only the body, so the same
        // message from different senders shares one cache entry.
        if (request.Realtime == true &&
            RealtimeTemplateCache.TrySplitChatPrefix(request.Text ?? string.Empty, out var chatPrefix, out var chatBody))
        {
            var bodyOutcome = await TranslateCoreAsync(request with { Text = chatBody }, onDelta, cancellationToken);
            return bodyOutcome.IsSuccess
                ? bodyOutcome with { Text = chatPrefix + bodyOutcome.Text }
                : bodyOutcome with { Text = request.Text ?? string.Empty };
        }

        return await TranslateMaybeChunkedAsync(request, onDelta, cancellationToken);
    }

    /// <summary>
    /// Long source text that would overrun the model's context window (silently truncating
    /// the prompt) or its output-token cap (truncating the translation) is split on
    /// paragraph/sentence boundaries and translated chunk-by-chunk through the normal
    /// pipeline, then re-stitched with the original separators. Short text and realtime
    /// subtitle lines bypass this and translate in a single pass.
    /// </summary>
    private async Task<TranslationOutcome> TranslateMaybeChunkedAsync(
        MortTranslateRequest request,
        Func<string, Task>? onDelta,
        CancellationToken cancellationToken)
    {
        var text = request.Text ?? string.Empty;
        var chunking = _options.Chunking;
        if (request.Realtime == true ||
            !chunking.Enabled ||
            chunking.MaxCharactersPerChunk <= 0 ||
            text.Length <= chunking.MaxCharactersPerChunk)
        {
            return await TranslateCoreAsync(request, onDelta, cancellationToken);
        }

        var segments = TextChunker.Split(text, chunking.MaxCharactersPerChunk);
        if (segments.Count <= 1)
        {
            return await TranslateCoreAsync(request, onDelta, cancellationToken);
        }

        var maxConcurrency = Math.Clamp(
            chunking.MaxConcurrency,
            1,
            Math.Min(16, segments.Count));
        if (onDelta is not null || maxConcurrency <= 1)
        {
            return await TranslateChunkedSequentialAsync(request, onDelta, segments, cancellationToken);
        }

        return await TranslateChunkedParallelAsync(request, segments, maxConcurrency, cancellationToken);
    }

    private async Task<TranslationOutcome> TranslateChunkedSequentialAsync(
        MortTranslateRequest request,
        Func<string, Task>? onDelta,
        IReadOnlyList<TextSegment> segments,
        CancellationToken cancellationToken)
    {
        var text = request.Text ?? string.Empty;
        var builder = new StringBuilder(text.Length);
        long totalLatencyMs = 0;
        var allCacheHit = true;
        var translatedAny = false;
        string lastEngine = "none";
        var tokenUsages = new List<TokenUsage>();
        var childTraces = new List<TranslationPerformanceTrace>();
        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            if (!string.IsNullOrWhiteSpace(segment.Content))
            {
                var chunkEmittedDelta = false;
                Func<string, Task>? chunkDelta = null;
                if (onDelta is not null)
                {
                    chunkDelta = delta =>
                    {
                        chunkEmittedDelta = true;
                        return onDelta(delta);
                    };
                }

                var chunkOutcome = await TranslateCoreAsync(
                    request with { Text = segment.Content, ChunkId = index.ToString() }, chunkDelta, cancellationToken);
                if (!chunkOutcome.IsSuccess)
                {
                    // Surface the first failure without replacing the whole document
                    // with only the failed chunk's fallback text.
                    return BuildChunkFailure(text, index, segments.Count, chunkOutcome);
                }

                builder.Append(chunkOutcome.Text);
                if (onDelta is not null && !chunkEmittedDelta && chunkOutcome.Text.Length > 0)
                {
                    await onDelta(chunkOutcome.Text);
                }
                totalLatencyMs += chunkOutcome.LatencyMs;
                allCacheHit &= chunkOutcome.CacheHit;
                lastEngine = chunkOutcome.Engine;
                translatedAny = true;
                if (chunkOutcome.TokenUsage is not null)
                {
                    tokenUsages.Add(chunkOutcome.TokenUsage);
                }
                if (chunkOutcome.PerformanceTrace is not null)
                {
                    childTraces.Add(chunkOutcome.PerformanceTrace);
                }
            }
            else if (segment.Content.Length > 0)
            {
                // Whitespace-only content (rare): keep it verbatim, do not call the model.
                builder.Append(segment.Content);
            }

            if (segment.Separator.Length > 0)
            {
                builder.Append(segment.Separator);
                if (onDelta is not null)
                {
                    await onDelta(segment.Separator);
                }
            }
        }

        if (!translatedAny)
        {
            return await TranslateCoreAsync(request, onDelta, cancellationToken);
        }

        return TranslationOutcome.Success(
            builder.ToString(),
            $"chunked:{lastEngine}",
            totalLatencyMs,
            cacheHit: allCacheHit,
            tokenUsage: MergeTokenUsage(tokenUsages, "chunked"),
            performanceTrace: TranslationTraceBuilder.CompleteChunked(
                request,
                text.Length,
                $"chunked:{lastEngine}",
                totalLatencyMs,
                allCacheHit,
                segments.Count,
                maxConcurrency: 1,
                childTraces));
    }

    private async Task<TranslationOutcome> TranslateChunkedParallelAsync(
        MortTranslateRequest request,
        IReadOnlyList<TextSegment> segments,
        int maxConcurrency,
        CancellationToken cancellationToken)
    {
        var text = request.Text ?? string.Empty;
        var outcomes = new TranslationOutcome?[segments.Count];
        var tasks = new List<Task>(segments.Count);
        using var gate = new SemaphoreSlim(maxConcurrency);
        var stopwatch = Stopwatch.StartNew();

        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            if (string.IsNullOrWhiteSpace(segment.Content))
            {
                continue;
            }

            var segmentIndex = index;
            tasks.Add(TranslateChunkAsync(segmentIndex, segment.Content));
        }

        if (tasks.Count == 0)
        {
            return await TranslateCoreAsync(request, onDelta: null, cancellationToken);
        }

        await Task.WhenAll(tasks);
        stopwatch.Stop();

        var builder = new StringBuilder(text.Length);
        var allCacheHit = true;
        var translatedAny = false;
        string lastEngine = "none";
        var tokenUsages = new List<TokenUsage>();
        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            if (!string.IsNullOrWhiteSpace(segment.Content))
            {
                var chunkOutcome = outcomes[index];
                if (chunkOutcome is null)
                {
                    return TranslationOutcome.Failure(
                        text,
                        $"Chunk {index + 1}/{segments.Count} did not complete.",
                        "chunk_failed");
                }

                if (!chunkOutcome.IsSuccess)
                {
                    return BuildChunkFailure(text, index, segments.Count, chunkOutcome);
                }

                builder.Append(chunkOutcome.Text);
                allCacheHit &= chunkOutcome.CacheHit;
                lastEngine = chunkOutcome.Engine;
                translatedAny = true;
                if (chunkOutcome.TokenUsage is not null)
                {
                    tokenUsages.Add(chunkOutcome.TokenUsage);
                }
            }
            else if (segment.Content.Length > 0)
            {
                builder.Append(segment.Content);
            }

            if (segment.Separator.Length > 0)
            {
                builder.Append(segment.Separator);
            }
        }

        if (!translatedAny)
        {
            return await TranslateCoreAsync(request, onDelta: null, cancellationToken);
        }

        return TranslationOutcome.Success(
            builder.ToString(),
            $"chunked-parallel:{lastEngine}",
            stopwatch.ElapsedMilliseconds,
            cacheHit: allCacheHit,
            tokenUsage: MergeTokenUsage(tokenUsages, "chunked-parallel"),
            performanceTrace: TranslationTraceBuilder.CompleteChunked(
                request,
                text.Length,
                $"chunked-parallel:{lastEngine}",
                stopwatch.ElapsedMilliseconds,
                allCacheHit,
                segments.Count,
                maxConcurrency,
                outcomes
                    .Where(outcome => outcome?.PerformanceTrace is not null)
                    .Select(outcome => outcome!.PerformanceTrace!)));

        async Task TranslateChunkAsync(int index, string content)
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                outcomes[index] = await TranslateCoreAsync(
                    request with { Text = content, ChunkId = index.ToString() },
                    onDelta: null,
                    cancellationToken);
            }
            finally
            {
                gate.Release();
            }
        }
    }

    private static TranslationOutcome BuildChunkFailure(
        string originalText,
        int index,
        int total,
        TranslationOutcome chunkOutcome)
    {
        var errorCode = string.IsNullOrWhiteSpace(chunkOutcome.ErrorCode)
            ? "chunk_failed"
            : chunkOutcome.ErrorCode;
        var message = string.IsNullOrWhiteSpace(chunkOutcome.ErrorMessage)
            ? "Chunk translation failed."
            : chunkOutcome.ErrorMessage;
        return TranslationOutcome.Failure(
            originalText,
            $"Chunk {index + 1}/{total} failed: {message}",
            errorCode,
            chunkOutcome.PerformanceTrace);
    }

    private async Task<TranslationOutcome> TranslateCoreAsync(
        MortTranslateRequest request,
        Func<string, Task>? onDelta,
        CancellationToken cancellationToken)
    {
        var text = request.Text ?? string.Empty;
        var trace = TranslationTraceBuilder.Start(request, text);
        var source = Pick(request.Source, _options.DefaultSource);
        var target = Pick(request.Target, _options.DefaultTarget);
        ScriptDetectionResult? autoDetection = null;
        if (string.Equals(source, LanguageRegistry.Auto, StringComparison.OrdinalIgnoreCase))
        {
            // Resolve "auto" before memory lookup and cache key creation so neither is
            // keyed on the literal "auto"; OCR callers usually pre-resolve from the
            // detected block language, this covers direct /translate calls.
            autoDetection = trace.Measure("language.detect", () => UnicodeScriptDetector.Detect(text));
            source = string.IsNullOrEmpty(autoDetection.DetectedLanguage)
                ? LanguageRegistry.IsAuto(_options.DefaultSource) ? "ja" : _options.DefaultSource
                : LanguageRegistry.ToTranslationCode(autoDetection.DetectedLanguage);
        }
        var mode = Pick(request.Mode, _options.DefaultMode);
        var providerName = Pick(request.Provider, _options.DefaultProvider);
        var profileId = Pick(request.Profile, "default");
        var sessionId = Pick(request.SessionId, string.Empty);
        var principalId = Pick(request.PrincipalId, "local");
        var allowSharedMemory = _options.Memory.SharedMemoryEnabled && request.AllowSharedMemory == true;
        var realtime = request.Realtime == true;
        var skipMemoryContext = request.SkipMemoryContext == true;
        var model = string.Empty;
        var glossaryHash = string.Empty;
        var key = string.Empty;

        if (IsBlankForMort(text))
        {
            await RecordEventAsync(
                request,
                sessionId,
                profileId,
                translationKey: null,
                text,
                translatedText: string.Empty,
                source,
                target,
                mode,
                providerName,
                glossaryHash: string.Empty,
                engine: "none",
                model: string.Empty,
                latencyMs: 0,
                cacheHit: false,
                errorCode: "0",
                errorMessage: string.Empty,
                cancellationToken);
            return TranslationOutcome.Success(
                string.Empty,
                "none",
                0,
                cacheHit: false,
                tokenUsage: TokenUsage.Zero("none"),
                performanceTrace: trace.Complete(providerName, model, "none", cacheHit: false, latencyMs: 0));
        }

        try
        {
            if (!skipMemoryContext)
            {
                var memoryStore = await _gameStores.MemoryFor(profileId, cancellationToken);
                var memory = await trace.MeasureAsync("memory.exact_lookup", () => memoryStore.FindExactAsync(
                    profileId,
                    "translation",
                    source,
                    target,
                    text,
                    cancellationToken,
                    includeShared: allowSharedMemory));
                if (memory is not null)
                {
                    var memoryEngine = $"memory:{memory.TrustLevel.Replace('_', '-')}";
                    var memoryOutputPolicy = OutputPolicyValidator.Validate(memory.TargetText);
                    if (!memoryOutputPolicy.IsValid)
                    {
                        var failedEventId = await RecordEventAsync(
                            request,
                            sessionId,
                            profileId,
                            translationKey: null,
                            text,
                            translatedText: string.Empty,
                            source,
                            target,
                            mode,
                            providerName,
                            glossaryHash: string.Empty,
                            engine: memoryEngine,
                            model: string.Empty,
                            latencyMs: 0,
                            cacheHit: false,
                            errorCode: memoryOutputPolicy.ErrorCode,
                            errorMessage: memoryOutputPolicy.ErrorMessage,
                            cancellationToken);
                        await RecordExactMemoryAuditAsync(
                            memory,
                            failedEventId,
                            principalId,
                            profileId,
                            sessionId,
                            translationKey: null,
                            decision: "blocked",
                            reason: memoryOutputPolicy.ErrorCode,
                            cancellationToken);
                        return TranslationOutcome.Failure(
                            text,
                            memoryOutputPolicy.ErrorMessage,
                            memoryOutputPolicy.ErrorCode,
                            trace.Complete(providerName, model, memoryEngine, cacheHit: false, latencyMs: 0));
                    }

                    await memoryStore.RecordUseAsync([memory.Id], cancellationToken);
                    var memoryEventId = await RecordEventAsync(
                        request,
                        sessionId,
                        profileId,
                        translationKey: null,
                        text,
                        memory.TargetText,
                        source,
                        target,
                        mode,
                        providerName,
                        glossaryHash: string.Empty,
                        engine: memoryEngine,
                        model: string.Empty,
                        latencyMs: 0,
                        cacheHit: false,
                        errorCode: "0",
                        errorMessage: string.Empty,
                        cancellationToken);
                    await RecordExactMemoryAuditAsync(
                        memory,
                        memoryEventId,
                        principalId,
                        profileId,
                        sessionId,
                        translationKey: null,
                        decision: "used",
                        reason: "exact_memory_override",
                        cancellationToken);
                    if (!realtime && !skipMemoryContext)
                    {
                        await MaintainSuccessfulTranslationAsync(profileId, sessionId, source, target, mode, cancellationToken);
                    }

                    return TranslationOutcome.Success(
                        memory.TargetText,
                        memoryEngine,
                        0,
                        cacheHit: false,
                        tokenUsage: TokenUsage.Zero("memory"),
                        performanceTrace: trace.Complete(providerName, model, memoryEngine, cacheHit: false, latencyMs: 0));
                }
            }

            var provider = trace.Measure("provider.resolve", () => _providers.GetRequired(providerName));
            var defaultModel = provider.Descriptor.Name.Equals("ollama", StringComparison.OrdinalIgnoreCase)
                ? _options.Ollama.Model
                : provider.Descriptor.DefaultModel;
            model = Pick(request.Model, defaultModel);
            var compressedContext = trace.Measure("context.compress", () => _contextCompression.Compress(GetContextPieces(request)));
            // Realtime subtitle calls skip RAG retrieval: per-line memory search, semantic
            // scoring, and trim re-renders cost more than they add for short subtitle lines.
            MemoryContext memoryContext;
            if (realtime || skipMemoryContext)
            {
                memoryContext = MemoryContext.Empty;
            }
            else
            {
                var contextBuilder = await _gameServices.ContextBuilderFor(profileId, cancellationToken);
                memoryContext = await trace.MeasureAsync("memory.context_build", () => contextBuilder.BuildAsync(
                    new MemoryContextRequest(
                        profileId,
                        sessionId,
                        source,
                        target,
                        mode,
                        text,
                        allowSharedMemory),
                    cancellationToken));
            }
            var contextHash = CombineContextHashes(compressedContext.Hash, memoryContext.Hash);

            var preset = await trace.MeasureAsync("preset.load", () => _presets.GetRequiredAsync(mode, cancellationToken));
            var glossary = await trace.MeasureAsync("glossary.load", () => _glossaries.GetOptionalAsync(request.Glossary, cancellationToken));
            glossaryHash = glossary.Hash;

            // Deterministic term map: when the whole (normalized) text is exactly a
            // glossary term — e.g. an OCR label like "（Compile)" — return the mapped
            // translation directly instead of asking the LLM to guess at it.
            var normalizedTerm = GlossaryStore.NormalizeTerm(text);
            if (glossary.NormalizedTerms.TryGetValue(normalizedTerm, out var mappedTerm) ||
                glossary.NormalizedTerms.TryGetValue(GlossaryStore.NormalizeTermCompact(normalizedTerm), out mappedTerm))
            {
                await RecordEventAsync(
                    request,
                    sessionId,
                    profileId,
                    translationKey: null,
                    text,
                    mappedTerm,
                    source,
                    target,
                    mode,
                    provider.Descriptor.Name,
                    glossary.Hash,
                    engine: "glossary:exact",
                    model,
                    latencyMs: 0,
                    cacheHit: false,
                    errorCode: "0",
                    errorMessage: string.Empty,
                    cancellationToken);
                return TranslationOutcome.Success(
                    mappedTerm,
                    "glossary:exact",
                    0,
                    cacheHit: false,
                    tokenUsage: TokenUsage.Zero("glossary"),
                    performanceTrace: trace.Complete(provider.Descriptor.Name, model, "glossary:exact", cacheHit: false, latencyMs: 0));
            }

            // Chinese -> Chinese variant (e.g. zh-CN -> zh-TW): the content is the
            // same language and only the script differs, so convert deterministically
            // with OpenCC instead of asking the LLM. This is faithful (only swaps
            // characters, never rephrases / drops / truncates), instant (0ms), and
            // never leaves the wrong variant behind. Already-correct or variant-neutral
            // text is a no-op (effective passthrough). Cross-language into Chinese
            // (ja/en -> zh) is NOT Chinese-source, so it still goes to the LLM below.
            // Disable via UseOpenCcVariantConversion to force zh->zh through the LLM.
            // EXCEPTION: when the caller supplied a glossary, the request wants term
            // substitution + real translation of the surrounding non-term text, so defer
            // to the full glossary + provider pipeline below rather than short-circuiting
            // the whole line through OpenCC (which would never reach the provider).
            if (_options.UseOpenCcVariantConversion && IsChineseVariant(target) && IsChineseLanguage(source) &&
                string.IsNullOrWhiteSpace(request.Glossary))
            {
                var converted = ApplyGlossaryTermReplacements(
                    ConvertChineseVariant(text, target), glossary.Terms);
                var conversionEngine = string.Equals(converted, text, StringComparison.Ordinal)
                    ? "opencc:passthrough"
                    : IsTraditionalTarget(target) ? "opencc:s2tw" : "opencc:t2s";
                await RecordEventAsync(
                    request,
                    sessionId,
                    profileId,
                    translationKey: null,
                    text,
                    converted,
                    source,
                    target,
                    mode,
                    provider.Descriptor.Name,
                    glossary.Hash,
                    engine: conversionEngine,
                    model,
                    latencyMs: 0,
                    cacheHit: false,
                    errorCode: "0",
                    errorMessage: string.Empty,
                    cancellationToken);
                return TranslationOutcome.Success(
                    converted,
                    conversionEngine,
                    0,
                    cacheHit: false,
                    tokenUsage: TokenUsage.Zero("opencc"),
                    performanceTrace: trace.Complete(provider.Descriptor.Name, model, conversionEngine, cacheHit: false, latencyMs: 0));
            }

            // Same (non-Chinese) language on both sides: there is nothing to translate.
            // Apply the deterministic glossary term replacements and return without the LLM.
            var sameLanguage = string.Equals(
                LanguageRegistry.Normalize(source),
                LanguageRegistry.Normalize(target),
                StringComparison.OrdinalIgnoreCase);
            if (sameLanguage)
            {
                var normalizedText = ApplyGlossaryTermReplacements(text, glossary.Terms);
                await RecordEventAsync(
                    request,
                    sessionId,
                    profileId,
                    translationKey: null,
                    text,
                    normalizedText,
                    source,
                    target,
                    mode,
                    provider.Descriptor.Name,
                    glossary.Hash,
                    engine: "glossary:passthrough",
                    model,
                    latencyMs: 0,
                    cacheHit: false,
                    errorCode: "0",
                    errorMessage: string.Empty,
                    cancellationToken);
                return TranslationOutcome.Success(
                    normalizedText,
                    "glossary:passthrough",
                    0,
                    cacheHit: false,
                    tokenUsage: TokenUsage.Zero("passthrough"),
                    performanceTrace: trace.Complete(provider.Descriptor.Name, model, "glossary:passthrough", cacheHit: false, latencyMs: 0));
            }

            // Realtime digit-slot templates: lines that differ only in numbers (game
            // trade spam, scoreboards) substitute the new values into a previously
            // validated translation at zero cost.
            var templateScope = string.Join("", source, target, mode, provider.Descriptor.Name, model, preset.Version, glossary.Hash);
            // Conversation scope for the rolling context window: same stream =
            // same languages/provider/model/session. Deliberately NOT part of
            // the cache key — the window shapes prompts, never keys.
            var contextScope = realtime
                ? string.Join('', source, target, mode, provider.Descriptor.Name, model, sessionId)
                : string.Empty;
            if (realtime && _templateCache is not null)
            {
                var templated = _templateCache.TryApply(text, templateScope);
                if (templated is not null)
                {
                    await RecordEventAsync(
                        request,
                        sessionId,
                        profileId,
                        translationKey: null,
                        text,
                        templated,
                        source,
                        target,
                        mode,
                        provider.Descriptor.Name,
                        glossary.Hash,
                        engine: "template:exact",
                        model,
                        latencyMs: 0,
                        cacheHit: false,
                        errorCode: "0",
                        errorMessage: string.Empty,
                        cancellationToken);
                    _contextWindow?.Append(contextScope, text, templated);
                    return TranslationOutcome.Success(
                        templated,
                        "template:exact",
                        0,
                        cacheHit: false,
                        tokenUsage: TokenUsage.Zero("template"),
                        performanceTrace: trace.Complete(provider.Descriptor.Name, model, "template:exact", cacheHit: false, latencyMs: 0));
                }
            }

            key = TranslationCacheKey.Create(
                text,
                source,
                target,
                mode,
                provider.Descriptor.Name,
                model,
                preset.Version,
                glossary.Hash,
                contextHash);

            // Per-game translation cache (gameId ≡ profileId) — isolates each game's
            // translations into its own realtime.sqlite. Resolved here, not at method
            // entry, so the template / OpenCC / same-language fast paths above never
            // create a game's cache file.
            var gameCache = await _gameStores.CacheFor(profileId, cancellationToken);
            var cached = await trace.MeasureAsync("cache.lookup", () => gameCache.GetAsync(key, cancellationToken));
            if (cached is not null)
            {
                var cachedTranslatedText = TranslationOutputCleaner.Clean(cached.TranslatedText);
                // Normalize the cached output's Chinese script too, so entries cached before this
                // pass existed (or with leaked Simplified) still deliver the requested variant.
                if (_options.UseOpenCcVariantConversion && IsChineseVariant(target))
                {
                    cachedTranslatedText = ConvertChineseVariant(cachedTranslatedText, target);
                }

                var cachedOutputPolicy = OutputPolicyValidator.Validate(cachedTranslatedText);
                if (cachedOutputPolicy.IsValid && !string.IsNullOrWhiteSpace(cachedTranslatedText))
                {
                    var cacheEventId = await RecordEventAsync(
                        request,
                        sessionId,
                        profileId,
                        key,
                        text,
                        cachedTranslatedText,
                        source,
                        target,
                        mode,
                        provider.Descriptor.Name,
                        glossary.Hash,
                        cached.Engine,
                        model,
                        cached.LatencyMs,
                        cacheHit: true,
                        errorCode: "0",
                        errorMessage: string.Empty,
                        cancellationToken);
                    await RecordMemoryContextAuditAsync(memoryContext, cacheEventId, principalId, profileId, sessionId, key, cancellationToken);
                    await RecordMemoryContextUseAsync(profileId, memoryContext, cancellationToken);
                    if (!realtime && !skipMemoryContext)
                    {
                        await MaintainSuccessfulTranslationAsync(profileId, sessionId, source, target, mode, cancellationToken);
                    }

                    if (realtime)
                    {
                        _templateCache?.TryLearn(text, cachedTranslatedText, templateScope);
                        _contextWindow?.Append(contextScope, text, cachedTranslatedText);
                    }

                    return TranslationOutcome.Success(
                        cachedTranslatedText,
                        cached.Engine,
                        cached.LatencyMs,
                        cacheHit: true,
                        tokenUsage: TokenUsage.Zero("cache"),
                        performanceTrace: trace.Complete(provider.Descriptor.Name, model, cached.Engine, cacheHit: true, latencyMs: cached.LatencyMs));
                }
            }

            // Realtime rides the (otherwise empty) memory-context slot. The cache
            // key above was computed without it: window contents never re-key.
            var realtimeContext = realtime ? _contextWindow?.BuildContext(contextScope, text) : null;
            var providerRequest = new ProviderTranslationRequest(
                text,
                source,
                target,
                mode,
                model,
                preset,
                glossary.Terms,
                compressedContext.Text,
                realtimeContext ?? memoryContext.Text);

            var stopwatch = Stopwatch.StartNew();
            ProviderTranslationResult result;
            if (onDelta is not null)
            {
                // Stream tokens to the caller as they arrive; assemble the full result
                // for the unchanged post-processing (output policy, cache, events) below.
                var streamed = new System.Text.StringBuilder();
                ProviderTranslationResult? finalChunk = null;
                var providerStageStartedAt = trace.ElapsedMilliseconds;
                await foreach (var chunk in provider.TranslateStreamAsync(providerRequest, cancellationToken))
                {
                    if (chunk.Final is not null)
                    {
                        finalChunk = chunk.Final;
                    }
                    else if (!string.IsNullOrEmpty(chunk.Delta))
                    {
                        streamed.Append(chunk.Delta);
                        await onDelta(chunk.Delta);
                    }
                }

                result = finalChunk ?? new ProviderTranslationResult(streamed.ToString(), provider.Descriptor.Name);
                trace.Add("provider.call", providerStageStartedAt, stopwatch.ElapsedMilliseconds);
            }
            else
            {
                result = await trace.MeasureAsync("provider.call", () => provider.TranslateAsync(providerRequest, cancellationToken));
            }

            stopwatch.Stop();
            var translatedText = TranslationOutputCleaner.Clean(result.Text);
            // Normalize the model output's Chinese script to the requested variant. Small MT
            // models leak Simplified into a zh-TW (Traditional) target (e.g. 还/对 instead of
            // 還/對) and vice versa; the OpenCC fast-path above only covers Chinese SOURCE, so
            // cross-language output (ja/en -> zh-*) is unconverted. OpenCC s2tw/t2s is faithful
            // (character swaps only) and a no-op when already correct, guaranteeing the delivered
            // text matches the target variant. This also normalizes the value before it is cached.
            if (_options.UseOpenCcVariantConversion && IsChineseVariant(target))
            {
                translatedText = ConvertChineseVariant(translatedText, target);
            }
            var tokenUsage = result.TokenUsage
                ?? TokenUsageEstimator.EstimateProviderRequest(
                    providerRequest,
                    translatedText,
                    $"{provider.Descriptor.Name}:estimated");

            var outputPolicy = string.IsNullOrWhiteSpace(translatedText)
                ? new OutputPolicyValidationResult(
                    IsValid: false,
                    OutputPolicyValidator.ErrorCode,
                    "translation output was empty after removing prompt template residue.")
                : OutputPolicyValidator.Validate(translatedText);
            if (!outputPolicy.IsValid)
            {
                var failedEventId = await RecordEventAsync(
                    request,
                    sessionId,
                    profileId,
                    translationKey: null,
                    text,
                    translatedText: string.Empty,
                    source,
                    target,
                    mode,
                    provider.Descriptor.Name,
                    glossary.Hash,
                    result.Engine,
                    model,
                    stopwatch.ElapsedMilliseconds,
                    cacheHit: false,
                    errorCode: outputPolicy.ErrorCode,
                    errorMessage: outputPolicy.ErrorMessage,
                    cancellationToken);
                await RecordMemoryContextAuditAsync(memoryContext, failedEventId, principalId, profileId, sessionId, translationKey: null, cancellationToken);
                return TranslationOutcome.Failure(
                    text,
                    outputPolicy.ErrorMessage,
                    outputPolicy.ErrorCode,
                    trace.Complete(
                        provider.Descriptor.Name,
                        model,
                        result.Engine,
                        cacheHit: false,
                        latencyMs: stopwatch.ElapsedMilliseconds,
                        providerTimings: result.Timings));
            }

            await trace.MeasureAsync("cache.write", () => gameCache.SetAsync(
                new CachedTranslation(
                    key,
                    text,
                    translatedText,
                    source,
                    target,
                    mode,
                    provider.Descriptor.Name,
                    result.Engine,
                    model,
                    preset.Version,
                    glossary.Hash,
                    stopwatch.ElapsedMilliseconds,
                    DateTimeOffset.UtcNow),
                cancellationToken));

            var successEventId = await trace.MeasureAsync("event.write", () => RecordEventAsync(
                request,
                sessionId,
                profileId,
                key,
                text,
                translatedText,
                source,
                target,
                mode,
                provider.Descriptor.Name,
                glossary.Hash,
                result.Engine,
                model,
                stopwatch.ElapsedMilliseconds,
                cacheHit: false,
                errorCode: "0",
                errorMessage: string.Empty,
                cancellationToken,
                tokenUsage: tokenUsage));
            await RecordMemoryContextAuditAsync(memoryContext, successEventId, principalId, profileId, sessionId, key, cancellationToken);
            await RecordMemoryContextUseAsync(profileId, memoryContext, cancellationToken);
            if (!realtime && !skipMemoryContext)
            {
                await MaintainSuccessfulTranslationAsync(profileId, sessionId, source, target, mode, cancellationToken);
            }

            if (realtime)
            {
                _templateCache?.TryLearn(text, translatedText, templateScope);
                _contextWindow?.Append(contextScope, text, translatedText);
            }

            return TranslationOutcome.Success(
                translatedText,
                result.Engine,
                stopwatch.ElapsedMilliseconds,
                cacheHit: false,
                tokenUsage: tokenUsage,
                performanceTrace: trace.Complete(
                    provider.Descriptor.Name,
                    model,
                    result.Engine,
                    cacheHit: false,
                    latencyMs: stopwatch.ElapsedMilliseconds,
                    providerTimings: result.Timings));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            await RecordEventAsync(
                request,
                sessionId,
                profileId,
                translationKey: null,
                text,
                translatedText: string.Empty,
                source,
                target,
                mode,
                providerName,
                glossaryHash,
                engine: string.Empty,
                model,
                latencyMs: 0,
                cacheHit: false,
                errorCode: "1",
                errorMessage: ex.Message,
                cancellationToken);
            throw;
        }
    }

    private sealed class TranslationTraceBuilder
    {
        private readonly MortTranslateRequest _request;
        private readonly int _textCharacters;
        private readonly Stopwatch _total = Stopwatch.StartNew();
        private readonly List<TranslationTraceStage> _stages = [];

        private TranslationTraceBuilder(MortTranslateRequest request, string text)
        {
            _request = request;
            _textCharacters = text.Length;
        }

        public long ElapsedMilliseconds => _total.ElapsedMilliseconds;

        private bool Enabled => ShouldTrace(_request);

        public static TranslationTraceBuilder Start(MortTranslateRequest request, string text)
            => new(request, text);

        public static bool ShouldTrace(MortTranslateRequest request)
            => !string.IsNullOrWhiteSpace(request.TraceId);

        public T Measure<T>(string name, Func<T> action)
        {
            if (!Enabled)
            {
                return action();
            }

            var startedAt = _total.ElapsedMilliseconds;
            var stopwatch = Stopwatch.StartNew();
            try
            {
                return action();
            }
            finally
            {
                stopwatch.Stop();
                Add(name, startedAt, stopwatch.ElapsedMilliseconds);
            }
        }

        public async Task<T> MeasureAsync<T>(string name, Func<Task<T>> action)
        {
            if (!Enabled)
            {
                return await action();
            }

            var startedAt = _total.ElapsedMilliseconds;
            var stopwatch = Stopwatch.StartNew();
            try
            {
                return await action();
            }
            finally
            {
                stopwatch.Stop();
                Add(name, startedAt, stopwatch.ElapsedMilliseconds);
            }
        }

        public async Task MeasureAsync(string name, Func<Task> action)
        {
            if (!Enabled)
            {
                await action();
                return;
            }

            var startedAt = _total.ElapsedMilliseconds;
            var stopwatch = Stopwatch.StartNew();
            try
            {
                await action();
            }
            finally
            {
                stopwatch.Stop();
                Add(name, startedAt, stopwatch.ElapsedMilliseconds);
            }
        }

        public void Add(string name, long startedOffsetMs, long durationMs)
        {
            if (!Enabled)
            {
                return;
            }

            _stages.Add(new TranslationTraceStage(
                name,
                Math.Max(0, durationMs),
                Math.Max(0, startedOffsetMs)));
        }

        public TranslationPerformanceTrace? Complete(
            string provider,
            string model,
            string engine,
            bool cacheHit,
            long latencyMs,
            IReadOnlyDictionary<string, double>? providerTimings = null,
            IReadOnlyList<TranslationPerformanceTrace>? children = null)
        {
            if (!Enabled)
            {
                return null;
            }

            _total.Stop();
            return new TranslationPerformanceTrace(
                Pick(_request.TraceId, string.Empty),
                Pick(_request.ItemId, string.Empty),
                string.IsNullOrWhiteSpace(_request.ChunkId) ? null : _request.ChunkId.Trim(),
                provider,
                model,
                engine,
                _textCharacters,
                cacheHit,
                latencyMs,
                _stages.ToArray(),
                SanitizeTimings(providerTimings),
                BuildClientUnixMs(_request),
                children ?? Array.Empty<TranslationPerformanceTrace>());
        }

        public static TranslationPerformanceTrace? CompleteChunked(
            MortTranslateRequest request,
            int textCharacters,
            string engine,
            long latencyMs,
            bool cacheHit,
            int segmentCount,
            int maxConcurrency,
            IEnumerable<TranslationPerformanceTrace> children)
        {
            if (!ShouldTrace(request))
            {
                return null;
            }

            var childList = children.ToArray();
            return new TranslationPerformanceTrace(
                Pick(request.TraceId, string.Empty),
                Pick(request.ItemId, string.Empty),
                string.IsNullOrWhiteSpace(request.ChunkId) ? null : request.ChunkId.Trim(),
                "chunked",
                Pick(request.Model, string.Empty),
                engine,
                textCharacters,
                cacheHit,
                latencyMs,
                [new TranslationTraceStage("chunked.total", Math.Max(0, latencyMs), 0)],
                new Dictionary<string, double>
                {
                    ["chunked.segment_count"] = segmentCount,
                    ["chunked.max_concurrency"] = maxConcurrency,
                    ["chunked.child_trace_count"] = childList.Length
                },
                BuildClientUnixMs(request),
                childList);
        }

        private static IReadOnlyDictionary<string, long> BuildClientUnixMs(MortTranslateRequest request)
        {
            var values = new Dictionary<string, long>();
            AddIfPresent(values, "client.queued_at_unix_ms", request.ClientQueuedAtUnixMs);
            AddIfPresent(values, "client.request_started_at_unix_ms", request.ClientRequestStartedAtUnixMs);
            AddIfPresent(values, "background.received_at_unix_ms", request.BackgroundReceivedAtUnixMs);
            AddIfPresent(values, "background.fetch_started_at_unix_ms", request.BackgroundFetchStartedAtUnixMs);
            return values;
        }

        private static void AddIfPresent(Dictionary<string, long> values, string name, long? value)
        {
            if (value is long number && number > 0)
            {
                values[name] = number;
            }
        }

        private static IReadOnlyDictionary<string, double> SanitizeTimings(IReadOnlyDictionary<string, double>? timings)
        {
            if (timings is null || timings.Count == 0)
            {
                return new Dictionary<string, double>();
            }

            return timings
                .Where(item => double.IsFinite(item.Value))
                .ToDictionary(item => item.Key, item => item.Value);
        }
    }

    private static string Pick(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static TokenUsage? MergeTokenUsage(IReadOnlyCollection<TokenUsage> usages, string source)
    {
        if (usages.Count == 0)
        {
            return null;
        }

        var input = usages.Sum(usage => usage.InputTokens);
        var output = usages.Sum(usage => usage.OutputTokens);
        var total = usages.Sum(usage => usage.TotalTokens);
        if (total <= 0)
        {
            total = input + output;
        }

        return new TokenUsage(input, output, total, source, usages.Any(usage => usage.IsEstimated));
    }

    private static bool IsChineseVariant(string? language)
    {
        var normalized = LanguageRegistry.Normalize(language);
        return normalized.Equals(LanguageRegistry.TraditionalChinese, StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals(LanguageRegistry.SimplifiedChinese, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True for any Chinese language tag (zh, zh-CN, zh-TW, zh-Hans, zh-Hant, ...).</summary>
    private static bool IsChineseLanguage(string? language)
    {
        if (IsChineseVariant(language))
        {
            return true;
        }

        var raw = (language ?? string.Empty).Trim();
        return raw.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ||
               LanguageRegistry.Normalize(language).StartsWith("zh", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTraditionalTarget(string? target)
        => LanguageRegistry.Normalize(target).Equals(LanguageRegistry.TraditionalChinese, StringComparison.OrdinalIgnoreCase);

    /// <summary>Deterministic Simplified&lt;-&gt;Traditional conversion toward the target variant.</summary>
    private static string ConvertChineseVariant(string text, string? target)
        => IsTraditionalTarget(target)
            ? ChineseVariantConverter.Shared.ToTraditionalTaiwan(text)
            : ChineseVariantConverter.Shared.ToSimplified(text);

    /// <summary>
    /// Deterministic in-text terminology normalization for the source==target path.
    /// Longest keys win ("Source Code" before "Source"); ASCII terms replace on word
    /// boundaries case-insensitively, CJK terms replace ordinally.
    /// </summary>
    private static string ApplyGlossaryTermReplacements(
        string text,
        IReadOnlyDictionary<string, string> terms)
    {
        if (string.IsNullOrWhiteSpace(text) || terms.Count == 0)
        {
            return text;
        }

        var result = text;
        foreach (var term in terms.OrderByDescending(item => item.Key.Length))
        {
            if (term.Key.Length == 0 || term.Key == term.Value)
            {
                continue;
            }

            if (term.Key.All(ch => ch < 128))
            {
                result = Regex.Replace(
                    result,
                    $@"\b{Regex.Escape(term.Key)}\b",
                    term.Value.Replace("$", "$$"),
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
            else
            {
                result = result.Replace(term.Key, term.Value, StringComparison.Ordinal);
            }
        }

        return result;
    }

    private static bool IsBlankForMort(string text)
        => string.IsNullOrWhiteSpace(text.Replace(" ", string.Empty).ReplaceLineEndings(string.Empty));

    private static IEnumerable<string?> GetContextPieces(MortTranslateRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Context))
        {
            yield return request.Context;
        }

        if (request.ContextItems is null)
        {
            yield break;
        }

        foreach (var item in request.ContextItems)
        {
            yield return item;
        }
    }

    private static string CombineContextHashes(params string[] hashes)
    {
        var active = hashes
            .Where(hash => !string.IsNullOrWhiteSpace(hash))
            .ToArray();
        return active.Length switch
        {
            0 => string.Empty,
            1 => active[0],
            _ => RagSecurityPolicy.ComputeSourceHash(active)
        };
    }

    private async Task RecordMemoryContextUseAsync(
        string profileId,
        MemoryContext memoryContext,
        CancellationToken cancellationToken)
    {
        if (memoryContext.MemoryIds.Count == 0)
        {
            return;
        }

        var memoryStore = await _gameStores.MemoryFor(profileId, cancellationToken);
        await memoryStore.RecordUseAsync(memoryContext.MemoryIds, cancellationToken);
    }

    private async Task MaintainSuccessfulTranslationAsync(
        string profileId,
        string sessionId,
        string sourceLanguage,
        string targetLanguage,
        string mode,
        CancellationToken cancellationToken)
    {
        var sceneSummaryMaintenance = await _gameServices.SceneMaintenanceFor(profileId, cancellationToken);
        await sceneSummaryMaintenance.MaintainAsync(
            profileId,
            sessionId,
            sourceLanguage,
            targetLanguage,
            mode,
            cancellationToken);
        QueueMemoryMaintenance(profileId, sessionId, sourceLanguage, targetLanguage, mode);
    }

    private void QueueMemoryMaintenance(
        string profileId,
        string sessionId,
        string sourceLanguage,
        string targetLanguage,
        string mode)
    {
        var shouldExtractCandidates = _options.Memory.AutoExtractionEnabled && !string.IsNullOrWhiteSpace(sessionId);
        var shouldMaintainEmbeddings = _options.Memory.SemanticRetrievalEnabled;
        if (!shouldExtractCandidates && !shouldMaintainEmbeddings)
        {
            return;
        }

        var task = Task.Run(
            async () =>
            {
                try
                {
                    var memoryMaintenance = await _gameServices.MaintenanceFor(profileId, CancellationToken.None);
                    if (memoryMaintenance.HasDurableQueue)
                    {
                        await memoryMaintenance.EnqueueMaintenanceJobsAsync(
                            profileId,
                            sessionId,
                            sourceLanguage,
                            targetLanguage,
                            mode,
                            shouldExtractCandidates,
                            shouldMaintainEmbeddings,
                            CancellationToken.None);

                        await memoryMaintenance.DrainQueuedJobsAsync(5, CancellationToken.None);
                        return;
                    }

                    if (shouldExtractCandidates)
                    {
                        await memoryMaintenance.MaintainTranslationCandidatesAsync(
                            profileId,
                            sessionId,
                            sourceLanguage,
                            targetLanguage,
                            mode,
                            CancellationToken.None);
                    }

                    if (shouldMaintainEmbeddings)
                    {
                        await memoryMaintenance.MaintainEmbeddingsAsync(
                            profileId,
                            sourceLanguage,
                            targetLanguage,
                            cancellationToken: CancellationToken.None);
                    }
                }
                catch
                {
                    // Candidate extraction must never break realtime translation.
                }
            },
            CancellationToken.None);
        lock (_memoryMaintenanceTasksGate)
        {
            _memoryMaintenanceTasks.Add(task);
        }

        _ = task.ContinueWith(
            completed =>
            {
                lock (_memoryMaintenanceTasksGate)
                {
                    _memoryMaintenanceTasks.Remove(completed);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task DrainMemoryMaintenanceTasksAsync()
    {
        Task[] tasks;
        lock (_memoryMaintenanceTasksGate)
        {
            tasks = _memoryMaintenanceTasks.ToArray();
        }

        if (tasks.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // Shutdown must not surface best-effort maintenance failures.
        }
    }

    private async Task RecordMemoryContextAuditAsync(
        MemoryContext memoryContext,
        string requestId,
        string principalId,
        string profileId,
        string sessionId,
        string? translationKey,
        CancellationToken cancellationToken)
    {
        if (memoryContext.Snippets.Count == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var entries = memoryContext.Snippets
            .Select(snippet => new MemoryContextAuditEntry(
                Guid.NewGuid().ToString("N"),
                requestId,
                profileId,
                principalId,
                sessionId,
                translationKey,
                snippet.MemoryId,
                snippet.MemoryKind,
                snippet.SnippetHash,
                memoryContext.Hash,
                snippet.TrustLevel,
                snippet.SourceHash,
                memoryContext.PolicyVersion,
                memoryContext.Text.Length,
                memoryContext.Snippets.Count,
                memoryContext.RecentEventIds.Count,
                "used",
                "memory_context",
                now))
            .ToArray();

        var auditStore = await _gameStores.AuditFor(profileId, cancellationToken);
        await auditStore.AddEntriesAsync(entries, cancellationToken);
    }

    private async Task RecordExactMemoryAuditAsync(
        MemoryItem memory,
        string requestId,
        string principalId,
        string profileId,
        string sessionId,
        string? translationKey,
        string decision,
        string reason,
        CancellationToken cancellationToken)
    {
        var snippetHash = CreateMemorySnippetHash(memory);
        var characterCount = memory.SourceText.Length + memory.TargetText.Length + memory.Note.Length;
        var entry = new MemoryContextAuditEntry(
            Guid.NewGuid().ToString("N"),
            requestId,
            profileId,
            principalId,
            sessionId,
            translationKey,
            memory.Id,
            memory.MemoryKind,
            snippetHash,
            snippetHash,
            memory.TrustLevel,
            memory.SourceHash,
            MemoryContextBuilder.RetrievalPolicyVersion,
            characterCount,
            1,
            0,
            decision,
            reason,
            DateTimeOffset.UtcNow);

        var auditStore = await _gameStores.AuditFor(profileId, cancellationToken);
        await auditStore.AddEntriesAsync([entry], cancellationToken);
    }

    private static string CreateMemorySnippetHash(MemoryItem memory)
        => RagSecurityPolicy.ComputeSourceHash(
            MemoryContextBuilder.RetrievalPolicyVersion,
            memory.Id,
            memory.MemoryKind,
            memory.TrustLevel,
            memory.SourceHash,
            memory.SourceText,
            memory.TargetText,
            memory.Note);

    private async Task<string> RecordEventAsync(
        MortTranslateRequest request,
        string sessionId,
        string profileId,
        string? translationKey,
        string sourceText,
        string translatedText,
        string source,
        string target,
        string mode,
        string provider,
        string glossaryHash,
        string engine,
        string model,
        long latencyMs,
        bool cacheHit,
        string errorCode,
        string errorMessage,
        CancellationToken cancellationToken,
        TokenUsage? tokenUsage = null)
    {
        var eventId = Guid.NewGuid().ToString("N");
        var entry = new TranslationEvent(
                eventId,
                sessionId,
                profileId,
                translationKey,
                request.Name?.Trim() ?? string.Empty,
                sourceText,
                translatedText,
                source,
                target,
                mode,
                provider,
                request.Glossary?.Trim() ?? string.Empty,
                glossaryHash,
                engine,
                model,
                latencyMs,
                cacheHit,
                errorCode,
                errorMessage,
                DateTimeOffset.UtcNow)
        {
            InputTokens = tokenUsage?.InputTokens ?? 0,
            OutputTokens = tokenUsage?.OutputTokens ?? 0,
            TotalTokens = tokenUsage?.TotalTokens ?? 0,
            TokenSource = tokenUsage?.Source ?? string.Empty,
            TokenEstimated = tokenUsage?.IsEstimated ?? false,
            Surface = TranslationSurface.FromString(request.Surface)
        };

        // Realtime loops produce one event per frame; batch those writes instead of
        // paying a SQLite INSERT each time. Realtime paths never link audits to the
        // event id, so deferred persistence is safe.
        if (request.Realtime == true && _eventBatcher is not null)
        {
            _eventBatcher.Enqueue(entry);
        }
        else
        {
            var eventStore = await _gameStores.EventsFor(profileId, cancellationToken);
            await eventStore.AddEventAsync(entry, cancellationToken);
        }

        return eventId;
    }
}
