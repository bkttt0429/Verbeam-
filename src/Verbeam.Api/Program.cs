using Verbeam.Api.Broadcast;
using Verbeam.Api.Audio;
using Verbeam.Api.Pages;
using Verbeam.Api.Tray;
using Verbeam.Core.Models;
using Verbeam.Core.Options;
using Verbeam.Core.Providers;
using Verbeam.Core.Services;
using Verbeam.Core.Storage;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using System.Drawing.Imaging;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddEnvironmentVariables(prefix: "VB_");
builder.WebHost.UseUrls(builder.Configuration["Urls"] ?? "http://localhost:5757");

var options = builder.Configuration.GetSection("Verbeam").Get<VerbeamOptions>()
    ?? new VerbeamOptions();

// SSE payloads must keep CJK characters unescaped so the streamed deltas render directly.
var SseJsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
};

var presetsPath = PathResolver.Resolve(builder.Environment.ContentRootPath, options.PresetsDirectory);
var glossariesPath = PathResolver.Resolve(builder.Environment.ContentRootPath, options.GlossariesDirectory);
var modelCatalogPath = PathResolver.Resolve(builder.Environment.ContentRootPath, options.ModelCatalogPath);
var modelCatalogCachePath = PathResolver.Resolve(builder.Environment.ContentRootPath, options.ModelCatalog.CachePath);
var apiSupplierCatalogPath = PathResolver.Resolve(builder.Environment.ContentRootPath, options.ApiSuppliers.CatalogPath);
var apiSupplierCatalogCachePath = PathResolver.Resolve(builder.Environment.ContentRootPath, options.ApiSuppliers.CachePath);
var apiSupplierStorePath = PathResolver.Resolve(builder.Environment.ContentRootPath, options.ApiSuppliers.StorePath);
var apiSupplierSecretsPath = PathResolver.Resolve(builder.Environment.ContentRootPath, options.ApiSuppliers.SecretsPath);
var translationRoutesPath = PathResolver.Resolve(builder.Environment.ContentRootPath, options.ApiSuppliers.RoutesPath);
var cachePath = PathResolver.Resolve(builder.Environment.ContentRootPath, options.CachePath);
var gameProfilesPath = PathResolver.Resolve(builder.Environment.ContentRootPath, options.GameProfilesPath);
var llamaCppRuntimeSettingsPath = PathResolver.Resolve(builder.Environment.ContentRootPath, options.LlamaCpp.RuntimeSettingsPath);
var hotkeySettingsPath = PathResolver.Resolve(builder.Environment.ContentRootPath, options.Hotkeys.SettingsPath);
var shellSettingsPath = PathResolver.Resolve(builder.Environment.ContentRootPath, options.Shell.SettingsPath);

var llamaCppRuntimeSettingsStore = new LlamaCppRuntimeSettingsStore(llamaCppRuntimeSettingsPath);
// The persisted Install-and-Use choice wins over appsettings; otherwise the
// in-memory switch to managed mode is lost on restart and llama-cpp requests
// hit a remote endpoint nobody started (connection refused on localhost:8088).
await llamaCppRuntimeSettingsStore.ApplyAsync(options.LlamaCpp);
var hotkeySettingsService = new HotkeySettingsService(options.Hotkeys, hotkeySettingsPath);
await hotkeySettingsService.ApplyPersistedAsync(options.Hotkeys);
var hotkeyRuntimeService = new HotkeyRuntimeService(hotkeySettingsService);
var shellSettingsService = new ShellSettingsService(options.Shell, shellSettingsPath);
await shellSettingsService.ApplyPersistedAsync(options.Shell);

var modelCatalogService = new ModelCatalogService(
    modelCatalogPath,
    modelCatalogCachePath,
    options.ModelCatalog,
    new HttpClient());
await modelCatalogService.InitializeAsync();
var apiSupplierPresetCatalogService = new ApiSupplierPresetCatalogService(
    apiSupplierCatalogPath,
    apiSupplierCatalogCachePath,
    options.ApiSuppliers,
    new HttpClient());
await apiSupplierPresetCatalogService.InitializeAsync();
var apiSupplierStore = new ApiSupplierStore(apiSupplierStorePath);
var apiSecretStore = new ApiSecretStore(apiSupplierSecretsPath);
var translationRouteStore = new TranslationRouteStore(translationRoutesPath);
var gameProfileStore = new GameProfileStore(gameProfilesPath);
var llamaCppDownloadTracker = new LlamaCppDownloadTracker();
var llamaCppArtifactStore = new LlamaCppArtifactStore(
    options.LlamaCpp,
    modelCatalogService,
    builder.Environment.ContentRootPath,
    new HttpClient
    {
        Timeout = TimeSpan.FromMinutes(30)
    },
    llamaCppDownloadTracker);
var hardwareProbe = new HardwareProbe();
var llamaCppBinaryStore = new LlamaCppBinaryStore(
    options.LlamaCpp,
    modelCatalogService,
    builder.Environment.ContentRootPath,
    new HttpClient
    {
        Timeout = TimeSpan.FromMinutes(30)
    },
    llamaCppDownloadTracker,
    hardwareProbe);
var llamaCppRuntimeManager = new LlamaCppRuntimeManager(
    options.LlamaCpp,
    modelCatalogService,
    builder.Environment.ContentRootPath,
    new HttpClient(),
    llamaCppArtifactStore,
    llamaCppBinaryStore);
var ollamaRuntimeManager = new OllamaRuntimeManager(
    options.Ollama,
    builder.Environment.ContentRootPath,
    new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(Math.Clamp(options.Ollama.ModelDiscoveryTimeoutSeconds, 1, 10))
    });
var llamaCppInstallService = new LlamaCppInstallService(
    options,
    modelCatalogService,
    llamaCppArtifactStore,
    llamaCppBinaryStore,
    llamaCppRuntimeManager,
    llamaCppRuntimeSettingsStore);
var memoryBearerJwtKeyStore = new MemoryBearerJwtKeyStore(
    options.Memory.BearerJwt,
    builder.Environment.ContentRootPath,
    new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(5)
    });
var memoryOidcClient = new MemoryOidcClient(
    options.Memory.Oidc,
    new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(10)
    });
var cache = new MemoryFrontedTranslationCache(new SqliteTranslationCache(cachePath));
await cache.InitializeAsync();

// Per-game database partitioning. The router maps a (domain, gameId ≡ profileId) pair to
// a physical SQLite file; GameScopedStores resolves a per-game store of each kind, all
// sharing that game's games/{gameId}/realtime.sqlite. GameScopedServices (built below)
// composes the stateless RAG/memory services bound to one game's stores. Function-domain
// stores (document/speech/ocr/principals) stay on the shared file until their own phase.
// DataDirectory defaults to the existing cache file's directory.
var embeddingProvider = new HashEmbeddingProvider(options.Memory.EmbeddingDimensions);
var databaseDataDirectory = string.IsNullOrWhiteSpace(options.Database.DataDirectory)
    ? Path.GetDirectoryName(cachePath) ?? builder.Environment.ContentRootPath
    : PathResolver.Resolve(builder.Environment.ContentRootPath, options.Database.DataDirectory);
var databaseRouter = new DatabaseRouter(databaseDataDirectory, options.Database);
var gameScopedStores = new GameScopedStores(
    databaseRouter,
    path => new MemoryFrontedTranslationCache(new SqliteTranslationCache(path)),
    path => new SqliteTranslationEventStore(path),
    path => new SqliteMemoryStore(path),
    path => new SqliteMemoryContextAuditStore(path),
    path => new SqliteSceneSummaryStore(path),
    path => new SqliteMemoryMaintenanceJobStore(path));
var translationEvents = new SqliteTranslationEventStore(cachePath);
await translationEvents.InitializeAsync();
var sceneSummaries = new SqliteSceneSummaryStore(cachePath);
await sceneSummaries.InitializeAsync();
var memoryStore = new SqliteMemoryStore(cachePath);
await memoryStore.InitializeAsync();
var memoryPrincipalPermissions = new SqliteMemoryPrincipalPermissionStore(cachePath);
await memoryPrincipalPermissions.InitializeAsync();
var memoryPrincipalSessions = new SqliteMemoryPrincipalSessionStore(cachePath);
await memoryPrincipalSessions.InitializeAsync();
var memoryPrincipalCredentials = new SqliteMemoryPrincipalCredentialStore(cachePath);
await memoryPrincipalCredentials.InitializeAsync();
var memoryMaintenanceJobs = new SqliteMemoryMaintenanceJobStore(cachePath);
await memoryMaintenanceJobs.InitializeAsync();
IMemoryOidcRefreshTokenStore? memoryOidcRefreshTokens = null;
if (!string.IsNullOrWhiteSpace(options.Memory.Oidc.RefreshTokenProtectionKey))
{
    var sqliteMemoryOidcRefreshTokens = new SqliteMemoryOidcRefreshTokenStore(
        cachePath,
        options.Memory.Oidc.RefreshTokenProtectionKey);
    await sqliteMemoryOidcRefreshTokens.InitializeAsync();
    memoryOidcRefreshTokens = sqliteMemoryOidcRefreshTokens;
}

var memoryAudit = new SqliteMemoryContextAuditStore(cachePath);
await memoryAudit.InitializeAsync();
var ocrMemory = new SqliteOcrMemoryStore(cachePath);
await ocrMemory.InitializeAsync();
// Per-game RAG/memory services. The OCR memory store is still shared here — it moves
// per-game in the OCR phase, which is why it is passed in explicitly.
var gameScopedServices = new GameScopedServices(gameScopedStores, options, embeddingProvider, ocrMemory);
var ocrJobs = new SqliteOcrJobStore(cachePath);
await ocrJobs.InitializeAsync();
var ocrBlockAnnotations = new SqliteOcrBlockAnnotationStore(cachePath);
await ocrBlockAnnotations.InitializeAsync();
var ocrBlockLayout = new SqliteOcrBlockLayoutStore(cachePath);
await ocrBlockLayout.InitializeAsync();
var documentJobs = new SqliteDocumentJobStore(cachePath);
await documentJobs.InitializeAsync();
var speechEvents = new SqliteSpeechEventStore(cachePath);
await speechEvents.InitializeAsync();
var speechJobs = new SqliteSpeechJobStore(cachePath);
await speechJobs.InitializeAsync();
var videoSpeechSessions = new SqliteVideoSpeechSessionStore(cachePath);
await videoSpeechSessions.InitializeAsync();
var speechBufferPath = Path.Combine(Path.GetDirectoryName(cachePath) ?? builder.Environment.ContentRootPath, "speech-buffers");

builder.Services.AddSingleton(options);
builder.Services.AddSingleton(hotkeySettingsService);
builder.Services.AddSingleton(hotkeyRuntimeService);
builder.Services.AddSingleton(shellSettingsService);
builder.Services.AddSingleton<IEmbeddingProvider>(embeddingProvider);
builder.Services.AddSingleton(modelCatalogService);
builder.Services.AddSingleton(apiSupplierPresetCatalogService);
builder.Services.AddSingleton(apiSupplierStore);
builder.Services.AddSingleton(apiSecretStore);
builder.Services.AddSingleton(translationRouteStore);
builder.Services.AddSingleton(gameProfileStore);
builder.Services.AddSingleton<WindowsAudioSessionService>();
builder.Services.AddSingleton<Verbeam.Api.Tray.NativeRegionService>();
builder.Services.AddSingleton<VisibleWindowSurfaceAnalyzer>();
builder.Services.AddSingleton<Verbeam.Api.Tray.WindowThumbnailService>();
builder.Services.AddSingleton<RegionSelectionSafety>();
builder.Services.AddSingleton(_ => new ApiModelDiscoveryService(
    new HttpClient(),
    options.ApiSuppliers,
    apiSecretStore,
    apiSupplierPresetCatalogService));
builder.Services.AddSingleton(_ => new ApiBalanceQueryService(
    new HttpClient(),
    options.ApiSuppliers,
    apiSecretStore,
    apiSupplierPresetCatalogService));
builder.Services.AddSingleton(llamaCppDownloadTracker);
builder.Services.AddSingleton(llamaCppArtifactStore);
builder.Services.AddSingleton(hardwareProbe);
builder.Services.AddSingleton(llamaCppBinaryStore);
builder.Services.AddSingleton(llamaCppRuntimeManager);
builder.Services.AddSingleton(ollamaRuntimeManager);
builder.Services.AddSingleton(llamaCppInstallService);
builder.Services.AddSingleton(new ContextCompressionService(options.ContextCompression));
builder.Services.AddSingleton(new TranslationConfigurationCatalog(options, modelCatalogService));
builder.Services.AddSingleton(_ => new OllamaModelCatalog(
    new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(Math.Clamp(options.Ollama.ModelDiscoveryTimeoutSeconds, 1, 10))
    },
    options.Ollama));
builder.Services.AddSingleton(new PromptPresetStore(presetsPath));
builder.Services.AddSingleton(new GlossaryStore(glossariesPath));
builder.Services.AddSingleton<ITranslationCache>(cache);
builder.Services.AddSingleton<IDatabaseRouter>(databaseRouter);
builder.Services.AddSingleton(gameScopedStores);
builder.Services.AddSingleton(gameScopedServices);
builder.Services.AddSingleton<ITranslationEventStore>(translationEvents);
// Factory registration so the container disposes the batcher (flushing buffered
// realtime events) on shutdown.
builder.Services.AddSingleton(_ => new TranslationEventBatcher(gameScopedStores.EventsFor));
builder.Services.AddSingleton<RealtimeTemplateCache>();
builder.Services.AddSingleton<RealtimeContextWindow>();
builder.Services.AddSingleton<ISceneSummaryStore>(sceneSummaries);
builder.Services.AddSingleton<IMemoryStore>(memoryStore);
builder.Services.AddSingleton<IMemoryPrincipalPermissionStore>(memoryPrincipalPermissions);
builder.Services.AddSingleton<IMemoryPrincipalSessionStore>(memoryPrincipalSessions);
builder.Services.AddSingleton<IMemoryPrincipalCredentialStore>(memoryPrincipalCredentials);
builder.Services.AddSingleton<IMemoryMaintenanceJobStore>(memoryMaintenanceJobs);
if (memoryOidcRefreshTokens is not null)
{
    builder.Services.AddSingleton(memoryOidcRefreshTokens);
}

builder.Services.AddSingleton<IMemoryContextAuditStore>(memoryAudit);
builder.Services.AddSingleton(memoryBearerJwtKeyStore);
builder.Services.AddSingleton<IMemoryOidcClient>(memoryOidcClient);
builder.Services.AddSingleton<MemoryContextBuilder>();
builder.Services.AddSingleton<SceneSummaryMaintenanceService>();
builder.Services.AddSingleton<MemoryMaintenanceService>();
builder.Services.AddSingleton<IOcrMemoryStore>(ocrMemory);
builder.Services.AddSingleton<IOcrJobStore>(ocrJobs);
builder.Services.AddSingleton<IOcrBlockAnnotationStore>(ocrBlockAnnotations);
builder.Services.AddSingleton<IOcrBlockLayoutStore>(ocrBlockLayout);
builder.Services.AddSingleton<Pdf2zhTranslationBridge>();
builder.Services.AddSingleton<IDocumentJobStore>(documentJobs);
builder.Services.AddSingleton<ISpeechEventStore>(speechEvents);
builder.Services.AddSingleton<ISpeechJobStore>(speechJobs);
builder.Services.AddSingleton<IVideoSpeechSessionStore>(videoSpeechSessions);
builder.Services.AddSingleton<ITranslationProvider, MockTranslationProvider>();
builder.Services.AddSingleton<ITranslationProvider>(_ =>
{
    var httpClient = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(Math.Max(1, options.Ollama.TimeoutSeconds))
    };

    return new OllamaTranslationProvider(httpClient, options.Ollama);
});
builder.Services.AddSingleton<ITranslationProvider>(_ =>
{
    var httpClient = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(Math.Clamp(options.LlamaCpp.RequestTimeoutSeconds, 1, 600))
    };

    return new LlamaCppTranslationProvider(httpClient, options.LlamaCpp, llamaCppRuntimeManager);
});
builder.Services.AddSingleton<ITranslationProvider>(_ =>
{
    var httpClient = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(Math.Clamp(options.ApiSuppliers.RequestTimeoutSeconds, 1, 600))
    };

    return new ApiCompatibleTranslationProvider(
        httpClient,
        options.ApiSuppliers,
        apiSupplierStore,
        apiSecretStore,
        apiSupplierPresetCatalogService,
        translationRouteStore);
});
builder.Services.AddSingleton<ITranslationProvider>(_ =>
{
    var httpClient = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(Math.Clamp(options.DeepL.RequestTimeoutSeconds, 1, 600))
    };

    return new DeepLTranslationProvider(httpClient, options.DeepL);
});
// Hybrid = local first, cloud rescue on a decode spike. Resolves its inner providers lazily from
// the registry (which contains this provider too) to avoid a construction-time DI cycle.
builder.Services.AddSingleton<ITranslationProvider>(sp => new HybridTranslationProvider(
    () => sp.GetRequiredService<TranslationProviderRegistry>().GetRequired(options.Hybrid.LocalProvider),
    () => sp.GetRequiredService<TranslationProviderRegistry>().GetRequired(options.Hybrid.CloudProvider),
    options.Hybrid.DeadlineMs));
builder.Services.AddSingleton<IOcrProvider, MockOcrProvider>();
builder.Services.AddSingleton<IOcrProvider>(_ => new ExternalCommandOcrProvider(options.Ocr.External, builder.Environment.ContentRootPath));
if (OneOcrProvider.TryResolveRuntime(out var oneOcrRuntime, out _) && oneOcrRuntime is not null)
{
    builder.Services.AddSingleton<IOcrProvider>(_ => new OneOcrProvider(
        new OcrProviderDescriptor(
            "oneocr",
            "Snipping Tool OCR (OneOCR)",
            "local-native",
            options.Ocr.DefaultLanguage,
            RequiresExternalProcess: false,
            IsLocal: true)
        {
            IsLanguageAgnostic = true
        },
        oneOcrRuntime));
}
if (WindowsMediaOcrProvider.TryProbeAvailability(out _))
{
    builder.Services.AddSingleton<IOcrProvider>(_ => new WindowsMediaOcrProvider(
        new OcrProviderDescriptor(
            "windows",
            "Windows OCR (in-process)",
            "local-winrt",
            options.Ocr.DefaultLanguage,
            RequiresExternalProcess: false,
            IsLocal: true)));
}
if (AppleVisionOcrProvider.TryProbeAvailability(builder.Environment.ContentRootPath, configuredPath: null, out var appleVisionHelperPath, out _))
{
    builder.Services.AddSingleton<IOcrProvider>(_ => AppleVisionOcrProvider.Create(
        appleVisionHelperPath,
        builder.Environment.ContentRootPath,
        options.Ocr.DefaultLanguage));
}
builder.Services.AddSingleton<IOcrProvider>(_ => new RapidOcrNetProvider(
    new OcrProviderDescriptor(
        "rapidocr-net",
        "RapidOcrNet / PP-OCRv5 ONNX",
        "local-dotnet-onnx",
        options.Ocr.DefaultLanguage,
        RequiresExternalProcess: false,
        IsLocal: true)
    {
        IsLanguageAgnostic = true
    },
    options.Ocr.RapidOcrNet,
    builder.Environment.ContentRootPath));
if (HasConfiguredRapidOcrNetModels(options.Ocr.RapidOcrNetV6, builder.Environment.ContentRootPath))
{
    builder.Services.AddSingleton<IOcrProvider>(_ => new RapidOcrNetProvider(
        new OcrProviderDescriptor(
            "rapidocr-net-v6",
            "RapidOcrNet / PP-OCRv6 ONNX",
            "local-dotnet-onnx",
            options.Ocr.DefaultLanguage,
            RequiresExternalProcess: false,
            IsLocal: true)
        {
            IsLanguageAgnostic = true
        },
        options.Ocr.RapidOcrNetV6,
        builder.Environment.ContentRootPath));
}
builder.Services.AddSingleton<IOcrProvider>(_ => CreateLocalOcrProvider("tesseract", "Tesseract OCR", "local-process", options, builder.Environment.ContentRootPath));
builder.Services.AddSingleton<IOcrProvider>(_ => CreateLocalOcrProvider("rapidocr-ppocrv5", "RapidOCR + PP-OCRv5", "local-onnx", options, builder.Environment.ContentRootPath));
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
builder.Services.AddSingleton<OcrRoutingService>();
builder.Services.AddSingleton<OcrConcurrencyLimiter>();
builder.Services.AddSingleton<IVideoMediaSourceProvider, MockVideoMediaSourceProvider>();
builder.Services.AddSingleton<IVideoMediaSourceProvider>(provider => new YouTubeMediaSourceProvider(provider.GetRequiredService<SpeechService>()));
builder.Services.AddSingleton<IVideoMediaSourceProvider>(provider => new BilibiliMediaSourceProvider(provider.GetRequiredService<SpeechService>()));
builder.Services.AddSingleton<VideoMediaSourceRegistry>();
builder.Services.AddSingleton<TranslationService>();
builder.Services.AddSingleton<ReadFrogTranslationService>();
builder.Services.AddSingleton<RecurringTextSuppressor>();
builder.Services.AddSingleton<OcrService>();
builder.Services.AddSingleton(provider => new DocumentJobService(
    options,
    provider.GetRequiredService<IDocumentJobStore>(),
    provider.GetRequiredService<OcrService>(),
    provider.GetRequiredService<TranslationService>(),
    builder.Environment.ContentRootPath));
builder.Services.AddSingleton<SpeechService>();
builder.Services.AddSingleton<OcrJobService>();
builder.Services.AddSingleton<SpeechJobService>();
builder.Services.AddSingleton<VideoSpeechEventBroker>();
builder.Services.AddSingleton(provider => new VideoSpeechSessionService(
    options,
    provider.GetRequiredService<VideoMediaSourceRegistry>(),
    provider.GetRequiredService<SpeechProviderRegistry>(),
    provider.GetRequiredService<GlossaryStore>(),
    provider.GetRequiredService<IVideoSpeechSessionStore>(),
    provider.GetRequiredService<SpeechService>(),
    provider.GetRequiredService<TranslationService>(),
    provider.GetRequiredService<VideoSpeechEventBroker>(),
    speechBufferPath));

var app = builder.Build();
var startedAt = DateTimeOffset.UtcNow;

if (options.Ollama.AutoStart)
{
    _ = Task.Run(async () =>
    {
        var status = await ollamaRuntimeManager.EnsureStartedAsync(CancellationToken.None);
        Console.Error.WriteLine(status.IsReady
            ? $"[ollama] ready at {status.BaseUrl}{(status.StartedByVerbeam ? " (started by Verbeam)" : "")}."
            : $"[ollama] unavailable at {status.BaseUrl}: {status.LastError}");
    });
}

// Preload the managed llama.cpp runtime in the background so the user's first
// translation doesn't pay model load + compute-graph/Vulkan compilation, which
// can exceed the per-request timeout and surface as a TaskCanceledException.
if (options.LlamaCpp.Mode.Equals("managed", StringComparison.OrdinalIgnoreCase))
{
    _ = Task.Run(async () =>
    {
        try
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            await llamaCppRuntimeManager.WarmUpAsync(CancellationToken.None);
            Console.Error.WriteLine($"[llama-cpp] warmed up managed runtime in {stopwatch.ElapsedMilliseconds} ms.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[llama-cpp] managed warmup failed: {ex.Message}");
        }
    });
}

// Preload OCR engines in the background so the first region frame doesn't pay
// model startup (RapidOCR worker init alone is 1-3 seconds).
_ = Task.Run(async () =>
{
    // 32x32 white PNG.
    var warmupImage = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAACAAAAAgCAAAAABWESUoAAAAFklEQVR4nGP4TwAwjCoYVTCqYKQqAAA/aPwuq5iY/wAAAABJRU5ErkJggg==");
    var registry = app.Services.GetRequiredService<OcrProviderRegistry>();
    var warmupNames = new[] { options.Ocr.DefaultProvider, "rapidocr-net-v6", "rapidocr-net", "windows" }
        .Where(name => !string.IsNullOrWhiteSpace(name) &&
            !name.Equals(OcrRoutingService.AutoProvider, StringComparison.OrdinalIgnoreCase) &&
            registry.Contains(name))
        .Distinct(StringComparer.OrdinalIgnoreCase);
    foreach (var name in warmupNames)
    {
        try
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            // Warm up with a concrete language: the default may be "auto", which providers
            // never see (OcrService resolves it before calling them).
            var warmupLanguage = LanguageRegistry.IsAuto(options.Ocr.DefaultLanguage)
                ? LanguageRegistry.Japanese
                : LanguageRegistry.Normalize(options.Ocr.DefaultLanguage);
            await registry.GetRequired(name).RecognizeAsync(
                new OcrProviderRequest(warmupImage, "image/png", warmupLanguage, NormalizeWhitespace: false),
                CancellationToken.None);
            Console.Error.WriteLine($"[ocr] warmed up '{name}' in {stopwatch.ElapsedMilliseconds} ms.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ocr] warmup for '{name}' failed: {ex.Message}");
        }
    }
});

app.UseWebSockets();
// Ensure ES modules (.mjs, e.g. vendored PDF.js) are served as JavaScript so the browser
// accepts `import` under strict MIME checking.
var staticContentTypeProvider = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
staticContentTypeProvider.Mappings[".mjs"] = "text/javascript";
app.UseStaticFiles(new StaticFileOptions { ContentTypeProvider = staticContentTypeProvider });

app.MapGet("/", () => Results.Redirect("/health"));
app.MapGet("/design-system", () => Results.Redirect("/app"));
app.MapGet("/design-system/reference", () => Results.Redirect("/design-system/ui_kits/workbench/index.html"));

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
    var detected = hardwareProbe.Detect();
    var funAsrHealth = await ProbeFunAsrHealthAsync(options.Speech.FunAsrHttp.BaseUrl, cancellationToken);

    return Results.Ok(new
    {
        status = "ok",
        startedAt,
        defaultProvider = options.DefaultProvider,
        defaultMode = options.DefaultMode,
        defaultSource = options.DefaultSource,
        defaultTarget = options.DefaultTarget,
        shell = new
        {
            webView2GpuMode = options.Shell.WebView2GpuMode,
            webView2AdditionalArgsConfigured = !string.IsNullOrWhiteSpace(options.Shell.WebView2AdditionalArgs),
            browserRegionQuality = options.Shell.BrowserRegionQuality,
            browserRegionCapture = ShellSettingsService.ResolveBrowserRegionQuality(options.Shell.BrowserRegionQuality)
        },
        ollama = new
        {
            baseUrl = options.Ollama.BaseUrl,
            autoStart = options.Ollama.AutoStart,
            executablePath = options.Ollama.ExecutablePath,
            modelsDirectory = options.Ollama.ModelsDirectory,
            model = options.Ollama.Model,
            models = options.Ollama.Models,
            modelDiscoveryTimeoutSeconds = options.Ollama.ModelDiscoveryTimeoutSeconds,
            startupTimeoutSeconds = options.Ollama.StartupTimeoutSeconds,
            numContext = options.Ollama.NumContext,
            numPredict = options.Ollama.NumPredict,
            temperature = options.Ollama.Temperature,
            keepAlive = options.Ollama.KeepAlive,
            runtime = ollamaRuntimeManager.GetStatus()
        },
        llamaCpp = new
        {
            options.LlamaCpp.Mode,
            options.LlamaCpp.BaseUrl,
            options.LlamaCpp.Model,
            options.LlamaCpp.Profile,
            options.LlamaCpp.PinnedVersion,
            options.LlamaCpp.BinaryFlavor,
            options.LlamaCpp.ComputeTarget,
            // The flavor actually selected after "auto" hardware resolution.
            resolvedFlavor = llamaCppBinaryStore.ResolveEffectiveFlavor(),
            options.LlamaCpp.ModelsDirectory,
            runtime = llamaCppRuntimeManager.GetStatus()
        },
        hardware = new
        {
            detected.Platform,
            detected.Architecture,
            gpus = detected.Gpus.Select(gpu => new
            {
                vendor = gpu.Vendor.ToString(),
                gpu.Name,
                vramGb = gpu.VramGb
            })
        },
        ocr = new
        {
            defaultProvider = options.Ocr.DefaultProvider,
            defaultLanguage = options.Ocr.DefaultLanguage,
            maxImageBytes = options.Ocr.MaxImageBytes,
            normalizeWhitespace = options.Ocr.NormalizeWhitespace,
            preprocessing = new
            {
                defaultPreset = options.Ocr.Preprocessing.DefaultPreset,
                allowedPresets = options.Ocr.Preprocessing.AllowedPresets
            },
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
                timeoutSeconds = options.Speech.FunAsrHttp.TimeoutSeconds,
                runtime = new
                {
                    funAsrHealth.Reachable,
                    funAsrHealth.Ready,
                    funAsrHealth.Status,
                    funAsrHealth.ErrorMessage,
                    funAsrHealth.ProbedAt
                }
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
            video = new
            {
                sectionSeconds = options.Speech.Video.SectionSeconds,
                firstWindowSeconds = options.Speech.Video.FirstWindowSeconds,
                windowSeconds = options.Speech.Video.WindowSeconds,
                windowPaddingSeconds = options.Speech.Video.WindowPaddingSeconds,
                prefetchWindows = options.Speech.Video.PrefetchWindows,
                maxDownloadWorkers = options.Speech.Video.MaxDownloadWorkers,
                maxAsrWorkers = options.Speech.Video.MaxAsrWorkers,
                bufferPath = speechBufferPath
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
        memory = new
        {
            promptContextEnabled = options.Memory.PromptContextEnabled,
            candidateLimit = options.Memory.CandidateLimit,
            maxPromptItems = options.Memory.MaxPromptItems,
            maxContextCharacters = options.Memory.MaxContextCharacters,
            maxRecentLines = options.Memory.MaxRecentLines,
            maxRecentContextCharacters = options.Memory.MaxRecentContextCharacters,
            sceneSummaryMaintenanceEnabled = options.Memory.SceneSummaryMaintenanceEnabled,
            sceneSummaryEventThreshold = options.Memory.SceneSummaryEventThreshold,
            sceneSummaryMaxEvents = options.Memory.SceneSummaryMaxEvents,
            sceneSummaryMaxCharacters = options.Memory.SceneSummaryMaxCharacters,
            autoExtractionEnabled = options.Memory.AutoExtractionEnabled,
            autoTranslationCandidateEventThreshold = options.Memory.AutoTranslationCandidateEventThreshold,
            autoTranslationCandidateMaxEvents = options.Memory.AutoTranslationCandidateMaxEvents,
            autoTranslationCandidateConfidence = options.Memory.AutoTranslationCandidateConfidence,
            minimumConfidence = options.Memory.MinimumConfidence,
            sharedMemoryEnabled = options.Memory.SharedMemoryEnabled,
            sharedMemoryAuthorizedPrincipals = options.Memory.SharedMemoryAuthorizedPrincipals,
            adminTokenConfigured = !string.IsNullOrWhiteSpace(options.Memory.AdminToken),
            externalIdentity = new
            {
                enabled = options.Memory.ExternalIdentity.Enabled,
                sharedSecretConfigured = !string.IsNullOrWhiteSpace(options.Memory.ExternalIdentity.SharedSecret),
                principalHeader = options.Memory.ExternalIdentity.PrincipalHeader,
                groupsHeader = options.Memory.ExternalIdentity.GroupsHeader,
                roleMappingCount = options.Memory.ExternalIdentity.RoleMappings.Length
            },
            bearerJwt = new
            {
                enabled = options.Memory.BearerJwt.Enabled,
                issuer = options.Memory.BearerJwt.Issuer,
                audiences = options.Memory.BearerJwt.Audiences,
                hmacSecretConfigured = !string.IsNullOrWhiteSpace(options.Memory.BearerJwt.HmacSecret),
                jwksConfigured = !string.IsNullOrWhiteSpace(options.Memory.BearerJwt.JwksJson) ||
                                 !string.IsNullOrWhiteSpace(options.Memory.BearerJwt.JwksPath) ||
                                 !string.IsNullOrWhiteSpace(options.Memory.BearerJwt.JwksUrl) ||
                                 !string.IsNullOrWhiteSpace(options.Memory.BearerJwt.OidcDiscoveryUrl),
                jwksUrlConfigured = !string.IsNullOrWhiteSpace(options.Memory.BearerJwt.JwksUrl),
                oidcDiscoveryConfigured = !string.IsNullOrWhiteSpace(options.Memory.BearerJwt.OidcDiscoveryUrl),
                principalClaim = options.Memory.BearerJwt.PrincipalClaim,
                groupsClaim = options.Memory.BearerJwt.GroupsClaim
            },
            oidc = new
            {
                enabled = options.Memory.Oidc.Enabled,
                discoveryConfigured = !string.IsNullOrWhiteSpace(options.Memory.Oidc.DiscoveryUrl),
                authorizationEndpointConfigured = !string.IsNullOrWhiteSpace(options.Memory.Oidc.AuthorizationEndpoint),
                tokenEndpointConfigured = !string.IsNullOrWhiteSpace(options.Memory.Oidc.TokenEndpoint),
                clientConfigured = !string.IsNullOrWhiteSpace(options.Memory.Oidc.ClientId),
                redirectUriConfigured = !string.IsNullOrWhiteSpace(options.Memory.Oidc.RedirectUri),
                refreshTokenStorage = OidcRefreshTokenStorageStatus(options.Memory.Oidc),
                scopes = options.Memory.Oidc.Scopes
            },
            semanticRetrievalEnabled = options.Memory.SemanticRetrievalEnabled,
            semanticCandidateLimit = options.Memory.SemanticCandidateLimit,
            semanticTimeoutMs = options.Memory.SemanticTimeoutMs,
            embeddingMaintenanceBatchSize = options.Memory.EmbeddingMaintenanceBatchSize,
            embeddingDimensions = options.Memory.EmbeddingDimensions,
            semanticMinimumSimilarity = options.Memory.SemanticMinimumSimilarity
        },
        modelCatalog = modelCatalogService.GetStatus(),
        paths = new
        {
            presets = presetsPath,
            glossaries = glossariesPath,
            modelCatalog = modelCatalogPath,
            modelCatalogCache = modelCatalogCachePath,
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

app.MapGet("/system/memory-summary", () => Results.Ok(BuildMemorySummary()));

app.MapGet("/providers", (TranslationProviderRegistry providers) => Results.Ok(providers.List()));

app.MapGet("/translation/model-catalog", (ModelCatalogService modelCatalogs) =>
{
    var catalog = modelCatalogs.GetCurrent();
    return Results.Ok(new
    {
        status = modelCatalogs.GetStatus(),
        llamaCppBinaries = catalog.LlamaCppBinaries,
        models = catalog.Models
            .OrderBy(model => model.Provider, StringComparer.OrdinalIgnoreCase)
            .ThenBy(model => model.Rank)
            .ThenBy(model => model.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray()
    });
});

app.MapPost("/translation/model-catalog/refresh", async (
    ModelCatalogService modelCatalogs,
    CancellationToken cancellationToken) =>
    Results.Ok(await modelCatalogs.RefreshAsync(cancellationToken)));

app.MapGet("/translation/api-supplier-presets", (ApiSupplierPresetCatalogService supplierPresets) =>
{
    var catalog = supplierPresets.GetCurrent();
    return Results.Ok(new
    {
        status = supplierPresets.GetStatus(),
        presets = catalog.Presets
            .OrderBy(preset => preset.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(preset => preset.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray()
    });
});

app.MapPost("/translation/api-supplier-presets/refresh", async (
    ApiSupplierPresetCatalogService supplierPresets,
    CancellationToken cancellationToken) =>
    Results.Ok(await supplierPresets.RefreshAsync(cancellationToken)));

app.MapGet("/translation/api-suppliers", async (
    ApiSupplierStore suppliers,
    ApiSupplierPresetCatalogService supplierPresets,
    CancellationToken cancellationToken) =>
{
    var profiles = await suppliers.ListAsync(cancellationToken);
    return Results.Ok(profiles
        .Select(profile => ApiSupplierStore.ToResponse(profile, supplierPresets.GetBalanceTemplate(profile.PresetId)))
        .ToArray());
});

app.MapGet("/translation/api-suppliers/{id}", async Task<IResult> (
    string id,
    ApiSupplierStore suppliers,
    ApiSupplierPresetCatalogService supplierPresets,
    CancellationToken cancellationToken) =>
{
    var profile = await suppliers.GetAsync(id, cancellationToken);
    return profile is null
        ? Results.NotFound(new { error = $"API supplier was not found: {id}" })
        : Results.Ok(ApiSupplierStore.ToResponse(profile, supplierPresets.GetBalanceTemplate(profile.PresetId)));
});

app.MapPost("/translation/api-suppliers", async Task<IResult> (
    ApiSupplierUpsertRequest request,
    ApiSupplierStore suppliers,
    ApiSecretStore secrets,
    ApiSupplierPresetCatalogService supplierPresets,
    CancellationToken cancellationToken) =>
{
    try
    {
        var preset = supplierPresets.GetRequiredPreset(request.PresetId);
        var id = $"supplier_{Guid.NewGuid():N}";
        var apiKeyRef = string.IsNullOrWhiteSpace(request.ApiKey)
            ? string.Empty
            : await secrets.SaveApiKeyAsync(id, request.ApiKey, cancellationToken);
        var profile = new ApiSupplierProfile
        {
            Id = id,
            PresetId = preset.Id,
            Name = Pick(request.Name, preset.DisplayName),
            Protocol = preset.Protocol,
            BaseUrl = Pick(request.BaseUrl, preset.BaseUrl),
            ModelsUrl = Pick(request.ModelsUrl, preset.ModelsUrl),
            ApiKeyRef = apiKeyRef,
            ActiveModel = Pick(request.ActiveModel, preset.DefaultModel),
            BalanceTemplate = (request.BalanceTemplate ?? string.Empty).Trim(),
            BalanceUrl = (request.BalanceUrl ?? string.Empty).Trim(),
            BalanceAutoIntervalMinutes = Math.Clamp(request.BalanceAutoIntervalMinutes, 0, 1440),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        var saved = await suppliers.UpsertAsync(profile, cancellationToken);
        return Results.Ok(ApiSupplierStore.ToResponse(saved, supplierPresets.GetBalanceTemplate(saved.PresetId)));
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapGet("/api-suppliers/new", async Task<IResult> (
    HttpContext ctx,
    ApiSupplierPresetCatalogService supplierPresets,
    ApiSupplierStore suppliers,
    CancellationToken cancellationToken) =>
{
    ctx.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
    ctx.Response.Headers.Pragma = "no-cache";

    var catalog = supplierPresets.GetCurrent();
    var profiles = await suppliers.ListAsync(cancellationToken);
    var saved = ctx.Request.Query["saved"].ToString();
    var error = ctx.Request.Query["error"].ToString();
    return Results.Content(
        RenderApiSupplierFormPage(catalog.Presets, profiles, saved, error),
        "text/html; charset=utf-8");
});

app.MapPost("/api-suppliers/new", async Task<IResult> (
    HttpContext ctx,
    ApiSupplierStore suppliers,
    ApiSecretStore secrets,
    ApiSupplierPresetCatalogService supplierPresets,
    CancellationToken cancellationToken) =>
{
    try
    {
        var form = await ctx.Request.ReadFormAsync(cancellationToken);
        var preset = supplierPresets.GetRequiredPreset(Pick(form["presetId"].ToString(), "custom"));
        var name = Pick(form["name"].ToString(), preset.DisplayName);
        var baseUrl = Pick(form["baseUrl"].ToString(), preset.BaseUrl);
        var modelsUrl = Pick(form["modelsUrl"].ToString(), preset.ModelsUrl);
        var apiKey = form["apiKey"].ToString();
        var activeModel = Pick(form["activeModel"].ToString(), preset.DefaultModel);

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(baseUrl))
        {
            return Results.Redirect($"/api-suppliers/new?error={Uri.EscapeDataString("Name and Base URL are required.")}");
        }

        if (preset.RequiresApiKey && string.IsNullOrWhiteSpace(apiKey))
        {
            return Results.Redirect($"/api-suppliers/new?error={Uri.EscapeDataString($"{preset.DisplayName} requires an API key.")}");
        }

        var id = $"supplier_{Guid.NewGuid():N}";
        var apiKeyRef = string.IsNullOrWhiteSpace(apiKey)
            ? string.Empty
            : await secrets.SaveApiKeyAsync(id, apiKey, cancellationToken);

        var profile = new ApiSupplierProfile
        {
            Id = id,
            PresetId = preset.Id,
            Name = name,
            Protocol = preset.Protocol,
            BaseUrl = baseUrl,
            ModelsUrl = modelsUrl,
            ApiKeyRef = apiKeyRef,
            ActiveModel = activeModel,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        await suppliers.UpsertAsync(profile, cancellationToken);
        return Results.Redirect($"/api-suppliers/new?saved={Uri.EscapeDataString(name)}");
    }
    catch (InvalidOperationException ex)
    {
        return Results.Redirect($"/api-suppliers/new?error={Uri.EscapeDataString(ex.Message)}");
    }
});

app.MapPut("/translation/api-suppliers/{id}", async Task<IResult> (
    string id,
    ApiSupplierUpsertRequest request,
    ApiSupplierStore suppliers,
    ApiSecretStore secrets,
    ApiSupplierPresetCatalogService supplierPresets,
    CancellationToken cancellationToken) =>
{
    try
    {
        var existing = await suppliers.GetAsync(id, cancellationToken);
        if (existing is null)
        {
            return Results.NotFound(new { error = $"API supplier was not found: {id}" });
        }

        var preset = supplierPresets.GetRequiredPreset(Pick(request.PresetId, existing.PresetId));
        var apiKeyRef = existing.ApiKeyRef;
        if (!string.IsNullOrWhiteSpace(request.ApiKey))
        {
            apiKeyRef = await secrets.SaveApiKeyAsync(id, request.ApiKey, cancellationToken);
        }

        var updated = existing with
        {
            PresetId = preset.Id,
            Name = Pick(request.Name, existing.Name),
            Protocol = preset.Protocol,
            BaseUrl = Pick(request.BaseUrl, existing.BaseUrl),
            ModelsUrl = Pick(request.ModelsUrl, existing.ModelsUrl),
            ApiKeyRef = apiKeyRef,
            ActiveModel = Pick(request.ActiveModel, existing.ActiveModel),
            BalanceTemplate = (request.BalanceTemplate ?? string.Empty).Trim(),
            BalanceUrl = (request.BalanceUrl ?? string.Empty).Trim(),
            BalanceAutoIntervalMinutes = Math.Clamp(request.BalanceAutoIntervalMinutes, 0, 1440)
        };
        var saved = await suppliers.UpsertAsync(updated, cancellationToken);
        return Results.Ok(ApiSupplierStore.ToResponse(saved, supplierPresets.GetBalanceTemplate(saved.PresetId)));
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapDelete("/translation/api-suppliers/{id}", async Task<IResult> (
    string id,
    ApiSupplierStore suppliers,
    ApiSecretStore secrets,
    CancellationToken cancellationToken) =>
{
    var existing = await suppliers.GetAsync(id, cancellationToken);
    if (existing is null)
    {
        return Results.NotFound(new { error = $"API supplier was not found: {id}" });
    }

    await suppliers.DeleteAsync(id, cancellationToken);
    await secrets.DeleteAsync(existing.ApiKeyRef, cancellationToken);
    return Results.Ok(new { deleted = true, id });
});

app.MapPost("/translation/api-suppliers/{id}/test", async Task<IResult> (
    string id,
    ApiSupplierStore suppliers,
    ApiModelDiscoveryService discovery,
    CancellationToken cancellationToken) =>
{
    var profile = await suppliers.GetAsync(id, cancellationToken);
    if (profile is null)
    {
        return Results.NotFound(new { error = $"API supplier was not found: {id}" });
    }

    var result = await discovery.TestAsync(profile, cancellationToken);
    var updated = profile with
    {
        LastHealth = new ApiSupplierHealth
        {
            Status = result.Status,
            LatencyMs = result.LatencyMs,
            CheckedAt = DateTimeOffset.UtcNow,
            Message = result.Message
        }
    };
    await suppliers.UpsertAsync(updated, cancellationToken);
    return Results.Ok(result);
});

app.MapPost("/translation/api-suppliers/{id}/balance", async Task<IResult> (
    string id,
    ApiSupplierStore suppliers,
    ApiBalanceQueryService balances,
    ApiSupplierPresetCatalogService supplierPresets,
    CancellationToken cancellationToken) =>
{
    var profile = await suppliers.GetAsync(id, cancellationToken);
    if (profile is null)
    {
        return Results.NotFound(new { error = $"API supplier was not found: {id}" });
    }

    var result = await balances.QueryAsync(profile, cancellationToken);
    // Cache the last balance so the card renders immediately on reload (no auto-query on open).
    await suppliers.UpsertAsync(profile with { LastBalance = result }, cancellationToken);
    return Results.Ok(result);
});

app.MapGet("/translation/api-suppliers/{id}/models", async Task<IResult> (
    string id,
    ApiSupplierStore suppliers,
    CancellationToken cancellationToken) =>
{
    var profile = await suppliers.GetAsync(id, cancellationToken);
    return profile is null
        ? Results.NotFound(new { error = $"API supplier was not found: {id}" })
        : Results.Ok(profile.ModelCatalog);
});

app.MapPost("/translation/api-suppliers/{id}/models/fetch", async Task<IResult> (
    string id,
    ApiSupplierStore suppliers,
    ApiModelDiscoveryService discovery,
    CancellationToken cancellationToken) =>
{
    var profile = await suppliers.GetAsync(id, cancellationToken);
    if (profile is null)
    {
        return Results.NotFound(new { error = $"API supplier was not found: {id}" });
    }

    var result = await discovery.FetchAndClassifyAsync(profile, cancellationToken);
    if (result.Status.Equals("ready", StringComparison.OrdinalIgnoreCase))
    {
        var activeModel = string.IsNullOrWhiteSpace(profile.ActiveModel) && result.Models.Count > 0
            ? result.Models[0].Id
            : profile.ActiveModel;
        await suppliers.UpsertAsync(profile with
        {
            ActiveModel = activeModel,
            ModelCatalog = result.Models,
            LastHealth = new ApiSupplierHealth
            {
                Status = "ready",
                CheckedAt = DateTimeOffset.UtcNow,
                Message = result.Message
            }
        }, cancellationToken);
    }

    return Results.Ok(result);
});

app.MapPost("/translation/api-suppliers/{id}/activate", async Task<IResult> (
    string id,
    string? profile,
    string? model,
    ApiSupplierStore suppliers,
    ApiSupplierPresetCatalogService supplierPresets,
    TranslationRouteStore routes,
    CancellationToken cancellationToken) =>
{
    var supplier = await suppliers.GetAsync(id, cancellationToken);
    if (supplier is null)
    {
        return Results.NotFound(new { error = $"API supplier was not found: {id}" });
    }

    var preset = supplierPresets.GetRequiredPreset(supplier.PresetId);
    var activeModel = Pick(model, Pick(supplier.ActiveModel, preset.DefaultModel));
    var route = await routes.SetAsync(new TranslationRoute
    {
        ProfileId = Pick(profile, "default"),
        Provider = "api-compatible",
        SupplierId = supplier.Id,
        Model = activeModel,
        Fallback =
        [
            new TranslationRouteFallback
            {
                Provider = "llama-cpp",
                Model = options.LlamaCpp.Model
            },
            new TranslationRouteFallback
            {
                Provider = "ollama",
                Model = options.Ollama.Model
            }
        ]
    }, cancellationToken);

    if (!string.Equals(supplier.ActiveModel, activeModel, StringComparison.OrdinalIgnoreCase))
    {
        await suppliers.UpsertAsync(supplier with { ActiveModel = activeModel }, cancellationToken);
    }

    return Results.Ok(route);
});

app.MapGet("/translation/routes/active", async (
    string? profile,
    TranslationRouteStore routes,
    CancellationToken cancellationToken) =>
    Results.Ok(await routes.GetAsync(Pick(profile, "default"), cancellationToken)));

app.MapPut("/translation/routes/active", async (
    TranslationRouteRequest request,
    TranslationRouteStore routes,
    CancellationToken cancellationToken) =>
{
    var route = await routes.SetAsync(new TranslationRoute
    {
        ProfileId = Pick(request.ProfileId, "default"),
        Provider = request.Provider,
        SupplierId = request.SupplierId,
        Model = request.Model,
        Fallback = request.Fallback
    }, cancellationToken);
    return Results.Ok(route);
});

app.MapGet("/game-profiles", async (
    GameProfileStore profiles,
    CancellationToken cancellationToken) =>
    Results.Ok(await profiles.GetDocumentAsync(cancellationToken)));

app.MapPut("/game-profiles", async Task<IResult> (
    GameProfile profile,
    GameProfileStore profiles,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await profiles.UpsertAsync(profile, cancellationToken));
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { errorCode = "invalid_game_profile", errorMessage = ex.Message });
    }
});

app.MapDelete("/game-profiles/{id}", async Task<IResult> (
    string id,
    GameProfileStore profiles,
    CancellationToken cancellationToken) =>
    await profiles.DeleteAsync(id, cancellationToken)
        ? Results.Ok(await profiles.GetDocumentAsync(cancellationToken))
        : Results.NotFound(new { errorCode = "game_profile_not_found", errorMessage = $"No game profile '{id}'." }));

app.MapPost("/game-profiles/{id}/activate", async Task<IResult> (
    string id,
    GameProfileStore profiles,
    CancellationToken cancellationToken) =>
{
    var profile = await profiles.SetActiveAsync(id, cancellationToken);
    return profile is null
        ? Results.NotFound(new { errorCode = "game_profile_not_found", errorMessage = $"No game profile '{id}'." })
        : Results.Ok(profile);
});

// Native region engine control (drives the in-process NativeRegionService — same engine the tray
// uses). Lets the workbench / WebView2 shell run multi-region capture+overlay on the native side.
app.MapGet("/region/native/status", (Verbeam.Api.Tray.NativeRegionService engine) =>
    Results.Ok(engine.Status()));

app.MapGet("/region/native/selection-safety", (RegionSelectionSafety safety) =>
    Results.Ok(safety.Check()));

app.MapGet("/region/native/surface-map", (
    Verbeam.Api.Tray.NativeRegionService engine,
    VisibleWindowSurfaceAnalyzer analyzer,
    bool ignoreOwnProcess = true) =>
    Results.Ok(analyzer.Analyze(engine.RegionSnapshots(), ignoreOwnProcess)));

app.MapPost("/region/native/activate/{id}", async Task<IResult> (
    string id,
    Verbeam.Api.Tray.NativeRegionService engine,
    GameProfileStore profiles,
    CancellationToken cancellationToken) =>
{
    var profile = await profiles.GetAsync(id, cancellationToken);
    if (profile is null)
    {
        return Results.NotFound(new { errorCode = "game_profile_not_found", errorMessage = $"No game profile '{id}'." });
    }

    await profiles.SetActiveAsync(id, cancellationToken);
    engine.ApplyProfile(profile);
    return Results.Ok(engine.Status());
});

app.MapPost("/region/native/stop", (Verbeam.Api.Tray.NativeRegionService engine) =>
{
    engine.Stop();
    return Results.Ok(engine.Status());
});

app.MapPost("/region/native/clear", (Verbeam.Api.Tray.NativeRegionService engine) =>
{
    engine.Clear();
    return Results.Ok(engine.Status());
});

// Ad-hoc multi-region: shows the full-screen box-drawing overlay (blocks until the user finishes),
// then runs the framed screen regions. No profile / window binding.
app.MapPost("/region/native/select", (
    Verbeam.Api.Tray.NativeRegionService engine,
    RegionSelectionSafety selectionSafety,
    bool force = false,
    int? minOcrGapMs = null) =>
{
    var safety = selectionSafety.Check();
    if (!force && !safety.CanOpenSelector)
    {
        return SelectorBlockedResult(safety);
    }

    engine.ConfigureLoop(minOcrGapMs);
    engine.SelectAndRunRegions();
    return Results.Ok(engine.Status());
});

// Resume the loop on the current region set (Start after Stop, no re-selecting).
app.MapPost("/region/native/start", (Verbeam.Api.Tray.NativeRegionService engine, int? minOcrGapMs) =>
{
    var before = engine.Status();
    if (before.RegionCount <= 0)
    {
        return Results.BadRequest(new
        {
            errorCode = "native_region_missing_selection",
            errorMessage = "Select at least one region before starting."
        });
    }

    engine.ConfigureLoop(minOcrGapMs);
    engine.ResumeLoop();
    return Results.Ok(engine.Status());
});

app.MapPost("/region/native/snapshot", (Verbeam.Api.Tray.NativeRegionService engine) =>
{
    _ = engine.SnapshotAsync(reselect: false);
    return Results.Ok(engine.Status());
});

app.MapPost("/region/native/overlays/toggle", (Verbeam.Api.Tray.NativeRegionService engine) =>
{
    engine.ToggleOverlays();
    return Results.Ok(engine.Status());
});

app.MapPost("/region/native/overlays/show", (Verbeam.Api.Tray.NativeRegionService engine) =>
{
    engine.SetOverlaysVisible(true);
    return Results.Ok(engine.Status());
});

app.MapPost("/region/native/overlays/hide", (Verbeam.Api.Tray.NativeRegionService engine) =>
{
    engine.SetOverlaysVisible(false);
    return Results.Ok(engine.Status());
});

// Triggers the native full-screen framing overlay (the user draws boxes on the desktop), then
// saves them to the active profile. Blocks until the user finishes — fine for a localhost control API.
app.MapPost("/region/native/capture", async Task<IResult> (
    Verbeam.Api.Tray.NativeRegionService engine,
    GameProfileStore profiles,
    RegionSelectionSafety selectionSafety,
    CancellationToken cancellationToken,
    bool force = false) =>
{
    var active = engine.ActiveProfile;
    if (active is null)
    {
        return Results.BadRequest(new { errorCode = "no_active_profile", errorMessage = "Activate a game profile first." });
    }

    if (!engine.TryGetActiveSurfaceBounds(out _))
    {
        return Results.BadRequest(new { errorCode = "window_not_found", errorMessage = $"Can't find the window for '{active.Name}'." });
    }

    var safety = selectionSafety.Check();
    if (!force && !safety.CanOpenSelector)
    {
        return SelectorBlockedResult(safety);
    }

    var regions = engine.CaptureRegionsNormalized();
    if (regions is null || regions.Count == 0)
    {
        return Results.Ok(engine.Status());
    }

    var saved = await profiles.UpsertAsync(active with { Regions = regions }, cancellationToken);
    engine.ApplyProfile(saved);
    return Results.Ok(engine.Status());
});

app.MapGet("/shell-settings", async (
    ShellSettingsService settings,
    CancellationToken cancellationToken) =>
    Results.Ok(await settings.GetAsync(cancellationToken)));

app.MapPut("/shell-settings", async Task<IResult> (
    ShellSettingsSaveRequest request,
    ShellSettingsService settings,
    CancellationToken cancellationToken) =>
{
    try
    {
        var view = await settings.SaveAsync(request, cancellationToken);
        settings.ApplyToOptions(options.Shell, view);
        return Results.Ok(view);
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { errorCode = "invalid_shell_settings", errorMessage = ex.Message });
    }
});

app.MapPost("/shell-settings/reset", async (
    ShellSettingsService settings,
    CancellationToken cancellationToken) =>
{
    var view = await settings.ResetAsync(cancellationToken);
    settings.ApplyToOptions(options.Shell, view);
    return Results.Ok(view);
});

app.MapGet("/hotkeys", async (
    HotkeyRuntimeService hotkeys,
    CancellationToken cancellationToken) =>
    Results.Ok(await hotkeys.GetAsync(cancellationToken)));

app.MapPut("/hotkeys", async Task<IResult> (
    HotkeySaveRequest request,
    HotkeyRuntimeService hotkeys,
    HotkeySettingsService settings,
    CancellationToken cancellationToken) =>
{
    try
    {
        var view = await hotkeys.SaveAsync(request, cancellationToken);
        settings.ApplyToOptions(
            options.Hotkeys,
            view.Bindings.ToDictionary(item => item.Action, item => item.Spec, StringComparer.OrdinalIgnoreCase));
        return Results.Ok(view);
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { errorCode = "invalid_hotkey", errorMessage = ex.Message });
    }
});

app.MapPost("/hotkeys/reset", async Task<IResult> (
    HotkeyRuntimeService hotkeys,
    HotkeySettingsService settings,
    CancellationToken cancellationToken) =>
{
    var view = await hotkeys.ResetAsync(cancellationToken);
    settings.ApplyToOptions(
        options.Hotkeys,
        view.Bindings.ToDictionary(item => item.Action, item => item.Spec, StringComparer.OrdinalIgnoreCase));
    return Results.Ok(view);
});

app.MapPost("/hotkeys/trigger/{action}", async Task<IResult> (
    string action,
    Verbeam.Api.Tray.NativeRegionService engine,
    GameProfileStore profiles,
    RegionSelectionSafety selectionSafety,
    CancellationToken cancellationToken) =>
{
    if (!HotkeySettingsService.TryParseAction(action, out var parsed))
    {
        return Results.BadRequest(new { errorCode = "unknown_hotkey_action", errorMessage = $"Unknown hotkey action '{action}'." });
    }

    return await TriggerHotkeyActionAsync(parsed, engine, profiles, selectionSafety, cancellationToken);
});

app.MapGet("/translation/llama-cpp/artifacts", async (
    LlamaCppArtifactStore artifacts,
    CancellationToken cancellationToken) =>
    Results.Ok(await artifacts.GetStatusesAsync(cancellationToken)));

app.MapGet("/translation/llama-cpp/artifacts/{modelId}", async Task<IResult> (
    string modelId,
    LlamaCppArtifactStore artifacts,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await artifacts.GetStatusAsync(modelId, cancellationToken));
    }
    catch (InvalidOperationException ex)
    {
        return Results.NotFound(new { error = ex.Message });
    }
});

app.MapPost("/translation/llama-cpp/artifacts/{modelId}/download", async Task<IResult> (
    string modelId,
    LlamaCppArtifactStore artifacts,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await artifacts.EnsureModelAsync(modelId, cancellationToken));
    }
    catch (LlamaCppDownloadPausedException ex)
    {
        return Results.Ok(new { paused = true, message = ex.Message });
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapGet("/translation/llama-cpp/binaries", async (
    LlamaCppBinaryStore binaries,
    CancellationToken cancellationToken) =>
    Results.Ok(await binaries.GetStatusesAsync(cancellationToken)));

app.MapPost("/translation/llama-cpp/binaries/download", async Task<IResult> (
    LlamaCppBinaryStore binaries,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await binaries.EnsureBinaryAsync(cancellationToken));
    }
    catch (LlamaCppDownloadPausedException ex)
    {
        return Results.Ok(new { paused = true, message = ex.Message });
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapGet("/translation/llama-cpp/downloads", (LlamaCppDownloadTracker downloads)
    => Results.Ok(downloads.Snapshot()));

app.MapPost("/translation/llama-cpp/downloads/pause", (
    string key,
    LlamaCppDownloadTracker downloads) =>
{
    if (string.IsNullOrWhiteSpace(key))
    {
        return Results.BadRequest(new { error = "Query parameter 'key' is required." });
    }

    return downloads.RequestPause(key)
        ? Results.Ok(new { paused = true, key })
        : Results.NotFound(new { error = $"No active download found for key '{key}'." });
});

app.MapPost("/translation/llama-cpp/install-and-use", async Task<IResult> (
    LlamaCppInstallRequest request,
    LlamaCppInstallService installer,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await installer.InstallAndUseAsync(request, cancellationToken));
    }
    catch (LlamaCppDownloadPausedException ex)
    {
        return Results.Ok(new { paused = true, message = ex.Message });
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapPost("/translation/llama-cpp/stop", async (
    LlamaCppRuntimeManager runtime,
    CancellationToken cancellationToken) =>
{
    await runtime.StopAsync(cancellationToken);
    return Results.Ok(runtime.GetStatus());
});

// Game-mode compute switch: auto (best discrete GPU) / integrated (iGPU, keeps the
// discrete card for a game) / cpu. Persisted so it survives restart, then the managed
// server is stopped so the next translation relaunches on the new backend+device.
app.MapPost("/translation/llama-cpp/compute-target/{target}", async Task<IResult> (
    string target,
    LlamaCppRuntimeManager runtime,
    CancellationToken cancellationToken) =>
{
    var normalized = (target ?? string.Empty).Trim().ToLowerInvariant();
    if (normalized is not ("auto" or "integrated" or "cpu"))
    {
        return Results.BadRequest(new { errorCode = "invalid_compute_target", errorMessage = "target must be auto, integrated, or cpu." });
    }

    options.LlamaCpp.ComputeTarget = normalized;
    await llamaCppRuntimeSettingsStore.SaveAsync(options.LlamaCpp, cancellationToken);
    await runtime.StopAsync(cancellationToken);
    return Results.Ok(new { computeTarget = normalized, runtime = runtime.GetStatus() });
});

app.MapGet("/translation/languages", (TranslationConfigurationCatalog catalog)
    => Results.Ok(catalog.ListLanguages()));

app.MapGet("/translation/events", async (
    GameScopedStores gameStores,
    string? profile,
    int? limit,
    CancellationToken cancellationToken) =>
{
    var profileId = Pick(profile, "default");
    var eventStore = await gameStores.EventsFor(profileId, cancellationToken);
    return Results.Ok(await eventStore.ListEventsAsync(profileId, limit ?? 50, cancellationToken));
});

app.MapGet("/translation/usage-summary", async (
    GameScopedStores gameStores,
    string? profile,
    string? range,
    CancellationToken cancellationToken) =>
{
    var profileId = Pick(profile, "default");
    var normalizedRange = (range ?? "30d").Trim().ToLowerInvariant();
    DateTimeOffset? since = normalizedRange switch
    {
        "today" => DateTimeOffset.UtcNow.Date,
        "1d" => DateTimeOffset.UtcNow.AddDays(-1),
        "7d" => DateTimeOffset.UtcNow.AddDays(-7),
        "30d" => DateTimeOffset.UtcNow.AddDays(-30),
        "all" => null,
        _ => DateTimeOffset.UtcNow.AddDays(-30)
    };
    // Realtime region writes events to the per-game store (games/{profile}/realtime.sqlite), so
    // query the same scoped store — the core store alone would miss all region usage. Batched
    // realtime events flush within ~5s, so the numbers may briefly trail by a batch interval.
    var eventStore = await gameStores.EventsFor(profileId, cancellationToken);
    return Results.Ok(await eventStore.GetUsageSummaryAsync(profileId, normalizedRange, since, cancellationToken));
});

app.MapGet("/memories", async (
    GameScopedStores gameStores,
    string? profile,
    string? type,
    string? trust,
    string? source,
    string? target,
    string? visibility,
    string? q,
    int? limit,
    bool? includeInactive,
    CancellationToken cancellationToken) =>
{
    var profileId = Pick(profile, "default");
    var store = await gameStores.MemoryFor(profileId, cancellationToken);
    return Results.Ok(await store.ListAsync(
        profileId,
        type,
        limit ?? 100,
        activeOnly: includeInactive != true,
        trustLevel: trust,
        sourceLanguage: source,
        targetLanguage: target,
        visibility: visibility,
        query: q,
        cancellationToken));
});

app.MapGet("/memories/export", async (
    GameScopedStores gameStores,
    string? profile,
    string? type,
    string? trust,
    string? source,
    string? target,
    string? visibility,
    string? q,
    int? limit,
    bool? includeInactive,
    CancellationToken cancellationToken) =>
{
    var profileId = Pick(profile, "default");
    var store = await gameStores.MemoryFor(profileId, cancellationToken);
    var includesInactive = includeInactive != false;
    var items = await store.ListAsync(
        profileId,
        type,
        limit ?? 500,
        activeOnly: !includesInactive,
        trustLevel: trust,
        sourceLanguage: source,
        targetLanguage: target,
        visibility: visibility,
        query: q,
        cancellationToken);

    return Results.Ok(new MemoryExportPackage(
        profileId,
        DateTimeOffset.UtcNow,
        includesInactive,
        items));
});

app.MapGet("/memories/conflicts", async (
    GameScopedStores gameStores,
    string? profile,
    string? type,
    string? source,
    string? target,
    int? limit,
    bool? includeInactive,
    CancellationToken cancellationToken) =>
{
    var profileId = Pick(profile, "default");
    var store = await gameStores.MemoryFor(profileId, cancellationToken);
    var items = await store.ListAsync(
        profileId,
        type,
        limit ?? 500,
        activeOnly: includeInactive != true,
        sourceLanguage: source,
        targetLanguage: target,
        cancellationToken: cancellationToken);

    return Results.Ok(BuildMemoryConflictGroups(items));
});

app.MapGet("/memory/scope", async (
    GameScopedStores gameStores,
    IDatabaseRouter databaseRouter,
    string? profile,
    CancellationToken cancellationToken) =>
{
    var profileId = Pick(profile, "default");
    return Results.Ok(await BuildMemoryRuntimeScopeAsync(gameStores, databaseRouter, profileId, cancellationToken));
});

app.MapGet("/memory/search", async Task<IResult> (
    HttpContext context,
    GameScopedServices gameServices,
    IMemoryPrincipalPermissionStore principalPermissions,
    IMemoryPrincipalSessionStore principalSessions,
    string? profile,
    string? source,
    string? target,
    string? mode,
    string? sessionId,
    string? q,
    int? limit,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(q))
    {
        return Results.BadRequest(MemoryError("q is required."));
    }

    var profileId = Pick(profile, "default");
    var memoryContextBuilder = await gameServices.ContextBuilderFor(profileId, cancellationToken);
    var result = await memoryContextBuilder.BuildDebugAsync(
        new MemoryContextRequest(
            profileId,
            Pick(sessionId, string.Empty),
            Pick(source, options.DefaultSource),
            Pick(target, options.DefaultTarget),
            Pick(mode, options.DefaultMode),
            q.Trim(),
            await AllowSharedMemoryForRequestAsync(context, options, principalPermissions, principalSessions, profileId, cancellationToken)),
        limit,
        cancellationToken);

    return Results.Ok(result);
});

app.MapGet("/memory/context-audit", async Task<IResult> (
    HttpContext context,
    IMemoryContextAuditStore memoryAudit,
    string? profile,
    string? principal,
    int? limit,
    CancellationToken cancellationToken) =>
{
    if (!AllowMemoryAdminRequest(context, options))
    {
        return MemoryAdminForbidden();
    }

    var profileId = Pick(profile, "default");
    return Results.Ok(await memoryAudit.ListAsync(
        profileId,
        limit ?? 100,
        principal,
        cancellationToken));
});

app.MapGet("/memory/principal-permissions", async Task<IResult> (
    HttpContext context,
    IMemoryPrincipalPermissionStore principalPermissions,
    string? profile,
    string? principal,
    int? limit,
    CancellationToken cancellationToken) =>
{
    if (!AllowMemoryAdminRequest(context, options))
    {
        return MemoryAdminForbidden();
    }

    return Results.Ok(await principalPermissions.ListAsync(
        profile,
        principal,
        limit ?? 100,
        cancellationToken));
});

app.MapPost("/memory/principal-permissions", async Task<IResult> (
    HttpContext context,
    MemoryPrincipalPermissionUpsertRequest request,
    IMemoryPrincipalPermissionStore principalPermissions,
    CancellationToken cancellationToken) =>
{
    if (!AllowMemoryAdminRequest(context, options))
    {
        return MemoryAdminForbidden();
    }

    try
    {
        return Results.Ok(await principalPermissions.UpsertAsync(request, cancellationToken));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(MemoryError(ex.Message));
    }
});

app.MapDelete("/memory/principal-permissions", async Task<IResult> (
    HttpContext context,
    IMemoryPrincipalPermissionStore principalPermissions,
    string? profile,
    string? principal,
    CancellationToken cancellationToken) =>
{
    if (!AllowMemoryAdminRequest(context, options))
    {
        return MemoryAdminForbidden();
    }

    if (string.IsNullOrWhiteSpace(profile) || string.IsNullOrWhiteSpace(principal))
    {
        return Results.BadRequest(MemoryError("profile and principal are required."));
    }

    var deleted = await principalPermissions.DeleteAsync(principal, profile, cancellationToken);
    return Results.Ok(new { profile = profile.Trim(), principal = principal.Trim(), deleted });
});

app.MapGet("/memory/principal-sessions", async Task<IResult> (
    HttpContext context,
    IMemoryPrincipalSessionStore principalSessions,
    string? principal,
    bool? includeRevoked,
    int? limit,
    CancellationToken cancellationToken) =>
{
    if (!AllowMemoryAdminRequest(context, options))
    {
        return MemoryAdminForbidden();
    }

    return Results.Ok(await principalSessions.ListAsync(
        principal,
        includeRevoked == true,
        limit ?? 100,
        cancellationToken));
});

app.MapPost("/memory/principal-sessions", async Task<IResult> (
    HttpContext context,
    MemoryPrincipalSessionCreateRequest request,
    IMemoryPrincipalSessionStore principalSessions,
    CancellationToken cancellationToken) =>
{
    if (!AllowMemoryAdminRequest(context, options))
    {
        return MemoryAdminForbidden();
    }

    try
    {
        return Results.Ok(await principalSessions.CreateAsync(request, cancellationToken));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(MemoryError(ex.Message));
    }
});

app.MapDelete("/memory/principal-sessions/{id}", async Task<IResult> (
    HttpContext context,
    string id,
    IMemoryPrincipalSessionStore principalSessions,
    CancellationToken cancellationToken) =>
{
    if (!AllowMemoryAdminRequest(context, options))
    {
        return MemoryAdminForbidden();
    }

    var revoked = await principalSessions.RevokeAsync(id, cancellationToken);
    return Results.Ok(new { id, revoked });
});

app.MapGet("/memory/principal-credentials", async Task<IResult> (
    HttpContext context,
    IMemoryPrincipalCredentialStore principalCredentials,
    string? principal,
    bool? includeRevoked,
    int? limit,
    CancellationToken cancellationToken) =>
{
    if (!AllowMemoryAdminRequest(context, options))
    {
        return MemoryAdminForbidden();
    }

    return Results.Ok(await principalCredentials.ListAsync(
        principal,
        includeRevoked == true,
        limit ?? 100,
        cancellationToken));
});

app.MapPost("/memory/principal-credentials", async Task<IResult> (
    HttpContext context,
    MemoryPrincipalCredentialCreateRequest request,
    IMemoryPrincipalCredentialStore principalCredentials,
    CancellationToken cancellationToken) =>
{
    if (!AllowMemoryAdminRequest(context, options))
    {
        return MemoryAdminForbidden();
    }

    try
    {
        return Results.Ok(await principalCredentials.CreateAsync(request, cancellationToken));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(MemoryError(ex.Message));
    }
});

app.MapDelete("/memory/principal-credentials/{id}", async Task<IResult> (
    HttpContext context,
    string id,
    IMemoryPrincipalCredentialStore principalCredentials,
    CancellationToken cancellationToken) =>
{
    if (!AllowMemoryAdminRequest(context, options))
    {
        return MemoryAdminForbidden();
    }

    var revoked = await principalCredentials.RevokeAsync(id, cancellationToken);
    return Results.Ok(new { id, revoked });
});

app.MapPost("/memory/principals/deprovision", async Task<IResult> (
    HttpContext context,
    MemoryPrincipalDeprovisionRequest request,
    IMemoryPrincipalPermissionStore principalPermissions,
    IMemoryPrincipalSessionStore principalSessions,
    IMemoryPrincipalCredentialStore principalCredentials,
    CancellationToken cancellationToken) =>
{
    if (!AllowMemoryAdminRequest(context, options))
    {
        return MemoryAdminForbidden();
    }

    var principalId = FirstNonBlank(request.Principal, request.PrincipalId);
    if (string.IsNullOrWhiteSpace(principalId))
    {
        return Results.BadRequest(MemoryError("principal is required."));
    }

    principalId = principalId.Trim();
    var oidcRefreshTokens = context.RequestServices.GetService<IMemoryOidcRefreshTokenStore>();
    var revokedSessions = request.RevokeSessions
        ? await principalSessions.RevokePrincipalAsync(principalId, cancellationToken)
        : 0;
    var revokedCredentials = request.RevokeCredentials
        ? await principalCredentials.RevokePrincipalAsync(principalId, cancellationToken)
        : 0;
    var revokedOidcRefreshTokens = request.RevokeOidcRefreshTokens && oidcRefreshTokens is not null
        ? await oidcRefreshTokens.RevokePrincipalAsync(principalId, cancellationToken)
        : 0;
    var deletedPermissions = request.DeletePermissions
        ? await principalPermissions.DeletePrincipalAsync(principalId, cancellationToken)
        : 0;

    return Results.Ok(new MemoryPrincipalDeprovisionResult(
        principalId,
        revokedSessions,
        revokedCredentials,
        revokedOidcRefreshTokens,
        deletedPermissions));
});

app.MapPost("/memory/principal-login", async Task<IResult> (
    MemoryPrincipalCredentialLoginRequest request,
    IMemoryPrincipalCredentialStore principalCredentials,
    IMemoryPrincipalSessionStore principalSessions,
    CancellationToken cancellationToken) =>
{
    try
    {
        var principalId = FirstNonBlank(request.Principal, request.PrincipalId);
        if (string.IsNullOrWhiteSpace(principalId))
        {
            return Results.BadRequest(MemoryError("principal is required."));
        }

        if (string.IsNullOrWhiteSpace(request.Secret))
        {
            return Results.BadRequest(MemoryError("secret is required."));
        }

        var credential = await principalCredentials.ValidateAsync(principalId, request.Secret, cancellationToken);
        if (credential is null)
        {
            return MemoryForbidden("principal credential is invalid.");
        }

        return Results.Ok(await principalSessions.CreateAsync(
            new MemoryPrincipalSessionCreateRequest
            {
                Principal = credential.PrincipalId,
                ExpiresAt = request.ExpiresAt
            },
            cancellationToken));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(MemoryError(ex.Message));
    }
});

app.MapGet("/memory/oidc/login", async Task<IResult> (
    IMemoryOidcClient oidcClient,
    bool? redirect,
    CancellationToken cancellationToken) =>
{
    var login = await oidcClient.StartLoginAsync(cancellationToken);
    if (login is null)
    {
        return Results.BadRequest(MemoryError("memory OIDC login is not configured."));
    }

    return redirect == true
        ? Results.Redirect(login.AuthorizationUrl)
        : Results.Ok(login);
});

app.MapGet("/memory/oidc/callback", async Task<IResult> (
    string? code,
    string? state,
    string? error,
    HttpContext context,
    IMemoryOidcClient oidcClient,
    IMemoryPrincipalSessionStore principalSessions,
    MemoryBearerJwtKeyStore keyStore,
    CancellationToken cancellationToken) =>
{
    if (!string.IsNullOrWhiteSpace(error))
    {
        return Results.BadRequest(MemoryError(error));
    }

    if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
    {
        return Results.BadRequest(MemoryError("code and state are required."));
    }

    var tokens = await oidcClient.ExchangeCodeAsync(code, state, cancellationToken);
    var requestOptions = RequestVerbeamOptions(context, options);
    return tokens is null
        ? MemoryForbidden("memory OIDC authorization code is invalid.")
        : await CreateMemoryOidcSessionResultAsync(
            tokens,
            requestOptions,
            principalSessions,
            context.RequestServices.GetService<IMemoryOidcRefreshTokenStore>(),
            keyStore,
            null,
            null,
            null,
            cancellationToken);
});

app.MapPost("/memory/oidc/refresh", async Task<IResult> (
    MemoryOidcRefreshRequest request,
    HttpContext context,
    IMemoryOidcClient oidcClient,
    IMemoryPrincipalSessionStore principalSessions,
    MemoryBearerJwtKeyStore keyStore,
    CancellationToken cancellationToken) =>
{
    var requestOptions = RequestVerbeamOptions(context, options);
    var oidcRefreshTokens = context.RequestServices.GetService<IMemoryOidcRefreshTokenStore>();
    var refreshToken = request.RefreshToken?.Trim();
    MemoryOidcStoredRefreshToken? storedRefreshToken = null;
    if (string.IsNullOrWhiteSpace(refreshToken))
    {
        if (string.IsNullOrWhiteSpace(request.RefreshTokenHandle))
        {
            return Results.BadRequest(MemoryError("refreshToken or refreshTokenHandle is required."));
        }

        if (!UseEncryptedOidcRefreshTokenStorage(requestOptions.Memory.Oidc))
        {
            return Results.BadRequest(MemoryError("refreshToken is required when OIDC refresh-token storage is client_only."));
        }

        if (oidcRefreshTokens is null)
        {
            return Results.BadRequest(MemoryError("memory OIDC refresh-token vault is not configured."));
        }

        storedRefreshToken = await oidcRefreshTokens.ResolveAsync(request.RefreshTokenHandle, cancellationToken);
        if (storedRefreshToken is null)
        {
            return MemoryForbidden("memory OIDC refresh token handle is invalid.");
        }

        refreshToken = storedRefreshToken.RefreshToken;
    }

    if (string.IsNullOrWhiteSpace(refreshToken))
    {
        return Results.BadRequest(MemoryError("refreshToken or refreshTokenHandle is required."));
    }

    var tokens = await oidcClient.RefreshAsync(refreshToken, cancellationToken);
    return tokens is null
        ? MemoryForbidden("memory OIDC refresh token is invalid.")
        : await CreateMemoryOidcSessionResultAsync(
            tokens,
            requestOptions,
            principalSessions,
            oidcRefreshTokens,
            keyStore,
            request.ExpiresAt,
            request.RefreshTokenHandle,
            storedRefreshToken?.PrincipalId,
            cancellationToken);
});

app.MapGet("/memory/oidc/refresh-tokens", async Task<IResult> (
    HttpContext context,
    string? principal,
    bool? includeRevoked,
    int? limit,
    CancellationToken cancellationToken) =>
{
    if (!AllowMemoryAdminRequest(context, options))
    {
        return MemoryAdminForbidden();
    }

    var oidcRefreshTokens = context.RequestServices.GetService<IMemoryOidcRefreshTokenStore>();
    return oidcRefreshTokens is null
        ? Results.Ok(Array.Empty<MemoryOidcRefreshTokenHandle>())
        : Results.Ok(await oidcRefreshTokens.ListAsync(
            principal,
            includeRevoked == true,
            limit ?? 100,
            cancellationToken));
});

app.MapDelete("/memory/oidc/refresh-tokens/{id}", async Task<IResult> (
    HttpContext context,
    string id,
    CancellationToken cancellationToken) =>
{
    if (!AllowMemoryAdminRequest(context, options))
    {
        return MemoryAdminForbidden();
    }

    var oidcRefreshTokens = context.RequestServices.GetService<IMemoryOidcRefreshTokenStore>();
    if (oidcRefreshTokens is null)
    {
        return Results.BadRequest(MemoryError("memory OIDC refresh-token vault is not configured."));
    }

    var revoked = await oidcRefreshTokens.RevokeAsync(id, cancellationToken);
    return Results.Ok(new { id, revoked });
});

app.MapPost("/memories/embeddings/maintain", async Task<IResult> (
    MemoryEmbeddingMaintenanceRequest request,
    MemoryMaintenanceService memoryMaintenance,
    CancellationToken cancellationToken) =>
{
    var result = await memoryMaintenance.MaintainEmbeddingsAsync(
        Pick(request.Profile, "default"),
        Pick(request.Source, options.DefaultSource),
        Pick(request.Target, options.DefaultTarget),
        request.Limit,
        cancellationToken);
    return Results.Ok(result);
});

app.MapGet("/memory/maintenance/jobs", async Task<IResult> (
    HttpContext context,
    MemoryMaintenanceService memoryMaintenance,
    string? profile,
    string? status,
    int? limit,
    CancellationToken cancellationToken) =>
{
    if (!AllowMemoryAdminRequest(context, options))
    {
        return MemoryAdminForbidden();
    }

    return Results.Ok(await memoryMaintenance.ListQueuedJobsAsync(
        profile,
        status,
        limit ?? 100,
        cancellationToken));
});

app.MapPost("/memory/maintenance/jobs/drain", async Task<IResult> (
    HttpContext context,
    MemoryMaintenanceDrainRequest request,
    MemoryMaintenanceService memoryMaintenance,
    CancellationToken cancellationToken) =>
{
    if (!AllowMemoryAdminRequest(context, options))
    {
        return MemoryAdminForbidden();
    }

    return Results.Ok(await memoryMaintenance.DrainQueuedJobsAsync(request.Limit, cancellationToken));
});

app.MapPost("/memories", async Task<IResult> (
    HttpContext context,
    MemoryUpsertRequest request,
    GameScopedStores gameStores,
    IMemoryPrincipalPermissionStore principalPermissions,
    IMemoryPrincipalSessionStore principalSessions,
    CancellationToken cancellationToken) =>
{
    try
    {
        var profileId = Pick(request.Profile, "default");
        if (!await AllowMemoryWriteForRequestAsync(context, options, principalPermissions, principalSessions, profileId, cancellationToken))
        {
            return MemoryForbidden("memory write permission is required.");
        }

        if (RequestsTrustedMemory(request) &&
            !await AllowMemoryApproveForRequestAsync(context, options, principalPermissions, principalSessions, profileId, cancellationToken))
        {
            return MemoryForbidden("memory approve permission is required for trusted memory.");
        }

        var store = await gameStores.MemoryFor(profileId, cancellationToken);
        return Results.Ok(await store.AddOrUpdateAsync(request, cancellationToken));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(MemoryError(ex.Message));
    }
});

app.MapPost("/memories/import", async Task<IResult> (
    HttpContext context,
    MemoryImportRequest request,
    GameScopedStores gameStores,
    IMemoryPrincipalPermissionStore principalPermissions,
    IMemoryPrincipalSessionStore principalSessions,
    CancellationToken cancellationToken) =>
{
    if (request.Items is null || request.Items.Count == 0)
    {
        return Results.BadRequest(MemoryError("items is required."));
    }

    var importProfiles = request.Items
        .Select(item => FirstNonBlank(request.Profile, item.Profile, item.ProfileId, "default") ?? "default")
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
    foreach (var profileId in importProfiles)
    {
        if (!await AllowMemoryWriteForRequestAsync(context, options, principalPermissions, principalSessions, profileId, cancellationToken))
        {
            return MemoryForbidden("memory write permission is required.");
        }
    }

    var imported = new List<MemoryItem>();
    var errors = new List<MemoryImportError>();
    var conflicts = new List<MemoryImportConflict>();
    for (var index = 0; index < request.Items.Count; index++)
    {
        try
        {
            var item = request.Items[index];
            var upsert = BuildMemoryImportUpsert(request, item);
            var profileId = Pick(upsert.Profile, "default");
            var store = await gameStores.MemoryFor(profileId, cancellationToken);
            var conflict = await FindMemoryImportConflictAsync(store, index, upsert, cancellationToken);
            if (conflict is not null)
            {
                conflicts.Add(conflict);
                errors.Add(new MemoryImportError(index, "import conflicts with an existing memory target."));
                continue;
            }

            var memory = await store.AddOrUpdateAsync(upsert, cancellationToken);
            if (item.IsActive == false && memory.IsActive)
            {
                memory = await store.UpdateAsync(
                    memory.Id,
                    new MemoryUpdateRequest { IsActive = false },
                    cancellationToken) ?? memory;
            }

            imported.Add(memory);
        }
        catch (ArgumentException ex)
        {
            errors.Add(new MemoryImportError(index, ex.Message));
        }
    }

    return Results.Ok(new MemoryImportResult(
        request.Items.Count,
        imported.Count,
        errors.Count,
        imported.Count(item => item.TrustLevel == RagSecurityPolicy.Quarantined),
        imported,
        errors)
    {
        Conflicts = conflicts
    });
});

app.MapPatch("/memories/{id}", async Task<IResult> (
    HttpContext context,
    string id,
    string? profile,
    MemoryUpdateRequest request,
    GameScopedStores gameStores,
    IDatabaseRouter databaseRouter,
    IMemoryPrincipalPermissionStore principalPermissions,
    IMemoryPrincipalSessionStore principalSessions,
    CancellationToken cancellationToken) =>
{
    try
    {
        var resolved = await ResolveMemoryItemAsync(gameStores, databaseRouter, id, profile, cancellationToken);
        if (resolved is null)
        {
            return Results.NotFound(MemoryError("memory item was not found."));
        }
        var (store, existing) = resolved.Value;

        if (!await AllowMemoryWriteForRequestAsync(context, options, principalPermissions, principalSessions, existing.ProfileId, cancellationToken))
        {
            return MemoryForbidden("memory write permission is required.");
        }

        if (RequestsTrustMutation(request) &&
            !await AllowMemoryApproveForRequestAsync(context, options, principalPermissions, principalSessions, existing.ProfileId, cancellationToken))
        {
            return MemoryForbidden("memory approve permission is required for trust changes.");
        }

        var updated = await store.UpdateAsync(id, request, cancellationToken);
        return updated is null
            ? Results.NotFound(MemoryError("memory item was not found."))
            : Results.Ok(updated);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(MemoryError(ex.Message));
    }
});

app.MapPost("/memories/{id}/trust", async Task<IResult> (
    HttpContext context,
    string id,
    string? profile,
    MemoryTrustUpdateRequest request,
    GameScopedStores gameStores,
    IDatabaseRouter databaseRouter,
    IMemoryPrincipalPermissionStore principalPermissions,
    IMemoryPrincipalSessionStore principalSessions,
    CancellationToken cancellationToken) =>
{
    try
    {
        var resolved = await ResolveMemoryItemAsync(gameStores, databaseRouter, id, profile, cancellationToken);
        if (resolved is null)
        {
            return Results.NotFound(MemoryError("memory item was not found."));
        }
        var (store, existing) = resolved.Value;

        if (!await AllowMemoryApproveForRequestAsync(context, options, principalPermissions, principalSessions, existing.ProfileId, cancellationToken))
        {
            return MemoryForbidden("memory approve permission is required.");
        }

        var updated = await store.UpdateTrustAsync(id, request, cancellationToken);
        return updated is null
            ? Results.NotFound(MemoryError("memory item was not found."))
            : Results.Ok(updated);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(MemoryError(ex.Message));
    }
});

app.MapPost("/memories/{id}/review", async Task<IResult> (
    HttpContext context,
    string id,
    string? profile,
    MemoryReviewRequest request,
    GameScopedStores gameStores,
    IDatabaseRouter databaseRouter,
    IMemoryPrincipalPermissionStore principalPermissions,
    IMemoryPrincipalSessionStore principalSessions,
    CancellationToken cancellationToken) =>
{
    try
    {
        var resolved = await ResolveMemoryItemAsync(gameStores, databaseRouter, id, profile, cancellationToken);
        if (resolved is null)
        {
            return Results.NotFound(MemoryError("memory item was not found."));
        }
        var (store, existing) = resolved.Value;

        if (!await AllowMemoryApproveForRequestAsync(context, options, principalPermissions, principalSessions, existing.ProfileId, cancellationToken))
        {
            return MemoryForbidden("memory approve permission is required.");
        }

        var action = Pick(request.Action, string.Empty).Replace('-', '_').ToLowerInvariant();
        var reviewedBy = Pick(request.ReviewedBy, "memory-review");
        var update = action switch
        {
            "approve" or "accept" => new MemoryTrustUpdateRequest
            {
                TrustLevel = RagSecurityPolicy.UserVerified,
                ApprovedBy = reviewedBy,
                IsActive = true,
                AcknowledgeSecurityFlags = request.AcknowledgeSecurityFlags
            },
            "reject" or "dismiss" => new MemoryTrustUpdateRequest
            {
                ApprovedBy = reviewedBy,
                IsActive = false
            },
            _ => throw new ArgumentException("action must be approve or reject.")
        };

        var updated = await store.UpdateTrustAsync(id, update, cancellationToken);
        return updated is null
            ? Results.NotFound(MemoryError("memory item was not found."))
            : Results.Ok(updated);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(MemoryError(ex.Message));
    }
});

app.MapPost("/memories/review-batch", async Task<IResult> (
    HttpContext context,
    string? profile,
    MemoryReviewBatchRequest request,
    GameScopedStores gameStores,
    IDatabaseRouter databaseRouter,
    IMemoryPrincipalPermissionStore principalPermissions,
    IMemoryPrincipalSessionStore principalSessions,
    CancellationToken cancellationToken) =>
{
    try
    {
        if (request?.Ids is null || request.Ids.Count == 0)
        {
            return Results.BadRequest(MemoryError("ids are required."));
        }

        var action = Pick(request.Action, string.Empty).Replace('-', '_').ToLowerInvariant();
        if (action is not ("approve" or "accept" or "reject" or "dismiss"))
        {
            return Results.BadRequest(MemoryError("action must be approve or reject."));
        }

        var reviewedBy = Pick(request.ReviewedBy, "memory-review");
        var updated = new List<MemoryItem>();
        var skipped = new List<string>();
        var fixedStore = string.IsNullOrWhiteSpace(profile)
            ? null
            : await gameStores.MemoryFor(profile, cancellationToken);

        foreach (var rawId in request.Ids)
        {
            var id = (rawId ?? string.Empty).Trim();
            if (id.Length == 0)
            {
                continue;
            }

            IMemoryStore store;
            MemoryItem existing;
            if (fixedStore is not null)
            {
                store = fixedStore;
                var fixedExisting = await store.GetAsync(id, cancellationToken);
                if (fixedExisting is null)
                {
                    skipped.Add(id);
                    continue;
                }

                existing = fixedExisting;
            }
            else
            {
                var resolved = await ResolveMemoryItemAsync(gameStores, databaseRouter, id, profile, cancellationToken);
                if (resolved is null)
                {
                    skipped.Add(id);
                    continue;
                }

                (store, existing) = resolved.Value;
            }

            if (existing is null)
            {
                skipped.Add(id);
                continue;
            }

            if (!await AllowMemoryApproveForRequestAsync(context, options, principalPermissions, principalSessions, existing.ProfileId, cancellationToken))
            {
                return MemoryForbidden("memory approve permission is required.");
            }

            var update = action is "approve" or "accept"
                ? new MemoryTrustUpdateRequest
                {
                    TrustLevel = RagSecurityPolicy.UserVerified,
                    ApprovedBy = reviewedBy,
                    IsActive = true,
                    AcknowledgeSecurityFlags = request.AcknowledgeSecurityFlags
                }
                : new MemoryTrustUpdateRequest
                {
                    ApprovedBy = reviewedBy,
                    IsActive = false
                };

            var result = await store.UpdateTrustAsync(id, update, cancellationToken);
            if (result is null)
            {
                skipped.Add(id);
            }
            else
            {
                updated.Add(result);
            }
        }

        return Results.Ok(new { updated, skipped });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(MemoryError(ex.Message));
    }
});

app.MapPost("/memories/{id}/resolve-conflict", async Task<IResult> (
    HttpContext context,
    string id,
    string? profile,
    MemoryConflictResolveRequest request,
    GameScopedStores gameStores,
    IDatabaseRouter databaseRouter,
    IMemoryPrincipalPermissionStore principalPermissions,
    IMemoryPrincipalSessionStore principalSessions,
    CancellationToken cancellationToken) =>
{
    try
    {
        var resolved = await ResolveMemoryItemAsync(gameStores, databaseRouter, id, profile, cancellationToken);
        if (resolved is null)
        {
            return Results.NotFound(MemoryError("memory item was not found."));
        }
        var (store, winner) = resolved.Value;

        if (!winner.IsActive)
        {
            return Results.BadRequest(MemoryError("winner memory must be active."));
        }

        if (!await AllowMemoryApproveForRequestAsync(context, options, principalPermissions, principalSessions, winner.ProfileId, cancellationToken))
        {
            return MemoryForbidden("memory approve permission is required.");
        }

        var reviewedBy = Pick(request.ReviewedBy, "memory-conflict-resolution");
        if (request.ApproveWinner != false &&
            winner.TrustLevel != RagSecurityPolicy.UserVerified)
        {
            winner = await store.UpdateTrustAsync(
                winner.Id,
                new MemoryTrustUpdateRequest
                {
                    TrustLevel = RagSecurityPolicy.UserVerified,
                    ApprovedBy = reviewedBy,
                    IsActive = true,
                    AcknowledgeSecurityFlags = request.AcknowledgeSecurityFlags
                },
                cancellationToken) ?? winner;
        }

        var groupItems = await ListExactMemoryGroupAsync(store, winner, activeOnly: false, cancellationToken);
        var activeOthers = groupItems
            .Where(item => item.IsActive)
            .Where(item => !string.Equals(item.Id, winner.Id, StringComparison.Ordinal))
            .ToArray();
        var deactivated = new List<MemoryItem>();
        foreach (var item in activeOthers)
        {
            var updated = await store.UpdateAsync(
                item.Id,
                new MemoryUpdateRequest
                {
                    ApprovedBy = reviewedBy,
                    IsActive = false
                },
                cancellationToken);
            if (updated is not null)
            {
                deactivated.Add(updated);
            }
        }

        var refreshedGroup = await ListExactMemoryGroupAsync(store, winner, activeOnly: false, cancellationToken);
        var remainingConflicts = BuildMemoryConflictGroups(refreshedGroup);
        return Results.Ok(new MemoryConflictResolveResult(winner, deactivated, remainingConflicts));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(MemoryError(ex.Message));
    }
});

app.MapPost("/memories/{id}/merge-conflict", async Task<IResult> (
    HttpContext context,
    string id,
    string? profile,
    MemoryConflictMergeRequest request,
    GameScopedStores gameStores,
    IDatabaseRouter databaseRouter,
    IMemoryPrincipalPermissionStore principalPermissions,
    IMemoryPrincipalSessionStore principalSessions,
    CancellationToken cancellationToken) =>
{
    try
    {
        var resolved = await ResolveMemoryItemAsync(gameStores, databaseRouter, id, profile, cancellationToken);
        if (resolved is null)
        {
            return Results.NotFound(MemoryError("memory item was not found."));
        }
        var (store, winner) = resolved.Value;

        if (!winner.IsActive)
        {
            return Results.BadRequest(MemoryError("winner memory must be active."));
        }

        if (!await AllowMemoryApproveForRequestAsync(context, options, principalPermissions, principalSessions, winner.ProfileId, cancellationToken))
        {
            return MemoryForbidden("memory approve permission is required.");
        }

        if (request.TargetText is not null && string.IsNullOrWhiteSpace(request.TargetText))
        {
            return Results.BadRequest(MemoryError("targetText cannot be blank."));
        }

        var reviewedBy = Pick(request.ReviewedBy, "memory-conflict-merge");
        var merged = await store.UpdateAsync(
            winner.Id,
            new MemoryUpdateRequest
            {
                TargetText = request.TargetText,
                Note = request.Note,
                Priority = request.Priority,
                Confidence = request.Confidence,
                TrustLevel = request.ApproveWinner == false ? null : RagSecurityPolicy.UserVerified,
                ApprovedBy = reviewedBy,
                IsActive = true,
                AcknowledgeSecurityFlags = request.AcknowledgeSecurityFlags
            },
            cancellationToken) ?? winner;

        var groupItems = await ListExactMemoryGroupAsync(store, merged, activeOnly: false, cancellationToken);
        var activeOthers = groupItems
            .Where(item => item.IsActive)
            .Where(item => !string.Equals(item.Id, merged.Id, StringComparison.Ordinal))
            .ToArray();
        var deactivated = new List<MemoryItem>();
        foreach (var item in activeOthers)
        {
            var updated = await store.UpdateAsync(
                item.Id,
                new MemoryUpdateRequest
                {
                    ApprovedBy = reviewedBy,
                    IsActive = false
                },
                cancellationToken);
            if (updated is not null)
            {
                deactivated.Add(updated);
            }
        }

        var refreshedGroup = await ListExactMemoryGroupAsync(store, merged, activeOnly: false, cancellationToken);
        var remainingConflicts = BuildMemoryConflictGroups(refreshedGroup);
        return Results.Ok(new MemoryConflictResolveResult(merged, deactivated, remainingConflicts));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(MemoryError(ex.Message));
    }
});

app.MapGet("/scene-summaries", async (
    GameScopedStores gameStores,
    string? profile,
    string? sessionId,
    int? limit,
    CancellationToken cancellationToken) =>
{
    var profileId = Pick(profile, "default");
    var store = await gameStores.ScenesFor(profileId, cancellationToken);
    return Results.Ok(await store.ListAsync(profileId, sessionId, limit ?? 50, cancellationToken));
});

app.MapPost("/scene-summaries", async Task<IResult> (
    SceneSummaryUpsertRequest request,
    GameScopedStores gameStores,
    CancellationToken cancellationToken) =>
{
    try
    {
        var store = await gameStores.ScenesFor(Pick(request.Profile, "default"), cancellationToken);
        return Results.Ok(await store.AddOrUpdateAsync(request, cancellationToken));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(MemoryError(ex.Message));
    }
});

app.MapPost("/translation/corrections", async Task<IResult> (
    HttpContext context,
    TranslationCorrectionRequest request,
    GameScopedStores gameStores,
    IMemoryPrincipalPermissionStore principalPermissions,
    IMemoryPrincipalSessionStore principalSessions,
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

        var profileHint = Pick(request.Profile, "default");
        var eventStore = await gameStores.EventsFor(profileHint, cancellationToken);
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

        if (!await AllowMemoryWriteForRequestAsync(context, options, principalPermissions, principalSessions, entry.ProfileId, cancellationToken) ||
            !await AllowMemoryApproveForRequestAsync(context, options, principalPermissions, principalSessions, entry.ProfileId, cancellationToken))
        {
            return MemoryForbidden("memory write and approve permissions are required.");
        }

        var memoryStore = await gameStores.MemoryFor(entry.ProfileId, cancellationToken);
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
    LlamaCppArtifactStore llamaArtifacts,
    ApiSupplierStore apiSuppliers,
    ApiSupplierPresetCatalogService apiSupplierPresets,
    TranslationRouteStore routes,
    TranslationConfigurationCatalog catalog,
    CancellationToken cancellationToken) =>
{
    try
    {
        var providerName = Pick(provider, options.DefaultProvider);
        var descriptor = providers.GetRequired(providerName).Descriptor;
        return Results.Ok(await ProviderModelResponseBuilder.BuildProviderModelsAsync(
            descriptor,
            ollamaModels,
            llamaArtifacts,
            apiSuppliers,
            apiSupplierPresets,
            routes,
            catalog,
            cancellationToken));
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { errorCode = "invalid_provider", errorMessage = ex.Message });
    }
});

app.MapGet("/translation/model-recommendation", async Task<IResult> (
    string? provider,
    string? workload,
    string? preference,
    string? source,
    string? target,
    int? contextTokens,
    int? cpuLogicalCores,
    double? memoryGb,
    bool? hasDedicatedGpu,
    double? gpuVramGb,
    TranslationProviderRegistry providers,
    OllamaModelCatalog ollamaModels,
    LlamaCppArtifactStore llamaArtifacts,
    ApiSupplierStore apiSuppliers,
    ApiSupplierPresetCatalogService apiSupplierPresets,
    TranslationRouteStore routes,
    TranslationConfigurationCatalog catalog,
    CancellationToken cancellationToken) =>
{
    try
    {
        var providerName = Pick(provider, options.DefaultProvider);
        var descriptor = providers.GetRequired(providerName).Descriptor;
        var models = await ProviderModelResponseBuilder.BuildProviderModelsAsync(
            descriptor,
            ollamaModels,
            llamaArtifacts,
            apiSuppliers,
            apiSupplierPresets,
            routes,
            catalog,
            cancellationToken);
        if (descriptor.Name.Equals("api-compatible", StringComparison.OrdinalIgnoreCase) &&
            models.Count == 0)
        {
            return Results.BadRequest(new
            {
                errorCode = "no_api_supplier",
                errorMessage = "Add and activate an API supplier before requesting an api-compatible model recommendation."
            });
        }

        return Results.Ok(catalog.RecommendModelForComputer(
            new ComputerModelRecommendationRequest
            {
                Provider = descriptor.Name,
                Workload = workload,
                Preference = preference,
                Source = source,
                Target = target,
                ContextTokens = contextTokens,
                CpuLogicalCores = cpuLogicalCores,
                MemoryGb = memoryGb,
                HasDedicatedGpu = hasDedicatedGpu,
                GpuVramGb = gpuVramGb
            },
            descriptor.Name,
            models));
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { errorCode = "invalid_provider", errorMessage = ex.Message });
    }
});

app.MapGet("/ocr/providers", (OcrProviderRegistry providers) => Results.Ok(providers.List()));

app.MapPost("/ocr/screen-capture", () =>
{
    if (!OperatingSystem.IsWindows())
    {
        return Results.Problem("Screen region capture is only available on Windows in this build.", statusCode: StatusCodes.Status501NotImplemented);
    }

    try
    {
        using var capture = Verbeam.Api.Tray.FrozenRegionSelectionOverlay.CaptureRegion();
        if (capture is null)
        {
            return Results.NoContent();
        }

        return Results.Ok(new ScreenCaptureResponse(
            EncodePngBase64(capture.Image),
            "image/png",
            $"screen-region-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.png",
            capture.Image.Width,
            capture.Image.Height,
            capture.Bounds.X,
            capture.Bounds.Y));
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapGet("/windows", (Verbeam.Api.Tray.WindowThumbnailService windows) =>
{
    if (!OperatingSystem.IsWindows())
    {
        return Results.Problem("Window listing is only available on Windows in this build.", statusCode: StatusCodes.Status501NotImplemented);
    }

    return Results.Ok(windows.ListWindows());
});

app.MapGet("/windows/{hwnd}/thumbnail", (string hwnd, int? width, Verbeam.Api.Tray.WindowThumbnailService windows) =>
{
    if (!OperatingSystem.IsWindows())
    {
        return Results.Problem("Window capture is only available on Windows in this build.", statusCode: StatusCodes.Status501NotImplemented);
    }

    try
    {
        var thumbnail = windows.CaptureThumbnail(hwnd, width ?? 320);
        return thumbnail is null
            ? Results.NotFound(new { error = $"Window could not be captured: {hwnd}" })
            : Results.Ok(thumbnail);
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapGet("/ocr/engines", async (OcrProviderRegistry providers, CancellationToken cancellationToken)
    => Results.Ok(await BuildOcrEnginesAsync(providers, options, builder.Environment.ContentRootPath, cancellationToken)));

app.MapGet("/ocr/routing-profiles", (OcrRoutingService routing)
    => Results.Ok(routing.ListProfiles()));

app.MapGet("/ocr/route", async (
    OcrRoutingService routing,
    IOcrMemoryStore memoryStore,
    string? provider,
    string? contentType,
    string? preference,
    string? profile,
    int? qualityLimit,
    CancellationToken cancellationToken) =>
{
    try
    {
        var profileId = Pick(profile, "default");
        var smokeRecords = await memoryStore.ListSmokeResultsAsync(profileId, qualityLimit ?? 100, cancellationToken);
        var qualitySummaries = BuildOcrSmokeQualitySummaries(profileId, smokeRecords);
        return Results.Ok(routing.ResolveDecision(provider, contentType, preference, qualitySummaries));
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(OcrError(ex.Message));
    }
});

app.MapGet("/asr/providers", (SpeechProviderRegistry providers) => Results.Ok(providers.List()));

app.MapGet("/asr/engines", async (
    SpeechProviderRegistry providers,
    CancellationToken cancellationToken)
    => Results.Ok(await BuildSpeechEnginesAsync(providers, options, cancellationToken)));

app.MapGet("/audio/sessions", (WindowsAudioSessionService audioSessions)
    => Results.Ok(audioSessions.ListSessions()));

app.MapGet("/ocr/events", async (
    IOcrMemoryStore memoryStore,
    string? profile,
    int? limit,
    CancellationToken cancellationToken) =>
{
    var profileId = Pick(profile, "default");
    return Results.Ok(await memoryStore.ListEventsAsync(profileId, limit ?? 50, cancellationToken));
});

app.MapGet("/ocr/blocks/annotations", async (
    IOcrBlockAnnotationStore annotations,
    string? imageHash,
    string? profile,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(imageHash))
    {
        return Results.BadRequest(OcrError("imageHash is required."));
    }

    var profileId = Pick(profile, "default");
    return Results.Ok(await annotations.ListByImageAsync(profileId, imageHash.Trim(), cancellationToken));
});

app.MapPost("/ocr/blocks/annotations", async (
    OcrBlockAnnotationRequest request,
    IOcrBlockAnnotationStore annotations,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.ImageHash))
    {
        return Results.BadRequest(OcrError("imageHash is required."));
    }

    if (string.IsNullOrWhiteSpace(request.BlockId))
    {
        return Results.BadRequest(OcrError("blockId is required."));
    }

    var profileId = Pick(request.Profile, "default");
    var locked = request.Locked ?? false;
    var status = Pick(request.Status, locked ? OcrBlockStatuses.Locked : OcrBlockStatuses.Translated);

    var annotation = new OcrBlockAnnotation(
        profileId,
        request.ImageHash.Trim(),
        request.BlockId.Trim(),
        status,
        locked,
        request.EditedTranslation ?? string.Empty,
        DateTimeOffset.UtcNow)
    {
        EditedSource = request.EditedSource ?? string.Empty,
        Note = request.Note ?? string.Empty,
        ReadingOrderOverride = request.ReadingOrderOverride,
        TypeOverride = request.TypeOverride ?? string.Empty
    };

    var stored = await annotations.UpsertAsync(annotation, cancellationToken);

    if (request.History is not null)
    {
        await annotations.AppendHistoryAsync(
            new OcrBlockHistoryEntry(
                Guid.NewGuid().ToString("N"),
                profileId,
                annotation.ImageHash,
                annotation.BlockId,
                Pick(request.History.Kind, OcrBlockHistoryKinds.Translation),
                request.History.SourceText ?? string.Empty,
                request.History.TranslatedText ?? string.Empty,
                request.History.Engine ?? string.Empty,
                request.History.Provider ?? string.Empty,
                DateTimeOffset.UtcNow),
            cancellationToken);
    }

    return Results.Ok(stored);
});

app.MapGet("/ocr/blocks/history", async (
    IOcrBlockAnnotationStore annotations,
    string? imageHash,
    string? blockId,
    string? profile,
    int? limit,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(imageHash) || string.IsNullOrWhiteSpace(blockId))
    {
        return Results.BadRequest(OcrError("imageHash and blockId are required."));
    }

    var profileId = Pick(profile, "default");
    return Results.Ok(await annotations.ListHistoryAsync(
        profileId,
        imageHash.Trim(),
        blockId.Trim(),
        limit ?? 50,
        cancellationToken));
});

// Per-block geometry + text-format overrides for the PDF overlay editor, keyed by
// docKey = "{jobId}:{pageIndex}". Mirrors /ocr/blocks/annotations but for layout.
app.MapGet("/ocr/blocks/layout", async (
    IOcrBlockLayoutStore layout,
    string? docKey,
    string? profile,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(docKey))
    {
        return Results.BadRequest(OcrError("docKey is required."));
    }

    var profileId = Pick(profile, "default");
    return Results.Ok(await layout.ListByDocKeyAsync(profileId, docKey.Trim(), cancellationToken));
});

app.MapPost("/ocr/blocks/layout", async (
    OcrBlockLayoutRequest request,
    IOcrBlockLayoutStore layout,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.DocKey))
    {
        return Results.BadRequest(OcrError("docKey is required."));
    }

    if (string.IsNullOrWhiteSpace(request.BlockId))
    {
        return Results.BadRequest(OcrError("blockId is required."));
    }

    var stored = await layout.UpsertLayoutAsync(
        new OcrBlockLayout(
            Pick(request.Profile, "default"),
            request.DocKey.Trim(),
            request.BlockId.Trim(),
            DateTimeOffset.UtcNow)
        {
            Nx = request.Nx,
            Ny = request.Ny,
            Nw = request.Nw,
            Nh = request.Nh,
            FontSize = request.FontSize,
            LineHeight = request.LineHeight,
            TextAlign = request.TextAlign ?? string.Empty,
            Overflow = Pick(request.Overflow, OcrBlockOverflowModes.Shrink)
        },
        cancellationToken);

    return Results.Ok(stored);
});

app.MapGet("/ocr/jobs", async (
    OcrJobService ocrJobs,
    string? profile,
    int? limit,
    CancellationToken cancellationToken) =>
{
    var profileId = Pick(profile, "default");
    return Results.Ok(await ocrJobs.ListAsync(profileId, limit ?? 50, cancellationToken));
});

app.MapPost("/ocr/jobs", async (
    OcrJobRequest request,
    OcrJobService ocrJobs,
    CancellationToken cancellationToken) =>
{
    try
    {
        var job = await ocrJobs.StartAsync(request, cancellationToken);
        return Results.Accepted($"/ocr/jobs/{job.Id}", job);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(OcrError(ex.Message));
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(OcrError(ex.Message));
    }
});

app.MapGet("/ocr/jobs/{jobId}", async (
    string jobId,
    OcrJobService ocrJobs,
    CancellationToken cancellationToken) =>
{
    var job = await ocrJobs.GetAsync(jobId, cancellationToken);
    return job is null ? Results.NotFound(OcrError("OCR job was not found.")) : Results.Ok(job);
});

app.MapGet("/ocr/jobs/{jobId}/result", async (
    string jobId,
    OcrJobService ocrJobs,
    CancellationToken cancellationToken) =>
{
    var result = await ocrJobs.GetResultAsync(jobId, cancellationToken);
    return result is null ? Results.NotFound(OcrError("OCR job was not found.")) : Results.Ok(result);
});

app.MapPost("/ocr/jobs/{jobId}/cancel", async (
    string jobId,
    OcrJobService ocrJobs,
    CancellationToken cancellationToken) =>
{
    var canceled = await ocrJobs.CancelAsync(jobId, cancellationToken);
    return canceled ? Results.Ok(new { jobId, canceled = true }) : Results.NotFound(OcrError("OCR job is not running."));
});

app.MapGet("/ocr/jobs/{jobId}/events", async (
    string jobId,
    HttpContext context,
    OcrJobService ocrJobs,
    CancellationToken cancellationToken) =>
{
    await StreamOcrJobEventsAsync(jobId, context, ocrJobs, cancellationToken);
});

static string GetMultipartBoundary(MediaTypeHeaderValue contentType)
{
    var boundary = HeaderUtilities.RemoveQuotes(contentType.Boundary).Value;
    if (string.IsNullOrWhiteSpace(boundary))
    {
        throw new InvalidDataException("multipart/form-data boundary is required.");
    }

    return boundary;
}

static string? DocumentFormValue(IReadOnlyDictionary<string, string?> form, string key)
{
    if (!form.TryGetValue(key, out var value))
    {
        return null;
    }

    return string.IsNullOrWhiteSpace(value) ? null : value;
}

static bool? ParseDocumentNullableBool(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return null;
    }

    return bool.TryParse(value, out var parsed) ? parsed : null;
}

static void TryDeleteDocumentUpload(string? path)
{
    if (string.IsNullOrWhiteSpace(path))
    {
        return;
    }

    try
    {
        if (System.IO.File.Exists(path))
        {
            System.IO.File.Delete(path);
        }
    }
    catch (IOException)
    {
    }
    catch (UnauthorizedAccessException)
    {
    }
}

static long ReadDocumentEventSequenceCursor(HttpContext context)
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

static bool IsTerminalDocumentJobStatusForEndpoint(string status)
    => status.Equals("succeeded", StringComparison.OrdinalIgnoreCase) ||
       status.Equals("failed", StringComparison.OrdinalIgnoreCase) ||
       status.Equals("canceled", StringComparison.OrdinalIgnoreCase);

static async Task StreamDocumentJobEventsForEndpointAsync(
    string jobId,
    HttpContext context,
    DocumentJobService documentJobs,
    CancellationToken cancellationToken)
{
    var job = await documentJobs.GetAsync(jobId, cancellationToken);
    if (job is null)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync(JsonSerializer.Serialize(OcrError("Document OCR job was not found.")), cancellationToken);
        return;
    }

    context.Response.Headers.CacheControl = "no-cache";
    context.Response.Headers.Connection = "keep-alive";
    context.Response.ContentType = "text/event-stream; charset=utf-8";

    var afterSequence = ReadDocumentEventSequenceCursor(context);
    while (!cancellationToken.IsCancellationRequested)
    {
        var events = await documentJobs.ListEventsAsync(jobId, afterSequence, 100, cancellationToken);
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

        job = await documentJobs.GetAsync(jobId, cancellationToken);
        if (job is null || IsTerminalDocumentJobStatusForEndpoint(job.Status))
        {
            break;
        }

        await context.Response.WriteAsync($": ping {DateTimeOffset.UtcNow:O}\n\n", cancellationToken);
        await context.Response.Body.FlushAsync(cancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
    }
}

app.MapGet("/ocr/document-jobs", async (
    DocumentJobService documentJobs,
    string? profile,
    int? limit,
    CancellationToken cancellationToken) =>
{
    var profileId = Pick(profile, "default");
    return Results.Ok(await documentJobs.ListAsync(profileId, limit ?? 50, cancellationToken));
});

app.MapPost("/ocr/document-jobs", async (
    HttpContext context,
    DocumentJobService documentJobs,
    IMemoryPrincipalPermissionStore principalPermissions,
    IMemoryPrincipalSessionStore principalSessions,
    CancellationToken cancellationToken) =>
{
    var httpRequest = context.Request;
    if (!httpRequest.HasFormContentType)
    {
        return Results.BadRequest(OcrError("multipart/form-data with a file field is required."));
    }

    if (!MediaTypeHeaderValue.TryParse(httpRequest.ContentType, out var mediaTypeHeader))
    {
        return Results.BadRequest(OcrError("A valid multipart/form-data Content-Type is required."));
    }

    var uploadDirectory = Path.Combine(Path.GetTempPath(), "verbeam-document-uploads");
    Directory.CreateDirectory(uploadDirectory);
    var fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    string boundary;
    try
    {
        boundary = GetMultipartBoundary(mediaTypeHeader);
    }
    catch (InvalidDataException ex)
    {
        return Results.BadRequest(OcrError(ex.Message));
    }

    var reader = new MultipartReader(boundary, httpRequest.Body)
    {
        BodyLengthLimit = long.MaxValue
    };
    string? tempPath = null;
    string? originalFileName = null;
    string? fileContentType = null;
    var uploadedBytes = 0L;

    MultipartSection? section;
    while ((section = await reader.ReadNextSectionAsync(cancellationToken)) is not null)
    {
        if (string.IsNullOrWhiteSpace(section.ContentDisposition) ||
            !ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var contentDisposition))
        {
            continue;
        }

        var fieldName = HeaderUtilities.RemoveQuotes(contentDisposition.Name).Value;
        if (string.IsNullOrWhiteSpace(fieldName))
        {
            continue;
        }

        var fileName = HeaderUtilities.RemoveQuotes(contentDisposition.FileNameStar).Value;
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = HeaderUtilities.RemoveQuotes(contentDisposition.FileName).Value;
        }

        if (string.IsNullOrWhiteSpace(fileName))
        {
            using var fieldReader = new StreamReader(
                section.Body,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true,
                bufferSize: 1024,
                leaveOpen: true);
            fields[fieldName] = await fieldReader.ReadToEndAsync(cancellationToken);
            continue;
        }

        if (tempPath is not null)
        {
            TryDeleteDocumentUpload(tempPath);
            return Results.BadRequest(OcrError("Only one file field is supported per document job."));
        }

        originalFileName = Path.GetFileName(fileName);
        fileContentType = section.ContentType;
        tempPath = Path.Combine(uploadDirectory, $"{Guid.NewGuid():N}{Path.GetExtension(originalFileName)}");
        await using var output = new FileStream(
            tempPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1024 * 128,
            useAsync: true);
        await section.Body.CopyToAsync(output, cancellationToken);
        uploadedBytes = output.Length;
    }

    if (string.IsNullOrWhiteSpace(tempPath) || uploadedBytes == 0)
    {
        TryDeleteDocumentUpload(tempPath);
        return Results.BadRequest(OcrError("A non-empty file field is required."));
    }

    try
    {
        var profileId = Pick(DocumentFormValue(fields, "profile"), "default");
        var identity = await RequestMemoryIdentityAsync(context, options, principalSessions, cancellationToken);
        var request = new DocumentJobRequest
        {
            InputPath = tempPath,
            OriginalFileName = originalFileName,
            ContentType = string.IsNullOrWhiteSpace(fileContentType) ? DocumentFormValue(fields, "contentType") : fileContentType,
            SourceKind = DocumentFormValue(fields, "sourceKind") ?? DocumentFormValue(fields, "source"),
            Source = DocumentFormValue(fields, "sourceLanguage") ?? DocumentFormValue(fields, "source"),
            Target = DocumentFormValue(fields, "target") ?? DocumentFormValue(fields, "targetLanguage"),
            Mode = DocumentFormValue(fields, "mode"),
            Glossary = DocumentFormValue(fields, "glossary"),
            TranslationProvider = DocumentFormValue(fields, "translationProvider") ?? DocumentFormValue(fields, "provider"),
            Model = DocumentFormValue(fields, "model"),
            Profile = profileId,
            SessionId = DocumentFormValue(fields, "sessionId"),
            PrincipalId = DocumentFormValue(fields, "principalId") ?? identity.Principal,
            AllowSharedMemory = ParseDocumentNullableBool(DocumentFormValue(fields, "allowSharedMemory")) ??
                await AllowSharedMemoryForIdentityAsync(
                    options,
                    principalPermissions,
                    identity,
                    profileId,
                    cancellationToken),
            OcrProvider = DocumentFormValue(fields, "ocrProvider"),
            OcrLanguage = DocumentFormValue(fields, "ocrLanguage") ?? DocumentFormValue(fields, "language"),
            OcrPreference = DocumentFormValue(fields, "ocrPreference") ?? DocumentFormValue(fields, "preference"),
            OcrPreprocessingPreset = DocumentFormValue(fields, "ocrPreprocessingPreset") ?? DocumentFormValue(fields, "preprocessingPreset"),
            PageRange = DocumentFormValue(fields, "pageRange")
        };
        var job = await documentJobs.StartAsync(request, cancellationToken);
        return Results.Accepted($"/ocr/document-jobs/{job.Id}", job);
    }
    catch (ArgumentException ex)
    {
        TryDeleteDocumentUpload(tempPath);
        return Results.BadRequest(OcrError(ex.Message));
    }
    catch (InvalidOperationException ex)
    {
        TryDeleteDocumentUpload(tempPath);
        return Results.BadRequest(OcrError(ex.Message));
    }
}).WithMetadata(new Microsoft.AspNetCore.Mvc.DisableRequestSizeLimitAttribute());

app.MapGet("/ocr/document-jobs/{jobId}", async (
    string jobId,
    DocumentJobService documentJobs,
    CancellationToken cancellationToken) =>
{
    var job = await documentJobs.GetAsync(jobId, cancellationToken);
    return job is null ? Results.NotFound(OcrError("Document OCR job was not found.")) : Results.Ok(job);
});

app.MapGet("/ocr/document-jobs/{jobId}/result", async (
    string jobId,
    DocumentJobService documentJobs,
    CancellationToken cancellationToken) =>
{
    var result = await documentJobs.GetResultAsync(jobId, cancellationToken);
    return result is null ? Results.NotFound(OcrError("Document OCR job was not found.")) : Results.Ok(result);
});

// PDF overlay editor: per-page blocks with normalized 0..1 geometry (so they overlay a
// PDF.js backdrop at any scale), merged with the user's annotations (status/locked/edited
// text) and layout overrides (moved bbox, font, overflow). Ignored blocks are omitted.
app.MapGet("/ocr/document-jobs/{jobId}/pages", async (
    string jobId,
    DocumentJobService documentJobs,
    IOcrBlockAnnotationStore annotations,
    IOcrBlockLayoutStore layout,
    string? profile,
    CancellationToken cancellationToken) =>
{
    var job = await documentJobs.GetAsync(jobId, cancellationToken);
    if (job is null)
    {
        return Results.NotFound(OcrError("Document OCR job was not found."));
    }

    var document = await documentJobs.GetTranslatedDocumentAsync(jobId, cancellationToken);
    if (document is null)
    {
        return Results.Ok(new { jobId, pageCount = 0, pages = Array.Empty<object>() });
    }

    var profileId = Pick(profile, string.IsNullOrWhiteSpace(job.ProfileId) ? "default" : job.ProfileId);
    var pages = new List<object>();
    foreach (var page in document.Pages)
    {
        var docKey = $"{jobId}:{page.PageIndex}";
        var pageAnnotations = (await annotations.ListByImageAsync(profileId, docKey, cancellationToken))
            .ToDictionary(item => item.BlockId, StringComparer.Ordinal);
        var pageLayout = (await layout.ListByDocKeyAsync(profileId, docKey, cancellationToken))
            .ToDictionary(item => item.BlockId, StringComparer.Ordinal);
        var pageImage = job.Artifacts.FirstOrDefault(item =>
            item.Kind.Equals($"page-image-{page.PageIndex}", StringComparison.OrdinalIgnoreCase));

        var blocks = new List<object>();
        foreach (var block in Verbeam.Core.Services.OcrBlockFlattener.Flatten(page))
        {
            pageAnnotations.TryGetValue(block.Id, out var annotation);
            pageLayout.TryGetValue(block.Id, out var blockLayout);
            if (annotation is not null && annotation.Status == OcrBlockStatuses.Ignored)
            {
                continue;
            }

            blocks.Add(new
            {
                id = block.Id,
                type = string.IsNullOrEmpty(annotation?.TypeOverride) ? block.Type : annotation!.TypeOverride,
                sourceText = string.IsNullOrEmpty(annotation?.EditedSource) ? block.SourceText : annotation!.EditedSource,
                text = string.IsNullOrEmpty(annotation?.EditedTranslation) ? block.Text : annotation!.EditedTranslation,
                status = annotation?.Status ?? OcrBlockStatuses.Translated,
                locked = annotation?.Locked ?? false,
                confidence = block.Confidence,
                engine = block.Engine,
                shouldTranslate = block.ShouldTranslate,
                box = NormalizeBlockBox(page, block, blockLayout),
                fontSize = blockLayout?.FontSize,
                lineHeight = blockLayout?.LineHeight,
                textAlign = string.IsNullOrEmpty(blockLayout?.TextAlign) ? null : blockLayout!.TextAlign,
                overflow = blockLayout?.Overflow ?? OcrBlockOverflowModes.Shrink
            });
        }

        pages.Add(new
        {
            pageIndex = page.PageIndex,
            widthPoints = page.PageWidthPoints ?? (double?)page.Width,
            heightPoints = page.PageHeightPoints ?? (double?)page.Height,
            imageWidth = page.ImageWidth,
            imageHeight = page.ImageHeight,
            renderDpi = page.RenderDpi,
            pageImageArtifactId = pageImage?.Id,
            docKey,
            blocks
        });
    }

    return Results.Ok(new { jobId, pageCount = pages.Count, pages });
});

// Export a layout-preserving translated PDF. engine="overlay" (default) masks the original
// and draws the edited translations at their boxes via PyMuPDF (honors manual bbox/font);
// engine="pdf2zh" (high-fidelity, selectable) is wired in a later stage.
app.MapPost("/ocr/document-jobs/{jobId}/export", async (
    HttpContext httpContext,
    string jobId,
    DocumentExportRequest request,
    DocumentJobService documentJobs,
    IOcrBlockAnnotationStore annotations,
    IOcrBlockLayoutStore layout,
    Pdf2zhTranslationBridge pdf2zhBridge,
    CancellationToken cancellationToken) =>
{
    var engine = Pick(request.Engine, "overlay");
    var job = await documentJobs.GetAsync(jobId, cancellationToken);
    if (job is null)
    {
        return Results.NotFound(OcrError("Document OCR job was not found."));
    }

    var profileId = Pick(request.Profile, string.IsNullOrWhiteSpace(job.ProfileId) ? "default" : job.ProfileId);

    // High-fidelity, selectable-text path: pdf2zh re-renders the original layout and pulls each
    // segment's translation from Verbeam's internal shim (job edits + auto-translations).
    if (engine.Equals("pdf2zh", StringComparison.OrdinalIgnoreCase))
    {
        await pdf2zhBridge.RegisterJobAsync(jobId, profileId, cancellationToken);
        var baseUrl = $"http://127.0.0.1:{httpContext.Connection.LocalPort}";
        try
        {
            var produced = await documentJobs.ExportPdf2zhAsync(jobId, baseUrl, profileId, cancellationToken);
            if (produced.Count == 0)
            {
                return Results.Json(OcrError("pdf2zh produced no PDF output."), statusCode: 500);
            }

            var wantsDual = Pick(request.Variant, "mono").Equals("dual", StringComparison.OrdinalIgnoreCase);
            var chosen = produced.FirstOrDefault(a => wantsDual
                ? a.Kind.Contains("dual", StringComparison.OrdinalIgnoreCase)
                : !a.Kind.Contains("dual", StringComparison.OrdinalIgnoreCase)) ?? produced[0];
            return Results.Ok(new
            {
                artifactId = chosen.Id,
                kind = chosen.Kind,
                fileName = chosen.FileName,
                variants = produced.Select(a => new { artifactId = a.Id, kind = a.Kind, fileName = a.FileName })
            });
        }
        catch (Exception ex)
        {
            return Results.Json(OcrError("pdf2zh export failed: " + ex.Message), statusCode: 500);
        }
    }

    if (!engine.Equals("overlay", StringComparison.OrdinalIgnoreCase))
    {
        return Results.Json(OcrError($"Export engine '{engine}' is not available."), statusCode: 501);
    }

    var document = await documentJobs.GetTranslatedDocumentAsync(jobId, cancellationToken);
    if (document is null)
    {
        return Results.BadRequest(OcrError("This job has no translated layout to export."));
    }

    var targetLanguage = Pick(request.Target, options.DefaultTarget);

    var specPages = new List<object>();
    foreach (var page in document.Pages)
    {
        var docKey = $"{jobId}:{page.PageIndex}";
        var pageAnnotations = (await annotations.ListByImageAsync(profileId, docKey, cancellationToken))
            .ToDictionary(item => item.BlockId, StringComparer.Ordinal);
        var pageLayout = (await layout.ListByDocKeyAsync(profileId, docKey, cancellationToken))
            .ToDictionary(item => item.BlockId, StringComparer.Ordinal);
        var widthPoints = page.PageWidthPoints ?? page.Width ?? 0;
        var heightPoints = page.PageHeightPoints ?? page.Height ?? 0;
        if (widthPoints <= 0 || heightPoints <= 0)
        {
            continue;
        }

        var specBlocks = new List<object>();
        foreach (var block in Verbeam.Core.Services.OcrBlockFlattener.Flatten(page))
        {
            if (!block.ShouldTranslate)
            {
                continue;
            }

            pageAnnotations.TryGetValue(block.Id, out var annotation);
            pageLayout.TryGetValue(block.Id, out var blockLayout);
            if (annotation is not null && annotation.Status == OcrBlockStatuses.Ignored)
            {
                continue;
            }

            var text = string.IsNullOrEmpty(annotation?.EditedTranslation) ? block.Text : annotation!.EditedTranslation;
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var box = Verbeam.Core.Services.OcrCoordinateMath.NormalizeBox(page, block, blockLayout);
            specBlocks.Add(new
            {
                text,
                x = box.Nx * widthPoints,
                y = box.Ny * heightPoints,
                w = box.Nw * widthPoints,
                h = box.Nh * heightPoints,
                fontSize = blockLayout?.FontSize ?? 0,
                align = string.IsNullOrEmpty(blockLayout?.TextAlign) ? "left" : blockLayout!.TextAlign,
                overflow = blockLayout?.Overflow ?? OcrBlockOverflowModes.Shrink
            });
        }

        specPages.Add(new { pageIndex = page.PageIndex, widthPoints, heightPoints, blocks = specBlocks });
    }

    var variant = Pick(request.Variant, "mono");
    var specJson = JsonSerializer.Serialize(new { variant, pages = specPages });
    var artifact = await documentJobs.ExportOverlayPdfAsync(jobId, specJson, targetLanguage, variant, cancellationToken);
    return artifact is null
        ? Results.NotFound(OcrError("Overlay export produced no artifact."))
        : Results.Ok(new { artifactId = artifact.Id, kind = artifact.Kind, fileName = artifact.FileName });
});

// Loopback-only OpenAI-compatible shim that pdf2zh calls during a pdf2zh export. The job +
// profile ride in the model name ("verbeam:{jobId}:{profileId}"); the requested source segment
// is matched against the job's block edits/translations (or LLM-translated as a fallback).
app.MapPost("/internal/v1/chat/completions", async (
    HttpContext httpContext,
    Pdf2zhTranslationBridge pdf2zhBridge,
    CancellationToken cancellationToken) =>
{
    var remote = httpContext.Connection.RemoteIpAddress;
    if (remote is not null && !System.Net.IPAddress.IsLoopback(remote))
    {
        return Results.StatusCode(403);
    }

    using var doc = await JsonDocument.ParseAsync(httpContext.Request.Body, cancellationToken: cancellationToken);
    var root = doc.RootElement;
    var model = root.TryGetProperty("model", out var modelEl) ? modelEl.GetString() ?? string.Empty : string.Empty;

    var jobId = string.Empty;
    var profileId = "default";
    var modelParts = model.Split(':');
    if (modelParts.Length >= 2 && modelParts[0] == "verbeam")
    {
        jobId = modelParts[1];
        if (modelParts.Length >= 3 && !string.IsNullOrWhiteSpace(modelParts[2]))
        {
            profileId = modelParts[2];
        }
    }

    var userContent = string.Empty;
    if (root.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
    {
        foreach (var message in messages.EnumerateArray())
        {
            if (message.TryGetProperty("role", out var role) && role.GetString() == "user" &&
                message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
            {
                userContent = content.GetString() ?? string.Empty;
            }
        }
    }

    string reply;
    if (userContent.Contains("terminologist", StringComparison.OrdinalIgnoreCase))
    {
        reply = "[]"; // term-extraction request: Verbeam supplies no glossary terms here
    }
    else
    {
        var source = ExtractPdf2zhSource(userContent);
        reply = string.IsNullOrEmpty(jobId)
            ? source
            : await pdf2zhBridge.TranslateSegmentAsync(jobId, profileId, source, cancellationToken);
    }

    return Results.Json(new
    {
        id = "verbeam-cmpl",
        @object = "chat.completion",
        created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        model,
        choices = new[]
        {
            new { index = 0, message = new { role = "assistant", content = reply }, finish_reason = "stop" }
        },
        usage = new { prompt_tokens = 0, completion_tokens = 0, total_tokens = 0 }
    });
});

app.MapPost("/ocr/document-jobs/{jobId}/cancel", async (
    string jobId,
    DocumentJobService documentJobs,
    CancellationToken cancellationToken) =>
{
    var canceled = await documentJobs.CancelAsync(jobId, cancellationToken);
    return canceled ? Results.Ok(new { jobId, canceled = true }) : Results.NotFound(OcrError("Document OCR job is not running."));
});

app.MapGet("/ocr/document-jobs/{jobId}/events", async (
    string jobId,
    HttpContext context,
    DocumentJobService documentJobs,
    CancellationToken cancellationToken) =>
{
    await StreamDocumentJobEventsForEndpointAsync(jobId, context, documentJobs, cancellationToken);
});

app.MapGet("/ocr/document-jobs/{jobId}/artifacts/{artifactId}", async (
    string jobId,
    string artifactId,
    DocumentJobService documentJobs,
    CancellationToken cancellationToken) =>
{
    var artifact = await documentJobs.GetArtifactAsync(jobId, artifactId, cancellationToken);
    if (artifact is null || !System.IO.File.Exists(artifact.Path))
    {
        return Results.NotFound(OcrError("Document artifact was not found."));
    }

    return Results.File(artifact.Path, artifact.ContentType, artifact.FileName, enableRangeProcessing: true);
});

app.MapGet("/ocr/document-jobs/{jobId}/preview", async (
    string jobId,
    DocumentJobService documentJobs,
    CancellationToken cancellationToken) =>
{
    var job = await documentJobs.GetAsync(jobId, cancellationToken);
    if (job is null)
    {
        return Results.NotFound(OcrError("Document OCR job was not found."));
    }

    var document = await documentJobs.GetTranslatedDocumentAsync(jobId, cancellationToken);
    if (document is not null && document.Pages.Count > 0)
    {
        return Results.Ok(new
        {
            kind = "layout",
            pageCount = document.Pages.Count,
            layoutHtml = RenderOcrDocumentLayoutHtml(document)
        });
    }

    if (job.Status is "failed" or "canceled")
    {
        return Results.Ok(new
        {
            kind = "error",
            status = job.Status,
            errorCode = job.ErrorCode,
            errorMessage = job.ErrorMessage
        });
    }

    if (job.SourceKind is "text" or "markdown" or "html")
    {
        var translated = job.Artifacts.FirstOrDefault(item => item.Kind.Equals("translated", StringComparison.OrdinalIgnoreCase));
        if (translated is not null && System.IO.File.Exists(translated.Path))
        {
            const int previewLimitBytes = 256 * 1024;
            await using var stream = new FileStream(translated.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var buffer = new char[previewLimitBytes];
            var read = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken);
            return Results.Ok(new
            {
                kind = job.SourceKind == "html" ? "html" : "text",
                truncated = !reader.EndOfStream,
                content = new string(buffer, 0, read)
            });
        }
    }

    if (job.SourceKind is "docx" or "pptx" or "xlsx" && job.Artifacts.Count > 0)
    {
        return Results.Ok(new { kind = "file" });
    }

    return Results.Ok(new { kind = "pending" });
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

app.MapGet("/asr/video-sessions", async (
    VideoSpeechSessionService videoSessions,
    string? profile,
    int? limit,
    CancellationToken cancellationToken) =>
{
    var profileId = Pick(profile, "default");
    return Results.Ok(await videoSessions.ListAsync(profileId, limit ?? 50, cancellationToken));
});

app.MapPost("/asr/video-sessions", async (
    HttpContext context,
    VideoSpeechSessionRequest request,
    VideoSpeechSessionService videoSessions,
    IMemoryPrincipalPermissionStore principalPermissions,
    IMemoryPrincipalSessionStore principalSessions,
    CancellationToken cancellationToken) =>
{
    try
    {
        var profileId = Pick(request.Profile, "default");
        var identity = await RequestMemoryIdentityAsync(context, options, principalSessions, cancellationToken);
        request = request with
        {
            PrincipalId = identity.Principal,
            AllowSharedMemory = await AllowSharedMemoryForIdentityAsync(
                options,
                principalPermissions,
                identity,
                profileId,
                cancellationToken)
        };
        var session = await videoSessions.StartAsync(request, cancellationToken);
        return Results.Accepted($"/asr/video-sessions/{session.Id}", session);
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

app.MapGet("/asr/video-sessions/{sessionId}", async (
    string sessionId,
    VideoSpeechSessionService videoSessions,
    CancellationToken cancellationToken) =>
{
    var session = await videoSessions.GetAsync(sessionId, cancellationToken);
    return session is null ? Results.NotFound(AsrError("Video speech session was not found.")) : Results.Ok(session);
});

app.MapPost("/asr/video-sessions/{sessionId}/position", async (
    string sessionId,
    VideoSpeechPositionRequest request,
    VideoSpeechSessionService videoSessions,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await videoSessions.UpdatePositionAsync(sessionId, request, cancellationToken));
    }
    catch (InvalidOperationException ex)
    {
        return Results.NotFound(AsrError(ex.Message));
    }
});

app.MapGet("/asr/video-sessions/{sessionId}/segments", async (
    string sessionId,
    double? start,
    double? end,
    VideoSpeechSessionService videoSessions,
    CancellationToken cancellationToken) =>
{
    var from = Math.Max(0, start ?? 0);
    var to = Math.Max(from, end ?? (from + 120));
    return Results.Ok(await videoSessions.ListSegmentsAsync(sessionId, from, to, cancellationToken));
});

app.MapPost("/asr/video-sessions/{sessionId}/cancel", async (
    string sessionId,
    VideoSpeechSessionService videoSessions,
    CancellationToken cancellationToken) =>
{
    var canceled = await videoSessions.CancelAsync(sessionId, cancellationToken);
    return canceled ? Results.Ok(new { sessionId, canceled = true }) : Results.NotFound(AsrError("Video speech session was not found."));
});

app.MapGet("/asr/video-sessions/{sessionId}/events", async (
    string sessionId,
    HttpContext context,
    VideoSpeechSessionService videoSessions,
    VideoSpeechEventBroker eventBroker,
    CancellationToken cancellationToken) =>
{
    await StreamVideoSpeechSessionEventsAsync(sessionId, context, videoSessions, eventBroker, cancellationToken);
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

app.MapPatch("/ocr/corrections/{correctionId}", async (
    string correctionId,
    OcrCorrectionUpdateRequest request,
    IOcrMemoryStore memoryStore,
    CancellationToken cancellationToken) =>
{
    try
    {
        var updated = await memoryStore.UpdateCorrectionAsync(correctionId, request, cancellationToken);
        return updated is null
            ? Results.NotFound(OcrError("OCR correction was not found."))
            : Results.Ok(updated);
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

app.MapGet("/app", (HttpContext ctx) =>
{
    // The workbench HTML/JS is compiled into the binary; never let the browser serve a
    // stale cached copy — that silently ran old JS across several rebuild/test cycles.
    ctx.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
    ctx.Response.Headers.Pragma = "no-cache";
    return Results.Content(AppWorkbenchPage.Html, "text/html; charset=utf-8");
});

app.MapGet("/pdf-editor", (HttpContext ctx) =>
{
    ctx.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
    ctx.Response.Headers.Pragma = "no-cache";
    return Results.Content(AppPdfEditorPage.Html, "text/html; charset=utf-8");
});

app.MapGet("/profiles", (HttpContext ctx) =>
{
    ctx.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
    ctx.Response.Headers.Pragma = "no-cache";
    return Results.Content(AppGameProfilesPage.Html, "text/html; charset=utf-8");
});

app.MapGet("/viewer", () => Results.Content(BroadcastViewerPage.Html, "text/html; charset=utf-8"));

app.MapGet("/projector", () => Results.Content(ProjectorPage.Html, "text/html; charset=utf-8"));

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
    HttpContext context,
    SpeechTranslateRequest request,
    SpeechService speechService,
    TranslationService translationService,
    IMemoryPrincipalPermissionStore principalPermissions,
    IMemoryPrincipalSessionStore principalSessions,
    TranslationBroadcastHub broadcastHub,
    CancellationToken cancellationToken) =>
{
    try
    {
        var profileId = Pick(request.Profile, "default");
        var identity = await RequestMemoryIdentityAsync(context, options, principalSessions, cancellationToken);
        var principalId = identity.Principal;
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
            await AllowSharedMemoryForIdentityAsync(
                options,
                principalPermissions,
                identity,
                profileId,
                cancellationToken),
            principalId,
            cancellationToken);

        return Results.Ok(new SpeechTranslateResponse(
            speech,
            translations,
            MergeTokenUsage(translations.Select(segment => segment.TokenUsage), "asr:segments")));
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
    IMemoryPrincipalPermissionStore principalPermissions,
    IMemoryPrincipalSessionStore principalSessions,
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
        principalPermissions,
        principalSessions,
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

app.MapPost("/ocr/bytes", async (
    HttpRequest httpRequest,
    OcrService ocrService,
    CancellationToken cancellationToken) =>
{
    try
    {
        var maxBytes = Math.Max(1, options.Ocr.MaxImageBytes);
        if (httpRequest.ContentLength is > 0 && httpRequest.ContentLength > maxBytes)
        {
            return Results.BadRequest(OcrError($"image payload is too large. Max size is {maxBytes} bytes."));
        }

        await using var imageBytes = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await httpRequest.Body.ReadAsync(buffer, cancellationToken);
            if (read <= 0)
            {
                break;
            }

            if (imageBytes.Length + read > maxBytes)
            {
                return Results.BadRequest(OcrError($"image payload is too large. Max size is {maxBytes} bytes."));
            }

            imageBytes.Write(buffer, 0, read);
        }

        if (imageBytes.Length == 0)
        {
            throw new ArgumentException("image body is required.");
        }

        string? Query(string name)
            => httpRequest.Query.TryGetValue(name, out var values) && values.Count > 0
                ? values.ToString()
                : null;

        bool? QueryBool(string name)
        {
            var value = Query(name);
            return bool.TryParse(value, out var parsed) ? parsed : null;
        }

        var contentType = Query("imageMimeType")
            ?? httpRequest.ContentType?.Split(';', 2, StringSplitOptions.TrimEntries)[0]
            ?? "application/octet-stream";
        var allowedLanguages = Query("allowedLanguages")?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var request = new OcrRequest
        {
            ImageBase64 = Convert.ToBase64String(imageBytes.ToArray()),
            ImageMimeType = contentType,
            Provider = Query("provider"),
            ContentType = Query("contentType"),
            Preference = Query("preference"),
            Language = Query("language"),
            LanguageHint = Query("languageHint"),
            AllowedLanguages = allowedLanguages,
            Profile = Query("profile"),
            SessionId = Query("sessionId"),
            NormalizeWhitespace = QueryBool("normalizeWhitespace"),
            PreprocessingPreset = Query("preprocessingPreset"),
            Realtime = QueryBool("realtime"),
            Refine = QueryBool("refine"),
            AutoSuppressRecurringText = QueryBool("autoSuppressRecurringText")
        };

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

app.MapPost("/ocr/smoke", async (
    OcrSmokeTestRequest request,
    OcrService ocrService,
    IOcrMemoryStore memoryStore,
    CancellationToken cancellationToken) =>
{
    try
    {
        var stopwatch = Stopwatch.StartNew();
        var ocr = await ocrService.RecognizeAsync(
            new OcrRequest
            {
                ImageBase64 = request.ImageBase64,
                ImageMimeType = request.ImageMimeType,
                Provider = request.Provider,
                ContentType = request.ContentType,
                Preference = request.Preference,
                Language = request.Language,
                Profile = request.Profile,
                SessionId = request.SessionId,
                NormalizeWhitespace = request.NormalizeWhitespace,
                PreprocessingPreset = request.PreprocessingPreset
            },
            cancellationToken);
        stopwatch.Stop();

        var response = BuildOcrSmokeResponse(request, ocr, stopwatch.ElapsedMilliseconds);
        var record = BuildOcrSmokeRecord(request, response);
        await memoryStore.AddSmokeResultAsync(record, cancellationToken);
        return Results.Ok(response);
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

app.MapPost("/ocr/smoke/matrix", async (
    OcrSmokeMatrixRequest request,
    OcrRoutingService routing,
    OcrService ocrService,
    IOcrMemoryStore memoryStore,
    CancellationToken cancellationToken) =>
{
    var providers = NormalizeOcrSmokeMatrixProviders(
        request.Providers,
        routing,
        request.ContentType,
        request.Preference);
    if (providers.Count == 0)
    {
        return Results.BadRequest(OcrError("providers are required."));
    }

    var items = new List<OcrSmokeMatrixItem>();
    foreach (var provider in providers)
    {
        var smokeRequest = ToOcrSmokeTestRequest(request, provider);
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var ocr = await ocrService.RecognizeAsync(
                new OcrRequest
                {
                    ImageBase64 = smokeRequest.ImageBase64,
                    ImageMimeType = smokeRequest.ImageMimeType,
                    Provider = smokeRequest.Provider,
                    ContentType = smokeRequest.ContentType,
                    Preference = smokeRequest.Preference,
                    Language = smokeRequest.Language,
                    Profile = smokeRequest.Profile,
                    SessionId = smokeRequest.SessionId,
                    NormalizeWhitespace = smokeRequest.NormalizeWhitespace,
                    PreprocessingPreset = smokeRequest.PreprocessingPreset
                },
                cancellationToken);
            stopwatch.Stop();

            var response = BuildOcrSmokeResponse(smokeRequest, ocr, stopwatch.ElapsedMilliseconds);
            var record = BuildOcrSmokeRecord(smokeRequest, response);
            await memoryStore.AddSmokeResultAsync(record, cancellationToken);
            items.Add(new OcrSmokeMatrixItem(provider, Succeeded: true, response, "0", string.Empty));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or TimeoutException)
        {
            var errorCode = ex is TimeoutException ? "ocr_timeout" : "invalid_ocr_request";
            await memoryStore.AddSmokeResultAsync(
                BuildFailedOcrSmokeRecord(smokeRequest, provider, options.Ocr.DefaultLanguage, errorCode, ex.Message),
                cancellationToken);
            items.Add(new OcrSmokeMatrixItem(provider, Succeeded: false, null, errorCode, ex.Message));
        }
    }

    return Results.Ok(new OcrSmokeMatrixResponse(
        items,
        items.Count(item => item.Succeeded),
        items.Count(item => !item.Succeeded),
        DateTimeOffset.UtcNow));
});

app.MapGet("/ocr/smoke", async (
    IOcrMemoryStore memoryStore,
    string? profile,
    int? limit,
    CancellationToken cancellationToken) =>
{
    var profileId = Pick(profile, "default");
    return Results.Ok(await memoryStore.ListSmokeResultsAsync(profileId, limit ?? 50, cancellationToken));
});

app.MapGet("/ocr/smoke/quality", async (
    IOcrMemoryStore memoryStore,
    string? profile,
    int? limit,
    CancellationToken cancellationToken) =>
{
    var profileId = Pick(profile, "default");
    var records = await memoryStore.ListSmokeResultsAsync(profileId, limit ?? 100, cancellationToken);
    return Results.Ok(BuildOcrSmokeQualitySummaries(profileId, records));
});

app.MapPost("/ocr/translate", async (
    HttpContext context,
    OcrTranslateRequest request,
    OcrService ocrService,
    TranslationService translationService,
    MemoryMaintenanceService memoryMaintenance,
    IMemoryPrincipalPermissionStore principalPermissions,
    IMemoryPrincipalSessionStore principalSessions,
    TranslationBroadcastHub broadcastHub,
    CancellationToken cancellationToken) =>
{
    try
    {
        var profileId = Pick(request.Profile, "default");
        var identity = await RequestMemoryIdentityAsync(context, options, principalSessions, cancellationToken);
        var principalId = identity.Principal;
        var ocr = await ocrService.RecognizeAsync(
            new OcrRequest
            {
                ImageBase64 = request.ImageBase64,
                ImageMimeType = request.ImageMimeType,
                Provider = request.OcrProvider,
                ContentType = request.ContentType,
                Preference = request.Preference,
                Language = request.Language,
                Profile = request.Profile,
                SessionId = request.SessionId,
                NormalizeWhitespace = request.NormalizeWhitespace,
                PreprocessingPreset = request.PreprocessingPreset,
                Realtime = request.Realtime
            },
            cancellationToken);

        if (request.Realtime != true)
        {
            await memoryMaintenance.MaintainOcrCorrectionCandidatesAsync(
                profileId,
                Pick(ResolveOcrTranslationSource(request.Source, ocr.DetectedLanguage), ocr.Language),
                Pick(request.Target, options.DefaultTarget),
                ocr.EventId,
                ocr.AppliedCorrections,
                cancellationToken);
        }

        var structured = await TranslateOcrDocumentAsync(
            ocr,
            request,
            translationService,
            options,
            await AllowSharedMemoryForIdentityAsync(
                options,
                principalPermissions,
                identity,
                profileId,
                cancellationToken),
            principalId,
            cancellationToken);
        var failedSegments = structured.Segments
            .Where(segment => segment.ErrorCode != "0")
            .ToArray();
        var response = failedSegments.Length == 0
            ? MortTranslateResponse.Success(structured.Text, structured.TokenUsage)
            : MortTranslateResponse.Error(
                ocr.Text,
                string.Join("; ", failedSegments.Select(segment => segment.ErrorMessage).Where(message => !string.IsNullOrWhiteSpace(message)).Distinct(StringComparer.Ordinal)),
                failedSegments[0].ErrorCode);
        var outcome = new TranslationOutcome(
            failedSegments.Length == 0,
            structured.Text,
            structured.Engine,
            structured.LatencyMs,
            structured.CacheHit,
            failedSegments.Length == 0 ? "0" : failedSegments[0].ErrorCode,
            failedSegments.Length == 0 ? string.Empty : response.ErrorMessage,
            structured.TokenUsage);

        if (outcome.IsSuccess)
        {
            await broadcastHub.BroadcastAsync(
                CreateBroadcastMessage(
                    ocr.Text,
                    outcome.Text,
                    Pick(ResolveOcrTranslationSource(request.Source, ocr.DetectedLanguage), options.DefaultSource),
                    Pick(request.Target, options.DefaultTarget),
                    Pick(request.Mode, options.DefaultMode),
                    Pick(request.TranslationProvider, options.DefaultProvider),
                    request.Glossary,
                    outcome.Engine,
                    outcome.LatencyMs,
                    outcome.CacheHit,
                    PickBroadcastSourceKind(request.BroadcastSourceKind, "ocr")),
                cancellationToken);
        }

        return Results.Ok(new OcrTranslateResponse(ocr, response, structured));
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
    HttpContext context,
    MortTranslateRequest request,
    TranslationService translationService,
    IMemoryPrincipalPermissionStore principalPermissions,
    IMemoryPrincipalSessionStore principalSessions,
    TranslationBroadcastHub broadcastHub,
    CancellationToken cancellationToken) =>
{
    try
    {
        var profileId = Pick(request.Profile, "default");
        var identity = await RequestMemoryIdentityAsync(context, options, principalSessions, cancellationToken);
        request = request with
        {
            PrincipalId = identity.Principal,
            AllowSharedMemory = await AllowSharedMemoryForIdentityAsync(
                options,
                principalPermissions,
                identity,
                profileId,
                cancellationToken)
        };
        var outcome = await translationService.TranslateAsync(request, cancellationToken);
        if (outcome.IsSuccess)
        {
            await broadcastHub.BroadcastAsync(
                CreateBroadcastMessage(
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
                    PickBroadcastSourceKind(request.BroadcastSourceKind, "text")),
                cancellationToken);

            return Results.Ok(MortTranslateResponse.Success(outcome.Text, outcome.TokenUsage));
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

// Server-Sent-Events streaming translate: emits {"delta":"..."} per generated token
// (LLM path only), then a terminal {"done":true,"text":...,"engine":...} event. Cache /
// OpenCC / template hits resolve instantly and arrive as one done event with no deltas.
app.MapPost("/translate/stream", async (
    HttpContext context,
    MortTranslateRequest request,
    TranslationService translationService,
    IMemoryPrincipalPermissionStore principalPermissions,
    IMemoryPrincipalSessionStore principalSessions,
    TranslationBroadcastHub broadcastHub,
    CancellationToken cancellationToken) =>
{
    var profileId = Pick(request.Profile, "default");
    var identity = await RequestMemoryIdentityAsync(context, options, principalSessions, cancellationToken);
    request = request with
    {
        PrincipalId = identity.Principal,
        AllowSharedMemory = await AllowSharedMemoryForIdentityAsync(
            options, principalPermissions, identity, profileId, cancellationToken)
    };

    context.Response.Headers.ContentType = "text/event-stream";
    context.Response.Headers.CacheControl = "no-cache";
    context.Response.Headers["X-Accel-Buffering"] = "no";

    async Task WriteEventAsync(object payload)
    {
        await context.Response.WriteAsync(
            $"data: {JsonSerializer.Serialize(payload, SseJsonOptions)}\n\n", cancellationToken);
        await context.Response.Body.FlushAsync(cancellationToken);
    }

    try
    {
        var outcome = await translationService.TranslateAsync(
            request,
            onDelta: delta => WriteEventAsync(new { delta }),
            cancellationToken);

        if (outcome.IsSuccess)
        {
            await broadcastHub.BroadcastAsync(
                CreateBroadcastMessage(
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
                    PickBroadcastSourceKind(request.BroadcastSourceKind, "text")),
                cancellationToken);

            await WriteEventAsync(new { done = true, text = outcome.Text, engine = outcome.Engine, latencyMs = outcome.LatencyMs, cacheHit = outcome.CacheHit, tokenUsage = outcome.TokenUsage, errorCode = "0" });
        }
        else
        {
            await WriteEventAsync(new { done = true, text = request.Text ?? string.Empty, errorCode = outcome.ErrorCode, errorMessage = outcome.ErrorMessage });
        }
    }
    catch (OperationCanceledException)
    {
        throw;
    }
    catch (Exception ex)
    {
        await WriteEventAsync(new { done = true, text = request.Text ?? string.Empty, errorCode = "1", errorMessage = ex.Message });
    }
});

app.MapPost("/translate/web", async (
    HttpContext context,
    ReadFrogTranslateRequest request,
    ReadFrogTranslationService readFrogTranslationService,
    IMemoryPrincipalPermissionStore principalPermissions,
    IMemoryPrincipalSessionStore principalSessions,
    TranslationBroadcastHub broadcastHub,
    CancellationToken cancellationToken) =>
{
    try
    {
        var profileId = Pick(request.Profile, "read-frog");
        var identity = await RequestMemoryIdentityAsync(context, options, principalSessions, cancellationToken);
        var principalId = identity.Principal;
        var translated = await readFrogTranslationService.TranslateAsync(
            request,
            await AllowSharedMemoryForIdentityAsync(
                options,
                principalPermissions,
                identity,
                profileId,
                cancellationToken),
            principalId,
            cancellationToken);
        var translatedRequest = translated.Request;
        var outcome = translated.Outcome;
        if (outcome.IsSuccess)
        {
            await broadcastHub.BroadcastAsync(
                CreateBroadcastMessage(
                    translatedRequest.Text ?? string.Empty,
                    outcome.Text,
                    Pick(translatedRequest.Source, options.DefaultSource),
                    Pick(translatedRequest.Target, options.DefaultTarget),
                    Pick(translatedRequest.Mode, options.DefaultMode),
                    Pick(translatedRequest.Provider, options.DefaultProvider),
                    translatedRequest.Glossary,
                    outcome.Engine,
                    outcome.LatencyMs,
                    outcome.CacheHit,
                    "read-frog"),
                cancellationToken);

            return Results.Ok(ReadFrogTranslateResponse.Success(outcome));
        }

        return Results.Ok(ReadFrogTranslateResponse.Error(
            translatedRequest.Text ?? request.Text ?? string.Empty,
            outcome.ErrorMessage,
            outcome.ErrorCode));
    }
    catch (OperationCanceledException)
    {
        throw;
    }
    catch (Exception ex)
    {
        return Results.Ok(ReadFrogTranslateResponse.Error(request.Text ?? string.Empty, ex.Message));
    }
});

var trayEnabled = options.Tray.Enabled || args.Contains("--tray", StringComparer.OrdinalIgnoreCase);
if (trayEnabled)
{
    var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
    var workbenchUrl = (builder.Configuration["Urls"] ?? "http://localhost:5757").Split(';')[0];
    var showWindow = !args.Contains("--no-window", StringComparer.OrdinalIgnoreCase);
    Verbeam.Api.Tray.TrayHost.Start(
        app.Services,
        workbenchUrl,
        lifetime.StopApplication,
        lifetime.ApplicationStopping,
        showWindow);
    Verbeam.Api.Tray.TrayHost.HideConsoleWindow();
}

app.Run();

static string Pick(string? value, string fallback)
    => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

static string RenderApiSupplierFormPage(
    IReadOnlyList<ApiSupplierPreset> presets,
    IReadOnlyList<ApiSupplierProfile> suppliers,
    string saved,
    string error)
{
    static string H(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
    static string PresetOption(ApiSupplierPreset preset)
    {
        var label = $"{preset.DisplayName} ({preset.Category})";
        var hint = string.IsNullOrWhiteSpace(preset.DefaultModel)
            ? preset.Protocol
            : $"{preset.Protocol} / {preset.DefaultModel}";
        return $"""<option value="{H(preset.Id)}" data-base="{H(preset.BaseUrl)}" data-model="{H(preset.DefaultModel)}">{H(label)} - {H(hint)}</option>""";
    }

    var orderedPresets = presets
        .OrderBy(preset => preset.Id.Equals("custom", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
        .ThenBy(preset => preset.Category, StringComparer.OrdinalIgnoreCase)
        .ThenBy(preset => preset.DisplayName, StringComparer.OrdinalIgnoreCase)
        .ToArray();
    var presetOptions = string.Join(Environment.NewLine, orderedPresets.Select(PresetOption));
    var supplierRows = suppliers.Count == 0
        ? """<div class="empty">No API suppliers configured yet.</div>"""
        : string.Join(Environment.NewLine, suppliers
            .OrderBy(supplier => supplier.Name, StringComparer.OrdinalIgnoreCase)
            .Select(supplier => $"""
              <div class="supplier-row">
                <div>
                  <strong>{H(supplier.Name)}</strong>
                  <span>{H(supplier.PresetId)} / {H(supplier.Protocol)}</span>
                </div>
                <code>{H(supplier.ActiveModel)}</code>
              </div>
            """));
    var savedBlock = string.IsNullOrWhiteSpace(saved)
        ? string.Empty
        : $"""<div class="notice success">Saved supplier: {H(saved)}</div>""";
    var errorBlock = string.IsNullOrWhiteSpace(error)
        ? string.Empty
        : $"""<div class="notice error">{H(error)}</div>""";

    return $$"""
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Add API Supplier - Verbeam</title>
  <style>
    :root { color-scheme: light dark; --accent:#e33462; --line:#e8b8c5; --bg:#fff7fa; --panel:#ffffff; --text:#26141a; --muted:#765b65; }
    body { margin:0; min-height:100vh; background:var(--bg); color:var(--text); font:14px/1.5 system-ui,-apple-system,BlinkMacSystemFont,"Segoe UI",sans-serif; }
    main { max-width:860px; margin:0 auto; padding:32px 18px 48px; }
    header { display:flex; align-items:center; justify-content:space-between; gap:16px; margin-bottom:18px; }
    h1 { margin:0; font-size:24px; }
    a { color:var(--accent); text-decoration:none; font-weight:700; }
    .card { border:1px solid var(--line); border-radius:12px; background:var(--panel); box-shadow:0 16px 40px rgba(80,20,40,.08); padding:18px; margin:14px 0; }
    .grid { display:grid; grid-template-columns:1fr 1fr; gap:14px; }
    .full { grid-column:1 / -1; }
    label { display:grid; gap:6px; color:var(--muted); font-weight:700; font-size:12px; letter-spacing:.02em; text-transform:uppercase; }
    input, select { width:100%; box-sizing:border-box; min-height:40px; border:1px solid var(--line); border-radius:8px; padding:0 10px; background:#fff; color:#1f1117; font:14px ui-monospace,SFMono-Regular,Consolas,monospace; }
    button { min-height:42px; border:0; border-radius:8px; padding:0 18px; background:var(--accent); color:#fff; font-weight:800; cursor:pointer; }
    button:hover { filter:brightness(.96); }
    .notice { border-radius:8px; padding:10px 12px; margin:12px 0; font-weight:700; }
    .success { background:#e8fff1; color:#096b32; border:1px solid #9be7b8; }
    .error { background:#fff1f3; color:#b1122f; border:1px solid #ffb4c0; }
    .hint { color:var(--muted); margin-top:8px; }
    .supplier-row { display:flex; align-items:center; justify-content:space-between; gap:14px; padding:12px 0; border-top:1px solid var(--line); }
    .supplier-row:first-child { border-top:0; }
    .supplier-row span { display:block; color:var(--muted); font-size:12px; }
    code { overflow:hidden; text-overflow:ellipsis; white-space:nowrap; max-width:360px; color:#3b1c27; }
    .empty { color:var(--muted); }
    @media (max-width:700px) { .grid { grid-template-columns:1fr; } header { align-items:flex-start; flex-direction:column; } }
  </style>
</head>
<body>
  <main>
    <header>
      <div>
        <h1>Add API Supplier</h1>
        <div class="hint">Standalone form. This bypasses the workbench modal and JavaScript click path.</div>
      </div>
      <a href="/app?mode=settings">Back to Verbeam</a>
    </header>

    {{savedBlock}}
    {{errorBlock}}

    <form class="card" method="post" action="/api-suppliers/new">
      <div class="grid">
        <label class="full">Preset
          <select name="presetId">
            {{presetOptions}}
          </select>
        </label>
        <label>Name
          <input name="name" placeholder="DeepSeek, OpenRouter, Custom API" autocomplete="off">
        </label>
        <label>Active model
          <input name="activeModel" placeholder="deepseek-chat, gpt-4o-mini, ..." autocomplete="off">
        </label>
        <label class="full">Base URL
          <input name="baseUrl" placeholder="https://api.example.com/v1" autocomplete="off">
        </label>
        <label class="full">Models URL optional
          <input name="modelsUrl" placeholder="https://api.example.com/v1/models" autocomplete="off">
        </label>
        <label class="full">API Key optional
          <input name="apiKey" type="password" placeholder="sk-..." autocomplete="off">
        </label>
      </div>
      <p class="hint">If Base URL or model is left blank, Verbeam uses the selected preset defaults when available.</p>
      <button type="submit">Save API Supplier</button>
    </form>

    <section class="card">
      <h2>Configured suppliers</h2>
      {{supplierRows}}
    </section>
  </main>
</body>
</html>
""";
}

static object OcrError(string message)
    => new { errorCode = "invalid_ocr_request", errorMessage = message };

// Pull the raw source text out of a pdf2zh translation prompt: it follows the marker
// "Now translate the following text:" (rich/structured) or "Input:" (simple), else is the
// whole message. See Pdf2zhTranslationBridge / the Spike 0 findings.
static string ExtractPdf2zhSource(string content)
{
    const string richMarker = "Now translate the following text:";
    var rich = content.LastIndexOf(richMarker, StringComparison.OrdinalIgnoreCase);
    if (rich >= 0)
    {
        return content[(rich + richMarker.Length)..].Trim();
    }

    const string simpleMarker = "Input:";
    var simple = content.LastIndexOf(simpleMarker, StringComparison.Ordinal);
    if (simple >= 0)
    {
        return content[(simple + simpleMarker.Length)..].Trim();
    }

    return content.Trim();
}

// Normalize a block bbox into 0..1 page space for the PDF overlay editor (shared with the
// exporters via Verbeam.Core.Services.OcrCoordinateMath). Projected to camelCase nx/ny/nw/nh.
static object NormalizeBlockBox(
    Verbeam.Core.Models.OcrPageResult page,
    Verbeam.Core.Models.OcrBlock block,
    Verbeam.Core.Models.OcrBlockLayout? layout)
{
    var box = Verbeam.Core.Services.OcrCoordinateMath.NormalizeBox(page, block, layout);
    return new { nx = box.Nx, ny = box.Ny, nw = box.Nw, nh = box.Nh };
}

static OcrSmokeTestResponse BuildOcrSmokeResponse(
    OcrSmokeTestRequest request,
    OcrResponse ocr,
    long elapsedMs)
{
    var expected = request.ExpectedText?.Trim() ?? string.Empty;
    var recognized = ocr.Text ?? string.Empty;
    var normalizedExpected = NormalizeSmokeText(expected);
    var normalizedRecognized = NormalizeSmokeText(recognized);
    var hasExpected = normalizedExpected.Length > 0;
    var editDistance = hasExpected
        ? ComputeEditDistance(normalizedExpected, normalizedRecognized)
        : normalizedRecognized.Length;
    var maxLength = Math.Max(normalizedExpected.Length, normalizedRecognized.Length);
    var similarity = hasExpected && maxLength > 0
        ? Math.Max(0, 1.0 - (editDistance / (double)maxLength))
        : 0;

    var structure = BuildOcrStructureSummary(ocr.Document);
    var structureAssertion = BuildOcrStructureAssertion(request.ExpectedStructure, structure, ocr.Document);

    return new OcrSmokeTestResponse(
        ocr,
        expected,
        recognized,
        hasExpected && string.Equals(normalizedExpected, normalizedRecognized, StringComparison.Ordinal),
        hasExpected && normalizedRecognized.Contains(normalizedExpected, StringComparison.Ordinal),
        Math.Round(similarity, 4),
        editDistance,
        elapsedMs,
        DateTimeOffset.UtcNow)
    {
        Structure = structure,
        StructureAssertion = structureAssertion
    };
}

static OcrSmokeTestRecord BuildOcrSmokeRecord(
    OcrSmokeTestRequest request,
    OcrSmokeTestResponse response)
    => new OcrSmokeTestRecord(
        Guid.NewGuid().ToString("N"),
        Pick(request.Profile, "default"),
        Pick(request.SessionId, "smoke"),
        response.Ocr.Language,
        response.Ocr.Provider,
        response.Ocr.Engine,
        Pick(request.ContentType, string.Empty),
        Pick(request.Preference, string.Empty),
        Pick(request.PreprocessingPreset, "none"),
        response.Ocr.EventId,
        response.ExpectedText,
        response.RecognizedText,
        response.ExactMatch,
        response.ContainsExpected,
        response.Similarity,
        response.EditDistance,
        response.LatencyMs,
        response.CreatedAt)
    {
        Structure = response.Structure,
        StructureAssertion = response.StructureAssertion
    };

static OcrSmokeTestRecord BuildFailedOcrSmokeRecord(
    OcrSmokeTestRequest request,
    string provider,
    string defaultLanguage,
    string errorCode,
    string errorMessage)
    => new(
        Guid.NewGuid().ToString("N"),
        Pick(request.Profile, "default"),
        Pick(request.SessionId, "smoke"),
        Pick(request.Language, defaultLanguage),
        provider,
        string.Empty,
        Pick(request.ContentType, string.Empty),
        Pick(request.Preference, string.Empty),
        Pick(request.PreprocessingPreset, "none"),
        string.Empty,
        request.ExpectedText?.Trim() ?? string.Empty,
        string.Empty,
        ExactMatch: false,
        ContainsExpected: false,
        Similarity: 0,
        EditDistance: 0,
        LatencyMs: 0,
        DateTimeOffset.UtcNow)
    {
        Structure = OcrStructureSummary.Empty,
        StructureAssertion = request.ExpectedStructure is null
            ? OcrStructureAssertion.Empty
            : new OcrStructureAssertion(
                request.ExpectedStructure,
                HasExpected: true,
                Passed: false,
                new[] { $"{errorCode}: {errorMessage}" }),
        Succeeded = false,
        ErrorCode = errorCode,
        ErrorMessage = errorMessage
    };

static IReadOnlyList<string> NormalizeOcrSmokeMatrixProviders(
    IReadOnlyList<string>? providers,
    OcrRoutingService routing,
    string? contentType,
    string? preference)
    => (providers is { Count: > 0 }
            ? providers
            : routing.ListSmokeMatrixProviders(contentType, preference))
        .Select(provider => Pick(provider, string.Empty))
        .Where(provider => !string.IsNullOrWhiteSpace(provider) &&
                           !provider.Equals(OcrRoutingService.AutoProvider, StringComparison.OrdinalIgnoreCase))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Take(12)
        .ToArray();

static OcrSmokeTestRequest ToOcrSmokeTestRequest(
    OcrSmokeMatrixRequest request,
    string provider)
    => new()
    {
        ImageBase64 = request.ImageBase64,
        ImageMimeType = request.ImageMimeType,
        Provider = provider,
        ContentType = request.ContentType,
        Preference = request.Preference,
        Language = request.Language,
        Profile = request.Profile,
        SessionId = string.IsNullOrWhiteSpace(request.SessionId)
            ? $"matrix-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}"
            : request.SessionId,
        NormalizeWhitespace = request.NormalizeWhitespace,
        PreprocessingPreset = request.PreprocessingPreset,
        ExpectedText = request.ExpectedText,
        ExpectedStructure = request.ExpectedStructure
    };

static IReadOnlyList<OcrSmokeQualitySummary> BuildOcrSmokeQualitySummaries(
    string profileId,
    IReadOnlyList<OcrSmokeTestRecord> records)
{
    var engineSummaries = records
        .GroupBy(record => new
        {
            record.Provider,
            record.Engine,
            record.Language,
            record.ContentType,
            record.Preference
        })
        .Select(group => BuildOcrSmokeQualitySummary(profileId, group, scope: "engine"));

    var providerSummaries = records
        .GroupBy(record => new
        {
            record.Provider,
            record.Language,
            record.ContentType,
            record.Preference
        })
        .Select(group => BuildOcrSmokeQualitySummary(profileId, group, engine: "*", scope: "provider"));

    return providerSummaries
        .Concat(engineSummaries)
        .OrderBy(summary => OcrSmokeQualityRank(summary.Status))
        .ThenByDescending(summary => summary.TableIntegrityIssueCount)
        .ThenBy(summary => summary.Scope == "provider" ? 0 : 1)
        .ThenBy(summary => summary.AverageLatencyMs)
        .ThenBy(summary => summary.Provider, StringComparer.OrdinalIgnoreCase)
        .ThenBy(summary => summary.Engine, StringComparer.OrdinalIgnoreCase)
        .ToArray();
}

static OcrSmokeQualitySummary BuildOcrSmokeQualitySummary(
    string profileId,
    IEnumerable<OcrSmokeTestRecord> records,
    string? engine = null,
    string scope = "engine")
{
    var values = records.ToArray();
    var first = values[0];
    var sampleCount = values.Length;
    var textExpectedCount = values.Count(record => !string.IsNullOrWhiteSpace(record.ExpectedText));
    var textPassCount = values.Count(IsOcrTextSmokePass);
    var structureExpectedCount = values.Count(record => record.StructureAssertion.HasExpected);
    var structurePassCount = values.Count(record => record.StructureAssertion.HasExpected && record.StructureAssertion.Passed);
    var tableSampleCount = values.Count(record => record.Structure.TableBlockCount > 0);
    var tableIntegrityIssueCount = values.Count(HasOcrTableIntegrityIssue);
    var runtimeFailureCount = values.Count(record => !record.Succeeded);
    var successfulValues = values.Where(record => record.Succeeded).ToArray();
    var averageSimilarity = successfulValues.Length == 0 ? 0 : Math.Round(successfulValues.Average(record => record.Similarity), 4);
    var averageLatencyMs = successfulValues.Length == 0 ? 0 : (long)Math.Round(successfulValues.Average(record => record.LatencyMs));
    var textPassRate = textExpectedCount == 0 ? 0 : Math.Round(textPassCount / (double)textExpectedCount, 4);
    var structurePassRate = structureExpectedCount == 0 ? 0 : Math.Round(structurePassCount / (double)structureExpectedCount, 4);
    var lastSeenAt = values.Max(record => record.CreatedAt);
    var qualityIssues = BuildOcrSmokeQualityIssues(values);
    var status = BuildOcrSmokeQualityStatus(
        sampleCount,
        textExpectedCount,
        textPassRate,
        structureExpectedCount,
        structurePassRate,
        tableIntegrityIssueCount,
        runtimeFailureCount);

    return new OcrSmokeQualitySummary(
        profileId,
        first.Provider,
        engine ?? first.Engine,
        first.Language,
        first.ContentType,
        first.Preference,
        sampleCount,
        textExpectedCount,
        textPassCount,
        textPassRate,
        structureExpectedCount,
        structurePassCount,
        structurePassRate,
        tableSampleCount,
        tableIntegrityIssueCount,
        runtimeFailureCount,
        averageSimilarity,
        averageLatencyMs,
        lastSeenAt,
        status,
        BuildOcrSmokeQualityNote(status, textExpectedCount, structureExpectedCount, tableIntegrityIssueCount, runtimeFailureCount),
        scope)
    {
        Issues = qualityIssues
    };
}

static IReadOnlyList<OcrSmokeQualityIssue> BuildOcrSmokeQualityIssues(IReadOnlyList<OcrSmokeTestRecord> records)
{
    var issues = new List<(string Code, string Severity, string Message)>();
    foreach (var record in records)
    {
        if (!record.Succeeded)
        {
            var code = string.IsNullOrWhiteSpace(record.ErrorCode) || record.ErrorCode == "0"
                ? "ocr_runtime_failed"
                : record.ErrorCode;
            var message = string.IsNullOrWhiteSpace(record.ErrorMessage)
                ? "runtime failure"
                : record.ErrorMessage;
            issues.Add((code, "error", message));
        }

        foreach (var issue in record.StructureAssertion.Issues)
        {
            issues.Add((issue.Code, issue.Severity, issue.Message));
        }
    }

    return issues
        .Where(issue => !string.IsNullOrWhiteSpace(issue.Code))
        .GroupBy(issue => issue.Code, StringComparer.OrdinalIgnoreCase)
        .Select(group =>
        {
            var values = group.ToArray();
            var severity = values.Any(issue => issue.Severity.Equals("error", StringComparison.OrdinalIgnoreCase))
                ? "error"
                : values[0].Severity;
            var message = values
                .Select(issue => issue.Message)
                .FirstOrDefault(message => !string.IsNullOrWhiteSpace(message)) ?? group.Key;
            return new OcrSmokeQualityIssue(group.Key, severity, values.Length, message);
        })
        .OrderByDescending(issue => issue.Severity.Equals("error", StringComparison.OrdinalIgnoreCase))
        .ThenByDescending(issue => issue.Count)
        .ThenBy(issue => issue.Code, StringComparer.OrdinalIgnoreCase)
        .Take(8)
        .ToArray();
}

static bool IsOcrTextSmokePass(OcrSmokeTestRecord record)
    => !string.IsNullOrWhiteSpace(record.ExpectedText) &&
       (record.ExactMatch || record.ContainsExpected || record.Similarity >= 0.9);

static bool HasOcrTableIntegrityIssue(OcrSmokeTestRecord record)
    => record.Structure.InvalidTableCellCount > 0 ||
       record.Structure.MissingTableCellCount > 0 ||
       record.Structure.OverlappingTableCellCount > 0;

static string BuildOcrSmokeQualityStatus(
    int sampleCount,
    int textExpectedCount,
    double textPassRate,
    int structureExpectedCount,
    double structurePassRate,
    int tableIntegrityIssueCount,
    int runtimeFailureCount)
{
    if (runtimeFailureCount > 0 ||
        tableIntegrityIssueCount > 0 ||
        (structureExpectedCount > 0 && structurePassRate < 1.0) ||
        (textExpectedCount > 0 && textPassRate < 0.8))
    {
        return "fail";
    }

    if (sampleCount < 2 || (textExpectedCount == 0 && structureExpectedCount == 0))
    {
        return "warn";
    }

    return "pass";
}

static string BuildOcrSmokeQualityNote(
    string status,
    int textExpectedCount,
    int structureExpectedCount,
    int tableIntegrityIssueCount,
    int runtimeFailureCount)
{
    if (runtimeFailureCount > 0)
    {
        return "runtime failures detected";
    }

    if (tableIntegrityIssueCount > 0)
    {
        return "table integrity issues detected";
    }

    if (status == "fail" && structureExpectedCount > 0)
    {
        return "structure assertions failed";
    }

    if (status == "fail" && textExpectedCount > 0)
    {
        return "text smoke pass rate is low";
    }

    if (textExpectedCount == 0 && structureExpectedCount == 0)
    {
        return "add expected text or structure checks";
    }

    if (status == "warn")
    {
        return "needs more smoke samples";
    }

    return "qualified by recent smoke samples";
}

static int OcrSmokeQualityRank(string status)
    => status switch
    {
        "fail" => 0,
        "warn" => 1,
        _ => 2
    };

static OcrStructureAssertion BuildOcrStructureAssertion(
    OcrExpectedStructure? expected,
    OcrStructureSummary actual,
    OcrDocumentResult? document)
{
    if (expected is null)
    {
        return OcrStructureAssertion.Empty;
    }

    var issues = new List<OcrStructureIssue>();
    var blocks = EnumerateOcrBlocks(document).ToList();
    var tables = blocks
        .Where(block => block.Table is not null)
        .Select(block => block.Table!)
        .ToList();
    var formulas = blocks
        .Where(block => block.Formula is not null ||
                        string.Equals(block.Type, OcrBlockTypes.Formula, StringComparison.OrdinalIgnoreCase))
        .SelectMany(block => FormulaCandidates(block))
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .ToList();

    Compare(expected.PageCount, actual.PageCount, "pageCount");
    Compare(expected.BlockCount, actual.BlockCount, "blockCount");
    Compare(expected.TextBlockCount, actual.TextBlockCount, "textBlockCount");
    Compare(expected.TableBlockCount, actual.TableBlockCount, "tableBlockCount");
    Compare(expected.FormulaBlockCount, actual.FormulaBlockCount, "formulaBlockCount");
    Compare(expected.TableCellCount, actual.TableCellCount, "tableCellCount");
    CompareAny(expected.TableRowCount, tables.Select(table => table.RowCount), "tableRowCount");
    CompareAny(expected.TableColumnCount, tables.Select(table => table.ColumnCount), "tableColumnCount");
    Compare(expected.TranslatableCellCount, actual.TranslatableCellCount, "translatableCellCount");
    Compare(expected.InvalidTableCellCount, actual.InvalidTableCellCount, "invalidTableCellCount");
    Compare(expected.MissingTableCellCount, actual.MissingTableCellCount, "missingTableCellCount");
    Compare(expected.OverlappingTableCellCount, actual.OverlappingTableCellCount, "overlappingTableCellCount");
    Compare(expected.PassThroughBlockCount, actual.PassThroughBlockCount, "passThroughBlockCount");
    CompareFormulaContains(expected.FormulaLatexContains, formulas);

    var hasExpected = HasExpectedStructureValue(expected);
    return new OcrStructureAssertion(
        expected,
        hasExpected,
        issues.Count == 0,
        issues.Select(issue => issue.Message).ToArray())
    {
        Issues = issues
    };

    void Compare(int? expectedValue, int actualValue, string field)
    {
        if (!expectedValue.HasValue || expectedValue.Value == actualValue)
        {
            return;
        }

        AddIssue(field, expectedValue.Value.ToString(CultureInfo.InvariantCulture), actualValue.ToString(CultureInfo.InvariantCulture));
    }

    void CompareAny(int? expectedValue, IEnumerable<int> actualValues, string field)
    {
        if (!expectedValue.HasValue)
        {
            return;
        }

        var values = actualValues.Distinct().OrderBy(value => value).ToArray();
        if (values.Contains(expectedValue.Value))
        {
            return;
        }

        AddIssue(field, expectedValue.Value.ToString(CultureInfo.InvariantCulture), values.Length == 0 ? "none" : string.Join(",", values));
    }

    void CompareFormulaContains(string? expectedValue, IReadOnlyList<string> actualValues)
    {
        if (string.IsNullOrWhiteSpace(expectedValue))
        {
            return;
        }

        if (actualValues.Any(value => FormulaContains(value, expectedValue)))
        {
            return;
        }

        AddIssue("formulaLatexContains", expectedValue.Trim(), "not found");
    }

    void AddIssue(string field, string expectedValue, string actualValue)
    {
        var message = field == "formulaLatexContains"
            ? $"{field}: expected {expectedValue}"
            : $"{field}: expected {expectedValue}, got {actualValue}";
        issues.Add(new OcrStructureIssue(
            OcrStructureIssueCode(field),
            OcrStructureIssueSeverity(field),
            message,
            expectedValue,
            actualValue));
    }
}

static string OcrStructureIssueCode(string field)
    => field switch
    {
        "invalidTableCellCount" => "ocr_table_invalid_cell_count",
        "missingTableCellCount" => "ocr_table_missing_cell_count",
        "overlappingTableCellCount" => "ocr_table_overlapping_cell_count",
        "formulaLatexContains" => "ocr_formula_latex_missing",
        _ => $"ocr_structure_{ToSnakeCase(field)}_mismatch"
    };

static string OcrStructureIssueSeverity(string field)
    => field is "invalidTableCellCount" or "missingTableCellCount" or "overlappingTableCellCount"
        ? "error"
        : "warn";

static string ToSnakeCase(string value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return "unknown";
    }

    var builder = new StringBuilder(value.Length + 8);
    for (var index = 0; index < value.Length; index++)
    {
        var character = value[index];
        if (char.IsUpper(character) && index > 0)
        {
            builder.Append('_');
        }

        builder.Append(char.ToLowerInvariant(character));
    }

    return builder.ToString();
}

static bool HasExpectedStructureValue(OcrExpectedStructure expected)
    => expected.PageCount.HasValue ||
       expected.BlockCount.HasValue ||
       expected.TextBlockCount.HasValue ||
       expected.TableBlockCount.HasValue ||
       expected.FormulaBlockCount.HasValue ||
       expected.TableCellCount.HasValue ||
       expected.TableRowCount.HasValue ||
       expected.TableColumnCount.HasValue ||
       expected.TranslatableCellCount.HasValue ||
       expected.InvalidTableCellCount.HasValue ||
       expected.MissingTableCellCount.HasValue ||
       expected.OverlappingTableCellCount.HasValue ||
       expected.PassThroughBlockCount.HasValue ||
       !string.IsNullOrWhiteSpace(expected.FormulaLatexContains);

static IEnumerable<OcrBlock> EnumerateOcrBlocks(OcrDocumentResult? document)
{
    if (document is null)
    {
        yield break;
    }

    foreach (var block in document.Pages.SelectMany(page => page.Blocks))
    {
        foreach (var current in EnumerateOcrBlockTree(block))
        {
            yield return current;
        }
    }
}

static IEnumerable<OcrBlock> EnumerateOcrBlockTree(OcrBlock block)
{
    yield return block;

    foreach (var child in block.Children)
    {
        foreach (var current in EnumerateOcrBlockTree(child))
        {
            yield return current;
        }
    }
}

static IEnumerable<string> FormulaCandidates(OcrBlock block)
{
    if (block.Formula is not null)
    {
        yield return block.Formula.Latex;
        yield return block.Formula.SourceText;
    }

    yield return block.Text;
}

static bool FormulaContains(string candidate, string expected)
{
    var trimmedExpected = expected.Trim();
    if (candidate.Contains(trimmedExpected, StringComparison.OrdinalIgnoreCase))
    {
        return true;
    }

    var compactCandidate = RemoveFormulaWhitespace(candidate);
    var compactExpected = RemoveFormulaWhitespace(trimmedExpected);
    return compactExpected.Length > 0 &&
           compactCandidate.Contains(compactExpected, StringComparison.OrdinalIgnoreCase);
}

static string RemoveFormulaWhitespace(string value)
    => string.Concat(value.Where(character => !char.IsWhiteSpace(character)));

static OcrStructureSummary BuildOcrStructureSummary(OcrDocumentResult? document)
{
    if (document is null || document.Pages.Count == 0)
    {
        return OcrStructureSummary.Empty;
    }

    var blockCount = 0;
    var textBlockCount = 0;
    var tableBlockCount = 0;
    var formulaBlockCount = 0;
    var tableCellCount = 0;
    var translatableCellCount = 0;
    var invalidTableCellCount = 0;
    var missingTableCellCount = 0;
    var overlappingTableCellCount = 0;
    var passThroughBlockCount = 0;

    foreach (var block in document.Pages.SelectMany(page => page.Blocks))
    {
        CountBlock(block);
    }

    return new OcrStructureSummary(
        document.Pages.Count,
        blockCount,
        textBlockCount,
        tableBlockCount,
        formulaBlockCount,
        tableCellCount,
        translatableCellCount,
        invalidTableCellCount,
        missingTableCellCount,
        overlappingTableCellCount,
        passThroughBlockCount);

    void CountBlock(OcrBlock block)
    {
        blockCount++;
        if (block.Table is not null)
        {
            tableBlockCount++;
            tableCellCount += block.Table.Cells.Count;
            var tableIntegrity = AnalyzeOcrTableIntegrity(block.Table);
            missingTableCellCount += tableIntegrity.MissingCellCount;
            overlappingTableCellCount += tableIntegrity.OverlappingCellCount;

            foreach (var cell in block.Table.Cells)
            {
                if (cell.ShouldTranslate && !string.IsNullOrWhiteSpace(cell.Text))
                {
                    translatableCellCount++;
                }

                if (IsInvalidOcrTableCell(block.Table, cell))
                {
                    invalidTableCellCount++;
                }
            }
        }
        else if (string.Equals(block.Type, OcrBlockTypes.Formula, StringComparison.OrdinalIgnoreCase))
        {
            formulaBlockCount++;
            passThroughBlockCount++;
        }
        else if (string.Equals(block.Type, OcrBlockTypes.Code, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(block.Type, OcrBlockTypes.Figure, StringComparison.OrdinalIgnoreCase) ||
                 !block.ShouldTranslate)
        {
            passThroughBlockCount++;
        }
        else
        {
            textBlockCount++;
        }

        foreach (var child in block.Children)
        {
            CountBlock(child);
        }
    }
}

static (int MissingCellCount, int OverlappingCellCount) AnalyzeOcrTableIntegrity(OcrTableBlock table)
{
    if (table.RowCount <= 0 || table.ColumnCount <= 0)
    {
        return (0, 0);
    }

    var occupied = new int[table.RowCount, table.ColumnCount];
    foreach (var cell in table.Cells)
    {
        if (IsInvalidOcrTableCell(table, cell))
        {
            continue;
        }

        for (var rowIndex = cell.RowIndex; rowIndex < cell.RowIndex + cell.RowSpan; rowIndex++)
        {
            for (var columnIndex = cell.ColumnIndex; columnIndex < cell.ColumnIndex + cell.ColumnSpan; columnIndex++)
            {
                occupied[rowIndex, columnIndex]++;
            }
        }
    }

    var missing = 0;
    var overlapping = 0;
    for (var rowIndex = 0; rowIndex < table.RowCount; rowIndex++)
    {
        for (var columnIndex = 0; columnIndex < table.ColumnCount; columnIndex++)
        {
            if (occupied[rowIndex, columnIndex] == 0)
            {
                missing++;
            }
            else if (occupied[rowIndex, columnIndex] > 1)
            {
                overlapping++;
            }
        }
    }

    return (missing, overlapping);
}

static bool IsInvalidOcrTableCell(OcrTableBlock table, OcrTableCell cell)
    => table.RowCount <= 0 ||
       table.ColumnCount <= 0 ||
       cell.RowIndex < 0 ||
       cell.ColumnIndex < 0 ||
       cell.RowSpan <= 0 ||
       cell.ColumnSpan <= 0 ||
       cell.RowIndex >= table.RowCount ||
       cell.ColumnIndex >= table.ColumnCount ||
       cell.RowIndex + cell.RowSpan > table.RowCount ||
       cell.ColumnIndex + cell.ColumnSpan > table.ColumnCount;

static string NormalizeSmokeText(string value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return string.Empty;
    }

    return string.Join(' ', value.Normalize(NormalizationForm.FormKC).Split(
        (char[]?)null,
        StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
}

static int ComputeEditDistance(string left, string right)
{
    if (left.Length == 0)
    {
        return right.Length;
    }

    if (right.Length == 0)
    {
        return left.Length;
    }

    var previous = new int[right.Length + 1];
    var current = new int[right.Length + 1];
    for (var column = 0; column <= right.Length; column++)
    {
        previous[column] = column;
    }

    for (var row = 1; row <= left.Length; row++)
    {
        current[0] = row;
        for (var column = 1; column <= right.Length; column++)
        {
            var cost = left[row - 1] == right[column - 1] ? 0 : 1;
            current[column] = Math.Min(
                Math.Min(current[column - 1] + 1, previous[column] + 1),
                previous[column - 1] + cost);
        }

        (previous, current) = (current, previous);
    }

    return previous[right.Length];
}

static object AsrError(string message)
    => new { errorCode = "invalid_asr_request", errorMessage = message };

static object MemoryError(string message)
    => new { errorCode = "invalid_memory_request", errorMessage = message };

static async Task<object> BuildMemoryRuntimeScopeAsync(
    GameScopedStores gameStores,
    IDatabaseRouter databaseRouter,
    string profileId,
    CancellationToken cancellationToken)
{
    var memoryStore = await gameStores.MemoryFor(profileId, cancellationToken);
    var jobStore = await gameStores.MaintenanceJobsFor(profileId, cancellationToken);
    var pendingReviewCount =
        await memoryStore.CountAsync(profileId, activeOnly: false, trustLevel: RagSecurityPolicy.LocalGenerated, cancellationToken: cancellationToken) +
        await memoryStore.CountAsync(profileId, activeOnly: false, trustLevel: RagSecurityPolicy.UntrustedImport, cancellationToken: cancellationToken) +
        await memoryStore.CountAsync(profileId, activeOnly: false, trustLevel: RagSecurityPolicy.Quarantined, cancellationToken: cancellationToken);

    return new
    {
        profile = profileId,
        databaseScope = databaseRouter.ResolvePath(DbDomain.Realtime, profileId),
        memoryItemCount = await memoryStore.CountAsync(profileId, activeOnly: false, cancellationToken: cancellationToken),
        pendingReviewCount,
        failedJobsCount = await jobStore.CountAsync(profileId, "failed", cancellationToken: cancellationToken)
    };
}

static async Task<(IMemoryStore Store, MemoryItem Item)?> ResolveMemoryItemAsync(
    GameScopedStores gameStores,
    IDatabaseRouter databaseRouter,
    string rawId,
    string? profile,
    CancellationToken cancellationToken)
{
    var id = (rawId ?? string.Empty).Trim();
    if (id.Length == 0)
    {
        return null;
    }

    if (!string.IsNullOrWhiteSpace(profile))
    {
        var store = await gameStores.MemoryFor(profile, cancellationToken);
        var item = await store.GetAsync(id, cancellationToken);
        return item is null ? null : (store, item);
    }

    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var candidateProfile in EnumerateKnownMemoryProfiles(databaseRouter))
    {
        if (!seen.Add(candidateProfile))
        {
            continue;
        }

        var store = await gameStores.MemoryFor(candidateProfile, cancellationToken);
        var item = await store.GetAsync(id, cancellationToken);
        if (item is not null)
        {
            return (store, item);
        }
    }

    return null;
}

static IEnumerable<string> EnumerateKnownMemoryProfiles(IDatabaseRouter databaseRouter)
{
    yield return DatabaseRouter.DefaultGameId;

    var defaultDatabase = databaseRouter.ResolvePath(DbDomain.Realtime, DatabaseRouter.DefaultGameId);
    var defaultDirectory = Path.GetDirectoryName(defaultDatabase);
    if (string.IsNullOrWhiteSpace(defaultDirectory))
    {
        yield break;
    }

    var gamesDirectory = Directory.GetParent(defaultDirectory)?.FullName;
    if (string.IsNullOrWhiteSpace(gamesDirectory) || !Directory.Exists(gamesDirectory))
    {
        yield break;
    }

    string[] profileDirectories;
    try
    {
        profileDirectories = Directory.GetDirectories(gamesDirectory);
    }
    catch (IOException)
    {
        yield break;
    }
    catch (UnauthorizedAccessException)
    {
        yield break;
    }

    var databaseFileName = Path.GetFileName(defaultDatabase);
    foreach (var profileDirectory in profileDirectories)
    {
        var profileId = Path.GetFileName(profileDirectory);
        if (string.IsNullOrWhiteSpace(profileId))
        {
            continue;
        }

        if (!File.Exists(Path.Combine(profileDirectory, databaseFileName)))
        {
            continue;
        }

        yield return profileId;
    }
}

static IResult MemoryForbidden(string message)
    => Results.Json(MemoryError(message), statusCode: StatusCodes.Status403Forbidden);

static VerbeamOptions RequestVerbeamOptions(HttpContext context, VerbeamOptions fallback)
    => context.RequestServices
        .GetService<IConfiguration>()?
        .GetSection("Verbeam")
        .Get<VerbeamOptions>()
        ?? fallback;

static async Task<IResult> CreateMemoryOidcSessionResultAsync(
    MemoryOidcTokenResult tokens,
    VerbeamOptions options,
    IMemoryPrincipalSessionStore principalSessions,
    IMemoryOidcRefreshTokenStore? oidcRefreshTokens,
    MemoryBearerJwtKeyStore keyStore,
    DateTimeOffset? expiresAt,
    string? refreshTokenHandle,
    string? expectedPrincipalId,
    CancellationToken cancellationToken)
{
    var token = FirstNonBlank(tokens.IdToken, tokens.AccessToken);
    if (string.IsNullOrWhiteSpace(token))
    {
        return MemoryForbidden("memory OIDC token is invalid.");
    }

    var validation = await TryValidateBearerJwtAsync(
        token,
        options.Memory.BearerJwt,
        keyStore,
        cancellationToken);
    if (!validation.Valid)
    {
        return MemoryForbidden("memory OIDC token is invalid.");
    }

    if (!string.IsNullOrWhiteSpace(expectedPrincipalId) &&
        !string.Equals(expectedPrincipalId.Trim(), validation.Principal, StringComparison.Ordinal))
    {
        return MemoryForbidden("memory OIDC refresh principal mismatch.");
    }

    var responseRefreshToken = tokens.RefreshToken;
    var responseRefreshTokenHandle = string.Empty;
    if (UseEncryptedOidcRefreshTokenStorage(options.Memory.Oidc))
    {
        if (oidcRefreshTokens is null)
        {
            return Results.BadRequest(MemoryError("memory OIDC refresh-token vault is not configured."));
        }

        responseRefreshToken = string.Empty;
        if (!string.IsNullOrWhiteSpace(tokens.RefreshToken))
        {
            responseRefreshTokenHandle = await oidcRefreshTokens.StoreAsync(
                validation.Principal,
                tokens.RefreshToken,
                null,
                refreshTokenHandle,
                cancellationToken);
        }
        else if (!string.IsNullOrWhiteSpace(refreshTokenHandle))
        {
            responseRefreshTokenHandle = refreshTokenHandle.Trim();
        }
    }

    var sessionExpiresAt = expiresAt ??
        DateTimeOffset.UtcNow.AddMinutes(Math.Clamp(options.Memory.Oidc.SessionLifetimeMinutes, 5, 1440));
    var session = await principalSessions.CreateAsync(
        new MemoryPrincipalSessionCreateRequest
        {
            Principal = validation.Principal,
            ExpiresAt = sessionExpiresAt
        },
        cancellationToken);

    return Results.Ok(new MemoryOidcSessionResult(
        session.Session,
        session.SessionToken,
        validation.Principal,
        validation.Groups,
        responseRefreshToken)
    {
        RefreshTokenHandle = responseRefreshTokenHandle
    });
}

static string OidcRefreshTokenStorageStatus(MemoryOidcOptions options)
{
    if (!UseEncryptedOidcRefreshTokenStorage(options))
    {
        return "client_only";
    }

    return string.IsNullOrWhiteSpace(options.RefreshTokenProtectionKey)
        ? "encrypted_db_unconfigured"
        : "encrypted_db";
}

static bool UseEncryptedOidcRefreshTokenStorage(MemoryOidcOptions options)
    => string.Equals(options.RefreshTokenStorage?.Trim(), "encrypted_db", StringComparison.OrdinalIgnoreCase);

static IResult MemoryAdminForbidden()
    => MemoryForbidden("memory admin token is required.");

static bool AllowMemoryAdminRequest(HttpContext context, VerbeamOptions options)
{
    var configured = context.RequestServices
        .GetService<IConfiguration>()?
        .GetSection("Verbeam")
        .Get<VerbeamOptions>();
    var expected = FirstNonBlank(configured?.Memory.AdminToken, options.Memory.AdminToken);
    if (string.IsNullOrWhiteSpace(expected))
    {
        return true;
    }

    var provided = FirstNonBlank(
        context.Request.Headers["X-Verbeam-Admin-Token"].FirstOrDefault(),
        ReadBearerToken(context.Request.Headers["Authorization"].FirstOrDefault()));
    return SecureEquals(provided, expected.Trim());
}

static string? ReadBearerToken(string? authorization)
{
    const string prefix = "Bearer ";
    return !string.IsNullOrWhiteSpace(authorization) &&
           authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
        ? authorization[prefix.Length..].Trim()
        : null;
}

static bool SecureEquals(string? left, string right)
{
    if (string.IsNullOrWhiteSpace(left))
    {
        return false;
    }

    var leftBytes = Encoding.UTF8.GetBytes(left.Trim());
    var rightBytes = Encoding.UTF8.GetBytes(right);
    return leftBytes.Length == rightBytes.Length &&
           CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
}

static IReadOnlyList<MemoryConflictGroup> BuildMemoryConflictGroups(IReadOnlyList<MemoryItem> items)
    => items
        .Where(item => item.IsActive)
        .GroupBy(
            item => new
            {
                item.ProfileId,
                item.MemoryKind,
                item.SourceLanguage,
                item.TargetLanguage,
                SourceTextNormalized = SqliteMemoryStore.NormalizeKey(item.SourceText)
            })
        .Select(group =>
        {
            var groupItems = group
                .OrderByDescending(item => RagSecurityPolicy.CanUseForExactMemory(item.TrustLevel))
                .ThenByDescending(item => item.Confidence)
                .ThenByDescending(item => item.UpdatedAt)
                .ToArray();
            var targets = groupItems
                .Select(item => SqliteMemoryStore.NormalizeKey(item.TargetText))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            return new MemoryConflictGroup(
                group.Key.ProfileId,
                group.Key.MemoryKind,
                group.Key.SourceLanguage,
                group.Key.TargetLanguage,
                group.Key.SourceTextNormalized,
                targets,
                groupItems);
        })
        .Where(group => group.TargetTexts.Count > 1)
        .OrderBy(group => group.MemoryKind, StringComparer.Ordinal)
        .ThenBy(group => group.SourceTextNormalized, StringComparer.Ordinal)
        .ToArray();

static async Task<IReadOnlyList<MemoryItem>> ListExactMemoryGroupAsync(
    IMemoryStore store,
    MemoryItem item,
    bool activeOnly,
    CancellationToken cancellationToken)
{
    var normalized = SqliteMemoryStore.NormalizeKey(item.SourceText);
    var candidates = await store.ListAsync(
        item.ProfileId,
        item.MemoryKind,
        limit: 500,
        activeOnly,
        sourceLanguage: item.SourceLanguage,
        targetLanguage: item.TargetLanguage,
        query: item.SourceText,
        cancellationToken: cancellationToken);
    return candidates
        .Where(candidate => string.Equals(
            SqliteMemoryStore.NormalizeKey(candidate.SourceText),
            normalized,
            StringComparison.Ordinal))
        .ToArray();
}

static MemoryUpsertRequest BuildMemoryImportUpsert(MemoryImportRequest request, MemoryImportItem item)
    => new()
    {
        Profile = FirstNonBlank(request.Profile, item.Profile, item.ProfileId),
        MemoryKind = item.MemoryKind,
        Source = FirstNonBlank(item.Source, item.SourceLanguage),
        Target = FirstNonBlank(item.Target, item.TargetLanguage),
        SourceText = item.SourceText,
        TargetText = item.TargetText,
        Note = item.Note,
        Priority = item.Priority,
        Confidence = item.Confidence,
        Origin = RagSecurityPolicy.UntrustedImport,
        TrustLevel = RagSecurityPolicy.UntrustedImport,
        SourceUri = FirstNonBlank(request.SourceUri, item.SourceUri, "import://memory-import"),
        CreatedBy = FirstNonBlank(request.ImportedBy, "memory-import"),
        ApprovedBy = string.Empty,
        Classification = item.Classification,
        Visibility = item.Visibility
    };

static async Task<MemoryImportConflict?> FindMemoryImportConflictAsync(
    IMemoryStore store,
    int index,
    MemoryUpsertRequest upsert,
    CancellationToken cancellationToken)
{
    if (string.IsNullOrWhiteSpace(upsert.SourceText) || string.IsNullOrWhiteSpace(upsert.TargetText))
    {
        return null;
    }

    var profileId = Pick(upsert.Profile, "default");
    var memoryKind = Pick(upsert.MemoryKind, "translation").ToLowerInvariant();
    var sourceLanguage = Pick(upsert.Source, "ja");
    var targetLanguage = Pick(upsert.Target, "zh-TW");
    var importedTargetText = upsert.TargetText.Trim();

    var existing = await store.FindByKeyAsync(
        profileId,
        memoryKind,
        sourceLanguage,
        targetLanguage,
        upsert.SourceText,
        cancellationToken);
    if (existing is null ||
        string.Equals(existing.TargetText, importedTargetText, StringComparison.Ordinal))
    {
        return null;
    }

    return new MemoryImportConflict(
        index,
        existing.Id,
        profileId,
        memoryKind,
        sourceLanguage,
        targetLanguage,
        existing.SourceText,
        existing.TargetText,
        importedTargetText,
        existing.TrustLevel);
}

static string? FirstNonBlank(params string?[] values)
    => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

static string FirstNonBlankOrEmpty(params string?[] values)
    => FirstNonBlank(values) ?? string.Empty;

static async Task<bool> AllowSharedMemoryForRequestAsync(
    HttpContext context,
    VerbeamOptions options,
    IMemoryPrincipalPermissionStore principalPermissions,
    IMemoryPrincipalSessionStore principalSessions,
    string profileId,
    CancellationToken cancellationToken)
{
    if (!options.Memory.SharedMemoryEnabled)
    {
        return false;
    }

    var identity = await RequestMemoryIdentityAsync(context, options, principalSessions, cancellationToken);
    return await AllowSharedMemoryForIdentityAsync(
        options,
        principalPermissions,
        identity,
        profileId,
        cancellationToken);
}

static async Task<bool> AllowSharedMemoryForIdentityAsync(
    VerbeamOptions options,
    IMemoryPrincipalPermissionStore principalPermissions,
    RequestMemoryIdentity identity,
    string profileId,
    CancellationToken cancellationToken)
{
    if (!options.Memory.SharedMemoryEnabled)
    {
        return false;
    }

    var permission = await GetMemoryPrincipalPermissionAsync(
        options,
        principalPermissions,
        identity,
        profileId,
        cancellationToken);
    if (permission is not null)
    {
        return permission.CanReadSharedMemory;
    }

    var allowedPrincipals = options.Memory.SharedMemoryAuthorizedPrincipals
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => value.Trim())
        .ToArray();

    return allowedPrincipals.Length == 0
        ? string.Equals(identity.Principal, "local", StringComparison.OrdinalIgnoreCase)
        : allowedPrincipals.Any(value => string.Equals(value, identity.Principal, StringComparison.OrdinalIgnoreCase));
}

static Task<bool> AllowMemoryWriteForRequestAsync(
    HttpContext context,
    VerbeamOptions options,
    IMemoryPrincipalPermissionStore principalPermissions,
    IMemoryPrincipalSessionStore principalSessions,
    string profileId,
    CancellationToken cancellationToken)
    => AllowMemoryPermissionForRequestAsync(
        context,
        options,
        principalPermissions,
        principalSessions,
        profileId,
        permission => permission.CanWriteMemory,
        cancellationToken);

static Task<bool> AllowMemoryApproveForRequestAsync(
    HttpContext context,
    VerbeamOptions options,
    IMemoryPrincipalPermissionStore principalPermissions,
    IMemoryPrincipalSessionStore principalSessions,
    string profileId,
    CancellationToken cancellationToken)
    => AllowMemoryPermissionForRequestAsync(
        context,
        options,
        principalPermissions,
        principalSessions,
        profileId,
        permission => permission.CanApproveMemory,
        cancellationToken);

static async Task<bool> AllowMemoryPermissionForRequestAsync(
    HttpContext context,
    VerbeamOptions options,
    IMemoryPrincipalPermissionStore principalPermissions,
    IMemoryPrincipalSessionStore principalSessions,
    string profileId,
    Func<MemoryPrincipalPermission, bool> hasPermission,
    CancellationToken cancellationToken)
{
    var identity = await RequestMemoryIdentityAsync(context, options, principalSessions, cancellationToken);
    var permission = await GetMemoryPrincipalPermissionAsync(
        options,
        principalPermissions,
        identity,
        profileId,
        cancellationToken);
    return permission is null
        ? string.Equals(identity.Principal, "local", StringComparison.OrdinalIgnoreCase)
        : hasPermission(permission);
}

static string RequestPrincipal(HttpContext context)
    => FirstNonBlank(context.Request.Headers["X-Verbeam-Principal"].FirstOrDefault(), "local") ?? "local";

static async Task<RequestMemoryIdentity> RequestMemoryIdentityAsync(
    HttpContext context,
    VerbeamOptions options,
    IMemoryPrincipalSessionStore principalSessions,
    CancellationToken cancellationToken)
{
    var sessionToken = FirstNonBlank(context.Request.Headers["X-Verbeam-Session"].FirstOrDefault());
    if (!string.IsNullOrWhiteSpace(sessionToken))
    {
        return new RequestMemoryIdentity(
            await principalSessions.ResolvePrincipalAsync(sessionToken, cancellationToken) ?? string.Empty,
            [],
            IsExternal: false);
    }

    var bearerIdentity = await RequestBearerJwtIdentityAsync(context, options, cancellationToken);
    if (bearerIdentity is not null)
    {
        return bearerIdentity;
    }

    var externalIdentity = RequestExternalIdentity(context, options);
    if (externalIdentity is not null)
    {
        return externalIdentity;
    }

    return new RequestMemoryIdentity(RequestPrincipal(context), [], IsExternal: false);
}

static RequestMemoryIdentity? RequestExternalIdentity(HttpContext context, VerbeamOptions options)
{
    var external = options.Memory.ExternalIdentity;
    if (!external.Enabled)
    {
        return null;
    }

    var principal = FirstNonBlank(context.Request.Headers[external.PrincipalHeader].FirstOrDefault());
    var groupsRaw = FirstNonBlank(context.Request.Headers[external.GroupsHeader].FirstOrDefault());
    var providedSecret = FirstNonBlank(context.Request.Headers[external.SharedSecretHeader].FirstOrDefault());
    if (string.IsNullOrWhiteSpace(principal) && string.IsNullOrWhiteSpace(groupsRaw) && string.IsNullOrWhiteSpace(providedSecret))
    {
        return null;
    }

    if (string.IsNullOrWhiteSpace(external.SharedSecret) ||
        !SecureEquals(providedSecret, external.SharedSecret.Trim()) ||
        string.IsNullOrWhiteSpace(principal))
    {
        return new RequestMemoryIdentity(string.Empty, [], IsExternal: true);
    }

    var groups = (groupsRaw ?? string.Empty)
        .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
    return new RequestMemoryIdentity(principal, groups, IsExternal: true);
}

static async Task<RequestMemoryIdentity?> RequestBearerJwtIdentityAsync(
    HttpContext context,
    VerbeamOptions options,
    CancellationToken cancellationToken)
{
    var jwt = options.Memory.BearerJwt;
    if (!jwt.Enabled)
    {
        return null;
    }

    var token = ReadBearerToken(context.Request.Headers["Authorization"].FirstOrDefault());
    if (string.IsNullOrWhiteSpace(token))
    {
        return null;
    }

    var keyStore = context.RequestServices.GetService<MemoryBearerJwtKeyStore>();
    var validation = await TryValidateBearerJwtAsync(token, jwt, keyStore, cancellationToken);
    return validation.Valid
        ? new RequestMemoryIdentity(validation.Principal, validation.Groups, IsExternal: true)
        : new RequestMemoryIdentity(string.Empty, [], IsExternal: true);
}

static async Task<BearerJwtValidationResult> TryValidateBearerJwtAsync(
    string token,
    MemoryBearerJwtOptions options,
    MemoryBearerJwtKeyStore? keyStore,
    CancellationToken cancellationToken)
{
    var parts = token.Split('.');
    if (parts.Length != 3)
    {
        return BearerJwtValidationResult.Invalid;
    }

    try
    {
        using var headerDocument = JsonDocument.Parse(Base64UrlDecode(parts[0]));
        var header = headerDocument.RootElement;
        if (!header.TryGetProperty("alg", out var algProperty))
        {
            return BearerJwtValidationResult.Invalid;
        }

        var algorithm = algProperty.GetString() ?? string.Empty;
        var keyId = GetJwtStringClaim(header, "kid");
        if (!await ValidateJwtSignatureAsync(
            algorithm,
            keyId,
            parts[0],
            parts[1],
            parts[2],
            options,
            keyStore,
            cancellationToken))
        {
            return BearerJwtValidationResult.Invalid;
        }

        using var payloadDocument = JsonDocument.Parse(Base64UrlDecode(parts[1]));
        var payload = payloadDocument.RootElement;
        if (!ValidateJwtIssuer(payload, options) ||
            !ValidateJwtAudience(payload, options) ||
            !ValidateJwtLifetime(payload, options))
        {
            return BearerJwtValidationResult.Invalid;
        }

        var principal = GetJwtStringClaim(payload, options.PrincipalClaim);
        if (string.IsNullOrWhiteSpace(principal))
        {
            return BearerJwtValidationResult.Invalid;
        }

        var groups = GetJwtStringListClaim(payload, options.GroupsClaim);
        return new BearerJwtValidationResult(true, principal, groups);
    }
    catch (JsonException)
    {
        return BearerJwtValidationResult.Invalid;
    }
    catch (FormatException)
    {
        return BearerJwtValidationResult.Invalid;
    }
    catch (ArgumentException)
    {
        return BearerJwtValidationResult.Invalid;
    }
    catch (IOException)
    {
        return BearerJwtValidationResult.Invalid;
    }
    catch (UnauthorizedAccessException)
    {
        return BearerJwtValidationResult.Invalid;
    }
    catch (CryptographicException)
    {
        return BearerJwtValidationResult.Invalid;
    }
}

static async Task<bool> ValidateJwtSignatureAsync(
    string algorithm,
    string keyId,
    string encodedHeader,
    string encodedPayload,
    string encodedSignature,
    MemoryBearerJwtOptions options,
    MemoryBearerJwtKeyStore? keyStore,
    CancellationToken cancellationToken)
{
    return algorithm switch
    {
        "HS256" => ValidateJwtHs256Signature(encodedHeader, encodedPayload, encodedSignature, options),
        "RS256" => await ValidateJwtRs256SignatureAsync(
            keyId,
            encodedHeader,
            encodedPayload,
            encodedSignature,
            options,
            keyStore,
            cancellationToken),
        _ => false
    };
}

static bool ValidateJwtHs256Signature(
    string encodedHeader,
    string encodedPayload,
    string encodedSignature,
    MemoryBearerJwtOptions options)
{
    if (string.IsNullOrWhiteSpace(options.HmacSecret))
    {
        return false;
    }

    var signingInput = Encoding.UTF8.GetBytes($"{encodedHeader}.{encodedPayload}");
    var expectedSignature = HMACSHA256.HashData(
        Encoding.UTF8.GetBytes(options.HmacSecret),
        signingInput);
    var providedSignature = Base64UrlDecode(encodedSignature);
    return providedSignature.Length == expectedSignature.Length &&
           CryptographicOperations.FixedTimeEquals(providedSignature, expectedSignature);
}

static async Task<bool> ValidateJwtRs256SignatureAsync(
    string keyId,
    string encodedHeader,
    string encodedPayload,
    string encodedSignature,
    MemoryBearerJwtOptions options,
    MemoryBearerJwtKeyStore? keyStore,
    CancellationToken cancellationToken)
{
    var key = await FindRsaJwksKeyAsync(options, keyStore, keyId, cancellationToken);
    if (key is null)
    {
        return false;
    }

    using var rsa = RSA.Create();
    rsa.ImportParameters(key.Value);
    return rsa.VerifyData(
        Encoding.UTF8.GetBytes($"{encodedHeader}.{encodedPayload}"),
        Base64UrlDecode(encodedSignature),
        HashAlgorithmName.SHA256,
        RSASignaturePadding.Pkcs1);
}

static async Task<RSAParameters?> FindRsaJwksKeyAsync(
    MemoryBearerJwtOptions options,
    MemoryBearerJwtKeyStore? keyStore,
    string keyId,
    CancellationToken cancellationToken)
{
    var jwksJson = keyStore is null
        ? FirstNonBlank(options.JwksJson, ReadJwksFile(options.JwksPath))
        : await keyStore.GetJwksJsonAsync(cancellationToken);
    if (string.IsNullOrWhiteSpace(jwksJson))
    {
        return null;
    }

    using var document = JsonDocument.Parse(jwksJson);
    if (!document.RootElement.TryGetProperty("keys", out var keys) ||
        keys.ValueKind != JsonValueKind.Array)
    {
        return null;
    }

    RSAParameters? fallback = null;
    foreach (var key in keys.EnumerateArray())
    {
        if (!string.Equals(GetJwtStringClaim(key, "kty"), "RSA", StringComparison.Ordinal) ||
            !string.Equals(GetJwtStringClaim(key, "use"), "sig", StringComparison.OrdinalIgnoreCase) &&
            key.TryGetProperty("use", out _))
        {
            continue;
        }

        var currentKeyId = GetJwtStringClaim(key, "kid");
        if (!key.TryGetProperty("n", out var modulus) ||
            !key.TryGetProperty("e", out var exponent) ||
            modulus.ValueKind != JsonValueKind.String ||
            exponent.ValueKind != JsonValueKind.String)
        {
            continue;
        }

        var parameters = new RSAParameters
        {
            Modulus = Base64UrlDecode(modulus.GetString() ?? string.Empty),
            Exponent = Base64UrlDecode(exponent.GetString() ?? string.Empty)
        };
        if (!string.IsNullOrWhiteSpace(keyId) &&
            string.Equals(currentKeyId, keyId, StringComparison.Ordinal))
        {
            return parameters;
        }

        if (fallback is null)
        {
            fallback = parameters;
        }
    }

    return string.IsNullOrWhiteSpace(keyId) ? fallback : null;
}

static string? ReadJwksFile(string? path)
{
    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
    {
        return null;
    }

    return File.ReadAllText(path);
}

static bool ValidateJwtIssuer(JsonElement payload, MemoryBearerJwtOptions options)
{
    if (string.IsNullOrWhiteSpace(options.Issuer))
    {
        return true;
    }

    return string.Equals(
        GetJwtStringClaim(payload, "iss"),
        options.Issuer.Trim(),
        StringComparison.Ordinal);
}

static bool ValidateJwtAudience(JsonElement payload, MemoryBearerJwtOptions options)
{
    var expectedAudiences = options.Audiences
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => value.Trim())
        .ToArray();
    if (expectedAudiences.Length == 0)
    {
        return true;
    }

    var audiences = GetJwtStringListClaim(payload, "aud");
    return audiences.Any(audience => expectedAudiences.Any(expected =>
        string.Equals(audience, expected, StringComparison.Ordinal)));
}

static bool ValidateJwtLifetime(JsonElement payload, MemoryBearerJwtOptions options)
{
    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var skew = Math.Clamp(options.ClockSkewSeconds, 0, 3600);
    if (!TryGetJwtLongClaim(payload, "exp", out var expiresAt) || now - skew > expiresAt)
    {
        return false;
    }

    return !TryGetJwtLongClaim(payload, "nbf", out var notBefore) || now + skew >= notBefore;
}

static string GetJwtStringClaim(JsonElement payload, string claimName)
{
    if (string.IsNullOrWhiteSpace(claimName) ||
        !payload.TryGetProperty(claimName, out var claim) ||
        claim.ValueKind != JsonValueKind.String)
    {
        return string.Empty;
    }

    return claim.GetString()?.Trim() ?? string.Empty;
}

static IReadOnlyList<string> GetJwtStringListClaim(JsonElement payload, string claimName)
{
    if (string.IsNullOrWhiteSpace(claimName) ||
        !payload.TryGetProperty(claimName, out var claim))
    {
        return [];
    }

    if (claim.ValueKind == JsonValueKind.String)
    {
        return (claim.GetString() ?? string.Empty)
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    if (claim.ValueKind != JsonValueKind.Array)
    {
        return [];
    }

    return claim
        .EnumerateArray()
        .Where(item => item.ValueKind == JsonValueKind.String)
        .Select(item => item.GetString()?.Trim() ?? string.Empty)
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
}

static bool TryGetJwtLongClaim(JsonElement payload, string claimName, out long value)
{
    value = 0;
    return payload.TryGetProperty(claimName, out var claim) &&
           claim.ValueKind == JsonValueKind.Number &&
           claim.TryGetInt64(out value);
}

static byte[] Base64UrlDecode(string value)
{
    var padded = value.Replace('-', '+').Replace('_', '/');
    padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
    return Convert.FromBase64String(padded);
}

static async Task<MemoryPrincipalPermission?> GetMemoryPrincipalPermissionAsync(
    VerbeamOptions options,
    IMemoryPrincipalPermissionStore principalPermissions,
    RequestMemoryIdentity identity,
    string profileId,
    CancellationToken cancellationToken)
{
    if (string.IsNullOrWhiteSpace(identity.Principal))
    {
        return null;
    }

    var permission = await principalPermissions.GetAsync(identity.Principal, profileId, cancellationToken);
    return permission ?? BuildExternalIdentityPermission(options, identity, profileId);
}

static MemoryPrincipalPermission? BuildExternalIdentityPermission(
    VerbeamOptions options,
    RequestMemoryIdentity identity,
    string profileId)
{
    if (!identity.IsExternal || identity.Groups.Count == 0)
    {
        return null;
    }

    var mappings = options.Memory.ExternalIdentity.RoleMappings
        .Where(mapping => !string.IsNullOrWhiteSpace(mapping.Group))
        .Where(mapping => identity.Groups.Any(group => string.Equals(group, mapping.Group.Trim(), StringComparison.OrdinalIgnoreCase)))
        .Where(mapping => string.IsNullOrWhiteSpace(mapping.Profile) ||
                          string.Equals(mapping.Profile.Trim(), "*", StringComparison.Ordinal) ||
                          string.Equals(mapping.Profile.Trim(), profileId, StringComparison.OrdinalIgnoreCase))
        .ToArray();
    if (mappings.Length == 0)
    {
        return null;
    }

    var canReadSharedMemory = false;
    var canWriteMemory = false;
    var canApproveMemory = false;
    foreach (var mapping in mappings)
    {
        var role = MemoryPrincipalRoles.Normalize(mapping.Role);
        if (role == MemoryPrincipalRoles.Blocked)
        {
            return new MemoryPrincipalPermission(
                identity.Principal,
                profileId,
                MemoryPrincipalRoles.Blocked,
                CanReadSharedMemory: false,
                CanWriteMemory: false,
                CanApproveMemory: false,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow);
        }

        if (MemoryPrincipalRoles.TryGetPreset(role, out var canRead, out var canWrite, out var canApprove))
        {
            canReadSharedMemory |= canRead;
            canWriteMemory |= canWrite;
            canApproveMemory |= canApprove;
        }
    }

    var inferredRole = MemoryPrincipalRoles.Infer(canReadSharedMemory, canWriteMemory, canApproveMemory);
    return new MemoryPrincipalPermission(
        identity.Principal,
        profileId,
        inferredRole,
        canReadSharedMemory,
        canWriteMemory,
        canApproveMemory,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow);
}

static bool RequestsTrustedMemory(MemoryUpsertRequest request)
    => RagSecurityPolicy.CanUseForExactMemory(
        RagSecurityPolicy.NormalizeTrustLevel(request.TrustLevel, request.Origin));

static bool RequestsTrustMutation(MemoryUpdateRequest request)
    => !string.IsNullOrWhiteSpace(request.TrustLevel) || request.ApprovedBy is not null;

static async Task<OcrStructuredTranslation> TranslateOcrDocumentAsync(
    OcrResponse ocr,
    OcrTranslateRequest request,
    TranslationService translationService,
    VerbeamOptions options,
    bool allowSharedMemory,
    string principalId,
    CancellationToken cancellationToken)
{
    var document = ocr.Document ?? new OcrDocumentResult
    {
        Pages =
        [
            new OcrPageResult
            {
                PageIndex = 0,
                Blocks =
                [
                    new OcrBlock
                    {
                        Id = "p0-b0",
                        Type = OcrBlockTypes.Text,
                        Text = ocr.Text,
                        Confidence = 1,
                        ReadingOrder = 0,
                        Engine = ocr.Engine,
                        ShouldTranslate = true,
                        DetectedLanguage = ocr.DetectedLanguage
                    }
                ]
            }
        ]
    };

    var segments = new List<OcrSegmentTranslation>();
    var realtimePolicy = request.Realtime == true
        ? RealtimeOcrTextPolicy.Options.Create(request.ExcludeRegions, request.DropPatterns)
        : null;
    if (realtimePolicy is { IsEmpty: false })
    {
        var (filtered, dropped) = RealtimeOcrTextPolicy.DropBlocks(document, realtimePolicy);
        document = filtered;
        segments.AddRange(dropped);
    }

    if (request.MergeTextBlocks == true)
    {
        document = OcrTranslationBlockMerger.Merge(document);
    }

    if (realtimePolicy is { IsEmpty: false })
    {
        document = RealtimeOcrTextPolicy.StripText(document, realtimePolicy);
    }

    var pages = new List<OcrPageResult>();

    foreach (var page in document.Pages)
    {
        var translatedBlocks = new List<OcrBlock>();
        foreach (var block in page.Blocks.OrderBy(block => block.ReadingOrder))
        {
            translatedBlocks.Add(await TranslateOcrBlockAsync(
                block,
                request,
                ocr.DetectedLanguage,
                translationService,
                options,
                segments,
                allowSharedMemory,
                principalId,
                cancellationToken));
        }

        pages.Add(page with { Blocks = translatedBlocks });
    }

    var translatedDocument = document with { Pages = pages };
    // Realtime subtitle calls only consume the plain text; skip the markdown/html/overlay
    // renderings and diagnostics, which each traverse the whole document tree.
    var realtime = request.Realtime == true;
    var text = RenderOcrDocumentText(translatedDocument);
    var markdown = realtime ? string.Empty : RenderOcrDocumentMarkdown(translatedDocument);
    var html = realtime ? string.Empty : RenderOcrDocumentHtml(translatedDocument);
    var overlayHtml = realtime ? string.Empty : RenderOcrDocumentOverlayHtml(translatedDocument);
    var layoutHtml = realtime ? string.Empty : RenderOcrDocumentLayoutHtml(translatedDocument);
    var layoutDiagnostics = realtime ? OcrLayoutDiagnostics.Empty : BuildOcrLayoutDiagnostics(translatedDocument);
    var translatedSegments = segments.Where(segment => segment.Translated).ToArray();
    var failedSegments = segments.Where(segment => segment.ErrorCode != "0").ToArray();
    var engine = failedSegments.Length > 0
        ? string.Empty
        : string.Join("+", translatedSegments
            .Select(segment => segment.Engine)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .DefaultIfEmpty("none"));
    var latencyMs = translatedSegments.Sum(segment => segment.LatencyMs);
    var cacheHit = translatedSegments.Length > 0 && translatedSegments.All(segment => segment.CacheHit);
    var tokenUsage = MergeTokenUsage(translatedSegments.Select(segment => segment.TokenUsage), "ocr:structured");

    return new OcrStructuredTranslation(
        text,
        translatedDocument,
        segments,
        engine,
        latencyMs,
        cacheHit,
        tokenUsage)
    {
        Markdown = markdown,
        Html = html,
        OverlayHtml = overlayHtml,
        LayoutHtml = layoutHtml,
        LayoutDiagnostics = layoutDiagnostics
    };
}

static async Task<OcrBlock> TranslateOcrBlockAsync(
    OcrBlock block,
    OcrTranslateRequest request,
    string documentDetectedLanguage,
    TranslationService translationService,
    VerbeamOptions options,
    List<OcrSegmentTranslation> segments,
    bool allowSharedMemory,
    string principalId,
    CancellationToken cancellationToken)
{
    var children = new List<OcrBlock>();
    foreach (var child in block.Children.OrderBy(child => child.ReadingOrder))
    {
        children.Add(await TranslateOcrBlockAsync(
            child,
            request,
            documentDetectedLanguage,
            translationService,
            options,
            segments,
            allowSharedMemory,
            principalId,
            cancellationToken));
    }

    if (block.Table is not null)
    {
        var sourceText = FirstNonBlankOrEmpty(block.SourceText, RenderOcrTableText(block.Table), block.Text);
        var table = await TranslateOcrTableAsync(
            block.Id,
            block.Table,
            request,
            documentDetectedLanguage,
            translationService,
            options,
            segments,
            allowSharedMemory,
            principalId,
            cancellationToken);
        return block with
        {
            Table = table,
            Children = children,
            SourceText = sourceText,
            Text = RenderOcrTableText(table),
            ShouldTranslate = false
        };
    }

    if (!ShouldTranslateOcrBlock(block) || string.IsNullOrWhiteSpace(block.Text))
    {
        segments.Add(PassThroughOcrSegment(block.Id, block.Type, block.Text));
        return block with
        {
            Children = children,
            SourceText = FirstNonBlankOrEmpty(block.SourceText, block.Formula?.SourceText, block.Formula?.Latex, block.Text)
        };
    }

    if (OcrLabelTranslationFallback.SupportsTarget(Pick(request.Target, options.DefaultTarget)) &&
        OcrLabelTranslationFallback.TryTranslate(block.Text) is { } fallbackText)
    {
        var fallbackOutcome = TranslationOutcome.Success(fallbackText, "ocr:fallback", 0, cacheHit: false, tokenUsage: TokenUsage.Zero("ocr:fallback"));
        segments.Add(ToOcrSegmentTranslation(block.Id, block.Type, block.Text, fallbackOutcome, translated: true));
        return block with
        {
            SourceText = FirstNonBlankOrEmpty(block.SourceText, block.Text),
            Text = fallbackText,
            Children = children
        };
    }

    var outcome = await TranslateOcrTextAsync(
        block.Id,
        block.Type,
        block.Text,
        request,
        FirstNonBlank(block.DetectedLanguage, documentDetectedLanguage) ?? string.Empty,
        translationService,
        options,
        allowSharedMemory,
        principalId,
        cancellationToken);
    segments.Add(ToOcrSegmentTranslation(block.Id, block.Type, block.Text, outcome, translated: outcome.IsSuccess));

    return outcome.IsSuccess
        ? block with { SourceText = FirstNonBlankOrEmpty(block.SourceText, block.Text), Text = outcome.Text, Children = children }
        : block with { SourceText = FirstNonBlankOrEmpty(block.SourceText, block.Text), Children = children };
}

static async Task<OcrTableBlock> TranslateOcrTableAsync(
    string blockId,
    OcrTableBlock table,
    OcrTranslateRequest request,
    string documentDetectedLanguage,
    TranslationService translationService,
    VerbeamOptions options,
    List<OcrSegmentTranslation> segments,
    bool allowSharedMemory,
    string principalId,
    CancellationToken cancellationToken)
{
    var cells = new List<OcrTableCell>();
    foreach (var cell in table.Cells.OrderBy(cell => cell.RowIndex).ThenBy(cell => cell.ColumnIndex))
    {
        var segmentId = $"{blockId}:{cell.Id}";
        if (!cell.ShouldTranslate || string.IsNullOrWhiteSpace(cell.Text))
        {
            segments.Add(PassThroughOcrSegment(segmentId, "table_cell", cell.Text));
            cells.Add(cell with { SourceText = FirstNonBlankOrEmpty(cell.SourceText, cell.Text) });
            continue;
        }

        if (OcrLabelTranslationFallback.SupportsTarget(Pick(request.Target, options.DefaultTarget)) &&
            OcrLabelTranslationFallback.TryTranslate(cell.Text) is { } fallbackText)
        {
            var fallbackOutcome = TranslationOutcome.Success(fallbackText, "ocr:fallback", 0, cacheHit: false, tokenUsage: TokenUsage.Zero("ocr:fallback"));
            segments.Add(ToOcrSegmentTranslation(segmentId, "table_cell", cell.Text, fallbackOutcome, translated: true));
            cells.Add(cell with { SourceText = FirstNonBlankOrEmpty(cell.SourceText, cell.Text), Text = fallbackText });
            continue;
        }

        var outcome = await TranslateOcrTextAsync(
            segmentId,
            "table_cell",
            cell.Text,
            request,
            FirstNonBlank(cell.DetectedLanguage, documentDetectedLanguage) ?? string.Empty,
            translationService,
            options,
            allowSharedMemory,
            principalId,
            cancellationToken);
        segments.Add(ToOcrSegmentTranslation(segmentId, "table_cell", cell.Text, outcome, translated: outcome.IsSuccess));
        cells.Add(outcome.IsSuccess
            ? cell with { SourceText = FirstNonBlankOrEmpty(cell.SourceText, cell.Text), Text = outcome.Text }
            : cell with { SourceText = FirstNonBlankOrEmpty(cell.SourceText, cell.Text) });
    }

    return table with { Cells = cells };
}

// When the caller asked for source "auto" (or left it blank), translate from the
// detected language of the OCR block; an explicit source always wins. Falls back to
// the literal request value so TranslationService can run its own text detection.
static string? ResolveOcrTranslationSource(string? requestedSource, string detectedLanguage)
{
    var hasExplicitSource = !string.IsNullOrWhiteSpace(requestedSource) &&
        !requestedSource.Trim().Equals(LanguageRegistry.Auto, StringComparison.OrdinalIgnoreCase);
    if (hasExplicitSource)
    {
        return requestedSource;
    }

    return string.IsNullOrWhiteSpace(detectedLanguage)
        ? requestedSource
        : LanguageRegistry.ToTranslationCode(detectedLanguage);
}

static async Task<TranslationOutcome> TranslateOcrTextAsync(
    string segmentId,
    string segmentType,
    string text,
    OcrTranslateRequest request,
    string detectedLanguage,
    TranslationService translationService,
    VerbeamOptions options,
    bool allowSharedMemory,
    string principalId,
    CancellationToken cancellationToken)
{
    // Blocks already written in the target language (e.g. a zh-TW diagram label
    // when translating to zh-TW) must not round-trip through the LLM: small
    // models rewrite short CJK fragments ("原程式" -> "當程式").
    if (OcrTextAlreadyInTargetLanguage(text, request.Source, Pick(request.Target, options.DefaultTarget)))
    {
        return TranslationOutcome.Success(text, "ocr:same-language", 0, cacheHit: false, tokenUsage: TokenUsage.Zero("ocr:same-language"));
    }

    try
    {
        return await translationService.TranslateAsync(
            new MortTranslateRequest
            {
                Name = $"ocr:{segmentType}:{segmentId}",
                Text = text,
                Source = ResolveOcrTranslationSource(request.Source, detectedLanguage),
                Target = request.Target,
                Mode = request.Mode,
                Surface = Pick(request.Surface, "ocr"),
                Glossary = request.Glossary,
                Provider = request.TranslationProvider,
                Model = request.Model,
                Profile = request.Profile,
                SessionId = request.SessionId,
                Context = null,
                ContextItems = null,
                AllowSharedMemory = allowSharedMemory,
                PrincipalId = principalId,
                Realtime = request.Realtime
            },
            cancellationToken);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        // One block failing (e.g. the model returns an empty translation) must
        // not abort the whole document; record it as a failed segment and keep
        // the source text so the rest of the document still translates.
        return TranslationOutcome.Failure(text, ex.Message);
    }
}

static bool OcrTextAlreadyInTargetLanguage(string text, string? requestedSource, string? target)
{
    var normalizedTarget = LanguageRegistry.Normalize(target);
    if (normalizedTarget == LanguageRegistry.Auto)
    {
        return false;
    }

    var detection = UnicodeScriptDetector.Detect(text);
    if (detection.EffectiveCharCount == 0 ||
        detection.Confidence < 0.4 ||
        !detection.DetectedLanguage.Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    // A Hant/Hans verdict without variant-distinctive characters is just the
    // detector's tie-break default; an ambiguous block could be the other
    // variant, so let the model convert it instead of passing it through.
    if (!detection.HasChineseVariantEvidence &&
        (normalizedTarget.Equals(LanguageRegistry.TraditionalChinese, StringComparison.OrdinalIgnoreCase) ||
         normalizedTarget.Equals(LanguageRegistry.SimplifiedChinese, StringComparison.OrdinalIgnoreCase)))
    {
        return false;
    }

    // An explicitly requested source that differs from the target but is still a
    // plausible reading of the text (e.g. kanji-only Japanese detected as Chinese)
    // means the user intends a translation - do not skip.
    var normalizedSource = LanguageRegistry.Normalize(requestedSource);
    if (normalizedSource != LanguageRegistry.Auto &&
        !normalizedSource.Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase) &&
        detection.Candidates.Any(candidate =>
            candidate.Score > 0 &&
            candidate.Language.Equals(normalizedSource, StringComparison.OrdinalIgnoreCase)))
    {
        return false;
    }

    return true;
}

static OcrSegmentTranslation ToOcrSegmentTranslation(
    string id,
    string type,
    string sourceText,
    TranslationOutcome outcome,
    bool translated)
    => new(
        id,
        type,
        sourceText,
        outcome.IsSuccess ? outcome.Text : sourceText,
        translated,
        outcome.Engine,
        outcome.LatencyMs,
        outcome.CacheHit,
        outcome.ErrorCode,
        outcome.ErrorMessage,
        outcome.TokenUsage);

static TokenUsage? MergeTokenUsage(IEnumerable<TokenUsage?> usages, string source)
{
    var values = usages
        .Where(usage => usage is not null)
        .Select(usage => usage!)
        .ToArray();
    if (values.Length == 0)
    {
        return null;
    }

    var input = values.Sum(usage => usage.InputTokens);
    var output = values.Sum(usage => usage.OutputTokens);
    var total = values.Sum(usage => usage.TotalTokens);
    if (total <= 0)
    {
        total = input + output;
    }

    return new TokenUsage(input, output, total, source, values.Any(usage => usage.IsEstimated));
}

static OcrSegmentTranslation PassThroughOcrSegment(string id, string type, string text)
    => new(
        id,
        type,
        text,
        text,
        Translated: false,
        Engine: "none",
        LatencyMs: 0,
        CacheHit: false,
        ErrorCode: "0",
        ErrorMessage: string.Empty);

static bool ShouldTranslateOcrBlock(OcrBlock block)
    => block.ShouldTranslate &&
       !string.Equals(block.Type, OcrBlockTypes.Formula, StringComparison.OrdinalIgnoreCase) &&
       !string.Equals(block.Type, OcrBlockTypes.Code, StringComparison.OrdinalIgnoreCase) &&
       !string.Equals(block.Type, OcrBlockTypes.Figure, StringComparison.OrdinalIgnoreCase);

static string RenderOcrDocumentText(OcrDocumentResult document)
    => string.Join(
        Environment.NewLine,
        document.Pages
            .OrderBy(page => page.PageIndex)
            .SelectMany(page => page.Blocks.OrderBy(block => block.ReadingOrder))
            .Select(RenderOcrBlockText)
            .Where(value => !string.IsNullOrWhiteSpace(value)));

static string RenderOcrBlockText(OcrBlock block)
{
    if (block.Table is not null)
    {
        return RenderOcrTableText(block.Table);
    }

    if (block.Children.Count > 0)
    {
        var childText = string.Join(
            Environment.NewLine,
            block.Children
                .OrderBy(child => child.ReadingOrder)
                .Select(RenderOcrBlockText)
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        if (!string.IsNullOrWhiteSpace(childText))
        {
            return childText;
        }
    }

    return block.Text;
}

static string RenderOcrTableText(OcrTableBlock table)
{
    if (table.Cells.Count == 0)
    {
        return string.Empty;
    }

    var rowCount = Math.Max(table.RowCount, table.Cells.Max(cell => cell.RowIndex + 1));
    var columnCount = Math.Max(table.ColumnCount, table.Cells.Max(cell => cell.ColumnIndex + 1));
    var rows = new string[rowCount][];
    for (var rowIndex = 0; rowIndex < rowCount; rowIndex++)
    {
        rows[rowIndex] = Enumerable.Repeat(string.Empty, columnCount).ToArray();
    }

    foreach (var cell in table.Cells)
    {
        if (cell.RowIndex >= 0 && cell.RowIndex < rowCount && cell.ColumnIndex >= 0 && cell.ColumnIndex < columnCount)
        {
            rows[cell.RowIndex][cell.ColumnIndex] = cell.Text;
        }
    }

    return string.Join(
        Environment.NewLine,
        rows.Select(row => string.Join(" | ", row)));
}

static string RenderOcrDocumentMarkdown(OcrDocumentResult document)
    => string.Join(
        Environment.NewLine + Environment.NewLine,
        document.Pages
            .OrderBy(page => page.PageIndex)
            .SelectMany(page => page.Blocks.OrderBy(block => block.ReadingOrder))
            .Select(RenderOcrBlockMarkdown)
            .Where(value => !string.IsNullOrWhiteSpace(value)));

static string RenderOcrBlockMarkdown(OcrBlock block)
{
    if (block.Table is not null)
    {
        return RenderOcrTableMarkdown(block.Table);
    }

    if (string.Equals(block.Type, OcrBlockTypes.Formula, StringComparison.OrdinalIgnoreCase))
    {
        var formula = NormalizeOcrFormulaText(block.Formula?.Latex, block.Text);
        return string.IsNullOrWhiteSpace(formula) ? string.Empty : formula;
    }

    if (block.Children.Count > 0)
    {
        var childText = string.Join(
            Environment.NewLine + Environment.NewLine,
            block.Children
                .OrderBy(child => child.ReadingOrder)
                .Select(RenderOcrBlockMarkdown)
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        if (!string.IsNullOrWhiteSpace(childText))
        {
            return childText;
        }
    }

    return block.Text.Trim();
}

static string RenderOcrTableMarkdown(OcrTableBlock table)
{
    var rows = BuildOcrTableMatrix(table);
    if (rows.Length == 0)
    {
        return string.Empty;
    }

    var columnCount = rows.Max(row => row.Length);
    var lines = new List<string>
    {
        RenderMarkdownTableRow(rows[0], columnCount),
        RenderMarkdownTableSeparator(columnCount)
    };

    foreach (var row in rows.Skip(1))
    {
        lines.Add(RenderMarkdownTableRow(row, columnCount));
    }

    return string.Join(Environment.NewLine, lines);
}

static string RenderMarkdownTableRow(string[] row, int columnCount)
{
    var values = Enumerable.Range(0, columnCount)
        .Select(index => index < row.Length ? EscapeMarkdownTableCell(row[index]) : string.Empty);
    return $"| {string.Join(" | ", values)} |";
}

static string RenderMarkdownTableSeparator(int columnCount)
    => $"| {string.Join(" | ", Enumerable.Repeat("---", Math.Max(1, columnCount)))} |";

static string EscapeMarkdownTableCell(string value)
    => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("|", "\\|", StringComparison.Ordinal)
        .ReplaceLineEndings("<br>");

static string RenderOcrDocumentHtml(OcrDocumentResult document)
{
    var builder = new StringBuilder();
    builder.Append("<article class=\"ocr-render-document\">");
    foreach (var page in document.Pages.OrderBy(page => page.PageIndex))
    {
        builder
            .Append("<section class=\"ocr-render-page\" data-page-index=\"")
            .Append(page.PageIndex.ToString(CultureInfo.InvariantCulture))
            .Append("\">");

        foreach (var block in page.Blocks.OrderBy(block => block.ReadingOrder))
        {
            builder.Append(RenderOcrBlockHtml(block));
        }

        builder.Append("</section>");
    }

    builder.Append("</article>");
    return builder.ToString();
}

static string RenderOcrBlockHtml(OcrBlock block)
{
    if (block.Table is not null)
    {
        return RenderOcrTableHtml(block.Table, block.Id);
    }

    if (string.Equals(block.Type, OcrBlockTypes.Formula, StringComparison.OrdinalIgnoreCase))
    {
        var formula = NormalizeOcrFormulaText(block.Formula?.Latex, block.Text);
        if (string.IsNullOrWhiteSpace(formula))
        {
            return string.Empty;
        }

        return new StringBuilder()
            .Append("<div class=\"ocr-render-formula\" data-block-id=\"")
            .Append(HtmlAttribute(block.Id))
            .Append("\" data-latex=\"")
            .Append(HtmlAttribute(formula))
            .Append('"')
            .Append(RenderOcrTraceAttributes(OcrBlockSourceText(block), formula))
            .Append("><code>")
            .Append(HtmlText(formula))
            .Append("</code></div>")
            .ToString();
    }

    if (block.Children.Count > 0)
    {
        var children = string.Concat(block.Children.OrderBy(child => child.ReadingOrder).Select(RenderOcrBlockHtml));
        if (!string.IsNullOrWhiteSpace(children))
        {
            return children;
        }
    }

    if (string.IsNullOrWhiteSpace(block.Text))
    {
        return string.Empty;
    }

    return new StringBuilder()
        .Append("<p class=\"ocr-render-text\" data-block-id=\"")
        .Append(HtmlAttribute(block.Id))
        .Append('"')
        .Append(RenderOcrTraceAttributes(OcrBlockSourceText(block), block.Text))
        .Append('>')
        .Append(HtmlTextWithBreaks(block.Text))
        .Append("</p>")
        .ToString();
}

static string RenderOcrTableHtml(OcrTableBlock table, string blockId)
{
    var rows = BuildOcrTableLayout(table);
    if (rows.Count == 0)
    {
        return string.Empty;
    }

    var builder = new StringBuilder();
    builder
        .Append("<table class=\"ocr-render-table\" data-block-id=\"")
        .Append(HtmlAttribute(blockId))
        .Append("\"><tbody>");

    for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
    {
        builder.Append("<tr>");
        var row = rows[rowIndex];
        foreach (var cell in row)
        {
            var tag = rowIndex == 0 && rows.Count > 1 ? "th" : "td";
            builder
                .Append('<')
                .Append(tag)
                .Append(" data-cell-id=\"")
                .Append(HtmlAttribute(cell.Cell.Id))
                .Append('"')
                .Append(RenderOcrTraceAttributes(OcrCellSourceText(cell.Cell), cell.Cell.Text))
                .Append(" data-row=\"")
                .Append(rowIndex.ToString(CultureInfo.InvariantCulture))
                .Append("\" data-column=\"")
                .Append(cell.Cell.ColumnIndex.ToString(CultureInfo.InvariantCulture))
                .Append('"');

            if (cell.RowSpan > 1)
            {
                builder
                    .Append(" rowspan=\"")
                    .Append(cell.RowSpan.ToString(CultureInfo.InvariantCulture))
                    .Append('"');
            }

            if (cell.ColumnSpan > 1)
            {
                builder
                    .Append(" colspan=\"")
                    .Append(cell.ColumnSpan.ToString(CultureInfo.InvariantCulture))
                    .Append('"');
            }

            builder
                .Append('>')
                .Append(HtmlTextWithBreaks(cell.Cell.Text))
                .Append("</")
                .Append(tag)
                .Append('>');
        }

        builder.Append("</tr>");
    }

    builder.Append("</tbody></table>");
    return builder.ToString();
}

static string[][] BuildOcrTableMatrix(OcrTableBlock table)
{
    if (table.Cells.Count == 0)
    {
        return [];
    }

    var validCells = table.Cells
        .Where(cell => cell.RowIndex >= 0 && cell.ColumnIndex >= 0)
        .ToArray();
    if (validCells.Length == 0)
    {
        return [];
    }

    var rowCount = Math.Max(table.RowCount, validCells.Max(cell => cell.RowIndex + Math.Max(1, cell.RowSpan)));
    var columnCount = Math.Max(table.ColumnCount, validCells.Max(cell => cell.ColumnIndex + Math.Max(1, cell.ColumnSpan)));
    var rows = new string[rowCount][];
    for (var rowIndex = 0; rowIndex < rowCount; rowIndex++)
    {
        rows[rowIndex] = Enumerable.Repeat(string.Empty, columnCount).ToArray();
    }

    foreach (var cell in validCells.OrderBy(cell => cell.RowIndex).ThenBy(cell => cell.ColumnIndex))
    {
        if (cell.RowIndex < rowCount && cell.ColumnIndex < columnCount)
        {
            rows[cell.RowIndex][cell.ColumnIndex] = cell.Text;
        }
    }

    return rows;
}

static IReadOnlyList<IReadOnlyList<OcrTableRenderCell>> BuildOcrTableLayout(OcrTableBlock table)
{
    if (table.Cells.Count == 0)
    {
        return Array.Empty<IReadOnlyList<OcrTableRenderCell>>();
    }

    var validCells = table.Cells
        .Where(cell => cell.RowIndex >= 0 && cell.ColumnIndex >= 0)
        .OrderBy(cell => cell.RowIndex)
        .ThenBy(cell => cell.ColumnIndex)
        .ToArray();
    if (validCells.Length == 0)
    {
        return Array.Empty<IReadOnlyList<OcrTableRenderCell>>();
    }

    var rowCount = Math.Max(table.RowCount, validCells.Max(cell => cell.RowIndex + Math.Max(1, cell.RowSpan)));
    var columnCount = Math.Max(table.ColumnCount, validCells.Max(cell => cell.ColumnIndex + Math.Max(1, cell.ColumnSpan)));
    if (rowCount <= 0 || columnCount <= 0)
    {
        return Array.Empty<IReadOnlyList<OcrTableRenderCell>>();
    }

    var occupied = new bool[rowCount, columnCount];
    var starts = new Dictionary<(int Row, int Column), OcrTableRenderCell>();

    foreach (var cell in validCells)
    {
        if (cell.RowIndex >= rowCount || cell.ColumnIndex >= columnCount || occupied[cell.RowIndex, cell.ColumnIndex])
        {
            continue;
        }

        var rowSpan = Math.Min(Math.Max(1, cell.RowSpan), rowCount - cell.RowIndex);
        var columnSpan = Math.Min(Math.Max(1, cell.ColumnSpan), columnCount - cell.ColumnIndex);
        var renderCell = new OcrTableRenderCell(cell, rowSpan, columnSpan, IsPlaceholder: false);
        starts[(cell.RowIndex, cell.ColumnIndex)] = renderCell;

        for (var rowIndex = cell.RowIndex; rowIndex < cell.RowIndex + rowSpan; rowIndex++)
        {
            for (var columnIndex = cell.ColumnIndex; columnIndex < cell.ColumnIndex + columnSpan; columnIndex++)
            {
                occupied[rowIndex, columnIndex] = true;
            }
        }
    }

    var rows = new List<IReadOnlyList<OcrTableRenderCell>>();
    for (var rowIndex = 0; rowIndex < rowCount; rowIndex++)
    {
        var row = new List<OcrTableRenderCell>();
        for (var columnIndex = 0; columnIndex < columnCount;)
        {
            if (starts.TryGetValue((rowIndex, columnIndex), out var renderCell))
            {
                row.Add(renderCell);
                columnIndex += renderCell.ColumnSpan;
                continue;
            }

            if (occupied[rowIndex, columnIndex])
            {
                columnIndex++;
                continue;
            }

            row.Add(new OcrTableRenderCell(
                new OcrTableCell
                {
                    Id = $"empty-r{rowIndex}-c{columnIndex}",
                    RowIndex = rowIndex,
                    ColumnIndex = columnIndex,
                    Text = string.Empty,
                    ShouldTranslate = false
                },
                RowSpan: 1,
                ColumnSpan: 1,
                IsPlaceholder: true));
            columnIndex++;
        }

        rows.Add(row);
    }

    return rows;
}

static string RenderOcrDocumentOverlayHtml(OcrDocumentResult document)
{
    var pages = document.Pages
        .OrderBy(page => page.PageIndex)
        .Select(RenderOcrPageOverlayHtml)
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .ToArray();

    if (pages.Length == 0)
    {
        return string.Empty;
    }

    return new StringBuilder()
        .Append("<article class=\"ocr-overlay-document\">")
        .Append(string.Concat(pages))
        .Append("</article>")
        .ToString();
}

static string RenderOcrDocumentLayoutHtml(OcrDocumentResult document)
{
    var pages = document.Pages
        .OrderBy(page => page.PageIndex)
        .Select(RenderOcrPageLayoutHtml)
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .ToArray();

    if (pages.Length == 0)
    {
        return string.Empty;
    }

    return new StringBuilder()
        .Append("<article class=\"ocr-layout-document\">")
        .Append(string.Concat(pages))
        .Append("</article>")
        .ToString();
}

static OcrLayoutDiagnostics BuildOcrLayoutDiagnostics(OcrDocumentResult document)
{
    var issues = new List<OcrLayoutIssue>();
    var pages = document.Pages.OrderBy(page => page.PageIndex).ToArray();
    var pagesWithSize = 0;
    var blockCount = 0;
    var blocksWithBox = 0;
    var blocksMissingBox = 0;
    var tableCellCount = 0;
    var tableCellsWithBox = 0;
    var tableCellsMissingBox = 0;

    foreach (var page in pages)
    {
        var hasSize = page.Width.GetValueOrDefault() > 0 && page.Height.GetValueOrDefault() > 0;
        if (hasSize)
        {
            pagesWithSize++;
        }
        else
        {
            issues.Add(new OcrLayoutIssue(
                "ocr_layout_page_size_missing",
                "warn",
                "OCR page has no width/height; overlay may use a cropped coordinate space.",
                $"page:{page.PageIndex.ToString(CultureInfo.InvariantCulture)}"));
        }

        foreach (var block in page.Blocks.OrderBy(block => block.ReadingOrder))
        {
            VisitOcrLayoutBlock(block);
        }
    }

    var layoutReady = pages.Length > 0 &&
        pagesWithSize == pages.Length &&
        blockCount > 0 &&
        blocksWithBox > 0 &&
        blocksMissingBox == 0;
    var overlayReady = layoutReady &&
        tableCellsMissingBox == 0;

    if (!layoutReady)
    {
        issues.Add(new OcrLayoutIssue(
            "ocr_layout_page_layout_not_ready",
            issues.Any(issue => string.Equals(issue.Severity, "error", StringComparison.OrdinalIgnoreCase)) ? "error" : "warn",
            "OCR structure is incomplete for coordinate-preserving page layout.",
            "document"));
    }

    if (!overlayReady)
    {
        issues.Add(new OcrLayoutIssue(
            "ocr_layout_overlay_not_ready",
            issues.Any(issue => string.Equals(issue.Severity, "error", StringComparison.OrdinalIgnoreCase)) ? "error" : "warn",
            "OCR structure is incomplete for precise visual overlay.",
            "document"));
    }

    return new OcrLayoutDiagnostics(
        pages.Length,
        pagesWithSize,
        blockCount,
        blocksWithBox,
        blocksMissingBox,
        tableCellCount,
        tableCellsWithBox,
        tableCellsMissingBox,
        overlayReady,
        layoutReady,
        issues);

    void VisitOcrLayoutBlock(OcrBlock block)
    {
        blockCount++;
        var blockHasBox = IsRenderableOcrBlockBox(block);
        if (blockHasBox)
        {
            blocksWithBox++;
        }
        else if (IsRenderableOcrBlock(block))
        {
            blocksMissingBox++;
            issues.Add(new OcrLayoutIssue(
                "ocr_layout_block_box_missing",
                "warn",
                $"OCR block '{block.Id}' has text/structure but no bounding box.",
                block.Id));
        }

        if (block.Table is not null)
        {
            foreach (var cell in block.Table.Cells)
            {
                tableCellCount++;
                if (IsUsableOcrBox(cell.BoundingBox))
                {
                    tableCellsWithBox++;
                }
                else
                {
                    tableCellsMissingBox++;
                    issues.Add(new OcrLayoutIssue(
                        "ocr_layout_table_cell_box_missing",
                        "warn",
                        $"OCR table cell '{cell.Id}' has no bounding box.",
                        string.IsNullOrWhiteSpace(cell.Id) ? block.Id : $"{block.Id}:{cell.Id}"));
                }
            }
        }

        foreach (var child in block.Children.OrderBy(child => child.ReadingOrder))
        {
            VisitOcrLayoutBlock(child);
        }
    }
}

static bool IsRenderableOcrBlock(OcrBlock block)
    => block.Table is not null ||
       block.Formula is not null ||
       !string.IsNullOrWhiteSpace(block.Text) ||
       block.Children.Count > 0;

static bool IsRenderableOcrBlockBox(OcrBlock block)
    => IsUsableOcrBox(block.BoundingBox) ||
       block.Table?.Cells.Any(cell => IsUsableOcrBox(cell.BoundingBox)) == true ||
       block.Children.Any(IsRenderableOcrBlockBox);

static string RenderOcrPageOverlayHtml(OcrPageResult page)
{
    var pageSize = GetOcrPageOverlaySize(page);
    if (pageSize is null)
    {
        return string.Empty;
    }

    var (pageWidth, pageHeight) = pageSize.Value;
    var blocks = page.Blocks
        .OrderBy(block => block.ReadingOrder)
        .Select(block => RenderOcrOverlayBlockHtml(block, pageWidth, pageHeight))
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .ToArray();

    if (blocks.Length == 0)
    {
        return string.Empty;
    }

    return new StringBuilder()
        .Append("<section class=\"ocr-overlay-page\" data-page-index=\"")
        .Append(page.PageIndex.ToString(CultureInfo.InvariantCulture))
        .Append("\" data-page-width=\"")
        .Append(pageWidth.ToString(CultureInfo.InvariantCulture))
        .Append("\" data-page-height=\"")
        .Append(pageHeight.ToString(CultureInfo.InvariantCulture))
        .Append("\" style=\"aspect-ratio:")
        .Append(pageWidth.ToString(CultureInfo.InvariantCulture))
        .Append('/')
        .Append(pageHeight.ToString(CultureInfo.InvariantCulture))
        .Append(";\">")
        .Append("<div class=\"ocr-overlay-page-surface\">")
        .Append(string.Concat(blocks))
        .Append("</div></section>")
        .ToString();
}

static string RenderOcrPageLayoutHtml(OcrPageResult page)
{
    var pageSize = GetOcrPageOverlaySize(page);
    if (pageSize is null)
    {
        return string.Empty;
    }

    var (pageWidth, pageHeight) = pageSize.Value;
    var blocks = page.Blocks
        .OrderBy(block => block.ReadingOrder)
        .Select(block => RenderOcrLayoutBlockHtml(block, pageWidth, pageHeight))
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .ToArray();

    if (blocks.Length == 0)
    {
        return string.Empty;
    }

    return new StringBuilder()
        .Append("<section class=\"ocr-layout-page\" data-page-index=\"")
        .Append(page.PageIndex.ToString(CultureInfo.InvariantCulture))
        .Append("\" data-page-width=\"")
        .Append(pageWidth.ToString(CultureInfo.InvariantCulture))
        .Append("\" data-page-height=\"")
        .Append(pageHeight.ToString(CultureInfo.InvariantCulture))
        .Append("\" style=\"aspect-ratio:")
        .Append(pageWidth.ToString(CultureInfo.InvariantCulture))
        .Append('/')
        .Append(pageHeight.ToString(CultureInfo.InvariantCulture))
        .Append(";\">")
        .Append("<div class=\"ocr-layout-page-surface\">")
        .Append(string.Concat(blocks))
        .Append("</div></section>")
        .ToString();
}

static string RenderOcrOverlayBlockHtml(OcrBlock block, int pageWidth, int pageHeight)
{
    var box = GetOcrBlockOverlayBox(block);
    if (box is null)
    {
        return string.Concat(block.Children
            .OrderBy(child => child.ReadingOrder)
            .Select(child => RenderOcrOverlayBlockHtml(child, pageWidth, pageHeight)));
    }

    var content = RenderOcrOverlayBlockContentHtml(block, box);
    if (string.IsNullOrWhiteSpace(content))
    {
        return string.Empty;
    }

    var type = CssClassToken(block.Type);
    var fitAttributes = block.Table is null ? RenderOcrOverlayFitAttributes(block.Type) : string.Empty;
    return new StringBuilder()
        .Append("<div class=\"ocr-overlay-block ocr-overlay-")
        .Append(type)
        .Append("\" data-block-id=\"")
        .Append(HtmlAttribute(block.Id))
        .Append("\" data-block-type=\"")
        .Append(HtmlAttribute(block.Type))
        .Append('"')
        .Append(RenderOcrTraceAttributes(OcrBlockSourceText(block), RenderOcrBlockText(block)))
        .Append(RenderOcrOverlayBoxAttributes(box))
        .Append(" style=\"")
        .Append(RenderOcrOverlayStyle(box, pageWidth, pageHeight))
        .Append("\"><div class=\"ocr-overlay-block-inner")
        .Append(block.Table is null ? " ocr-overlay-fit" : string.Empty)
        .Append('"')
        .Append(fitAttributes)
        .Append('>')
        .Append(content)
        .Append("</div></div>")
        .ToString();
}

static string RenderOcrLayoutBlockHtml(OcrBlock block, int pageWidth, int pageHeight)
{
    var box = GetOcrBlockOverlayBox(block);
    if (box is null)
    {
        return string.Concat(block.Children
            .OrderBy(child => child.ReadingOrder)
            .Select(child => RenderOcrLayoutBlockHtml(child, pageWidth, pageHeight)));
    }

    var content = RenderOcrLayoutBlockContentHtml(block, box);
    if (string.IsNullOrWhiteSpace(content))
    {
        return string.Empty;
    }

    var type = CssClassToken(block.Type);
    var fitAttributes = block.Table is null ? RenderOcrOverlayFitAttributes(block.Type) : string.Empty;
    return new StringBuilder()
        .Append("<div class=\"ocr-layout-block ocr-layout-")
        .Append(type)
        .Append("\" data-block-id=\"")
        .Append(HtmlAttribute(block.Id))
        .Append("\" data-block-type=\"")
        .Append(HtmlAttribute(block.Type))
        .Append('"')
        .Append(RenderOcrTraceAttributes(OcrBlockSourceText(block), RenderOcrBlockText(block)))
        .Append(RenderOcrOverlayBoxAttributes(box))
        .Append(" style=\"")
        .Append(RenderOcrOverlayStyle(box, pageWidth, pageHeight))
        .Append("\"><div class=\"ocr-layout-block-inner")
        .Append(block.Table is null ? " ocr-layout-fit" : string.Empty)
        .Append('"')
        .Append(fitAttributes)
        .Append('>')
        .Append(content)
        .Append("</div></div>")
        .ToString();
}

static string RenderOcrOverlayBlockContentHtml(OcrBlock block, OcrBoundingBox box)
{
    if (block.Table is not null)
    {
        return RenderOcrOverlayTableHtml(block.Table, block.Id, box);
    }

    if (string.Equals(block.Type, OcrBlockTypes.Formula, StringComparison.OrdinalIgnoreCase))
    {
        var formula = NormalizeOcrFormulaText(block.Formula?.Latex, block.Text);
        return string.IsNullOrWhiteSpace(formula)
            ? string.Empty
            : new StringBuilder()
                .Append("<code>")
                .Append(HtmlText(formula))
                .Append("</code>")
                .ToString();
    }

    if (!string.IsNullOrWhiteSpace(block.Text))
    {
        return HtmlTextWithBreaks(block.Text);
    }

    if (block.Children.Count == 0)
    {
        return string.Empty;
    }

    return string.Join(
        "<br>",
        block.Children
            .OrderBy(child => child.ReadingOrder)
            .Select(RenderOcrBlockText)
            .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(HtmlTextWithBreaks));
}

static string RenderOcrLayoutBlockContentHtml(OcrBlock block, OcrBoundingBox box)
{
    if (block.Table is not null)
    {
        return RenderOcrLayoutTableHtml(block.Table, block.Id, box);
    }

    if (string.Equals(block.Type, OcrBlockTypes.Formula, StringComparison.OrdinalIgnoreCase))
    {
        var formula = NormalizeOcrFormulaText(block.Formula?.Latex, block.Text);
        return string.IsNullOrWhiteSpace(formula)
            ? string.Empty
            : new StringBuilder()
                .Append("<code>")
                .Append(HtmlText(formula))
                .Append("</code>")
                .ToString();
    }

    if (!string.IsNullOrWhiteSpace(block.Text))
    {
        return HtmlTextWithBreaks(block.Text);
    }

    if (block.Children.Count == 0)
    {
        return string.Empty;
    }

    return string.Join(
        "<br>",
        block.Children
            .OrderBy(child => child.ReadingOrder)
            .Select(RenderOcrBlockText)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(HtmlTextWithBreaks));
}

static string RenderOcrOverlayTableHtml(OcrTableBlock table, string blockId, OcrBoundingBox tableBox)
{
    var positionedCells = table.Cells
        .Where(cell => IsUsableOcrBox(cell.BoundingBox))
        .OrderBy(cell => cell.RowIndex)
        .ThenBy(cell => cell.ColumnIndex)
        .ToArray();

    if (positionedCells.Length == 0 || !IsUsableOcrBox(tableBox))
    {
        return RenderOcrTableHtml(table, blockId);
    }

    var builder = new StringBuilder();
    builder.Append("<div class=\"ocr-overlay-table-grid\" data-block-id=\"")
        .Append(HtmlAttribute(blockId))
        .Append("\">");

    foreach (var cell in positionedCells)
    {
        var cellBox = cell.BoundingBox!;
        var relative = new OcrBoundingBox(
            Math.Max(0, cellBox.X - tableBox.X),
            Math.Max(0, cellBox.Y - tableBox.Y),
            Math.Max(1, cellBox.Width),
            Math.Max(1, cellBox.Height));
        var cellClass = cell.RowIndex == 0 ? " ocr-overlay-table-cell-header" : string.Empty;
        builder
            .Append("<div class=\"ocr-overlay-table-cell")
            .Append(cellClass)
            .Append("\" data-cell-id=\"")
            .Append(HtmlAttribute(cell.Id))
            .Append("\" data-row=\"")
            .Append(cell.RowIndex.ToString(CultureInfo.InvariantCulture))
            .Append("\" data-column=\"")
            .Append(cell.ColumnIndex.ToString(CultureInfo.InvariantCulture))
            .Append('"')
            .Append(RenderOcrTraceAttributes(OcrCellSourceText(cell), cell.Text))
            .Append(RenderOcrOverlayBoxAttributes(cellBox))
            .Append(" style=\"")
            .Append(RenderOcrOverlayStyle(relative, tableBox.Width, tableBox.Height))
            .Append("\"><span class=\"ocr-overlay-fit\"")
            .Append(RenderOcrOverlayFitAttributes("table_cell"))
            .Append('>')
            .Append(HtmlTextWithBreaks(cell.Text))
            .Append("</span></div>");
    }

    builder.Append("</div>");
    return builder.ToString();
}

static string RenderOcrLayoutTableHtml(OcrTableBlock table, string blockId, OcrBoundingBox tableBox)
{
    var positionedCells = table.Cells
        .Where(cell => IsUsableOcrBox(cell.BoundingBox))
        .OrderBy(cell => cell.RowIndex)
        .ThenBy(cell => cell.ColumnIndex)
        .ToArray();

    if (positionedCells.Length == 0 || !IsUsableOcrBox(tableBox))
    {
        return RenderOcrTableHtml(table, blockId);
    }

    var builder = new StringBuilder();
    builder.Append("<div class=\"ocr-layout-table-grid\" data-block-id=\"")
        .Append(HtmlAttribute(blockId))
        .Append("\">");

    foreach (var cell in positionedCells)
    {
        var cellBox = cell.BoundingBox!;
        var relative = new OcrBoundingBox(
            Math.Max(0, cellBox.X - tableBox.X),
            Math.Max(0, cellBox.Y - tableBox.Y),
            Math.Max(1, cellBox.Width),
            Math.Max(1, cellBox.Height));
        var cellClass = cell.RowIndex == 0 ? " ocr-layout-table-cell-header" : string.Empty;
        builder
            .Append("<div class=\"ocr-layout-table-cell")
            .Append(cellClass)
            .Append("\" data-cell-id=\"")
            .Append(HtmlAttribute(cell.Id))
            .Append("\" data-row=\"")
            .Append(cell.RowIndex.ToString(CultureInfo.InvariantCulture))
            .Append("\" data-column=\"")
            .Append(cell.ColumnIndex.ToString(CultureInfo.InvariantCulture))
            .Append('"')
            .Append(RenderOcrTraceAttributes(OcrCellSourceText(cell), cell.Text))
            .Append(RenderOcrOverlayBoxAttributes(cellBox))
            .Append(" style=\"")
            .Append(RenderOcrOverlayStyle(relative, tableBox.Width, tableBox.Height))
            .Append("\"><span class=\"ocr-layout-fit\"")
            .Append(RenderOcrOverlayFitAttributes("table_cell"))
            .Append('>')
            .Append(HtmlTextWithBreaks(cell.Text))
            .Append("</span></div>");
    }

    builder.Append("</div>");
    return builder.ToString();
}

static (int Width, int Height)? GetOcrPageOverlaySize(OcrPageResult page)
{
    var boxes = page.Blocks.SelectMany(GetOcrBlockOverlayBoxes).Where(IsUsableOcrBox).ToArray();
    var width = Math.Max(0, page.Width ?? 0);
    var height = Math.Max(0, page.Height ?? 0);

    if (boxes.Length > 0)
    {
        width = Math.Max(width, boxes.Max(box => Math.Max(0, box.X) + Math.Max(1, box.Width)));
        height = Math.Max(height, boxes.Max(box => Math.Max(0, box.Y) + Math.Max(1, box.Height)));
    }

    return width > 0 && height > 0 ? (width, height) : null;
}

static IEnumerable<OcrBoundingBox> GetOcrBlockOverlayBoxes(OcrBlock block)
{
    if (IsUsableOcrBox(block.BoundingBox))
    {
        yield return block.BoundingBox!;
    }

    if (block.Table is not null)
    {
        foreach (var cell in block.Table.Cells)
        {
            if (IsUsableOcrBox(cell.BoundingBox))
            {
                yield return cell.BoundingBox!;
            }
        }
    }

    foreach (var child in block.Children)
    {
        foreach (var box in GetOcrBlockOverlayBoxes(child))
        {
            yield return box;
        }
    }
}

static OcrBoundingBox? GetOcrBlockOverlayBox(OcrBlock block)
{
    if (IsUsableOcrBox(block.BoundingBox))
    {
        return block.BoundingBox;
    }

    if (block.Table is not null)
    {
        var tableBox = UnionOcrBoxes(block.Table.Cells.Select(cell => cell.BoundingBox));
        if (tableBox is not null)
        {
            return tableBox;
        }
    }

    return UnionOcrBoxes(block.Children.Select(GetOcrBlockOverlayBox));
}

static OcrBoundingBox? UnionOcrBoxes(IEnumerable<OcrBoundingBox?> boxes)
{
    var valid = boxes.Where(IsUsableOcrBox).Select(box => box!).ToArray();
    if (valid.Length == 0)
    {
        return null;
    }

    var left = valid.Min(box => box.X);
    var top = valid.Min(box => box.Y);
    var right = valid.Max(box => box.X + box.Width);
    var bottom = valid.Max(box => box.Y + box.Height);
    return new OcrBoundingBox(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
}

static bool IsUsableOcrBox(OcrBoundingBox? box)
    => box is not null && box.Width > 0 && box.Height > 0;

static string RenderOcrOverlayBoxAttributes(OcrBoundingBox box)
    => string.Create(
        CultureInfo.InvariantCulture,
        $" data-overlay-x=\"{box.X}\" data-overlay-y=\"{box.Y}\" data-overlay-width=\"{box.Width}\" data-overlay-height=\"{box.Height}\"");

static string RenderOcrOverlayStyle(OcrBoundingBox box, int width, int height)
{
    var left = Percent(box.X, width);
    var top = Percent(box.Y, height);
    var itemWidth = Math.Min(100 - left, Math.Max(0.1, Percent(box.Width, width)));
    var itemHeight = Math.Min(100 - top, Math.Max(0.1, Percent(box.Height, height)));
    return string.Create(
        CultureInfo.InvariantCulture,
        $"left:{left:0.###}%;top:{top:0.###}%;width:{itemWidth:0.###}%;height:{itemHeight:0.###}%;");
}

static double Percent(int value, int whole)
    => whole <= 0 ? 0 : Math.Clamp(value / (double)whole * 100, 0, 100);

static string CssClassToken(string value)
{
    var token = new string((value ?? string.Empty)
        .Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' ? char.ToLowerInvariant(character) : '-')
        .ToArray())
        .Trim('-');
    return string.IsNullOrWhiteSpace(token) ? "unknown" : token;
}

static string RenderOcrOverlayFitAttributes(string type)
{
    var max = string.Equals(type, OcrBlockTypes.Formula, StringComparison.OrdinalIgnoreCase)
        ? 16
        : string.Equals(type, "table_cell", StringComparison.OrdinalIgnoreCase) ? 12 : 14;
    var min = string.Equals(type, "table_cell", StringComparison.OrdinalIgnoreCase) ? 6 : 7;
    return string.Create(
        CultureInfo.InvariantCulture,
        $" data-fit-text=\"true\" data-fit-min=\"{min}\" data-fit-max=\"{max}\"");
}

static string NormalizeOcrFormulaText(string? latex, string fallback)
{
    var value = string.IsNullOrWhiteSpace(latex) ? fallback.Trim() : latex.Trim();
    if (string.IsNullOrWhiteSpace(value))
    {
        return string.Empty;
    }

    return value.StartsWith("$$", StringComparison.Ordinal) && value.EndsWith("$$", StringComparison.Ordinal)
        ? value
        : $"$${value}$$";
}

static string RenderOcrTraceAttributes(string sourceText, string translatedText)
{
    var source = sourceText.Trim();
    var translated = translatedText.Trim();
    if (string.IsNullOrWhiteSpace(source) && string.IsNullOrWhiteSpace(translated))
    {
        return string.Empty;
    }

    var title = string.Equals(source, translated, StringComparison.Ordinal)
        ? source
        : $"{source} -> {translated}";
    return new StringBuilder()
        .Append(" data-source-text=\"")
        .Append(HtmlAttribute(source))
        .Append("\" data-translated-text=\"")
        .Append(HtmlAttribute(translated))
        .Append("\" title=\"")
        .Append(HtmlAttribute(title))
        .Append('"')
        .ToString();
}

static string OcrBlockSourceText(OcrBlock block)
    => FirstNonBlankOrEmpty(block.SourceText, block.Formula?.SourceText, block.Formula?.Latex, block.Text);

static string OcrCellSourceText(OcrTableCell cell)
    => FirstNonBlankOrEmpty(cell.SourceText, cell.Text);

static string HtmlText(string value)
    => WebUtility.HtmlEncode(value);

static string HtmlAttribute(string value)
    => WebUtility.HtmlEncode(value);

static string HtmlTextWithBreaks(string value)
    => HtmlText(value).ReplaceLineEndings("<br>");

static TranslationBroadcastMessage CreateBroadcastMessage(
    string sourceText,
    string translatedText,
    string source,
    string target,
    string mode,
    string provider,
    string? glossary,
    string engine,
    long latencyMs,
    bool cacheHit,
    string sourceKind,
    int? segmentIndex = null,
    double? startSeconds = null,
    double? endSeconds = null,
    double? confidence = null)
{
    var createdAt = DateTimeOffset.UtcNow;
    var normalizedKind = PickBroadcastSourceKind(sourceKind, "text");
    var displaySeconds = CalculateBroadcastDisplaySeconds(normalizedKind, startSeconds, endSeconds);

    return new TranslationBroadcastMessage(
        "translation",
        sourceText,
        translatedText,
        source,
        target,
        mode,
        provider,
        glossary,
        engine,
        latencyMs,
        cacheHit,
        createdAt,
        Guid.NewGuid().ToString("N"),
        normalizedKind,
        segmentIndex,
        startSeconds,
        endSeconds,
        confidence,
        createdAt.AddSeconds(displaySeconds),
        CreateBroadcastStableKey(normalizedKind, sourceText, translatedText, segmentIndex, startSeconds));
}

static string PickBroadcastSourceKind(string? value, string fallback)
{
    var normalized = Pick(value, fallback).ToLowerInvariant();
    return normalized is "text" or "ocr" or "region" or "asr" or "live" or "read-frog"
        ? normalized
        : fallback;
}

static double CalculateBroadcastDisplaySeconds(
    string sourceKind,
    double? startSeconds,
    double? endSeconds)
{
    if (sourceKind is "asr" or "live" &&
        startSeconds.HasValue &&
        endSeconds.HasValue &&
        endSeconds.Value > startSeconds.Value)
    {
        return Math.Clamp((endSeconds.Value - startSeconds.Value) + 1.5, 4, 10);
    }

    return sourceKind is "asr" or "live" ? 7 : 12;
}

static string CreateBroadcastStableKey(
    string sourceKind,
    string sourceText,
    string translatedText,
    int? segmentIndex,
    double? startSeconds)
{
    var basis = string.Join(
        "|",
        sourceKind,
        segmentIndex?.ToString() ?? string.Empty,
        startSeconds.HasValue ? Math.Round(startSeconds.Value, 2).ToString("0.##", CultureInfo.InvariantCulture) : string.Empty,
        sourceText,
        translatedText);
    return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(basis)))[..16].ToLowerInvariant();
}

static async Task<IReadOnlyList<SpeechTranslatedSegment>> TranslateSpeechSegmentsAsync(
    SpeechResponse speech,
    SpeechTranslateRequest request,
    TranslationService translationService,
    TranslationBroadcastHub broadcastHub,
    VerbeamOptions options,
    bool allowSharedMemory,
    string principalId,
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
                Surface = "audio",
                Glossary = request.Glossary,
                Provider = translationProvider,
                Model = request.Model,
                Profile = request.Profile,
                SessionId = request.SessionId,
                ContextItems = contextItems,
                AllowSharedMemory = allowSharedMemory,
                PrincipalId = principalId
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
            outcome.CacheHit,
            outcome.TokenUsage));

        if (outcome.IsSuccess)
        {
            await broadcastHub.BroadcastAsync(
                CreateBroadcastMessage(
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
                    "asr",
                    segment.Index,
                    segment.StartSeconds,
                    segment.EndSeconds,
                    segment.Confidence),
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

static async Task StreamOcrJobEventsAsync(
    string jobId,
    HttpContext context,
    OcrJobService ocrJobs,
    CancellationToken cancellationToken)
{
    var job = await ocrJobs.GetAsync(jobId, cancellationToken);
    if (job is null)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync(JsonSerializer.Serialize(OcrError("OCR job was not found.")), cancellationToken);
        return;
    }

    context.Response.Headers.CacheControl = "no-cache";
    context.Response.Headers.Connection = "keep-alive";
    context.Response.ContentType = "text/event-stream; charset=utf-8";

    var afterSequence = ReadEventSequenceCursor(context);
    while (!cancellationToken.IsCancellationRequested)
    {
        var events = await ocrJobs.ListEventsAsync(jobId, afterSequence, 100, cancellationToken);
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

        job = await ocrJobs.GetAsync(jobId, cancellationToken);
        if (job is null || IsTerminalOcrJobStatus(job.Status))
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

static bool IsTerminalOcrJobStatus(string status)
    => status.Equals("succeeded", StringComparison.OrdinalIgnoreCase) ||
       status.Equals("failed", StringComparison.OrdinalIgnoreCase) ||
       status.Equals("canceled", StringComparison.OrdinalIgnoreCase);

static async Task StreamVideoSpeechSessionEventsAsync(
    string sessionId,
    HttpContext context,
    VideoSpeechSessionService videoSessions,
    VideoSpeechEventBroker eventBroker,
    CancellationToken cancellationToken)
{
    var session = await videoSessions.GetAsync(sessionId, cancellationToken);
    if (session is null)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync(JsonSerializer.Serialize(AsrError("Video speech session was not found.")), cancellationToken);
        return;
    }

    context.Response.Headers.CacheControl = "no-cache";
    context.Response.Headers.Connection = "keep-alive";
    context.Response.ContentType = "text/event-stream; charset=utf-8";

    var afterSequence = ReadEventSequenceCursor(context);
    var once = context.Request.Query.TryGetValue("once", out var onceValue) &&
               bool.TryParse(onceValue.ToString(), out var onceParsed) &&
               onceParsed;

    // Subscribe BEFORE the catch-up query: anything committed afterwards is
    // pushed to the already-open channel, so no event can fall in between.
    // The DB stays the source of truth; channel items are payload-carrying
    // wake-up signals and drops are healed by re-reading from the cursor.
    using var subscription = eventBroker.Subscribe(sessionId, out var liveEvents);

    async Task<bool> WriteFromLogAsync()
    {
        var wroteAny = false;
        while (true)
        {
            var events = await videoSessions.ListEventsAsync(sessionId, afterSequence, 100, cancellationToken);
            foreach (var item in events)
            {
                afterSequence = item.Sequence;
                await context.Response.WriteAsync($"id: {item.Sequence}\n", cancellationToken);
                await context.Response.WriteAsync($"event: {item.Type}\n", cancellationToken);
                await context.Response.WriteAsync($"data: {item.PayloadJson}\n\n", cancellationToken);
                wroteAny = true;
            }

            if (events.Count < 100)
            {
                break;
            }
        }

        if (wroteAny)
        {
            await context.Response.Body.FlushAsync(cancellationToken);
        }

        return wroteAny;
    }

    await WriteFromLogAsync();
    if (once)
    {
        return;
    }

    while (!cancellationToken.IsCancellationRequested)
    {
        session = await videoSessions.GetAsync(sessionId, cancellationToken);
        if (session is null || IsTerminalVideoSpeechSessionStatus(session.Status))
        {
            // Flush anything written between the last drain and the terminal
            // transition before closing.
            await WriteFromLogAsync();
            break;
        }

        var waitForEvent = liveEvents.WaitToReadAsync(cancellationToken).AsTask();
        var completed = await Task.WhenAny(waitForEvent, Task.Delay(TimeSpan.FromSeconds(15), cancellationToken));
        if (completed == waitForEvent && await waitForEvent)
        {
            var maxPushed = afterSequence;
            while (liveEvents.TryRead(out var pushed))
            {
                maxPushed = Math.Max(maxPushed, pushed.Sequence);
            }

            if (maxPushed > afterSequence)
            {
                await WriteFromLogAsync();
            }
        }
        else
        {
            await context.Response.WriteAsync($": ping {DateTimeOffset.UtcNow:O}\n\n", cancellationToken);
            await context.Response.Body.FlushAsync(cancellationToken);
        }
    }
}

static bool IsTerminalVideoSpeechSessionStatus(string status)
    => status.Equals("failed", StringComparison.OrdinalIgnoreCase) ||
       status.Equals("canceled", StringComparison.OrdinalIgnoreCase) ||
       status.Equals("captions_ready", StringComparison.OrdinalIgnoreCase) ||
       status.Equals("completed", StringComparison.OrdinalIgnoreCase);

static async Task HandleSpeechLiveSocketAsync(
    HttpContext context,
    SpeechService speechService,
    TranslationService translationService,
    IMemoryPrincipalPermissionStore principalPermissions,
    IMemoryPrincipalSessionStore principalSessions,
    TranslationBroadcastHub broadcastHub,
    VerbeamOptions options,
    CancellationToken cancellationToken)
{
    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    var receiveBuffer = new byte[64 * 1024];
    using var audioBuffer = new MemoryStream();
    using var sendLock = new SemaphoreSlim(1, 1);
    using var socketCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    var liveQueue = Channel.CreateBounded<LiveSpeechWorkItem>(new BoundedChannelOptions(4)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = true
    });
    var config = new LiveSpeechConfig();
    var silenceMs = 0;
    var audioCursorMs = 0;
    var bufferStartMs = 0;
    DateTimeOffset? bufferStartedAt = null;
    long nextSequence = 0;

    var processingTask = ProcessLiveSpeechQueueAsync(
        liveQueue.Reader,
        context,
        socket,
        sendLock,
        speechService,
        translationService,
        principalPermissions,
        principalSessions,
        broadcastHub,
        options,
        jsonOptions,
        socketCts.Token);

    await SendLiveJsonAsync(socket, new { type = "ready" }, jsonOptions, cancellationToken, sendLock);

    try
    {
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
                    audioCursorMs = 0;
                    bufferStartMs = 0;
                    bufferStartedAt = null;
                    nextSequence = 0;
                    await SendLiveJsonAsync(
                        socket,
                        new
                        {
                            type = "started",
                            sampleRate = options.Speech.Live.SampleRate,
                            channels = options.Speech.Live.Channels,
                            bitsPerSample = options.Speech.Live.BitsPerSample
                        },
                        jsonOptions,
                        cancellationToken,
                        sendLock);
                }
                else if (string.Equals(type, "flush", StringComparison.OrdinalIgnoreCase))
                {
                    if (await EnqueueCurrentLiveBufferAsync("flush", cancellationToken))
                    {
                        silenceMs = 0;
                    }
                }
                else if (string.Equals(type, "stop", StringComparison.OrdinalIgnoreCase))
                {
                    await EnqueueCurrentLiveBufferAsync("stop", cancellationToken);
                    liveQueue.Writer.TryComplete();
                    await processingTask;
                    await SendLiveJsonAsync(socket, new { type = "done" }, jsonOptions, cancellationToken, sendLock);
                    break;
                }
            }
            else if (message.MessageType == WebSocketMessageType.Binary)
            {
                if (audioBuffer.Length == 0)
                {
                    bufferStartMs = audioCursorMs;
                    bufferStartedAt = DateTimeOffset.UtcNow;
                }

                audioBuffer.Write(message.Payload, 0, message.Payload.Length);
                var chunkDurationMs = PcmDurationMs(
                    message.Payload.Length,
                    options.Speech.Live.SampleRate,
                    options.Speech.Live.Channels,
                    options.Speech.Live.BitsPerSample);
                audioCursorMs += chunkDurationMs;

                if (IsSilentPcm16(message.Payload, options.Speech.Live.SilenceRmsThreshold))
                {
                    silenceMs += chunkDurationMs;
                }
                else
                {
                    silenceMs = 0;
                }

                var maxBytes = options.Speech.Live.SampleRate *
                    Math.Max(1, options.Speech.Live.Channels) *
                    Math.Max(1, options.Speech.Live.BitsPerSample / 8) *
                    Math.Max(1, options.Speech.Live.MaxSegmentSeconds);
                if (audioBuffer.Length >= maxBytes ||
                    (silenceMs >= options.Speech.Live.SilenceDurationMs && audioBuffer.Length > 0))
                {
                    if (await EnqueueCurrentLiveBufferAsync("auto", cancellationToken))
                    {
                        silenceMs = 0;
                    }
                }
            }
        }
    }
    finally
    {
        liveQueue.Writer.TryComplete();
        socketCts.Cancel();
        try
        {
            await processingTask;
        }
        catch (OperationCanceledException) when (socketCts.IsCancellationRequested)
        {
        }

        if (socket.State is WebSocketState.CloseReceived or WebSocketState.Open)
        {
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
        }
    }

    async Task<bool> EnqueueCurrentLiveBufferAsync(string reason, CancellationToken enqueueToken)
    {
        if (audioBuffer.Length == 0)
        {
            return false;
        }

        var pcm = audioBuffer.ToArray();
        audioBuffer.SetLength(0);
        var sequence = nextSequence++;
        var segmentId = $"{Pick(config.SessionId, "live")}-{sequence:000000}";
        var item = new LiveSpeechWorkItem(
            sequence,
            segmentId,
            pcm,
            config,
            bufferStartMs / 1000.0,
            audioCursorMs / 1000.0,
            bufferStartedAt ?? DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        bufferStartedAt = null;

        await SendLiveJsonAsync(
            socket,
            new
            {
                type = "queued",
                item.SegmentId,
                item.Sequence,
                reason,
                startSeconds = item.StartSeconds,
                endSeconds = item.EndSeconds,
                startedAt = item.StartedAt,
                endedAt = item.EndedAt
            },
            jsonOptions,
            enqueueToken,
            sendLock);
        await liveQueue.Writer.WriteAsync(item, enqueueToken);
        return true;
    }
}

static async Task ProcessLiveSpeechQueueAsync(
    ChannelReader<LiveSpeechWorkItem> queue,
    HttpContext context,
    WebSocket socket,
    SemaphoreSlim sendLock,
    SpeechService speechService,
    TranslationService translationService,
    IMemoryPrincipalPermissionStore principalPermissions,
    IMemoryPrincipalSessionStore principalSessions,
    TranslationBroadcastHub broadcastHub,
    VerbeamOptions options,
    JsonSerializerOptions jsonOptions,
    CancellationToken cancellationToken)
{
    await foreach (var item in queue.ReadAllAsync(cancellationToken))
    {
        await ProcessLiveSpeechItemAsync(
            item,
            context,
            socket,
            sendLock,
            speechService,
            translationService,
            principalPermissions,
            principalSessions,
            broadcastHub,
            options,
            jsonOptions,
            cancellationToken);
    }
}

static async Task ProcessLiveSpeechItemAsync(
    LiveSpeechWorkItem item,
    HttpContext context,
    WebSocket socket,
    SemaphoreSlim sendLock,
    SpeechService speechService,
    TranslationService translationService,
    IMemoryPrincipalPermissionStore principalPermissions,
    IMemoryPrincipalSessionStore principalSessions,
    TranslationBroadcastHub broadcastHub,
    VerbeamOptions options,
    JsonSerializerOptions jsonOptions,
    CancellationToken cancellationToken)
{
    var wav = PcmWaveWriter.BuildPcmWave(
        item.Pcm,
        Math.Max(1, options.Speech.Live.SampleRate),
        (short)Math.Max(1, options.Speech.Live.Channels),
        (short)Math.Max(1, options.Speech.Live.BitsPerSample));
    var stopwatch = Stopwatch.StartNew();

    try
    {
        await SendLiveJsonAsync(
            socket,
            new
            {
                type = "processing",
                status = "started",
                item.SegmentId,
                item.Sequence,
                startSeconds = item.StartSeconds,
                endSeconds = item.EndSeconds,
                startedAt = item.StartedAt,
                endedAt = item.EndedAt
            },
            jsonOptions,
            cancellationToken,
            sendLock);

        var speech = await speechService.RecognizeAsync(
            new SpeechRequest
            {
                AudioBase64 = Convert.ToBase64String(wav),
                AudioMimeType = "audio/wav",
                Provider = Pick(item.Config.Provider, options.Speech.DefaultProvider),
                Language = Pick(item.Config.Language, options.Speech.DefaultLanguage),
                Profile = Pick(item.Config.Profile, "default"),
                SessionId = Pick(item.Config.SessionId, "live"),
                Glossary = item.Config.Glossary,
                PreferCaptions = false
            },
            cancellationToken);
        var offsetSpeech = speech with
        {
            Segments = speech.Segments
                .Select(segment => segment with
                {
                    StartSeconds = item.StartSeconds + Math.Max(0, segment.StartSeconds),
                    EndSeconds = item.StartSeconds + Math.Max(segment.EndSeconds, segment.StartSeconds)
                })
                .ToArray()
        };

        foreach (var segment in offsetSpeech.Segments)
        {
            await SendLiveJsonAsync(
                socket,
                new
                {
                    type = "segment",
                    item.SegmentId,
                    item.Sequence,
                    offsetSpeech.EventId,
                    segment,
                    offsetSpeech.Provider,
                    offsetSpeech.Engine,
                    offsetSpeech.Language,
                    startSeconds = item.StartSeconds,
                    endSeconds = item.EndSeconds,
                    startedAt = item.StartedAt,
                    endedAt = item.EndedAt
                },
                jsonOptions,
                cancellationToken,
                sendLock);
        }

        if (item.Config.Translate)
        {
            var identity = await RequestMemoryIdentityAsync(context, options, principalSessions, cancellationToken);
            var translations = await TranslateSpeechSegmentsAsync(
                offsetSpeech,
                new SpeechTranslateRequest
                {
                    Language = item.Config.Language,
                    Source = item.Config.Source,
                    Target = item.Config.Target,
                    Mode = item.Config.Mode,
                    Glossary = item.Config.Glossary,
                    TranslationProvider = item.Config.TranslationProvider,
                    Model = item.Config.Model,
                    Profile = item.Config.Profile,
                    SessionId = item.Config.SessionId
                },
                translationService,
                broadcastHub,
                options,
                await AllowSharedMemoryForIdentityAsync(
                    options,
                    principalPermissions,
                    identity,
                    Pick(item.Config.Profile, "default"),
                    cancellationToken),
                identity.Principal,
                cancellationToken);

            foreach (var translation in translations)
            {
                await SendLiveJsonAsync(
                socket,
                    new
                    {
                        type = "translation",
                        item.SegmentId,
                        item.Sequence,
                        offsetSpeech.EventId,
                        translation,
                        startSeconds = item.StartSeconds,
                        endSeconds = item.EndSeconds,
                        startedAt = item.StartedAt,
                        endedAt = item.EndedAt
                    },
                    jsonOptions,
                    cancellationToken,
                    sendLock);
            }
        }

        stopwatch.Stop();
        await SendLiveJsonAsync(
            socket,
            new
            {
                type = "processing",
                status = "done",
                item.SegmentId,
                item.Sequence,
                latencyMs = stopwatch.ElapsedMilliseconds
            },
            jsonOptions,
            cancellationToken,
            sendLock);
    }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or TimeoutException)
    {
        stopwatch.Stop();
        await SendLiveJsonAsync(
            socket,
            new
            {
                type = "error",
                item.SegmentId,
                item.Sequence,
                errorMessage = ex.Message,
                latencyMs = stopwatch.ElapsedMilliseconds
            },
            jsonOptions,
            cancellationToken,
            sendLock);
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
    CancellationToken cancellationToken,
    SemaphoreSlim? sendLock = null)
{
    if (sendLock is null)
    {
        if (socket.State != WebSocketState.Open)
        {
            return;
        }

        var unlockedBytes = JsonSerializer.SerializeToUtf8Bytes(payload, jsonOptions);
        await socket.SendAsync(unlockedBytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
        return;
    }

    await sendLock.WaitAsync(cancellationToken);
    try
    {
        if (socket.State != WebSocketState.Open)
        {
            return;
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, jsonOptions);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
    }
    finally
    {
        sendLock.Release();
    }
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

static string BuildFunAsrEngineNote(
    bool configured,
    FunAsrHealthProbe health,
    VerbeamOptions options)
{
    if (!configured)
    {
        return "FunASR base URL is not configured.";
    }

    var transcriptionUrl = options.Speech.FunAsrHttp.BaseUrl.TrimEnd('/') + "/v1/audio/transcriptions";
    if (health.Ready)
    {
        return $"Ready. Calls {transcriptionUrl} with model {options.Speech.FunAsrHttp.Model}.";
    }

    if (health.Reachable)
    {
        return $"FunASR health is reachable but not ready (status: {health.Status}).";
    }

    return $"FunASR health check failed: {health.ErrorMessage}";
}

static async Task<FunAsrHealthProbe> ProbeFunAsrHealthAsync(
    string? baseUrl,
    CancellationToken cancellationToken)
{
    var probedAt = DateTimeOffset.UtcNow;
    if (string.IsNullOrWhiteSpace(baseUrl))
    {
        return new FunAsrHealthProbe(
            Reachable: false,
            Ready: false,
            Status: "unconfigured",
            ErrorMessage: "FunASR base URL is empty.",
            probedAt);
    }

    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(750));
    using var httpClient = new HttpClient
    {
        Timeout = TimeSpan.FromMilliseconds(900)
    };

    try
    {
        using var response = await httpClient.GetAsync(BuildFunAsrHealthEndpoint(baseUrl), timeoutCts.Token);
        var body = await response.Content.ReadAsStringAsync(timeoutCts.Token);
        if (!response.IsSuccessStatusCode)
        {
            return new FunAsrHealthProbe(
                Reachable: true,
                Ready: false,
                Status: $"http_{(int)response.StatusCode}",
                ErrorMessage: TrimHealthMessage(body),
                probedAt);
        }

        var status = ReadHealthStatus(body);
        return new FunAsrHealthProbe(
            Reachable: true,
            Ready: status.Equals("ok", StringComparison.OrdinalIgnoreCase),
            Status: status,
            ErrorMessage: string.Empty,
            probedAt);
    }
    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
    {
        return new FunAsrHealthProbe(
            Reachable: false,
            Ready: false,
            Status: "timeout",
            ErrorMessage: "FunASR health check timed out.",
            probedAt);
    }
    catch (Exception ex) when (ex is HttpRequestException or UriFormatException or InvalidOperationException)
    {
        return new FunAsrHealthProbe(
            Reachable: false,
            Ready: false,
            Status: "unreachable",
            ErrorMessage: TrimHealthMessage(ex.Message),
            probedAt);
    }
}

static Uri BuildFunAsrHealthEndpoint(string baseUrl)
{
    var normalized = baseUrl.TrimEnd('/');
    if (normalized.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
    {
        normalized = normalized[..^3];
    }

    return new Uri(new Uri(normalized + "/"), "health");
}

static string ReadHealthStatus(string body)
{
    try
    {
        using var document = JsonDocument.Parse(body);
        if (document.RootElement.TryGetProperty("status", out var status) &&
            status.ValueKind == JsonValueKind.String)
        {
            return Pick(status.GetString(), "unknown");
        }
    }
    catch (JsonException)
    {
    }

    return string.IsNullOrWhiteSpace(body) ? "unknown" : TrimHealthMessage(body);
}

static string TrimHealthMessage(string value)
{
    value = value.ReplaceLineEndings(" ").Trim();
    return value.Length <= 160 ? value : value[..160];
}

static async Task<IReadOnlyList<SpeechEngineDescriptor>> BuildSpeechEnginesAsync(
    SpeechProviderRegistry providers,
    VerbeamOptions options,
    CancellationToken cancellationToken)
{
    var funAsrHealth = await ProbeFunAsrHealthAsync(options.Speech.FunAsrHttp.BaseUrl, cancellationToken);

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
                var configured = !string.IsNullOrWhiteSpace(options.Speech.FunAsrHttp.BaseUrl);
                var available = configured && funAsrHealth.Ready;
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
                    BuildFunAsrEngineNote(configured, funAsrHealth, options));
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

static bool HasConfiguredRapidOcrNetModels(RapidOcrNetOptions options, string contentRootPath)
{
    var requiredPaths = new[]
        {
            options.DetModelPath,
            options.RecModelPath,
            options.KeysPath
        }
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .Select(path => PathResolver.Resolve(contentRootPath, path))
        .ToArray();

    return requiredPaths.Length == 3 && requiredPaths.All(File.Exists);
}

static LocalPythonOcrProvider CreateLocalOcrProvider(
    string name,
    string displayName,
    string kind,
    VerbeamOptions options,
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
    VerbeamOptions options,
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
    VerbeamOptions options,
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

    if (provider.Name.Equals("oneocr", StringComparison.OrdinalIgnoreCase))
    {
        var available = OneOcrProvider.TryProbeAvailability(out var probeNote);
        return new OcrEngineDescriptor(
            provider.Name,
            provider.DisplayName,
            provider.Kind,
            provider.DefaultLanguage,
            available,
            provider.Name.Equals(options.Ocr.DefaultProvider, StringComparison.OrdinalIgnoreCase),
            provider.RequiresExternalProcess,
            provider.IsLocal,
            "windows",
            available ? "available" : "unavailable",
            RequiresApiConfiguration: false,
            $"Uses the installed Microsoft Snipping Tool OneOCR runtime in-process; Verbeam does not bundle the DLL or model. {probeNote}".Trim())
        {
            SupportedLanguages = provider.SupportedLanguages
        };
    }

    if (provider.Name.Equals("windows", StringComparison.OrdinalIgnoreCase))
    {
        var available = WindowsMediaOcrProvider.TryProbeAvailability(out var probeNote);
        return new OcrEngineDescriptor(
            provider.Name,
            provider.DisplayName,
            provider.Kind,
            provider.DefaultLanguage,
            available,
            provider.Name.Equals(options.Ocr.DefaultProvider, StringComparison.OrdinalIgnoreCase),
            provider.RequiresExternalProcess,
            provider.IsLocal,
            "windows",
            available ? "available" : "unavailable",
            RequiresApiConfiguration: false,
            $"In-process Windows.Media.Ocr; no process spawn per frame. {probeNote}".Trim());
    }

    if (provider.Name.Equals(AppleVisionOcrProvider.ProviderName, StringComparison.OrdinalIgnoreCase))
    {
        var available = AppleVisionOcrProvider.TryProbeAvailability(
            contentRootPath, configuredPath: null, out _, out var probeNote);
        return new OcrEngineDescriptor(
            provider.Name,
            provider.DisplayName,
            provider.Kind,
            provider.DefaultLanguage,
            available,
            provider.Name.Equals(options.Ocr.DefaultProvider, StringComparison.OrdinalIgnoreCase),
            provider.RequiresExternalProcess,
            provider.IsLocal,
            "macos",
            available ? "available" : "unavailable",
            RequiresApiConfiguration: false,
            $"macOS Apple Vision (VNRecognizeText) via the Swift helper; NPU-accelerated, reads CJK + ru/uk/th/vi. {probeNote}".Trim())
        {
            SupportedLanguages = provider.SupportedLanguages
        };
    }

    if (provider.Name.StartsWith("rapidocr-net", StringComparison.OrdinalIgnoreCase) &&
        providers.GetRequired(provider.Name) is RapidOcrNetProvider rapidOcrNetProvider)
    {
        var status = await rapidOcrNetProvider.CheckAsync(cancellationToken);
        var statusText = string.IsNullOrWhiteSpace(status.Status)
            ? status.IsAvailable ? "available" : "missing_dependency"
            : status.Status;
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
            statusText,
            RequiresApiConfiguration: false,
            $"Native .NET ONNX RapidOCR provider; no Python worker. {status.Note}{missing}".Trim())
        {
            SupportedLanguages = provider.SupportedLanguages
        };
    }

    if (IsLocalOcrSetEngine(provider.Name) &&
        providers.GetRequired(provider.Name) is LocalPythonOcrProvider localProvider)
    {
        var status = await localProvider.CheckAsync(cancellationToken);
        var statusText = string.IsNullOrWhiteSpace(status.Status)
            ? status.IsAvailable ? "available" : "missing_dependency"
            : status.Status;
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
            statusText,
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
       name.Equals("rapidocr-ppocrv5", StringComparison.OrdinalIgnoreCase) ||
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
        "rapidocr-ppocrv5" => "Local RapidOCR/ONNX text-line provider for low-latency dialogue and prose.",
        "easyocr" => "Local EasyOCR provider.",
        "paddleocr" => "Local PaddleOCR text provider; not a structure OCR path.",
        "pix2text" => "Local Pix2Text provider for math/table-like document extraction.",
        "pp-structure-v3" => "Local PP-StructureV3 provider for layout, tables, and formulas.",
        "paddleocr-vl" => "Local PaddleOCR-VL provider for high-precision document structure OCR.",
        "dots-ocr" => "Local dots.ocr-compatible VLM provider; large model weights may be downloaded on first run.",
        _ => string.Empty
    };

static IReadOnlyList<OcrEngineDescriptor> BuildPlannedOcrSet(VerbeamOptions options)
{
    var defaultLanguage = options.Ocr.DefaultLanguage;
    return
    [
        new OcrEngineDescriptor(
            "oneocr",
            "Snipping Tool OCR (OneOCR)",
            "local-native",
            defaultLanguage,
            IsAvailable: false,
            IsDefault: false,
            RequiresExternalProcess: false,
            IsLocal: true,
            "windows",
            "unavailable",
            RequiresApiConfiguration: false,
            "Uses the installed Microsoft Snipping Tool OneOCR runtime in-process; Verbeam does not bundle the DLL or model. Install or update Snipping Tool to enable this provider."),
        new OcrEngineDescriptor(
            AppleVisionOcrProvider.ProviderName,
            "Apple Vision (macOS)",
            "local-native",
            defaultLanguage,
            IsAvailable: false,
            IsDefault: false,
            RequiresExternalProcess: true,
            IsLocal: true,
            "macos",
            "unavailable",
            RequiresApiConfiguration: false,
            "macOS-only realtime OCR via Apple's Vision framework (NPU-accelerated; reads CJK + ru/uk/th/vi). Build app/ocr-helpers/vision-ocr on a Mac to enable."),
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

static string EncodePngBase64(Bitmap bitmap)
{
    using var stream = new MemoryStream();
    bitmap.Save(stream, ImageFormat.Png);
    return Convert.ToBase64String(stream.ToArray());
}

static IResult SelectorBlockedResult(RegionSelectionSafetySnapshot safety)
    => Results.BadRequest(new
    {
        errorCode = "native_region_selector_blocked",
        errorMessage = safety.Message,
        selectionSafety = safety
    });

static async Task<IResult> TriggerHotkeyActionAsync(
    HotkeyAction action,
    NativeRegionService engine,
    GameProfileStore profiles,
    RegionSelectionSafety selectionSafety,
    CancellationToken cancellationToken)
{
    switch (action)
    {
        case HotkeyAction.Snapshot:
            var safety = selectionSafety.Check();
            if (!safety.CanOpenSelector)
            {
                var before = engine.Status();
                if (before.RegionCount > 0)
                {
                    await engine.SnapshotAsync(reselect: false);
                    return Results.Ok(new
                    {
                        action = HotkeySettingsService.ActionId(action),
                        status = engine.Status(),
                        selectionSafety = safety,
                        fullscreenSafeFallback = true
                    });
                }

                return SelectorBlockedResult(safety);
            }

            await engine.SnapshotAsync(reselect: true);
            return Results.Ok(new { action = HotkeySettingsService.ActionId(action), status = engine.Status(), selectionSafety = safety });
        case HotkeyAction.ToggleLoop:
            engine.ToggleLoop();
            return Results.Ok(new { action = HotkeySettingsService.ActionId(action), status = engine.Status() });
        case HotkeyAction.ToggleOverlays:
            engine.ToggleOverlays();
            return Results.Ok(new { action = HotkeySettingsService.ActionId(action), status = engine.Status() });
        case HotkeyAction.CaptureRegions:
            return await CaptureActiveProfileRegionsAsync(engine, profiles, selectionSafety, cancellationToken);
        case HotkeyAction.NextProfile:
            return await CycleProfileAsync(1, engine, profiles, cancellationToken);
        case HotkeyAction.PrevProfile:
            return await CycleProfileAsync(-1, engine, profiles, cancellationToken);
        default:
            return Results.BadRequest(new { errorCode = "unknown_hotkey_action", errorMessage = $"Unknown hotkey action '{action}'." });
    }
}

static async Task<IResult> CaptureActiveProfileRegionsAsync(
    NativeRegionService engine,
    GameProfileStore profiles,
    RegionSelectionSafety selectionSafety,
    CancellationToken cancellationToken)
{
    var active = engine.ActiveProfile;
    if (active is null)
    {
        return Results.BadRequest(new { errorCode = "no_active_profile", errorMessage = "Activate a game profile first." });
    }

    if (!engine.TryGetActiveSurfaceBounds(out _))
    {
        return Results.BadRequest(new { errorCode = "window_not_found", errorMessage = $"Can't find the window for '{active.Name}'." });
    }

    var safety = selectionSafety.Check();
    if (!safety.CanOpenSelector)
    {
        return SelectorBlockedResult(safety);
    }

    var regions = engine.CaptureRegionsNormalized();
    if (regions is null || regions.Count == 0)
    {
        return Results.Ok(new { action = HotkeySettingsService.ActionId(HotkeyAction.CaptureRegions), status = engine.Status() });
    }

    var saved = await profiles.UpsertAsync(active with { Regions = regions }, cancellationToken);
    engine.ApplyProfile(saved);
    return Results.Ok(new { action = HotkeySettingsService.ActionId(HotkeyAction.CaptureRegions), status = engine.Status() });
}

static async Task<IResult> CycleProfileAsync(
    int direction,
    NativeRegionService engine,
    GameProfileStore profiles,
    CancellationToken cancellationToken)
{
    var document = await profiles.GetDocumentAsync(cancellationToken);
    var list = document.Profiles;
    if (list.Count == 0)
    {
        return Results.BadRequest(new { errorCode = "no_game_profiles", errorMessage = "No game profiles to switch." });
    }

    var activeId = engine.ActiveProfile?.Id;
    if (string.IsNullOrWhiteSpace(activeId))
    {
        activeId = document.ActiveId;
    }

    var index = -1;
    for (var i = 0; i < list.Count; i++)
    {
        if (string.Equals(list[i].Id, activeId, StringComparison.OrdinalIgnoreCase))
        {
            index = i;
            break;
        }
    }

    var next = index < 0
        ? (direction > 0 ? 0 : list.Count - 1)
        : (((index + direction) % list.Count) + list.Count) % list.Count;
    var profile = list[next];
    await profiles.SetActiveAsync(profile.Id, cancellationToken);
    engine.ApplyProfile(profile);
    return Results.Ok(new
    {
        action = direction > 0
            ? HotkeySettingsService.ActionId(HotkeyAction.NextProfile)
            : HotkeySettingsService.ActionId(HotkeyAction.PrevProfile),
        profile = new { profile.Id, profile.Name },
        status = engine.Status()
    });
}

static object BuildMemorySummary()
{
    var rows = CollectCurrentProcessTreeMemoryRows();
    var total = BuildMemoryGroup("total", rows);
    var app = BuildMemoryGroup("app", rows.Where(row => row.Category == "app"));
    var webView = BuildMemoryGroup("webView", rows.Where(row => row.Category == "webView"));
    var model = BuildMemoryGroup("model", rows.Where(row => row.Category == "model"));
    var other = BuildMemoryGroup("other", rows.Where(row => row.Category == "other"));
    var gpu = BuildGpuMemorySummary(rows.Select(row => row.ProcessId).ToHashSet());

    return new
    {
        updatedAt = DateTimeOffset.UtcNow,
        total,
        app,
        webView,
        model,
        other,
        gpu,
        processes = rows
            .OrderByDescending(row => row.WorkingSetBytes)
            .Select(row => new
            {
                pid = row.ProcessId,
                parentPid = row.ParentProcessId,
                name = row.Name,
                category = row.Category,
                workingSetMb = BytesToMiB(row.WorkingSetBytes),
                privateMb = BytesToMiB(row.PrivateBytes)
            })
            .ToArray(),
        note = "Working set is physical RAM currently resident. Private memory is committed address space and can be larger than RAM."
    };
}

static object BuildMemoryGroup(string id, IEnumerable<MemoryProcessRow> rows)
{
    var list = rows.ToArray();
    return new
    {
        id,
        processCount = list.Length,
        workingSetMb = BytesToMiB(list.Sum(row => row.WorkingSetBytes)),
        privateMb = BytesToMiB(list.Sum(row => row.PrivateBytes))
    };
}

static object BuildGpuMemorySummary(IReadOnlySet<int> trackedProcessIds)
{
    try
    {
        var gpuLines = RunNvidiaSmi("--query-gpu=name,memory.used,memory.total --format=csv,noheader,nounits");
        if (gpuLines.Length == 0)
        {
            return new { available = false, reason = "no_gpu_rows", usedMb = 0d, totalMb = 0d, trackedProcessMb = 0d, devices = Array.Empty<object>(), processes = Array.Empty<object>() };
        }

        var devices = new List<object>();
        double usedMb = 0;
        double totalMb = 0;
        foreach (var line in gpuLines)
        {
            var parts = line.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 3 ||
                !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var used) ||
                !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var total))
            {
                continue;
            }

            usedMb += used;
            totalMb += total;
            devices.Add(new
            {
                name = parts[0],
                usedMb = Math.Round(used, 1),
                totalMb = Math.Round(total, 1),
                percent = total > 0 ? Math.Round(used / total * 100d, 1) : 0d
            });
        }

        var gpuProcesses = new List<(object Row, double UsedMb)>();
        double trackedProcessMb = 0;
        foreach (var line in RunNvidiaSmi("--query-compute-apps=pid,process_name,used_memory --format=csv,noheader,nounits", allowFailure: true))
        {
            var parts = line.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 3 ||
                !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid) ||
                !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var memoryMb))
            {
                continue;
            }

            var tracked = trackedProcessIds.Contains(pid);
            if (tracked)
            {
                trackedProcessMb += memoryMb;
            }

            gpuProcesses.Add((new
            {
                pid,
                name = parts[1],
                usedMb = Math.Round(memoryMb, 1),
                tracked
            }, memoryMb));
        }

        return new
        {
            available = devices.Count > 0,
            reason = devices.Count > 0 ? "" : "parse_empty",
            usedMb = Math.Round(usedMb, 1),
            totalMb = Math.Round(totalMb, 1),
            percent = totalMb > 0 ? Math.Round(usedMb / totalMb * 100d, 1) : 0d,
            trackedProcessMb = Math.Round(trackedProcessMb, 1),
            devices,
            processes = gpuProcesses.OrderByDescending(process => process.UsedMb).Select(process => process.Row).ToArray()
        };
    }
    catch (Exception ex) when (ex is Win32Exception or FileNotFoundException or TimeoutException or InvalidOperationException)
    {
        return new { available = false, reason = "nvidia_smi_unavailable", message = ex.Message, usedMb = 0d, totalMb = 0d, trackedProcessMb = 0d, devices = Array.Empty<object>(), processes = Array.Empty<object>() };
    }
}

static string[] RunNvidiaSmi(string arguments, bool allowFailure = false)
{
    var executable = ResolveNvidiaSmiPath();
    if (string.IsNullOrWhiteSpace(executable))
    {
        throw new FileNotFoundException("nvidia-smi was not found.");
    }

    using var process = new Process
    {
        StartInfo = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        }
    };

    if (!process.Start())
    {
        throw new InvalidOperationException("nvidia-smi did not start.");
    }

    if (!process.WaitForExit(1500))
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best effort cleanup after a diagnostics timeout.
        }

        throw new TimeoutException("nvidia-smi timed out.");
    }

    var output = process.StandardOutput.ReadToEnd();
    var error = process.StandardError.ReadToEnd();
    if (process.ExitCode != 0)
    {
        if (allowFailure)
        {
            return Array.Empty<string>();
        }

        throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "nvidia-smi failed." : error.Trim());
    }

    return output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

static string ResolveNvidiaSmiPath()
{
    const string executable = "nvidia-smi.exe";
    var systemPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", executable);
    if (File.Exists(systemPath))
    {
        return systemPath;
    }

    var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
    foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        var candidate = Path.Combine(entry, executable);
        if (File.Exists(candidate))
        {
            return candidate;
        }
    }

    return string.Empty;
}

static double BytesToMiB(long bytes)
    => Math.Round(bytes / 1024d / 1024d, 1);

static IReadOnlyList<MemoryProcessRow> CollectCurrentProcessTreeMemoryRows()
{
    var current = Process.GetCurrentProcess();
    var parentByPid = OperatingSystem.IsWindows()
        ? WindowsProcessTreeSnapshot.GetParentProcessMap()
        : new Dictionary<int, int>();
    var processByPid = Process.GetProcesses().ToDictionary(process => process.Id);
    var ids = new HashSet<int> { current.Id };

    var changed = true;
    while (changed)
    {
        changed = false;
        foreach (var (pid, parentPid) in parentByPid)
        {
            if (ids.Contains(parentPid) && ids.Add(pid))
            {
                changed = true;
            }
        }
    }

    var rows = new List<MemoryProcessRow>();
    foreach (var id in ids.OrderBy(id => id))
    {
        if (!processByPid.TryGetValue(id, out var process))
        {
            continue;
        }

        try
        {
            process.Refresh();
            var name = process.ProcessName;
            var parentPid = parentByPid.TryGetValue(id, out var parent) ? parent : 0;
            rows.Add(new MemoryProcessRow(
                id,
                parentPid,
                name,
                CategorizeMemoryProcess(id, current.Id, name),
                process.WorkingSet64,
                process.PrivateMemorySize64));
        }
        catch
        {
            // Processes can exit while the snapshot is being collected.
        }
    }

    return rows;
}

static string CategorizeMemoryProcess(int processId, int currentProcessId, string processName)
{
    if (processId == currentProcessId)
    {
        return "app";
    }

    var name = processName.ToLowerInvariant();
    if (name is "msedgewebview2")
    {
        return "webView";
    }

    if (name.Contains("llama", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("ollama", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("kobold", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("vllm", StringComparison.OrdinalIgnoreCase))
    {
        return "model";
    }

    return "other";
}

internal sealed record ScreenCaptureResponse(
    string ImageBase64,
    string ImageMimeType,
    string FileName,
    int Width,
    int Height,
    int X,
    int Y);

internal sealed record LiveSocketMessage(WebSocketMessageType MessageType, byte[] Payload);

internal sealed record FunAsrHealthProbe(
    bool Reachable,
    bool Ready,
    string Status,
    string ErrorMessage,
    DateTimeOffset ProbedAt);

internal sealed record LiveSpeechWorkItem(
    long Sequence,
    string SegmentId,
    byte[] Pcm,
    LiveSpeechConfig Config,
    double StartSeconds,
    double EndSeconds,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt);

internal sealed record OcrTableRenderCell(
    OcrTableCell Cell,
    int RowSpan,
    int ColumnSpan,
    bool IsPlaceholder);

internal sealed record RequestMemoryIdentity(
    string Principal,
    IReadOnlyList<string> Groups,
    bool IsExternal);

internal sealed record BearerJwtValidationResult(
    bool Valid,
    string Principal,
    IReadOnlyList<string> Groups)
{
    public static BearerJwtValidationResult Invalid { get; } = new(false, string.Empty, []);
}

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

internal sealed record MemoryProcessRow(
    int ProcessId,
    int ParentProcessId,
    string Name,
    string Category,
    long WorkingSetBytes,
    long PrivateBytes);

internal static class WindowsProcessTreeSnapshot
{
    private const uint SnapshotProcess = 0x00000002;

    public static Dictionary<int, int> GetParentProcessMap()
    {
        var result = new Dictionary<int, int>();
        var snapshot = CreateToolhelp32Snapshot(SnapshotProcess, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
        {
            return result;
        }

        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32First(snapshot, ref entry))
            {
                return result;
            }

            do
            {
                result[(int)entry.ProcessId] = (int)entry.ParentProcessId;
            } while (Process32Next(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return result;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint ThreadCount;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
