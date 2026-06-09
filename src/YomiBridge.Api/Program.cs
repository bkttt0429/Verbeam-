using YomiBridge.Api.Broadcast;
using YomiBridge.Api.Pages;
using YomiBridge.Core.Models;
using YomiBridge.Core.Options;
using YomiBridge.Core.Providers;
using YomiBridge.Core.Services;
using YomiBridge.Core.Storage;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddEnvironmentVariables(prefix: "YB_");
builder.WebHost.UseUrls(builder.Configuration["Urls"] ?? "http://localhost:5757");

var options = builder.Configuration.GetSection("YomiBridge").Get<YomiBridgeOptions>()
    ?? new YomiBridgeOptions();

var presetsPath = PathResolver.Resolve(builder.Environment.ContentRootPath, options.PresetsDirectory);
var glossariesPath = PathResolver.Resolve(builder.Environment.ContentRootPath, options.GlossariesDirectory);
var cachePath = PathResolver.Resolve(builder.Environment.ContentRootPath, options.CachePath);

var cache = new SqliteTranslationCache(cachePath);
await cache.InitializeAsync();
var translationEvents = new SqliteTranslationEventStore(cachePath);
await translationEvents.InitializeAsync();
var memoryStore = new SqliteMemoryStore(cachePath);
await memoryStore.InitializeAsync();
var ocrMemory = new SqliteOcrMemoryStore(cachePath);
await ocrMemory.InitializeAsync();
var speechEvents = new SqliteSpeechEventStore(cachePath);
await speechEvents.InitializeAsync();
var speechJobs = new SqliteSpeechJobStore(cachePath);
await speechJobs.InitializeAsync();

builder.Services.AddSingleton(options);
builder.Services.AddSingleton(new ContextCompressionService(options.ContextCompression));
builder.Services.AddSingleton(_ => new OllamaModelCatalog(
    new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(Math.Clamp(options.Ollama.ModelDiscoveryTimeoutSeconds, 1, 10))
    },
    options.Ollama));
builder.Services.AddSingleton(new PromptPresetStore(presetsPath));
builder.Services.AddSingleton(new GlossaryStore(glossariesPath));
builder.Services.AddSingleton<ITranslationCache>(cache);
builder.Services.AddSingleton<ITranslationEventStore>(translationEvents);
builder.Services.AddSingleton<IMemoryStore>(memoryStore);
builder.Services.AddSingleton<IOcrMemoryStore>(ocrMemory);
builder.Services.AddSingleton<ISpeechEventStore>(speechEvents);
builder.Services.AddSingleton<ISpeechJobStore>(speechJobs);
builder.Services.AddSingleton<ITranslationProvider, MockTranslationProvider>();
builder.Services.AddSingleton<ITranslationProvider>(_ =>
{
    var httpClient = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(Math.Max(1, options.Ollama.TimeoutSeconds))
    };

    return new OllamaTranslationProvider(httpClient, options.Ollama);
});
builder.Services.AddSingleton<IOcrProvider, MockOcrProvider>();
builder.Services.AddSingleton<IOcrProvider>(_ => new ExternalCommandOcrProvider(options.Ocr.External, builder.Environment.ContentRootPath));
builder.Services.AddSingleton<IOcrProvider>(_ => CreateLocalOcrProvider("tesseract", "Tesseract OCR", "local-process", options, builder.Environment.ContentRootPath));
builder.Services.AddSingleton<IOcrProvider>(_ => CreateLocalOcrProvider("easyocr", "EasyOCR", "local-python", options, builder.Environment.ContentRootPath));
builder.Services.AddSingleton<IOcrProvider>(_ => CreateLocalOcrProvider("paddleocr", "PaddleOCR / PP-OCR text", "local-python", options, builder.Environment.ContentRootPath));
builder.Services.AddSingleton<IOcrProvider>(_ => CreateLocalOcrProvider("pix2text", "Pix2Text", "local-python", options, builder.Environment.ContentRootPath));
builder.Services.AddSingleton<IOcrProvider>(_ => CreateLocalOcrProvider("pp-structure-v3", "PP-StructureV3", "local-pipeline", options, builder.Environment.ContentRootPath));
builder.Services.AddSingleton<IOcrProvider>(_ => CreateLocalOcrProvider("paddleocr-vl", "PaddleOCR-VL", "local-vlm", options, builder.Environment.ContentRootPath));
builder.Services.AddSingleton<IOcrProvider>(_ => CreateLocalOcrProvider("dots-ocr", "dots.ocr", "local-vlm", options, builder.Environment.ContentRootPath));
builder.Services.AddSingleton<ISpeechProvider, MockSpeechProvider>();
builder.Services.AddSingleton<ISpeechProvider>(_ => new ExternalCommandSpeechProvider(options.Speech.External, builder.Environment.ContentRootPath));
builder.Services.AddSingleton<ISpeechProvider>(_ =>
{
    var httpClient = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(Math.Max(1, options.Speech.FunAsrHttp.TimeoutSeconds))
    };

    return new FunAsrHttpSpeechProvider(httpClient, options.Speech.FunAsrHttp);
});
builder.Services.AddSingleton<TranslationBroadcastHub>();
builder.Services.AddSingleton<TranslationProviderRegistry>();
builder.Services.AddSingleton<OcrProviderRegistry>();
builder.Services.AddSingleton<SpeechProviderRegistry>();
builder.Services.AddSingleton<TranslationService>();
builder.Services.AddSingleton<OcrService>();
builder.Services.AddSingleton<SpeechService>();
builder.Services.AddSingleton<SpeechJobService>();

var app = builder.Build();
var startedAt = DateTimeOffset.UtcNow;

app.UseWebSockets();
app.UseStaticFiles();

app.MapGet("/", () => Results.Redirect("/health"));

app.MapGet("/health", async (
    PromptPresetStore presets,
    GlossaryStore glossaries,
    TranslationProviderRegistry providers,
    OcrProviderRegistry ocrProviders,
    SpeechProviderRegistry speechProviders,
    TranslationBroadcastHub broadcastHub,
    CancellationToken cancellationToken) =>
{
    var presetList = await presets.ListAsync(cancellationToken);
    var glossaryList = await glossaries.ListAsync(cancellationToken);

    return Results.Ok(new
    {
        status = "ok",
        startedAt,
        defaultProvider = options.DefaultProvider,
        defaultMode = options.DefaultMode,
        ollama = new
        {
            baseUrl = options.Ollama.BaseUrl,
            model = options.Ollama.Model,
            models = options.Ollama.Models,
            modelDiscoveryTimeoutSeconds = options.Ollama.ModelDiscoveryTimeoutSeconds,
            numContext = options.Ollama.NumContext,
            numPredict = options.Ollama.NumPredict,
            temperature = options.Ollama.Temperature,
            keepAlive = options.Ollama.KeepAlive
        },
        ocr = new
        {
            defaultProvider = options.Ocr.DefaultProvider,
            defaultLanguage = options.Ocr.DefaultLanguage,
            maxImageBytes = options.Ocr.MaxImageBytes,
            normalizeWhitespace = options.Ocr.NormalizeWhitespace,
            externalCommandConfigured = !string.IsNullOrWhiteSpace(options.Ocr.External.FileName)
        },
        speech = new
        {
            defaultProvider = options.Speech.DefaultProvider,
            defaultLanguage = options.Speech.DefaultLanguage,
            maxAudioBytes = options.Speech.MaxAudioBytes,
            preferCaptions = options.Speech.PreferCaptions,
            funAsrHttp = new
            {
                baseUrl = options.Speech.FunAsrHttp.BaseUrl,
                model = options.Speech.FunAsrHttp.Model,
                responseFormat = options.Speech.FunAsrHttp.ResponseFormat,
                timeoutSeconds = options.Speech.FunAsrHttp.TimeoutSeconds
            },
            youtube = new
            {
                ytDlpFileName = options.Speech.YouTube.YtDlpFileName,
                ffmpegFileName = options.Speech.YouTube.FfmpegFileName,
                audioFormat = options.Speech.YouTube.AudioFormat,
                captionLanguages = options.Speech.YouTube.CaptionLanguages,
                timeoutSeconds = options.Speech.YouTube.TimeoutSeconds,
                audioChunkSeconds = options.Speech.YouTube.AudioChunkSeconds
            },
            live = new
            {
                sampleRate = options.Speech.Live.SampleRate,
                channels = options.Speech.Live.Channels,
                bitsPerSample = options.Speech.Live.BitsPerSample,
                maxSegmentSeconds = options.Speech.Live.MaxSegmentSeconds,
                silenceDurationMs = options.Speech.Live.SilenceDurationMs,
                silenceRmsThreshold = options.Speech.Live.SilenceRmsThreshold
            },
            externalCommandConfigured = !string.IsNullOrWhiteSpace(options.Speech.External.FileName)
        },
        contextCompression = new
        {
            enabled = options.ContextCompression.Enabled,
            maxCharacters = options.ContextCompression.MaxCharacters,
            headCharacters = options.ContextCompression.HeadCharacters,
            tailCharacters = options.ContextCompression.TailCharacters
        },
        paths = new
        {
            presets = presetsPath,
            glossaries = glossariesPath,
            cache = cachePath
        },
        counts = new
        {
            providers = providers.List().Count,
            ocrProviders = ocrProviders.List().Count,
            speechProviders = speechProviders.List().Count,
            presets = presetList.Count,
            glossaries = glossaryList.Count,
            broadcastClients = broadcastHub.ClientCount
        }
    });
});

app.MapGet("/providers", (TranslationProviderRegistry providers) => Results.Ok(providers.List()));

app.MapGet("/translation/events", async (
    ITranslationEventStore eventStore,
    string? profile,
    int? limit,
    CancellationToken cancellationToken) =>
{
    var profileId = Pick(profile, "default");
    return Results.Ok(await eventStore.ListEventsAsync(profileId, limit ?? 50, cancellationToken));
});

app.MapGet("/memories", async (
    IMemoryStore store,
    string? profile,
    string? type,
    int? limit,
    bool? includeInactive,
    CancellationToken cancellationToken) =>
{
    var profileId = Pick(profile, "default");
    return Results.Ok(await store.ListAsync(
        profileId,
        type,
        limit ?? 100,
        activeOnly: includeInactive != true,
        cancellationToken));
});

app.MapPost("/memories", async (
    MemoryUpsertRequest request,
    IMemoryStore store,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await store.AddOrUpdateAsync(request, cancellationToken));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(MemoryError(ex.Message));
    }
});

app.MapPost("/translation/corrections", async Task<IResult> (
    TranslationCorrectionRequest request,
    ITranslationEventStore eventStore,
    IMemoryStore memoryStore,
    CancellationToken cancellationToken) =>
{
    try
    {
        if (string.IsNullOrWhiteSpace(request.EventId))
        {
            return Results.BadRequest(MemoryError("eventId is required."));
        }

        if (string.IsNullOrWhiteSpace(request.CorrectedText))
        {
            return Results.BadRequest(MemoryError("correctedText is required."));
        }

        var entry = await eventStore.GetEventAsync(request.EventId.Trim(), cancellationToken);
        if (entry is null)
        {
            return Results.NotFound(MemoryError("translation event was not found."));
        }

        if (!string.IsNullOrWhiteSpace(request.Profile) &&
            !string.Equals(request.Profile.Trim(), entry.ProfileId, StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest(MemoryError("profile does not match the translation event."));
        }

        if (!string.IsNullOrWhiteSpace(request.SessionId) &&
            !string.Equals(request.SessionId.Trim(), entry.SessionId, StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest(MemoryError("sessionId does not match the translation event."));
        }

        var memory = await memoryStore.AddOrUpdateAsync(
            new TranslationCorrectionMemoryUpsert
            {
                Profile = entry.ProfileId,
                MemoryKind = "translation",
                Source = entry.SourceLanguage,
                Target = entry.TargetLanguage,
                SourceText = entry.SourceText,
                TargetText = request.CorrectedText,
                Note = request.Note,
                Priority = request.Priority ?? 100,
                Confidence = request.Confidence ?? 1.0,
                Origin = "user-verified",
                SourceEventId = entry.Id
            },
            cancellationToken);

        return Results.Ok(memory);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(MemoryError(ex.Message));
    }
});

app.MapGet("/translation/models", async Task<IResult> (
    string? provider,
    TranslationProviderRegistry providers,
    OllamaModelCatalog ollamaModels,
    CancellationToken cancellationToken) =>
{
    try
    {
        var providerName = Pick(provider, options.DefaultProvider);
        var descriptor = providers.GetRequired(providerName).Descriptor;
        if (descriptor.Name.Equals("ollama", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Ok(await ollamaModels.ListAsync(descriptor.Name, cancellationToken));
        }

        var model = Pick(descriptor.DefaultModel, descriptor.Name);
        return Results.Ok(new[]
        {
            new TranslationModelDescriptor(
                descriptor.Name,
                model,
                model,
                IsDefault: true,
                IsInstalled: true,
                "provider")
        });
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { errorCode = "invalid_provider", errorMessage = ex.Message });
    }
});

app.MapGet("/ocr/providers", (OcrProviderRegistry providers) => Results.Ok(providers.List()));

app.MapGet("/ocr/engines", async (OcrProviderRegistry providers, CancellationToken cancellationToken)
    => Results.Ok(await BuildOcrEnginesAsync(providers, options, builder.Environment.ContentRootPath, cancellationToken)));

app.MapGet("/asr/providers", (SpeechProviderRegistry providers) => Results.Ok(providers.List()));

app.MapGet("/asr/engines", (SpeechProviderRegistry providers)
    => Results.Ok(BuildSpeechEngines(providers, options)));

app.MapGet("/ocr/events", async (
    IOcrMemoryStore memoryStore,
    string? profile,
    int? limit,
    CancellationToken cancellationToken) =>
{
    var profileId = Pick(profile, "default");
    return Results.Ok(await memoryStore.ListEventsAsync(profileId, limit ?? 50, cancellationToken));
});

app.MapGet("/asr/events", async (
    ISpeechEventStore eventStore,
    string? profile,
    int? limit,
    CancellationToken cancellationToken) =>
{
    var profileId = Pick(profile, "default");
    return Results.Ok(await eventStore.ListEventsAsync(profileId, limit ?? 50, cancellationToken));
});

app.MapGet("/asr/jobs", async (
    SpeechJobService speechJobs,
    string? profile,
    int? limit,
    CancellationToken cancellationToken) =>
{
    var profileId = Pick(profile, "default");
    return Results.Ok(await speechJobs.ListAsync(profileId, limit ?? 50, cancellationToken));
});

app.MapPost("/asr/jobs", async (
    SpeechJobRequest request,
    SpeechJobService speechJobs,
    CancellationToken cancellationToken) =>
{
    try
    {
        var job = await speechJobs.StartAsync(request, cancellationToken);
        return Results.Accepted($"/asr/jobs/{job.Id}", job);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(AsrError(ex.Message));
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(AsrError(ex.Message));
    }
});

app.MapGet("/asr/jobs/{jobId}", async (
    string jobId,
    SpeechJobService speechJobs,
    CancellationToken cancellationToken) =>
{
    var job = await speechJobs.GetAsync(jobId, cancellationToken);
    return job is null ? Results.NotFound(AsrError("ASR job was not found.")) : Results.Ok(job);
});

app.MapPost("/asr/jobs/{jobId}/cancel", async (
    string jobId,
    SpeechJobService speechJobs,
    CancellationToken cancellationToken) =>
{
    var canceled = await speechJobs.CancelAsync(jobId, cancellationToken);
    return canceled ? Results.Ok(new { jobId, canceled = true }) : Results.NotFound(AsrError("ASR job is not running."));
});

app.MapGet("/asr/jobs/{jobId}/events", async (
    string jobId,
    HttpContext context,
    SpeechJobService speechJobs,
    CancellationToken cancellationToken) =>
{
    await StreamSpeechJobEventsAsync(jobId, context, speechJobs, cancellationToken);
});

app.MapGet("/ocr/corrections", async (
    IOcrMemoryStore memoryStore,
    string? profile,
    string? language,
    int? limit,
    bool? includeInactive,
    CancellationToken cancellationToken) =>
{
    var profileId = Pick(profile, "default");
    var languageId = Pick(language, options.Ocr.DefaultLanguage);
    return Results.Ok(await memoryStore.ListCorrectionsAsync(
        profileId,
        languageId,
        limit ?? 100,
        activeOnly: includeInactive != true,
        cancellationToken));
});

app.MapPost("/ocr/corrections", async (
    OcrCorrectionRequest request,
    IOcrMemoryStore memoryStore,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await memoryStore.AddOrUpdateCorrectionAsync(request, cancellationToken));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(OcrError(ex.Message));
    }
});

app.MapGet("/presets", async (PromptPresetStore presets, CancellationToken cancellationToken)
    => Results.Ok(await presets.ListAsync(cancellationToken)));

app.MapGet("/glossaries", async (GlossaryStore glossaries, CancellationToken cancellationToken)
    => Results.Ok(await glossaries.ListAsync(cancellationToken)));

app.MapGet("/app", () => Results.Content(AppWorkbenchPage.Html, "text/html; charset=utf-8"));

app.MapGet("/viewer", () => Results.Content(BroadcastViewerPage.Html, "text/html; charset=utf-8"));

app.MapGet("/broadcast/latest", (TranslationBroadcastHub broadcastHub)
    => broadcastHub.Latest is null ? Results.NoContent() : Results.Ok(broadcastHub.Latest));

app.MapGet("/broadcast", async (
    HttpContext context,
    TranslationBroadcastHub broadcastHub,
    CancellationToken cancellationToken) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("Expected a WebSocket request.", cancellationToken);
        return;
    }

    await broadcastHub.AcceptAsync(context, cancellationToken);
});

app.MapPost("/asr", async (
    SpeechRequest request,
    SpeechService speechService,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await speechService.RecognizeAsync(request, cancellationToken));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(AsrError(ex.Message));
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(AsrError(ex.Message));
    }
    catch (TimeoutException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status504GatewayTimeout);
    }
});

app.MapPost("/asr/translate", async (
    SpeechTranslateRequest request,
    SpeechService speechService,
    TranslationService translationService,
    TranslationBroadcastHub broadcastHub,
    CancellationToken cancellationToken) =>
{
    try
    {
        var speech = await speechService.RecognizeAsync(
            new SpeechRequest
            {
                AudioBase64 = request.AudioBase64,
                AudioMimeType = request.AudioMimeType,
                SourceUrl = request.SourceUrl,
                Provider = request.SpeechProvider,
                Language = request.Language,
                Profile = request.Profile,
                SessionId = request.SessionId,
                Glossary = request.Glossary,
                PreferCaptions = request.PreferCaptions
            },
            cancellationToken);

        var translations = await TranslateSpeechSegmentsAsync(
            speech,
            request,
            translationService,
            broadcastHub,
            options,
            cancellationToken);

        return Results.Ok(new SpeechTranslateResponse(speech, translations));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(AsrError(ex.Message));
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(AsrError(ex.Message));
    }
    catch (TimeoutException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status504GatewayTimeout);
    }
});

app.MapGet("/asr/live", async (
    HttpContext context,
    SpeechService speechService,
    TranslationService translationService,
    TranslationBroadcastHub broadcastHub,
    CancellationToken cancellationToken) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("Expected a WebSocket request.", cancellationToken);
        return;
    }

    await HandleSpeechLiveSocketAsync(
        context,
        speechService,
        translationService,
        broadcastHub,
        options,
        cancellationToken);
});

app.MapPost("/ocr", async (
    OcrRequest request,
    OcrService ocrService,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await ocrService.RecognizeAsync(request, cancellationToken));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(OcrError(ex.Message));
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(OcrError(ex.Message));
    }
    catch (TimeoutException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status504GatewayTimeout);
    }
});

app.MapPost("/ocr/translate", async (
    OcrTranslateRequest request,
    OcrService ocrService,
    TranslationService translationService,
    TranslationBroadcastHub broadcastHub,
    CancellationToken cancellationToken) =>
{
    try
    {
        var ocr = await ocrService.RecognizeAsync(
            new OcrRequest
            {
                ImageBase64 = request.ImageBase64,
                ImageMimeType = request.ImageMimeType,
                Provider = request.OcrProvider,
                Language = request.Language,
                Profile = request.Profile,
                SessionId = request.SessionId,
                NormalizeWhitespace = request.NormalizeWhitespace
            },
            cancellationToken);

        var translationRequest = new MortTranslateRequest
        {
            Text = ocr.Text,
            Source = request.Source,
            Target = request.Target,
            Mode = request.Mode,
            Glossary = request.Glossary,
            Provider = request.TranslationProvider,
            Model = request.Model,
            Profile = request.Profile,
            SessionId = request.SessionId
        };

        var outcome = await translationService.TranslateAsync(translationRequest, cancellationToken);
        var response = outcome.IsSuccess
            ? MortTranslateResponse.Success(outcome.Text)
            : MortTranslateResponse.Error(ocr.Text, outcome.ErrorMessage, outcome.ErrorCode);

        if (outcome.IsSuccess)
        {
            await broadcastHub.BroadcastAsync(
                new TranslationBroadcastMessage(
                    "translation",
                    ocr.Text,
                    outcome.Text,
                    Pick(request.Source, options.DefaultSource),
                    Pick(request.Target, options.DefaultTarget),
                    Pick(request.Mode, options.DefaultMode),
                    Pick(request.TranslationProvider, options.DefaultProvider),
                    request.Glossary,
                    outcome.Engine,
                    outcome.LatencyMs,
                    outcome.CacheHit,
                    DateTimeOffset.UtcNow),
                cancellationToken);
        }

        return Results.Ok(new OcrTranslateResponse(ocr, response));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(OcrError(ex.Message));
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(OcrError(ex.Message));
    }
    catch (TimeoutException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status504GatewayTimeout);
    }
});

app.MapPost("/translate", async (
    MortTranslateRequest request,
    TranslationService translationService,
    TranslationBroadcastHub broadcastHub,
    CancellationToken cancellationToken) =>
{
    try
    {
        var outcome = await translationService.TranslateAsync(request, cancellationToken);
        if (outcome.IsSuccess)
        {
            await broadcastHub.BroadcastAsync(
                new TranslationBroadcastMessage(
                    "translation",
                    request.Text ?? string.Empty,
                    outcome.Text,
                    Pick(request.Source, options.DefaultSource),
                    Pick(request.Target, options.DefaultTarget),
                    Pick(request.Mode, options.DefaultMode),
                    Pick(request.Provider, options.DefaultProvider),
                    request.Glossary,
                    outcome.Engine,
                    outcome.LatencyMs,
                    outcome.CacheHit,
                    DateTimeOffset.UtcNow),
                cancellationToken);

            return Results.Ok(MortTranslateResponse.Success(outcome.Text));
        }

        return Results.Ok(MortTranslateResponse.Error(request.Text ?? string.Empty, outcome.ErrorMessage, outcome.ErrorCode));
    }
    catch (OperationCanceledException)
    {
        throw;
    }
    catch (Exception ex)
    {
        return Results.Ok(MortTranslateResponse.Error(request.Text ?? string.Empty, ex.Message));
    }
});

app.Run();

static string Pick(string? value, string fallback)
    => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

static object OcrError(string message)
    => new { errorCode = "invalid_ocr_request", errorMessage = message };

static object AsrError(string message)
    => new { errorCode = "invalid_asr_request", errorMessage = message };

static object MemoryError(string message)
    => new { errorCode = "invalid_memory_request", errorMessage = message };

static async Task<IReadOnlyList<SpeechTranslatedSegment>> TranslateSpeechSegmentsAsync(
    SpeechResponse speech,
    SpeechTranslateRequest request,
    TranslationService translationService,
    TranslationBroadcastHub broadcastHub,
    YomiBridgeOptions options,
    CancellationToken cancellationToken)
{
    var values = new List<SpeechTranslatedSegment>();
    var source = Pick(request.Source, Pick(request.Language, options.DefaultSource));
    var target = Pick(request.Target, options.DefaultTarget);
    var mode = Pick(request.Mode, "subtitle");
    var translationProvider = Pick(request.TranslationProvider, options.DefaultProvider);

    for (var index = 0; index < speech.Segments.Count; index++)
    {
        var segment = speech.Segments[index];
        if (string.IsNullOrWhiteSpace(segment.Text))
        {
            continue;
        }

        var contextItems = speech.Segments
            .Where((_, itemIndex) => itemIndex >= Math.Max(0, index - 2) && itemIndex <= Math.Min(speech.Segments.Count - 1, index + 2) && itemIndex != index)
            .Select(item => item.Text)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();

        var outcome = await translationService.TranslateAsync(
            new MortTranslateRequest
            {
                Text = segment.Text,
                Source = source,
                Target = target,
                Mode = mode,
                Glossary = request.Glossary,
                Provider = translationProvider,
                Model = request.Model,
                Profile = request.Profile,
                SessionId = request.SessionId,
                ContextItems = contextItems
            },
            cancellationToken);

        values.Add(new SpeechTranslatedSegment(
            segment.Index,
            segment.StartSeconds,
            segment.EndSeconds,
            segment.Text,
            outcome.Text,
            outcome.ErrorCode,
            outcome.ErrorMessage,
            translationProvider,
            outcome.Engine,
            outcome.LatencyMs,
            outcome.CacheHit));

        if (outcome.IsSuccess)
        {
            await broadcastHub.BroadcastAsync(
                new TranslationBroadcastMessage(
                    "translation",
                    segment.Text,
                    outcome.Text,
                    source,
                    target,
                    mode,
                    translationProvider,
                    request.Glossary,
                    outcome.Engine,
                    outcome.LatencyMs,
                    outcome.CacheHit,
                    DateTimeOffset.UtcNow),
                cancellationToken);
        }
    }

    return values;
}

static async Task StreamSpeechJobEventsAsync(
    string jobId,
    HttpContext context,
    SpeechJobService speechJobs,
    CancellationToken cancellationToken)
{
    var job = await speechJobs.GetAsync(jobId, cancellationToken);
    if (job is null)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync(JsonSerializer.Serialize(AsrError("ASR job was not found.")), cancellationToken);
        return;
    }

    context.Response.Headers.CacheControl = "no-cache";
    context.Response.Headers.Connection = "keep-alive";
    context.Response.ContentType = "text/event-stream; charset=utf-8";

    var afterSequence = ReadEventSequenceCursor(context);
    while (!cancellationToken.IsCancellationRequested)
    {
        var events = await speechJobs.ListEventsAsync(jobId, afterSequence, 100, cancellationToken);
        foreach (var item in events)
        {
            afterSequence = item.Sequence;
            await context.Response.WriteAsync($"id: {item.Sequence}\n", cancellationToken);
            await context.Response.WriteAsync($"event: {item.Type}\n", cancellationToken);
            await context.Response.WriteAsync($"data: {item.PayloadJson}\n\n", cancellationToken);
        }

        if (events.Count > 0)
        {
            await context.Response.Body.FlushAsync(cancellationToken);
            continue;
        }

        job = await speechJobs.GetAsync(jobId, cancellationToken);
        if (job is null || IsTerminalSpeechJobStatus(job.Status))
        {
            break;
        }

        await context.Response.WriteAsync($": ping {DateTimeOffset.UtcNow:O}\n\n", cancellationToken);
        await context.Response.Body.FlushAsync(cancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
    }
}

static long ReadEventSequenceCursor(HttpContext context)
{
    if (context.Request.Query.TryGetValue("after", out var queryValue) &&
        long.TryParse(queryValue.ToString(), out var querySequence))
    {
        return Math.Max(0, querySequence);
    }

    if (context.Request.Headers.TryGetValue("Last-Event-ID", out var headerValue) &&
        long.TryParse(headerValue.ToString(), out var headerSequence))
    {
        return Math.Max(0, headerSequence);
    }

    return 0;
}

static bool IsTerminalSpeechJobStatus(string status)
    => status.Equals("succeeded", StringComparison.OrdinalIgnoreCase) ||
       status.Equals("failed", StringComparison.OrdinalIgnoreCase) ||
       status.Equals("canceled", StringComparison.OrdinalIgnoreCase);

static async Task HandleSpeechLiveSocketAsync(
    HttpContext context,
    SpeechService speechService,
    TranslationService translationService,
    TranslationBroadcastHub broadcastHub,
    YomiBridgeOptions options,
    CancellationToken cancellationToken)
{
    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    var receiveBuffer = new byte[64 * 1024];
    using var audioBuffer = new MemoryStream();
    var config = new LiveSpeechConfig();
    var silenceMs = 0;

    await SendLiveJsonAsync(socket, new { type = "ready" }, jsonOptions, cancellationToken);

    while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
    {
        var message = await ReceiveSocketMessageAsync(socket, receiveBuffer, cancellationToken);
        if (message.MessageType == WebSocketMessageType.Close)
        {
            break;
        }

        if (message.MessageType == WebSocketMessageType.Text)
        {
            using var document = JsonDocument.Parse(message.Payload);
            var root = document.RootElement;
            var type = GetJsonString(root, "type");
            if (string.Equals(type, "start", StringComparison.OrdinalIgnoreCase))
            {
                config = ReadLiveConfig(root);
                audioBuffer.SetLength(0);
                silenceMs = 0;
                await SendLiveJsonAsync(socket, new { type = "started" }, jsonOptions, cancellationToken);
            }
            else if (string.Equals(type, "flush", StringComparison.OrdinalIgnoreCase))
            {
                await FlushLiveSpeechAsync(
                    socket,
                    audioBuffer,
                    config,
                    speechService,
                    translationService,
                    broadcastHub,
                    options,
                    jsonOptions,
                    cancellationToken);
                silenceMs = 0;
            }
            else if (string.Equals(type, "stop", StringComparison.OrdinalIgnoreCase))
            {
                await FlushLiveSpeechAsync(
                    socket,
                    audioBuffer,
                    config,
                    speechService,
                    translationService,
                    broadcastHub,
                    options,
                    jsonOptions,
                    cancellationToken);
                await SendLiveJsonAsync(socket, new { type = "done" }, jsonOptions, cancellationToken);
                break;
            }
        }
        else if (message.MessageType == WebSocketMessageType.Binary)
        {
            audioBuffer.Write(message.Payload, 0, message.Payload.Length);
            if (IsSilentPcm16(message.Payload, options.Speech.Live.SilenceRmsThreshold))
            {
                silenceMs += PcmDurationMs(message.Payload.Length, options.Speech.Live.SampleRate, options.Speech.Live.Channels, options.Speech.Live.BitsPerSample);
            }
            else
            {
                silenceMs = 0;
            }

            var maxBytes = options.Speech.Live.SampleRate *
                Math.Max(1, options.Speech.Live.Channels) *
                Math.Max(1, options.Speech.Live.BitsPerSample / 8) *
                Math.Max(1, options.Speech.Live.MaxSegmentSeconds);
            if (audioBuffer.Length >= maxBytes || (silenceMs >= options.Speech.Live.SilenceDurationMs && audioBuffer.Length > 0))
            {
                await FlushLiveSpeechAsync(
                    socket,
                    audioBuffer,
                    config,
                    speechService,
                    translationService,
                    broadcastHub,
                    options,
                    jsonOptions,
                    cancellationToken);
                silenceMs = 0;
            }
        }
    }

    if (socket.State is WebSocketState.CloseReceived or WebSocketState.Open)
    {
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
    }
}

static async Task FlushLiveSpeechAsync(
    WebSocket socket,
    MemoryStream audioBuffer,
    LiveSpeechConfig config,
    SpeechService speechService,
    TranslationService translationService,
    TranslationBroadcastHub broadcastHub,
    YomiBridgeOptions options,
    JsonSerializerOptions jsonOptions,
    CancellationToken cancellationToken)
{
    if (audioBuffer.Length == 0)
    {
        return;
    }

    var pcm = audioBuffer.ToArray();
    audioBuffer.SetLength(0);
    var wav = PcmWaveWriter.BuildPcmWave(
        pcm,
        Math.Max(1, options.Speech.Live.SampleRate),
        (short)Math.Max(1, options.Speech.Live.Channels),
        (short)Math.Max(1, options.Speech.Live.BitsPerSample));

    try
    {
        var speech = await speechService.RecognizeAsync(
            new SpeechRequest
            {
                AudioBase64 = Convert.ToBase64String(wav),
                AudioMimeType = "audio/wav",
                Provider = Pick(config.Provider, options.Speech.DefaultProvider),
                Language = Pick(config.Language, options.Speech.DefaultLanguage),
                Profile = Pick(config.Profile, "default"),
                SessionId = Pick(config.SessionId, "live"),
                Glossary = config.Glossary,
                PreferCaptions = false
            },
            cancellationToken);

        foreach (var segment in speech.Segments)
        {
            await SendLiveJsonAsync(
                socket,
                new { type = "segment", speech.EventId, segment, speech.Provider, speech.Engine, speech.Language },
                jsonOptions,
                cancellationToken);
        }

        if (config.Translate)
        {
            var translations = await TranslateSpeechSegmentsAsync(
                speech,
                new SpeechTranslateRequest
                {
                    Language = config.Language,
                    Source = config.Source,
                    Target = config.Target,
                    Mode = config.Mode,
                    Glossary = config.Glossary,
                    TranslationProvider = config.TranslationProvider,
                    Model = config.Model,
                    Profile = config.Profile,
                    SessionId = config.SessionId
                },
                translationService,
                broadcastHub,
                options,
                cancellationToken);

            foreach (var translation in translations)
            {
                await SendLiveJsonAsync(
                    socket,
                    new { type = "translation", speech.EventId, translation },
                    jsonOptions,
                    cancellationToken);
            }
        }
    }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or TimeoutException)
    {
        await SendLiveJsonAsync(socket, new { type = "error", errorMessage = ex.Message }, jsonOptions, cancellationToken);
    }
}

static async Task<LiveSocketMessage> ReceiveSocketMessageAsync(
    WebSocket socket,
    byte[] buffer,
    CancellationToken cancellationToken)
{
    using var stream = new MemoryStream();
    WebSocketReceiveResult result;
    do
    {
        result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
        if (result.MessageType == WebSocketMessageType.Close)
        {
            return new LiveSocketMessage(WebSocketMessageType.Close, Array.Empty<byte>());
        }

        stream.Write(buffer, 0, result.Count);
    }
    while (!result.EndOfMessage);

    return new LiveSocketMessage(result.MessageType, stream.ToArray());
}

static async Task SendLiveJsonAsync(
    WebSocket socket,
    object payload,
    JsonSerializerOptions jsonOptions,
    CancellationToken cancellationToken)
{
    if (socket.State != WebSocketState.Open)
    {
        return;
    }

    var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, jsonOptions);
    await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
}

static LiveSpeechConfig ReadLiveConfig(JsonElement root)
    => new(
        GetJsonString(root, "provider"),
        GetJsonString(root, "language"),
        GetJsonString(root, "profile"),
        GetJsonString(root, "sessionId"),
        GetJsonBool(root, "translate") ?? false,
        GetJsonString(root, "source"),
        GetJsonString(root, "target"),
        GetJsonString(root, "mode"),
        GetJsonString(root, "glossary"),
        GetJsonString(root, "translationProvider"),
        GetJsonString(root, "model"));

static string? GetJsonString(JsonElement root, string name)
    => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        ? value.GetString()
        : null;

static bool? GetJsonBool(JsonElement root, string name)
    => root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
        ? value.GetBoolean()
        : null;

static bool IsSilentPcm16(byte[] bytes, double threshold)
{
    if (bytes.Length < 2)
    {
        return true;
    }

    var sampleCount = bytes.Length / 2;
    double sumSquares = 0;
    for (var index = 0; index + 1 < bytes.Length; index += 2)
    {
        var sample = BitConverter.ToInt16(bytes, index) / 32768.0;
        sumSquares += sample * sample;
    }

    var rms = Math.Sqrt(sumSquares / Math.Max(1, sampleCount));
    return rms <= threshold;
}

static int PcmDurationMs(int byteCount, int sampleRate, int channels, int bitsPerSample)
{
    var bytesPerSecond = Math.Max(1, sampleRate) * Math.Max(1, channels) * Math.Max(1, bitsPerSample / 8);
    return (int)Math.Round(byteCount * 1000.0 / bytesPerSecond);
}

static IReadOnlyList<SpeechEngineDescriptor> BuildSpeechEngines(
    SpeechProviderRegistry providers,
    YomiBridgeOptions options)
{
    return providers.List()
        .Select(provider =>
        {
            if (provider.Name.Equals("mock", StringComparison.OrdinalIgnoreCase))
            {
                return new SpeechEngineDescriptor(
                    provider.Name,
                    "Mock ASR (pipeline test)",
                    provider.Kind,
                    provider.DefaultLanguage,
                    IsAvailable: true,
                    IsDefault: provider.Name.Equals(options.Speech.DefaultProvider, StringComparison.OrdinalIgnoreCase),
                    provider.RequiresExternalProcess,
                    provider.IsLocal,
                    "built-in",
                    "Returns UTF-8 text payloads as one segment per line; use only for tests.");
            }

            if (provider.Name.Equals("funasr-http", StringComparison.OrdinalIgnoreCase))
            {
                var available = !string.IsNullOrWhiteSpace(options.Speech.FunAsrHttp.BaseUrl);
                return new SpeechEngineDescriptor(
                    provider.Name,
                    "FunASR HTTP (SenseVoice)",
                    provider.Kind,
                    options.Speech.DefaultLanguage,
                    available,
                    provider.Name.Equals(options.Speech.DefaultProvider, StringComparison.OrdinalIgnoreCase),
                    provider.RequiresExternalProcess,
                    provider.IsLocal,
                    "http",
                    available
                        ? $"Calls {options.Speech.FunAsrHttp.BaseUrl.TrimEnd('/')}/v1/audio/transcriptions with model {options.Speech.FunAsrHttp.Model}."
                        : "FunASR base URL is not configured.");
            }

            if (provider.Name.Equals("external", StringComparison.OrdinalIgnoreCase))
            {
                var available = !string.IsNullOrWhiteSpace(options.Speech.External.FileName);
                return new SpeechEngineDescriptor(
                    provider.Name,
                    provider.DisplayName,
                    provider.Kind,
                    provider.DefaultLanguage,
                    available,
                    provider.Name.Equals(options.Speech.DefaultProvider, StringComparison.OrdinalIgnoreCase),
                    provider.RequiresExternalProcess,
                    provider.IsLocal,
                    "external",
                    available
                        ? "Runs the configured external ASR command and parses JSON or plain text output."
                        : "External ASR command is not configured.");
            }

            return new SpeechEngineDescriptor(
                provider.Name,
                provider.DisplayName,
                provider.Kind,
                provider.DefaultLanguage,
                IsAvailable: true,
                IsDefault: provider.Name.Equals(options.Speech.DefaultProvider, StringComparison.OrdinalIgnoreCase),
                provider.RequiresExternalProcess,
                provider.IsLocal,
                "provider",
                string.Empty);
        })
        .OrderByDescending(engine => engine.IsDefault)
        .ThenByDescending(engine => engine.IsAvailable)
        .ThenBy(engine => engine.DisplayName, StringComparer.OrdinalIgnoreCase)
        .ToArray();
}

static LocalPythonOcrProvider CreateLocalOcrProvider(
    string name,
    string displayName,
    string kind,
    YomiBridgeOptions options,
    string contentRootPath)
{
    return new LocalPythonOcrProvider(
        new OcrProviderDescriptor(
            name,
            displayName,
            kind,
            options.Ocr.DefaultLanguage,
            RequiresExternalProcess: true,
            IsLocal: true),
        name,
        options.Ocr.LocalSet,
        contentRootPath);
}

static async Task<IReadOnlyList<OcrEngineDescriptor>> BuildOcrEnginesAsync(
    OcrProviderRegistry providers,
    YomiBridgeOptions options,
    string contentRootPath,
    CancellationToken cancellationToken)
{
    var providerList = providers.List();
    var registeredNames = providerList
        .Select(provider => provider.Name)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    var values = new List<OcrEngineDescriptor>();

    foreach (var provider in providerList)
    {
        values.Add(await BuildRegisteredOcrEngineAsync(provider, providers, options, contentRootPath, cancellationToken));
    }

    values.AddRange(BuildPlannedOcrSet(options)
        .Where(engine => !registeredNames.Contains(engine.Name)));

    return values
        .OrderByDescending(engine => engine.IsDefault)
        .ThenByDescending(engine => engine.IsAvailable)
        .ThenBy(engine => engine.RequiresApiConfiguration)
        .ThenBy(engine => engine.Status, StringComparer.OrdinalIgnoreCase)
        .ThenBy(engine => engine.DisplayName, StringComparer.OrdinalIgnoreCase)
        .ToArray();
}

static async Task<OcrEngineDescriptor> BuildRegisteredOcrEngineAsync(
    OcrProviderDescriptor provider,
    OcrProviderRegistry providers,
    YomiBridgeOptions options,
    string contentRootPath,
    CancellationToken cancellationToken)
{
    if (provider.Name.Equals("mock", StringComparison.OrdinalIgnoreCase))
    {
        return new OcrEngineDescriptor(
            provider.Name,
            "Mock OCR (pipeline test)",
            provider.Kind,
            provider.DefaultLanguage,
            IsAvailable: true,
            IsDefault: provider.Name.Equals(options.Ocr.DefaultProvider, StringComparison.OrdinalIgnoreCase),
            provider.RequiresExternalProcess,
            provider.IsLocal,
            "built-in",
            "available",
            RequiresApiConfiguration: false,
            "Returns embedded text payloads; use only for testing the OCR pipeline.");
    }

    if (provider.Name.Equals("external", StringComparison.OrdinalIgnoreCase))
    {
        var isWindowsOcr = options.Ocr.External.Arguments.Contains(
            "windows_ocr_json.ps1",
            StringComparison.OrdinalIgnoreCase);
        var scriptPath = isWindowsOcr
            ? PathResolver.Resolve(contentRootPath, @"..\..\scripts\windows_ocr_json.ps1")
            : string.Empty;
        var hasCommand = !string.IsNullOrWhiteSpace(options.Ocr.External.FileName);
        var hasScript = !isWindowsOcr || File.Exists(scriptPath);
        var available = hasCommand && hasScript;
        var note = available
            ? isWindowsOcr
                ? "Uses the local Windows OCR language packs through Windows.Media.Ocr."
                : "Runs the configured external OCR command."
            : hasCommand
                ? "External OCR command is configured, but its script was not found."
                : "External OCR command is not configured.";

        return new OcrEngineDescriptor(
            provider.Name,
            isWindowsOcr ? "Windows OCR (local)" : provider.DisplayName,
            provider.Kind,
            provider.DefaultLanguage,
            available,
            provider.Name.Equals(options.Ocr.DefaultProvider, StringComparison.OrdinalIgnoreCase),
            provider.RequiresExternalProcess,
            provider.IsLocal,
            isWindowsOcr ? "windows" : "external",
            available ? "available" : "unavailable",
            RequiresApiConfiguration: false,
            note);
    }

    if (IsLocalOcrSetEngine(provider.Name) &&
        providers.GetRequired(provider.Name) is LocalPythonOcrProvider localProvider)
    {
        var status = await localProvider.CheckAsync(cancellationToken);
        var missing = status.Missing.Count > 0
            ? $" Missing: {string.Join(", ", status.Missing)}."
            : string.Empty;

        return new OcrEngineDescriptor(
            provider.Name,
            provider.DisplayName,
            provider.Kind,
            provider.DefaultLanguage,
            status.IsAvailable,
            provider.Name.Equals(options.Ocr.DefaultProvider, StringComparison.OrdinalIgnoreCase),
            provider.RequiresExternalProcess,
            provider.IsLocal,
            "local",
            status.IsAvailable ? "available" : "missing_dependency",
            RequiresApiConfiguration: false,
            $"{LocalOcrSetNote(provider.Name)} {status.Note}{missing}".Trim());
    }

    return new OcrEngineDescriptor(
        provider.Name,
        provider.DisplayName,
        provider.Kind,
        provider.DefaultLanguage,
        IsAvailable: true,
        IsDefault: provider.Name.Equals(options.Ocr.DefaultProvider, StringComparison.OrdinalIgnoreCase),
        provider.RequiresExternalProcess,
        provider.IsLocal,
        "provider",
        "available",
        RequiresApiConfiguration: false,
        string.Empty);
}

static bool IsLocalOcrSetEngine(string name)
    => name.Equals("tesseract", StringComparison.OrdinalIgnoreCase) ||
       name.Equals("easyocr", StringComparison.OrdinalIgnoreCase) ||
       name.Equals("paddleocr", StringComparison.OrdinalIgnoreCase) ||
       name.Equals("pix2text", StringComparison.OrdinalIgnoreCase) ||
       name.Equals("pp-structure-v3", StringComparison.OrdinalIgnoreCase) ||
       name.Equals("paddleocr-vl", StringComparison.OrdinalIgnoreCase) ||
       name.Equals("dots-ocr", StringComparison.OrdinalIgnoreCase);

static string LocalOcrSetNote(string name)
    => name.ToLowerInvariant() switch
    {
        "tesseract" => "Offline Tesseract OCR provider.",
        "easyocr" => "Local EasyOCR provider.",
        "paddleocr" => "Local PaddleOCR text provider; not a structure OCR path.",
        "pix2text" => "Local Pix2Text provider for math/table-like document extraction.",
        "pp-structure-v3" => "Local PP-StructureV3 provider for layout, tables, and formulas.",
        "paddleocr-vl" => "Local PaddleOCR-VL provider for high-precision document structure OCR.",
        "dots-ocr" => "Local dots.ocr-compatible VLM provider; large model weights may be downloaded on first run.",
        _ => string.Empty
    };

static IReadOnlyList<OcrEngineDescriptor> BuildPlannedOcrSet(YomiBridgeOptions options)
{
    var defaultLanguage = options.Ocr.DefaultLanguage;
    return
    [
        new OcrEngineDescriptor(
            "tesseract",
            "Tesseract OCR (local planned)",
            "local-process",
            defaultLanguage,
            IsAvailable: false,
            IsDefault: false,
            RequiresExternalProcess: true,
            IsLocal: true,
            "local",
            "planned_local",
            RequiresApiConfiguration: false,
            "Planned local OCR provider for offline use; not wired yet."),
        new OcrEngineDescriptor(
            "easyocr",
            "EasyOCR (local planned)",
            "local-python",
            defaultLanguage,
            IsAvailable: false,
            IsDefault: false,
            RequiresExternalProcess: true,
            IsLocal: true,
            "local",
            "planned_local",
            RequiresApiConfiguration: false,
            "Planned local Python OCR provider; not wired yet."),
        new OcrEngineDescriptor(
            "paddleocr",
            "PaddleOCR / PP-OCR text (local planned)",
            "local-python",
            defaultLanguage,
            IsAvailable: false,
            IsDefault: false,
            RequiresExternalProcess: true,
            IsLocal: true,
            "local",
            "planned_local",
            RequiresApiConfiguration: false,
            "Planned local line/text OCR provider with strong CJK support. Not suitable as the primary path for formulas or tables."),
        new OcrEngineDescriptor(
            "rapidocr-ppocrv5",
            "RapidOCR + PP-OCRv5 (realtime text planned)",
            "local-onnx",
            defaultLanguage,
            IsAvailable: false,
            IsDefault: false,
            RequiresExternalProcess: true,
            IsLocal: true,
            "local",
            "planned_local",
            RequiresApiConfiguration: false,
            "Planned low-latency text-line OCR path for game dialogue and prose. It does not preserve formula/table structure."),
        new OcrEngineDescriptor(
            "snipping-tool-ocr",
            "Snipping Tool OCR (Windows planned)",
            "windows",
            defaultLanguage,
            IsAvailable: false,
            IsDefault: false,
            RequiresExternalProcess: true,
            IsLocal: true,
            "windows",
            "planned_local",
            RequiresApiConfiguration: false,
            "Listed for parity with MORT-style OCR choices; not wired yet."),
        new OcrEngineDescriptor(
            "paddleocr-vl",
            "PaddleOCR-VL (structure planned)",
            "local-vlm",
            defaultLanguage,
            IsAvailable: false,
            IsDefault: false,
            RequiresExternalProcess: true,
            IsLocal: true,
            "local",
            "planned_structure",
            RequiresApiConfiguration: false,
            "Preferred planned high-precision structure OCR for formulas, tables, charts, and document regions. Formulas should bypass translation; table text should translate cell-by-cell."),
        new OcrEngineDescriptor(
            "pp-structure-v3",
            "PP-StructureV3 (structure planned)",
            "local-pipeline",
            defaultLanguage,
            IsAvailable: false,
            IsDefault: false,
            RequiresExternalProcess: true,
            IsLocal: true,
            "local",
            "planned_structure",
            RequiresApiConfiguration: false,
            "Planned lighter structured OCR/layout pipeline for formulas and tables when full VLM OCR is too heavy. Not wired yet."),
        new OcrEngineDescriptor(
            "pix2text",
            "Pix2Text (math/table planned)",
            "local-python",
            defaultLanguage,
            IsAvailable: false,
            IsDefault: false,
            RequiresExternalProcess: true,
            IsLocal: true,
            "local",
            "planned_structure",
            RequiresApiConfiguration: false,
            "Planned local helper for formulas, tables, and markdown-like document extraction. Not wired yet."),
        new OcrEngineDescriptor(
            "dots-ocr",
            "dots.ocr (document VLM listed)",
            "local-vlm",
            defaultLanguage,
            IsAvailable: false,
            IsDefault: false,
            RequiresExternalProcess: true,
            IsLocal: true,
            "local",
            "planned_structure",
            RequiresApiConfiguration: false,
            "Listed for document OCR comparison, but not preferred for table-heavy regions. Not wired yet."),
        new OcrEngineDescriptor(
            "google-cloud-vision",
            "Google Cloud Vision OCR (API listed)",
            "cloud-api",
            defaultLanguage,
            IsAvailable: false,
            IsDefault: false,
            RequiresExternalProcess: false,
            IsLocal: false,
            "api",
            "requires_api_configuration",
            RequiresApiConfiguration: true,
            "Listed only. Requires Google Cloud credentials and billing; not implemented yet."),
        new OcrEngineDescriptor(
            "deepseek-ocr-vlm",
            "DeepSeek-OCR / VLM OCR (API listed)",
            "vlm-api",
            defaultLanguage,
            IsAvailable: false,
            IsDefault: false,
            RequiresExternalProcess: false,
            IsLocal: false,
            "api",
            "requires_api_configuration",
            RequiresApiConfiguration: true,
            "Listed only. Requires a VLM/OCR API runtime and configuration; not implemented yet."),
        new OcrEngineDescriptor(
            "mathpix",
            "Mathpix (API listed)",
            "cloud-api",
            defaultLanguage,
            IsAvailable: false,
            IsDefault: false,
            RequiresExternalProcess: false,
            IsLocal: false,
            "api",
            "requires_api_configuration",
            RequiresApiConfiguration: true,
            "Listed only. Strong formula/table OCR, but requires paid cloud API configuration; not implemented yet.")
    ];
}

internal sealed record LiveSocketMessage(WebSocketMessageType MessageType, byte[] Payload);

internal sealed record LiveSpeechConfig(
    string? Provider = null,
    string? Language = null,
    string? Profile = null,
    string? SessionId = null,
    bool Translate = false,
    string? Source = null,
    string? Target = null,
    string? Mode = null,
    string? Glossary = null,
    string? TranslationProvider = null,
    string? Model = null);

public partial class Program;
