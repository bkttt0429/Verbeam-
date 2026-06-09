using System.Diagnostics;
using Verbeam.Core.Models;
using Verbeam.Core.Options;
using Verbeam.Core.Providers;
using Verbeam.Core.Storage;

namespace Verbeam.Core.Services;

public sealed class TranslationService
{
    private readonly VerbeamOptions _options;
    private readonly TranslationProviderRegistry _providers;
    private readonly PromptPresetStore _presets;
    private readonly GlossaryStore _glossaries;
    private readonly ITranslationCache _cache;
    private readonly ITranslationEventStore _eventStore;
    private readonly IMemoryStore _memoryStore;
    private readonly IMemoryContextAuditStore _memoryAuditStore;
    private readonly MemoryContextBuilder _memoryContextBuilder;
    private readonly ContextCompressionService _contextCompression;

    public TranslationService(
        VerbeamOptions options,
        TranslationProviderRegistry providers,
        PromptPresetStore presets,
        GlossaryStore glossaries,
        ITranslationCache cache,
        ITranslationEventStore eventStore,
        IMemoryStore memoryStore,
        IMemoryContextAuditStore memoryAuditStore,
        MemoryContextBuilder memoryContextBuilder,
        ContextCompressionService contextCompression)
    {
        _options = options;
        _providers = providers;
        _presets = presets;
        _glossaries = glossaries;
        _cache = cache;
        _eventStore = eventStore;
        _memoryStore = memoryStore;
        _memoryAuditStore = memoryAuditStore;
        _memoryContextBuilder = memoryContextBuilder;
        _contextCompression = contextCompression;
    }

    public async Task<TranslationOutcome> TranslateAsync(
        MortTranslateRequest request,
        CancellationToken cancellationToken = default)
    {
        var text = request.Text ?? string.Empty;
        var source = Pick(request.Source, _options.DefaultSource);
        var target = Pick(request.Target, _options.DefaultTarget);
        var mode = Pick(request.Mode, _options.DefaultMode);
        var providerName = Pick(request.Provider, _options.DefaultProvider);
        var profileId = Pick(request.Profile, "default");
        var sessionId = Pick(request.SessionId, string.Empty);
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
            return TranslationOutcome.Success(string.Empty, "none", 0, cacheHit: false);
        }

        try
        {
            var memory = await _memoryStore.FindExactAsync(
                profileId,
                "translation",
                source,
                target,
                text,
                cancellationToken);
            if (memory is not null)
            {
                var memoryEngine = $"memory:{memory.TrustLevel.Replace('_', '-')}";
                await _memoryStore.RecordUseAsync([memory.Id], cancellationToken);
                var eventId = await RecordEventAsync(
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

                return TranslationOutcome.Success(memory.TargetText, memoryEngine, 0, cacheHit: false);
            }

            var provider = _providers.GetRequired(providerName);
            var defaultModel = provider.Descriptor.Name.Equals("ollama", StringComparison.OrdinalIgnoreCase)
                ? _options.Ollama.Model
                : provider.Descriptor.DefaultModel;
            model = Pick(request.Model, defaultModel);
            var compressedContext = _contextCompression.Compress(GetContextPieces(request));
            var memoryContext = await _memoryContextBuilder.BuildAsync(
                new MemoryContextRequest(
                    profileId,
                    sessionId,
                    source,
                    target,
                    mode,
                    text),
                cancellationToken);
            var contextHash = CombineContextHashes(compressedContext.Hash, memoryContext.Hash);

            var preset = await _presets.GetRequiredAsync(mode, cancellationToken);
            var glossary = await _glossaries.GetOptionalAsync(request.Glossary, cancellationToken);
            glossaryHash = glossary.Hash;
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

            var cached = await _cache.GetAsync(key, cancellationToken);
            if (cached is not null)
            {
                var cacheEventId = await RecordEventAsync(
                    request,
                    sessionId,
                    profileId,
                    key,
                    text,
                    cached.TranslatedText,
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
                await RecordMemoryContextAuditAsync(memoryContext, cacheEventId, profileId, sessionId, key, cancellationToken);
                await RecordMemoryContextUseAsync(memoryContext, cancellationToken);
                return TranslationOutcome.Success(cached.TranslatedText, cached.Engine, cached.LatencyMs, cacheHit: true);
            }

            var providerRequest = new ProviderTranslationRequest(
                text,
                source,
                target,
                mode,
                model,
                preset,
                glossary.Terms,
                compressedContext.Text,
                memoryContext.Text);

            var stopwatch = Stopwatch.StartNew();
            var result = await provider.TranslateAsync(providerRequest, cancellationToken);
            stopwatch.Stop();

            await _cache.SetAsync(
                new CachedTranslation(
                    key,
                    text,
                    result.Text,
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
                cancellationToken);

            var successEventId = await RecordEventAsync(
                request,
                sessionId,
                profileId,
                key,
                text,
                result.Text,
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
                cancellationToken);
            await RecordMemoryContextAuditAsync(memoryContext, successEventId, profileId, sessionId, key, cancellationToken);
            await RecordMemoryContextUseAsync(memoryContext, cancellationToken);

            return TranslationOutcome.Success(result.Text, result.Engine, stopwatch.ElapsedMilliseconds, cacheHit: false);
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

    private static string Pick(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

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

    private Task RecordMemoryContextUseAsync(
        MemoryContext memoryContext,
        CancellationToken cancellationToken)
        => memoryContext.MemoryIds.Count == 0
            ? Task.CompletedTask
            : _memoryStore.RecordUseAsync(memoryContext.MemoryIds, cancellationToken);

    private Task RecordMemoryContextAuditAsync(
        MemoryContext memoryContext,
        string requestId,
        string profileId,
        string sessionId,
        string? translationKey,
        CancellationToken cancellationToken)
    {
        if (memoryContext.Snippets.Count == 0)
        {
            return Task.CompletedTask;
        }

        var now = DateTimeOffset.UtcNow;
        var entries = memoryContext.Snippets
            .Select(snippet => new MemoryContextAuditEntry(
                Guid.NewGuid().ToString("N"),
                requestId,
                profileId,
                sessionId,
                translationKey,
                snippet.MemoryId,
                snippet.MemoryKind,
                snippet.SnippetHash,
                memoryContext.Hash,
                snippet.TrustLevel,
                snippet.SourceHash,
                memoryContext.PolicyVersion,
                now))
            .ToArray();

        return _memoryAuditStore.AddEntriesAsync(entries, cancellationToken);
    }

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
        CancellationToken cancellationToken)
    {
        var eventId = Guid.NewGuid().ToString("N");
        await _eventStore.AddEventAsync(
            new TranslationEvent(
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
                DateTimeOffset.UtcNow),
            cancellationToken);
        return eventId;
    }
}
