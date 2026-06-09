using System.Diagnostics;
using LocalTranslateHub.Core.Models;
using LocalTranslateHub.Core.Options;
using LocalTranslateHub.Core.Providers;
using LocalTranslateHub.Core.Storage;

namespace LocalTranslateHub.Core.Services;

public sealed class TranslationService
{
    private readonly LocalTranslateHubOptions _options;
    private readonly TranslationProviderRegistry _providers;
    private readonly PromptPresetStore _presets;
    private readonly GlossaryStore _glossaries;
    private readonly ITranslationCache _cache;
    private readonly ITranslationEventStore _eventStore;
    private readonly IMemoryStore _memoryStore;
    private readonly ContextCompressionService _contextCompression;

    public TranslationService(
        LocalTranslateHubOptions options,
        TranslationProviderRegistry providers,
        PromptPresetStore presets,
        GlossaryStore glossaries,
        ITranslationCache cache,
        ITranslationEventStore eventStore,
        IMemoryStore memoryStore,
        ContextCompressionService contextCompression)
    {
        _options = options;
        _providers = providers;
        _presets = presets;
        _glossaries = glossaries;
        _cache = cache;
        _eventStore = eventStore;
        _memoryStore = memoryStore;
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
                await _memoryStore.RecordUseAsync([memory.Id], cancellationToken);
                await RecordEventAsync(
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
                    engine: "memory:user-verified",
                    model: string.Empty,
                    latencyMs: 0,
                    cacheHit: false,
                    errorCode: "0",
                    errorMessage: string.Empty,
                    cancellationToken);

                return TranslationOutcome.Success(memory.TargetText, "memory:user-verified", 0, cacheHit: false);
            }

            var provider = _providers.GetRequired(providerName);
            var defaultModel = provider.Descriptor.Name.Equals("ollama", StringComparison.OrdinalIgnoreCase)
                ? _options.Ollama.Model
                : provider.Descriptor.DefaultModel;
            model = Pick(request.Model, defaultModel);
            var compressedContext = _contextCompression.Compress(GetContextPieces(request));

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
                compressedContext.Hash);

            var cached = await _cache.GetAsync(key, cancellationToken);
            if (cached is not null)
            {
                await RecordEventAsync(
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
                compressedContext.Text);

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

            await RecordEventAsync(
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

    private Task RecordEventAsync(
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
        => _eventStore.AddEventAsync(
            new TranslationEvent(
                Guid.NewGuid().ToString("N"),
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
}
