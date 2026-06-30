namespace Verbeam.Core.Options;

public sealed class VerbeamOptions
{
    public string DefaultProvider { get; set; } = "ollama";
    public string DefaultMode { get; set; } = "game_dialogue";
    public string DefaultSource { get; set; } = "ja";
    public string DefaultTarget { get; set; } = "zh-TW";

    /// <summary>When true, zh-CN&lt;-&gt;zh-TW variant conversion uses the deterministic
    /// OpenCC converter (0ms, faithful). Set false to force those through the LLM
    /// instead (e.g. for benchmarking model latency/coverage on Chinese content).</summary>
    public bool UseOpenCcVariantConversion { get; set; } = true;
    public string PresetsDirectory { get; set; } = "presets";
    public string GlossariesDirectory { get; set; } = "glossaries";
    public string ModelCatalogPath { get; set; } = "models.catalog.json";
    public ModelCatalogOptions ModelCatalog { get; set; } = new();
    public ApiSupplierOptions ApiSuppliers { get; set; } = new();
    public string CachePath { get; set; } = "data/translations.sqlite";

    /// <summary>Physical database partition layout (per-function + per-game file split).
    /// See <see cref="DatabaseRoutingOptions"/> and rag-db-partition-design.</summary>
    public DatabaseRoutingOptions Database { get; set; } = new();
    public string GameProfilesPath { get; set; } = "data/game-profiles.json";
    public OllamaOptions Ollama { get; set; } = new();
    public LlamaCppOptions LlamaCpp { get; set; } = new();
    public DeepLOptions DeepL { get; set; } = new();
    public HybridTranslationOptions Hybrid { get; set; } = new();
    public OcrOptions Ocr { get; set; } = new();
    public SpeechOptions Speech { get; set; } = new();
    public DocumentOptions Document { get; set; } = new();
    public TranslationChunkingOptions Chunking { get; set; } = new();
    public ContextCompressionOptions ContextCompression { get; set; } = new();
    public MemoryOptions Memory { get; set; } = new();
    public TrayOptions Tray { get; set; } = new();
    public HotkeyOptions Hotkeys { get; set; } = new();
    public ShellOptions Shell { get; set; } = new();
}

/// <summary>
/// Physical database partition layout (see rag-db-partition-design). Splits the single
/// shared SQLite into per-function files plus per-game realtime files under
/// <see cref="DataDirectory"/>. The whole schema is created in every file; tables a
/// given file does not use stay empty. Flip a function domain onto the per-game axis via
/// its *PerGame flag without touching <see cref="Storage.DatabaseRouter"/>.
/// </summary>
public sealed class DatabaseRoutingOptions
{
    /// <summary>Base directory for all partitioned database files. Empty = derive from the
    /// legacy <see cref="VerbeamOptions.CachePath"/> directory at startup.</summary>
    public string DataDirectory { get; set; } = string.Empty;

    /// <summary>Global file: profiles registry, principals/auth/oidc.</summary>
    public string CoreFile { get; set; } = "core.sqlite";

    /// <summary>Document/PDF editor file (unless <see cref="DocumentPerGame"/>).</summary>
    public string DocumentFile { get; set; } = "document.sqlite";

    /// <summary>Speech file (unless <see cref="SpeechPerGame"/>).</summary>
    public string SpeechFile { get; set; } = "speech.sqlite";

    /// <summary>Cross-game OCR image-hash dedup cache file.</summary>
    public string OcrCacheFile { get; set; } = "ocr-cache.sqlite";

    /// <summary>Per-game realtime file name, placed under {GamesSubdirectory}/{gameId}/.</summary>
    public string RealtimeFile { get; set; } = "realtime.sqlite";

    /// <summary>Subdirectory (under <see cref="DataDirectory"/>) holding the per-game folders.</summary>
    public string GamesSubdirectory { get; set; } = "games";

    /// <summary>Move document data onto the per-game axis (default: shared document file).</summary>
    public bool DocumentPerGame { get; set; }

    /// <summary>Move speech data onto the per-game axis (default: shared speech file).</summary>
    public bool SpeechPerGame { get; set; }
}

/// <summary>
/// Global tray hotkey bindings (registered with RegisterHotKey). Each value is a "Mod+Mod+Key"
/// spec ??modifiers are Alt/Shift/Ctrl(Control)/Win(Windows) and the key is a
/// System.Windows.Forms.Keys name (R, F1, OemPeriod??. Rebind via appsettings "Verbeam:Hotkeys";
/// a blank value disables that action's hotkey. A binding that collides with another app (or an
/// already-running Verbeam instance) is skipped, not fatal.
/// </summary>
public sealed class HotkeyOptions
{
    public string SettingsPath { get; set; } = "data/hotkeys.json";
    public string Snapshot { get; set; } = "Ctrl+Alt+Shift+R";
    public string ToggleLoop { get; set; } = "Alt+Shift+L";
    public string CaptureRegions { get; set; } = "Alt+Shift+C";
    public string NextProfile { get; set; } = "Alt+Shift+N";
    public string PrevProfile { get; set; } = "Alt+Shift+P";
    public string ToggleOverlays { get; set; } = "Alt+Shift+H";
}

public sealed class ShellOptions
{
    public string SettingsPath { get; set; } = "data/shell-settings.json";

    /// <summary>
    /// WebView2 Chromium rendering mode. balanced keeps capture previews reliable while reducing
    /// GPU compositing pressure; saver is for native-region-first users who need maximum VRAM back.
    /// </summary>
    public string WebView2GpuMode { get; set; } = "balanced";

    /// <summary>Optional extra Chromium flags appended after the selected mode's defaults.</summary>
    public string WebView2AdditionalArgs { get; set; } = string.Empty;

    /// <summary>Browser getDisplayMedia capture quality for the in-window Region preview.</summary>
    public string BrowserRegionQuality { get; set; } = "balanced";
}

/// <summary>
/// Long-text auto-chunking for single /translate calls. When a non-realtime request's
/// text exceeds <see cref="MaxCharactersPerChunk"/>, <see cref="Services.TranslationService"/>
/// splits it on paragraph/sentence boundaries, translates each chunk through the normal
/// pipeline (cache, memory, glossary), and rejoins ??so a long document is never silently
/// truncated by the model's context window or output-token cap.
/// </summary>
public sealed class TranslationChunkingOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Maximum source characters per chunk. Sized so one chunk's prompt + translation
    /// fits comfortably inside the local model's context window and output-token cap
    /// (see Ollama.NumContext/NumPredict and the llama.cpp profile contextSize/maxTokens).
    /// Text at or below this length is translated in a single pass.
    /// </summary>
    public int MaxCharactersPerChunk { get; set; } = 800;

    /// <summary>
    /// Maximum chunks translated concurrently for one non-realtime request. 1 keeps
    /// the original sequential behaviour. Streaming requests stay sequential so
    /// token deltas are emitted in source order.
    /// </summary>
    public int MaxConcurrency { get; set; } = 4;
}

/// <summary>
/// Document translation job settings. See <see cref="Services.DocumentJobService"/>.
/// </summary>
public sealed class DocumentOptions
{
    /// <summary>
    /// Maximum translation requests issued concurrently per document unit batch
    /// (text lines, OOXML paragraphs, OCR blocks). 1 reproduces the original
    /// sequential behaviour; raise it to overlap latency for API providers. A
    /// single local model still serializes, so keep this modest there.
    /// </summary>
    public int TranslationConcurrency { get; set; } = 4;

    /// <summary>
    /// When true, consecutive non-blank markdown lines are merged into one
    /// paragraph translation unit. When false each line is translated separately.
    /// </summary>
    public bool MergeMarkdownParagraphs { get; set; } = true;

    /// <summary>
    /// pdf2zh_next console script for the high-fidelity "auto" PDF export engine, resolved
    /// relative to the content root. Installed in its own venv (no torch; ONNX DocLayout-YOLO).
    /// </summary>
    public string Pdf2zhExecutable { get; set; } = "../../.pdf2zh/venv/Scripts/pdf2zh.exe";

    /// <summary>Pre-generated offline-assets directory (or zip) passed as --restore-offline-assets
    /// when present, so pdf2zh never downloads at runtime. Generate with
    /// <c>babeldoc --generate-offline-assets &lt;dir&gt;</c>. Falls back to pdf2zh's cache if absent.</summary>
    public string Pdf2zhOfflineAssets { get; set; } = "../../.pdf2zh/offline-assets";

    /// <summary>Hard timeout for one pdf2zh export run (layout detect + render can be slow).</summary>
    public int Pdf2zhTimeoutSeconds { get; set; } = 600;
}

public sealed class TrayOptions
{
    public bool Enabled { get; set; } = false;

    /// <summary>Legacy fixed loop interval; superseded by the two-tier watcher settings below.</summary>
    public int LoopIntervalMs { get; set; } = 700;

    /// <summary>Cheap frame-sampling cadence of the loop watcher.</summary>
    public int WatchTickMs { get; set; } = 150;

    /// <summary>Consecutive unchanged ticks required before a changed frame is OCR'd (filters half-rendered fade-ins).</summary>
    public int StableTicks { get; set; } = 1;

    /// <summary>Minimum time between OCR passes while changes keep arriving.</summary>
    public int MinOcrGapMs { get; set; } = 300;

    /// <summary>Steady-cadence re-OCR: the downsampled sampler misses similar-looking
    /// subtitle changes, so re-OCR at least this often (text dedup keeps static frames cheap).
    /// ~450ms with rapidocr-net (~25ms) + OpenCC (~0ms) catches subtitles living >0.45s.</summary>
    public int ForceRefreshMs { get; set; } = 450;

    public string OcrLanguage { get; set; } = string.Empty;
    public string TranslationProvider { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
}

public sealed class ModelCatalogOptions
{
    public string CachePath { get; set; } = "data/model-catalog.cache.json";
    public string UpdateUrl { get; set; } = string.Empty;
    public bool RemoteRefreshEnabled { get; set; } = false;
    public int RefreshTimeoutSeconds { get; set; } = 10;
}

public sealed class ApiSupplierOptions
{
    public string CatalogPath { get; set; } = "api-suppliers.catalog.json";
    public string CachePath { get; set; } = "data/api-suppliers.catalog.cache.json";
    public string StorePath { get; set; } = "data/api-suppliers.json";
    public string SecretsPath { get; set; } = "data/api-supplier-secrets.json";
    public string RoutesPath { get; set; } = "data/translation-routes.json";
    public string UpdateUrl { get; set; } = string.Empty;
    public bool RemoteRefreshEnabled { get; set; } = false;
    public int RefreshTimeoutSeconds { get; set; } = 10;
    public int DiscoveryTimeoutSeconds { get; set; } = 15;
    public int RequestTimeoutSeconds { get; set; } = 30;
    public int MaxTokens { get; set; } = 256;
    public double Temperature { get; set; } = 0;
}

public sealed class OllamaOptions
{
    public string BaseUrl { get; set; } = "http://localhost:11434";
    public bool AutoStart { get; set; } = false;
    public string ExecutablePath { get; set; } = string.Empty;
    public string ModelsDirectory { get; set; } = string.Empty;
    public string Model { get; set; } = "verbeam-mort-qwen2.5-0.5b:latest";
    public string[] Models { get; set; } = [];
    public int ModelDiscoveryTimeoutSeconds { get; set; } = 2;
    public int StartupTimeoutSeconds { get; set; } = 20;
    public int TimeoutSeconds { get; set; } = 60;
    public int NumContext { get; set; } = 4096;
    public int NumPredict { get; set; } = 1024;
    public double Temperature { get; set; } = 0;
    public string KeepAlive { get; set; } = "30m";
}

public sealed class LlamaCppOptions
{
    public string Mode { get; set; } = "remote";
    public string BaseUrl { get; set; } = "http://localhost:8088/v1";
    public string Model { get; set; } = "verbeam-mort-qwen2.5-0.5b";
    public string Profile { get; set; } = "realtime-ocr";
    public string ExecutablePath { get; set; } = "llama-server";
    public string ModelsDirectory { get; set; } = "models/llama-cpp";
    public string BinariesDirectory { get; set; } = DefaultBinariesDirectory();
    public string SlotSaveDirectory { get; set; } = "data/llama-cpp-slots";
    public string RuntimeSettingsPath { get; set; } = "data/llama-cpp-runtime.json";
    public string PinnedVersion { get; set; } = "b9590";

    /// <summary>"auto" detects the host GPU and picks cuda/hip/vulkan/metal/cpu;
    /// an explicit flavor (e.g. "vulkan", "cuda") forces that backend.</summary>
    public string BinaryFlavor { get; set; } = "auto";

    /// <summary>Optional manual GPU index override; null = auto-pick the discrete GPU via --list-devices.</summary>
    public int? DeviceIndex { get; set; }

    /// <summary>
    /// Which compute device the translation LLM should target. "auto" (default) picks
    /// the best discrete GPU; "integrated" forces the Vulkan backend onto the
    /// integrated GPU so a game/video keeps the discrete card free (no GPU contention,
    /// at lower throughput); "cpu" runs on the CPU backend. Overrides
    /// <see cref="BinaryFlavor"/> and the device pick when not "auto".
    /// </summary>
    public string ComputeTarget { get; set; } = "auto";
    public int PortStart { get; set; } = 8088;
    public int PortEnd { get; set; } = 8098;
    public int IdleTimeoutSeconds { get; set; } = 300;
    public int StartupTimeoutSeconds { get; set; } = 20;
    public int RequestTimeoutSeconds { get; set; } = 20;

    private static string DefaultBinariesDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(localAppData)
            ? Path.Combine("runtimes", "llama-cpp")
            : Path.Combine(localAppData, "Verbeam", "runtimes", "llama-cpp");
    }
}

public sealed class DeepLOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public bool UseFreeApi { get; set; }
    public string ModelType { get; set; } = "default";
    public int RequestTimeoutSeconds { get; set; } = 30;
}

/// <summary>
/// Settings for the "hybrid" translation provider: run <see cref="LocalProvider"/> first and only
/// fall back to <see cref="CloudProvider"/> when the local call exceeds <see cref="DeadlineMs"/>
/// (a decode spike). Privacy-preserving by default ??only slow lines reach the cloud.
/// </summary>
public sealed class HybridTranslationOptions
{
    public string LocalProvider { get; set; } = "llama-cpp";
    public string CloudProvider { get; set; } = "deepl";
    public int DeadlineMs { get; set; } = 700;
}

public sealed class OcrOptions
{
    public string DefaultProvider { get; set; } = "external";
    public string DefaultLanguage { get; set; } = "auto";
    public int MaxImageBytes { get; set; } = 4 * 1024 * 1024;
    public bool NormalizeWhitespace { get; set; } = true;

    /// <summary>When an OCR pass returns no text at all (e.g. rapidocr-net's detector misses
    /// low-contrast / vertical text), retry once with OneOCR, which reads those cases. Realtime
    /// frames are gated separately by FallbackToOneOcrOnEmptyRealtime; also a no-op when OneOCR is
    /// already the engine, or when OneOCR is unavailable (non-Windows / missing model).</summary>
    public bool FallbackToOneOcrOnEmpty { get; set; } = true;

    /// <summary>Allow the empty-result OneOCR fallback on REALTIME frames too (region live OCR): a
    /// low-contrast subtitle rapidocr-net cannot detect (0 blocks) is re-read with OneOCR (~220 ms,
    /// in-process). Only fires on the failing frames, so steady frames keep the fast incremental path.</summary>
    public bool FallbackToOneOcrOnEmptyRealtime { get; set; } = true;

    public OcrConcurrencyOptions Concurrency { get; set; } = new();
    public OcrPreprocessingOptions Preprocessing { get; set; } = new();
    public OcrAutoDetectionOptions AutoDetection { get; set; } = new();
    public OcrRefinementOptions Refinement { get; set; } = new();
    public OcrShadowRepairOptions ShadowRepair { get; set; } = new();
    public ExternalOcrOptions External { get; set; } = new();
    public RapidOcrNetOptions RapidOcrNet { get; set; } = new();
    public RapidOcrNetOptions RapidOcrNetV6 { get; set; } = new();
    public LocalOcrSetOptions LocalSet { get; set; } = new();
    public OcrRealtimeAutoSuppressOptions RealtimeAutoSuppress { get; set; } = new();
}

/// <summary>
/// Low-contrast subtitle repair fallback for white text on bright backgrounds.
/// It is intentionally a repair path, not the default OCR input.
/// </summary>
public sealed class OcrShadowRepairOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Enables a narrow realtime repair path for low-contrast Latin subtitle strips.
    /// Realtime uses only one main-band CLAHE candidate and the preferred provider.
    /// </summary>
    public bool RealtimeEnabled { get; set; } = true;

    public string PreferredProvider { get; set; } = "oneocr";
    public string RealtimePreferredProvider { get; set; } = "rapidocr-net";
    public int Scale { get; set; } = 3;
    public int RealtimeScale { get; set; } = 3;
    public int MaxRepairedPixels { get; set; } = 2_500_000;
    public double MinAspectRatio { get; set; } = 3.0;
    public int MaxOriginalHeight { get; set; } = 720;
    public double CropTopRatio { get; set; } = 0.04;
    public double CropBottomRatio { get; set; } = 0.78;
    public double RealtimeCropTopRatio { get; set; } = 0.04;
    public double RealtimeCropBottomRatio { get; set; } = 0.78;
    public double TriggerAverageConfidence { get; set; } = 0.72;
    public double MinQualityGain { get; set; } = 4.0;
}

/// <summary>
/// Automatic watermark suppression for realtime (region) OCR sessions: text that
/// keeps reappearing across distinct frames far longer than a subtitle dwells is
/// dropped from realtime OCR responses. See <see cref="Services.RecurringTextSuppressor"/>.
/// </summary>
public sealed class OcrRealtimeAutoSuppressOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Sliding window size, in distinct frames, used to judge recurrence.</summary>
    public int WindowFrames { get; set; } = 20;

    /// <summary>Fraction of the window a text must appear in to be flagged.</summary>
    public double PresenceRatio { get; set; } = 0.8;

    /// <summary>Minimum lifetime in seconds before any text can be flagged (subtitles dwell ~5s).</summary>
    public double MinAgeSeconds { get; set; } = 15;

    /// <summary>Bigram Dice similarity above which two OCR readings count as the same text.</summary>
    public double Similarity { get; set; } = 0.6;

    /// <summary>A tracked text unseen for this long is forgotten (and unflagged).</summary>
    public double ClusterExpireSeconds { get; set; } = 60;

    /// <summary>Idle time after which a session's tracking state is evicted.</summary>
    public double SessionIdleExpireSeconds { get; set; } = 600;

    public int MaxClustersPerSession { get; set; } = 64;
    public int MaxSessions { get; set; } = 16;
}

public sealed class RapidOcrNetOptions
{
    public string DetModelPath { get; set; } = string.Empty;
    public string ClsModelPath { get; set; } = string.Empty;
    public string RecModelPath { get; set; } = string.Empty;
    public string KeysPath { get; set; } = string.Empty;

    /// <summary>
    /// Optional language-specific recognizer (e.g. japan_PP-OCRv4_rec) run via the
    /// in-process <see cref="Providers.OnnxRecognizer"/> instead of RapidOcrNet's
    /// bundled TextRecognizer (which mis-reads PP-OCRv4 recognizers). When set with
    /// <see cref="OnnxRecKeysPath"/>, the realtime path detects with the NuGet det
    /// but recognizes lines with this model. Det stays the layout/L2-amortized engine.
    /// </summary>
    public string OnnxRecModelPath { get; set; } = string.Empty;
    public string OnnxRecKeysPath { get; set; } = string.Empty;

    /// <summary>
    /// ONNX Runtime execution provider for the DETECTION session (DBNet): cpu / dml / cuda.
    /// Detection is the expensive (~90%) stage and tolerates GPU reduced precision (its output
    /// is a thresholded segmentation map), so it is the GPU candidate.
    /// </summary>
    public string ExecutionProvider { get; set; } = "cpu";

    /// <summary>
    /// ONNX Runtime execution provider for the RECOGNITION OnnxRecognizer (CRNN/CTC): cpu / dml / cuda.
    /// Recognition is cheap (~10%) but precision-critical: a CTC greedy argmax with a small margin
    /// between similar kana is corrupted by DirectML's fp16 math (???? ??35 observed), so this
    /// defaults to CPU even when detection runs on the GPU.
    /// </summary>
    public string RecExecutionProvider { get; set; } = "cpu";
    public int DeviceId { get; set; }

    /// <summary>
    /// When the detection EP is DirectML, auto-resolve <see cref="DeviceId"/> to the integrated GPU
    /// (e.g. Intel Iris Xe) instead of hardcoding an index. Offloads OCR off the discrete NVIDIA card
    /// so its VRAM stays free for the translation model (and the small det workload often runs faster
    /// on the iGPU's unified memory). Falls back to <see cref="DeviceId"/> on single-GPU machines.
    /// </summary>
    public bool PreferIntegratedGpu { get; set; }

    /// <summary>
    /// Runs a throwaway detect (+ recognize) at init so DirectML's per-input-shape kernel
    /// compilation (~20s the first time) happens at startup instead of on the user's first
    /// realtime frame. Only meaningful for GPU EPs; harmless (a few ms) on CPU.
    /// </summary>
    public bool WarmupOnInit { get; set; } = true;

    /// <summary>
    /// Detect input shape to pre-compile during warmup. DirectML compiles per exact shape, so
    /// this should match the realtime detect input ??i.e. the captured region after L1 scale-lock
    /// (short side = <see cref="DetTargetShortSide"/>). Width is the region short side.
    /// </summary>
    public int WarmupWidth { get; set; } = 960;
    public int WarmupHeight { get; set; } = 2762;

    /// <summary>
    /// L1 scale-lock: resize the detect input so its short side equals this before detection,
    /// then map result boxes back. PP-OCR detection is strongly scale-sensitive (??60 is the
    /// sweet spot for subtitle-sized text; off-scale collapses), and locking the scale also
    /// pins the DirectML input shape so it only compiles once. 0 disables (use input as-is).
    /// </summary>
    public int DetTargetShortSide { get; set; }

    /// <summary>
    /// Caps the detection model's internal resize (longest side ??this many px) via RapidOcrNet's
    /// <c>RapidOcrOptions.MaxSideLen</c>. det compute scales with this squared, and recognition reads
    /// from the ORIGINAL image (not the det-resized tensor), so lowering this speeds up the
    /// once-per-subtitle full detect without touching recognition accuracy ??as long as det still
    /// finds the (now smaller) text boxes. 0 = leave RapidOcrNet's default.
    /// </summary>
    public int DetMaxSideLen { get; set; }

    public int SessionThreadCount { get; set; }
    public bool UsePythonCompat { get; set; }
    public bool DoAngle { get; set; } = true;

    /// <summary>Enables the incremental layout-cache + batch rec-only fast path for realtime requests.</summary>
    public bool RealtimeIncremental { get; set; } = true;

    /// <summary>
    /// Optional coarse-locator DET model for the realtime two-stage large-frame path: stage-1 finds
    /// text regions with this (fast/sensitive, e.g. PP-OCRv6 tiny), stage-2 refines each region with
    /// the main DetModelPath (e.g. medium) ??the "TINY locate + MEDIUM refine" design. Empty = coarse
    /// locate uses the main engine. Overridden by the VB_OCR_LOCATOR_DET env var. Relative to content root.
    /// </summary>
    public string LocatorDetModelPath { get; set; } = string.Empty;

    /// <summary>Forces a full detect rebuild when the cached layout is older than this.</summary>
    public int RealtimeRedetectIntervalMs { get; set; } = 2000;

    /// <summary>Changed-line ratio above which the layout is considered stale and re-detected.</summary>
    public double RealtimeChangedLineRatio { get; set; } = 0.5;

    /// <summary>Maximum number of concurrent realtime layout sessions kept in memory.</summary>
    public int RealtimeMaxSessions { get; set; } = 8;

    /// <summary>Idle time after which a realtime layout session is evicted.</summary>
    public int RealtimeSessionIdleMs { get; set; } = 10000;

    /// <summary>
    /// Capture-noise tolerance for per-line change signatures (luminance is
    /// quantized to tolerance*8 gray levels before hashing; 0/1 = near-exact).
    /// </summary>
    public int RealtimeHashTolerance { get; set; } = 2;

    /// <summary>
    /// Keeps the previous realtime OCR result for a short window when a repair
    /// full-detect comes back empty after filtering. This hides one-frame
    /// detector misses without delaying a valid newly detected sentence.
    /// </summary>
    public int RealtimeTransientHoldMs { get; set; } = 900;

    /// <summary>
    /// Suppresses the two luminance-driven full re-detect escalations (outside-cell
    /// drift and changed-line-ratio "layout shift") on the realtime incremental path.
    /// Every realtime consumer is LIVE screen/video capture, where an animated
    /// background drifts those luminance samples every frame and the guards misread
    /// it as "new text", forcing a ~full-detect (~1s) on every frame and defeating the
    /// whole incremental fast path. With this on, the layout still refreshes via
    /// RealtimeRedetectIntervalMs (periodic) and the rec-empty guard (text vanished),
    /// while steady-state frames stay on the cheap per-line hash ??rec-only path.
    /// </summary>
    public bool RealtimeSuppressLuminanceRedetect { get; set; } = true;

    /// <summary>
    /// Frame-count debounce for the text-based repair escalations (rec-empty, script-flip) on the
    /// incremental path. A subtitle that is mid-change (karaoke per-char reveal, fade in/out) makes
    /// the per-line rec read empty or flip script for a frame or two; escalating to a full re-detect
    /// on the FIRST such frame turns every changing subtitle into a per-frame detect storm. Instead
    /// the previous result is held until the signal persists this many consecutive frames, then a
    /// (scoped) re-detect runs. ~2 frames (~85ms at 24fps) filters the transition flicker without
    /// showing stale text noticeably. 0/1 = escalate immediately (old behavior).
    /// </summary>
    public int RealtimeRepairDebounceFrames { get; set; } = 2;

    /// <summary>
    /// Absolute confidence floor for a changed incremental-recognition box below which the read is
    /// considered a stale-box collapse (the old box recycled a wrong subtitle, e.g. ?芰?????.
    /// Must be accompanied by a drop >= <see cref="RealtimeStaleBoxConfidenceDrop"/> to fire ??
    /// stable faint text (steady ~0.55) never crosses the drop threshold so it does not trigger.
    /// 0 disables.
    /// </summary>
    public double RealtimeStaleBoxConfidence { get; set; } = 0.6;

    /// <summary>
    /// The drop in confidence (old minus new) that, together with
    /// new &lt; <see cref="RealtimeStaleBoxConfidence"/>, triggers a stale-box whole-screen
    /// relocation. Protects stable faint text whose absolute confidence is naturally below the
    /// floor but whose frame-to-frame drop is near zero. 0 disables.
    /// </summary>
    public double RealtimeStaleBoxConfidenceDrop { get; set; } = 0.25;

    /// <summary>
    /// Minimum wall-clock gap (ms) between whole-screen relocations fired from the incremental
    /// repair path (stale-relocate, rec-empty, script-flip). Whole-screen scans are the single
    /// most expensive operation; without throttling a fast-changing subtitle can collapse the
    /// fast path into a per-frame detect storm. During the throttle window the previous good
    /// result is held. 0 disables the throttle (original behaviour ??escalate immediately).
    /// </summary>
    public int RealtimeRelocateMinGapMs { get; set; } = 700;
    /// <summary>
    /// Hard wall-clock budget (ms) for the combined sparse + side rescue on a whole-screen full
    /// detect. The rescues have no internal cap, so a faint vertical column can spin them to 6-14s on a
    /// cold/scene-cut frame (measured) ??the dominant realtime stall. Once exceeded the rescue loops
    /// abort and the primary read is returned (the rescue only ever ADDS to the primary, so capping it
    /// forgoes a marginal gain, never makes the result worse). Scoped ROI re-detects already skip
    /// rescue entirely, so this only bounds the rare cold frames.
    /// </summary>
    public int RealtimeRescueBudgetMs { get; set; } = 700;

    /// <summary>
    /// Confidence-triggered contrast rescue: when a whole-screen full detect's least-confident line
    /// reads below this, the recognizer is unsure ??usually faint/low-contrast text the detector
    /// mangled into noise (e.g. a faint vertical subtitle read as garbage). The frame is re-detected on
    /// a CLAHE-enhanced copy and whichever read is MORE confident wins. Measured separation on real
    /// footage: noise reads cap at ~0.90 confidence, coherent text floors at ~0.90 ??0.85 sits in the
    /// gap, so coherent text never triggers (and so is never at risk of being replaced). 0 disables.
    /// </summary>
    public double RealtimeContrastRescueConfidence { get; set; } = 0.85;

    /// <summary>
    /// Confidence floor below which a tall vertical full-detect candidate is treated as a candidate,
    /// not a trusted commit when a previous track exists. The suspect candidate is held instead of being
    /// allowed to poison the realtime cache. 0 disables the vertical commit gate.
    /// </summary>
    public double RealtimeVerticalCommitConfidence { get; set; } = 0.90;

    /// <summary>Recover missing sticky ROI bands from native PP-OCRv6 crops before committing a full-detect subset.</summary>
    public bool RealtimeTrackRecoveryEnabled { get; set; } = true;

    /// <summary>Maximum previous ROI bands to re-detect per commit.</summary>
    public int RealtimeTrackRecoveryMaxBands { get; set; } = 4;

    /// <summary>Minimum short side for a band recovery crop; smaller crops are upscaled before detect.</summary>
    public int RealtimeTrackRecoveryMinShortSide { get; set; } = 320;

    /// <summary>Consecutive missed recoveries before a previous ROI band is retired.</summary>
    public int RealtimeTrackRetireMissStreak { get; set; } = 2;

    /// <summary>IoU threshold that associates a fresh full-detect line with a previous ROI band.</summary>
    public double RealtimeTrackAssocIoU { get; set; } = 0.25;
}

public sealed class OcrRefinementOptions
{
    /// <summary>Enables the multi-pass block refinement (crop + re-run) for non-realtime requests.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Blocks below this confidence are re-run on their cropped region.</summary>
    public double TriggerConfidence { get; set; } = 0.85;

    /// <summary>Minimum confidence gain required to accept a refined reading.</summary>
    public double MinGain { get; set; } = 0.05;

    /// <summary>Maximum number of blocks re-run per request (lowest confidence first).</summary>
    public int MaxBlocks { get; set; } = 6;

    /// <summary>Padding added around the block crop, as a ratio of the box size.</summary>
    public double PaddingRatio { get; set; } = 0.15;

    /// <summary>Preprocessing preset used for the cropped re-run pass.</summary>
    public string RerunPreset { get; set; } = "text-line";
}

public sealed class OcrAutoDetectionOptions
{
    /// <summary>Below this detection confidence the auto flow re-runs OCR with candidate languages.</summary>
    public double RerunThreshold { get; set; } = 0.65;

    /// <summary>Maximum number of candidate languages to re-run after the first pass.</summary>
    public int MaxRerunCandidates { get; set; } = 2;

    /// <summary>Allowed languages for auto detection; empty falls back to the registry CJK + English preset.</summary>
    public string[] DefaultAllowedLanguages { get; set; } = [];
}

public sealed class OcrPreprocessingOptions
{
    public string DefaultPreset { get; set; } = "none";
    public string[] AllowedPresets { get; set; } =
    [
        "none",
        "upscale",
        "contrast",
        "threshold",
        "denoise",
        "crop-padding",
        "text-line",
        "screenshot",
        "document",
        "table",
        "formula",
        "subtitle",
        "flatten",
        "clahe-l",
        "isolate"
    ];
}

public sealed class OcrConcurrencyOptions
{
    public int DefaultMaxConcurrency { get; set; } = 2;
    public int RealtimeMaxConcurrency { get; set; } = 4;
    public int TextMaxConcurrency { get; set; } = 2;
    public int StructureMaxConcurrency { get; set; } = 1;
    public int VlmMaxConcurrency { get; set; } = 1;
    public Dictionary<string, int> Overrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ExternalOcrOptions
{
    public string FileName { get; set; } = "powershell";
    public string Arguments { get; set; } = "-NoProfile -ExecutionPolicy Bypass -File \"..\\..\\scripts\\windows_ocr_json.ps1\" -Image {image} -Language {language}";
    public int TimeoutSeconds { get; set; } = 15;
}

public sealed class LocalOcrSetOptions
{
    public string PythonFileName { get; set; } = "python";
    public string VenvPythonPath { get; set; } = "../../.ocr-set/venv/Scripts/python.exe";
    public string ScriptPath { get; set; } = "../../scripts/local_ocr_json.py";
    public int TimeoutSeconds { get; set; } = 180;
    public int CheckTimeoutSeconds { get; set; } = 5;
    public LocalOcrWorkerOptions Worker { get; set; } = new();
}

public sealed class LocalOcrWorkerOptions
{
    public bool Enabled { get; set; } = true;
    public string ScriptPath { get; set; } = "../../scripts/local_ocr_worker.py";
    public int TimeoutSeconds { get; set; } = 180;
    public bool FallbackToOneShot { get; set; } = true;
    public string[] Engines { get; set; } =
    [
        "rapidocr-ppocrv5",
        "easyocr",
        "paddleocr",
        "pix2text",
        "pp-structure-v3",
        "paddleocr-vl",
        "dots-ocr"
    ];
}

public sealed class SpeechOptions
{
    public string DefaultProvider { get; set; } = "funasr-http";
    public string DefaultLanguage { get; set; } = "ja";
    public int MaxAudioBytes { get; set; } = 64 * 1024 * 1024;
    public bool PreferCaptions { get; set; } = true;
    public FunAsrHttpOptions FunAsrHttp { get; set; } = new();
    public ExternalSpeechOptions External { get; set; } = new();
    public YouTubeSpeechOptions YouTube { get; set; } = new();
    public VideoSpeechOptions Video { get; set; } = new();
    public LiveSpeechOptions Live { get; set; } = new();
}

public sealed class FunAsrHttpOptions
{
    public string BaseUrl { get; set; } = "http://localhost:8000";
    public string Model { get; set; } = "sensevoice";
    public string ResponseFormat { get; set; } = "verbose_json";
    public int TimeoutSeconds { get; set; } = 120;
}

public sealed class ExternalSpeechOptions
{
    public string FileName { get; set; } = string.Empty;
    public string Arguments { get; set; } = "{audio} {language}";
    public int TimeoutSeconds { get; set; } = 120;
}

public sealed class YouTubeSpeechOptions
{
    public string YtDlpFileName { get; set; } = "yt-dlp";
    public string FfmpegFileName { get; set; } = "ffmpeg";
    public string AudioFormat { get; set; } = "bestaudio[abr<=64]/bestaudio/best";
    public string[] CaptionLanguages { get; set; } = ["ja", "ja-JP", "zh-TW", "zh-Hant", "zh-Hans", "zh", "en"];
    public int TimeoutSeconds { get; set; } = 900;
    public int AudioChunkSeconds { get; set; } = 600;
}

public sealed class VideoSpeechOptions
{
    public int SectionSeconds { get; set; } = 300;

    /// <summary>Deprecated: windows are now aligned to a fixed grid; the playhead window simply gets top priority.</summary>
    public int FirstWindowSeconds { get; set; } = 30;

    public int WindowSeconds { get; set; } = 90;
    public int WindowPaddingSeconds { get; set; } = 5;
    public int PrefetchWindows { get; set; } = 3;
    public int PositionDebounceMs { get; set; } = 250;
    public int MaxDownloadWorkers { get; set; } = 2;
    public int MaxAsrWorkers { get; set; } = 1;

    /// <summary>Length of the small low-latency download used for the playhead window.</summary>
    public int HotSectionSeconds { get; set; } = 60;

    /// <summary>Automatically transcribe the rest of the video at low priority after queued windows finish.</summary>
    public bool BackfillEnabled { get; set; } = true;

    /// <summary>Maximum succeeded audio buffers kept per session before LRU eviction. 0 = unlimited.</summary>
    public int MaxBufferedSections { get; set; } = 6;

    /// <summary>Events kept per session after it reaches a terminal status. 0 = keep all.</summary>
    public int EventRetentionCount { get; set; } = 500;

    public int MaxMediaRetryAttempts { get; set; } = 3;
    public int MediaRetryBaseDelayMs { get; set; } = 2000;
}

public sealed class LiveSpeechOptions
{
    public int SampleRate { get; set; } = 16000;
    public int Channels { get; set; } = 1;
    public int BitsPerSample { get; set; } = 16;
    public int MaxSegmentSeconds { get; set; } = 8;
    public int SilenceDurationMs { get; set; } = 700;
    public double SilenceRmsThreshold { get; set; } = 0.01;
}

public sealed class ContextCompressionOptions
{
    public bool Enabled { get; set; } = true;
    public int MaxCharacters { get; set; } = 1800;
    public int HeadCharacters { get; set; } = 900;
    public int TailCharacters { get; set; } = 700;
}

public sealed class MemoryOptions
{
    public bool PromptContextEnabled { get; set; } = true;
    public int CandidateLimit { get; set; } = 100;
    public int MaxPromptItems { get; set; } = 12;
    public int MaxTerms { get; set; } = 8;
    public int MaxOcrCorrections { get; set; } = 5;
    public int MaxExamples { get; set; } = 3;
    public int MaxStyles { get; set; } = 3;
    public int MaxSceneSummaries { get; set; } = 1;
    public int MaxContextCharacters { get; set; } = 1200;
    public int MaxRecentLines { get; set; } = 4;
    public int MaxRecentContextCharacters { get; set; } = 700;
    public bool SceneSummaryMaintenanceEnabled { get; set; } = true;
    public int SceneSummaryEventThreshold { get; set; } = 4;
    public int SceneSummaryMaxEvents { get; set; } = 12;
    public int SceneSummaryMaxCharacters { get; set; } = 900;
    public bool AutoExtractionEnabled { get; set; } = true;
    public int AutoTranslationCandidateEventThreshold { get; set; } = 3;
    public int AutoTranslationCandidateMaxEvents { get; set; } = 30;
    public double AutoTranslationCandidateConfidence { get; set; } = 0.45;
    public int AutoOcrCorrectionCandidateUseThreshold { get; set; } = 3;
    public double MinimumConfidence { get; set; } = 0.1;
    public bool SharedMemoryEnabled { get; set; } = false;
    public string[] SharedMemoryAuthorizedPrincipals { get; set; } = [];
    public string AdminToken { get; set; } = string.Empty;
    public MemoryExternalIdentityOptions ExternalIdentity { get; set; } = new();
    public MemoryBearerJwtOptions BearerJwt { get; set; } = new();
    public MemoryOidcOptions Oidc { get; set; } = new();
    public bool SemanticRetrievalEnabled { get; set; } = false;
    public int SemanticCandidateLimit { get; set; } = 50;
    public int SemanticTimeoutMs { get; set; } = 75;
    public int EmbeddingMaintenanceBatchSize { get; set; } = 100;
    public int EmbeddingDimensions { get; set; } = 64;
    public double SemanticMinimumSimilarity { get; set; } = 0.72;
}

public sealed class MemoryExternalIdentityOptions
{
    public bool Enabled { get; set; } = false;
    public string SharedSecret { get; set; } = string.Empty;
    public string SharedSecretHeader { get; set; } = "X-Verbeam-External-Token";
    public string PrincipalHeader { get; set; } = "X-Verbeam-External-Principal";
    public string GroupsHeader { get; set; } = "X-Verbeam-External-Groups";
    public MemoryExternalRoleMapping[] RoleMappings { get; set; } = [];
}

public sealed class MemoryExternalRoleMapping
{
    public string Group { get; set; } = string.Empty;
    public string Profile { get; set; } = "*";
    public string Role { get; set; } = "reader";
}

public sealed class MemoryBearerJwtOptions
{
    public bool Enabled { get; set; } = false;
    public string Issuer { get; set; } = string.Empty;
    public string[] Audiences { get; set; } = [];
    public string HmacSecret { get; set; } = string.Empty;
    public string JwksJson { get; set; } = string.Empty;
    public string JwksPath { get; set; } = string.Empty;
    public string JwksUrl { get; set; } = string.Empty;
    public string OidcDiscoveryUrl { get; set; } = string.Empty;
    public int JwksRefreshSeconds { get; set; } = 300;
    public string PrincipalClaim { get; set; } = "sub";
    public string GroupsClaim { get; set; } = "groups";
    public int ClockSkewSeconds { get; set; } = 60;
}

public sealed class MemoryOidcOptions
{
    public bool Enabled { get; set; } = false;
    public string DiscoveryUrl { get; set; } = string.Empty;
    public string AuthorizationEndpoint { get; set; } = string.Empty;
    public string TokenEndpoint { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string RedirectUri { get; set; } = string.Empty;
    public string[] Scopes { get; set; } = ["openid", "profile", "email"];
    public string RefreshTokenStorage { get; set; } = "client_only";
    public string RefreshTokenProtectionKey { get; set; } = string.Empty;
    public int StateTtlSeconds { get; set; } = 300;
    public int SessionLifetimeMinutes { get; set; } = 480;
}
