namespace Verbeam.Core.Models;

public sealed record OcrRequest
{
    public string? ImageBase64 { get; init; }
    public string? ImageMimeType { get; init; }
    public string? Provider { get; init; }
    public string? ContentType { get; init; }
    public string? Preference { get; init; }
    public string? Language { get; init; }
    public string? LanguageHint { get; init; }
    public IReadOnlyList<string>? AllowedLanguages { get; init; }
    public string? Profile { get; init; }
    public string? SessionId { get; init; }
    public bool? NormalizeWhitespace { get; init; }
    public string? PreprocessingPreset { get; init; }
    public bool? Realtime { get; init; }
    public bool? Refine { get; init; }

    /// <summary>Overrides Ocr:RealtimeAutoSuppress:Enabled for this request (realtime only).</summary>
    public bool? AutoSuppressRecurringText { get; init; }
}

public sealed record OcrSmokeTestRequest
{
    public string? ImageBase64 { get; init; }
    public string? ImageMimeType { get; init; }
    public string? Provider { get; init; }
    public string? ContentType { get; init; }
    public string? Preference { get; init; }
    public string? Language { get; init; }
    public string? LanguageHint { get; init; }
    public IReadOnlyList<string>? AllowedLanguages { get; init; }
    public string? Profile { get; init; }
    public string? SessionId { get; init; }
    public bool? NormalizeWhitespace { get; init; }
    public string? PreprocessingPreset { get; init; }
    public string? ExpectedText { get; init; }
    public OcrExpectedStructure? ExpectedStructure { get; init; }
}

public sealed record OcrSmokeMatrixRequest
{
    public string? ImageBase64 { get; init; }
    public string? ImageMimeType { get; init; }
    public IReadOnlyList<string>? Providers { get; init; }
    public string? ContentType { get; init; }
    public string? Preference { get; init; }
    public string? Language { get; init; }
    public string? LanguageHint { get; init; }
    public IReadOnlyList<string>? AllowedLanguages { get; init; }
    public string? Profile { get; init; }
    public string? SessionId { get; init; }
    public bool? NormalizeWhitespace { get; init; }
    public string? PreprocessingPreset { get; init; }
    public string? ExpectedText { get; init; }
    public OcrExpectedStructure? ExpectedStructure { get; init; }
}

public sealed record OcrSmokeMatrixResponse(
    IReadOnlyList<OcrSmokeMatrixItem> Items,
    int SuccessCount,
    int FailureCount,
    DateTimeOffset CreatedAt);

public sealed record OcrSmokeMatrixItem(
    string Provider,
    bool Succeeded,
    OcrSmokeTestResponse? Result,
    string ErrorCode,
    string ErrorMessage);

public sealed record OcrJobRequest
{
    public string? ImageBase64 { get; init; }
    public string? ImageMimeType { get; init; }
    public string? OcrProvider { get; init; }
    public string? Provider { get; init; }
    public string? ContentType { get; init; }
    public string? Preference { get; init; }
    public string? Language { get; init; }
    public string? LanguageHint { get; init; }
    public IReadOnlyList<string>? AllowedLanguages { get; init; }
    public string? Profile { get; init; }
    public string? SessionId { get; init; }
    public bool? NormalizeWhitespace { get; init; }
    public string? PreprocessingPreset { get; init; }
}

public static class OcrJobStages
{
    public const string Queued = "queued";
    public const string Preparing = "preparing";
    public const string Recognizing = "recognizing";
    public const string Assembling = "assembling";
    public const string Done = "done";
}

public sealed record OcrJobStatus(
    string Id,
    string Status,
    string ProfileId,
    string SessionId,
    string ImageHash,
    string ImageMimeType,
    string Language,
    string Provider,
    string Engine,
    int BlockCount,
    double Progress,
    string ResultEventId,
    bool CacheHit,
    string ErrorCode,
    string ErrorMessage,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset UpdatedAt)
{
    public string Stage { get; init; } = "";
    public long? EstimatedDurationMs { get; init; }
}

public sealed record OcrJobEvent(
    long Sequence,
    string JobId,
    string Type,
    string PayloadJson,
    DateTimeOffset CreatedAt);

public sealed record OcrJobResult(
    OcrJobStatus Job,
    OcrEvent? Result);

public sealed record OcrTranslateRequest
{
    public string? ImageBase64 { get; init; }
    public string? ImageMimeType { get; init; }
    public string? OcrProvider { get; init; }
    public string? ContentType { get; init; }
    public string? Preference { get; init; }
    public string? Language { get; init; }
    public string? LanguageHint { get; init; }
    public IReadOnlyList<string>? AllowedLanguages { get; init; }
    public string? Profile { get; init; }
    public string? SessionId { get; init; }
    public bool? NormalizeWhitespace { get; init; }
    public string? PreprocessingPreset { get; init; }
    public bool? MergeTextBlocks { get; init; }
    public bool? Realtime { get; init; }
    public IReadOnlyList<OcrNormalizedRegion>? ExcludeRegions { get; init; }
    public IReadOnlyList<string>? DropPatterns { get; init; }

    public string? Target { get; init; }
    public string? Source { get; init; }
    public string? Mode { get; init; }
    public string? Glossary { get; init; }
    public string? TranslationProvider { get; init; }
    public string? Model { get; init; }
    public string? BroadcastSourceKind { get; init; }
    /// <summary>App surface for usage analytics (region|ocr); defaults to ocr when unset.</summary>
    public string? Surface { get; init; }
}

public sealed record OcrProviderDescriptor(
    string Name,
    string DisplayName,
    string Kind,
    string DefaultLanguage,
    bool RequiresExternalProcess,
    bool IsLocal)
{
    /// <summary>Canonical language tags this provider can recognize; empty means unconstrained/multilingual.</summary>
    public IReadOnlyList<string> SupportedLanguages { get; init; } = Array.Empty<string>();

    /// <summary>
    /// True when the engine recognizes all of its languages in a single pass and ignores
    /// the language parameter (e.g. RapidOCR/PP-OCRv5); auto detection then never re-runs it.
    /// </summary>
    public bool IsLanguageAgnostic { get; init; }
}

public sealed record OcrEngineDescriptor(
    string Name,
    string DisplayName,
    string Kind,
    string DefaultLanguage,
    bool IsAvailable,
    bool IsDefault,
    bool RequiresExternalProcess,
    bool IsLocal,
    string Source,
    string Status,
    bool RequiresApiConfiguration,
    string Note)
{
    /// <summary>Canonical language tags this engine can recognize; empty means unconstrained/multilingual.</summary>
    public IReadOnlyList<string> SupportedLanguages { get; init; } = Array.Empty<string>();
}

public sealed record OcrRoutingProfile(
    string Name,
    string DisplayName,
    string ContentType,
    string SpeedClass,
    string RuntimeKind,
    string RecommendedProvider,
    string FallbackProvider,
    int ExpectedLatencyMs,
    bool PreferAsyncJob,
    bool PreservesStructure,
    string Note);

public sealed record OcrRoutingDecision(
    string Provider,
    string Profile,
    string ContentType,
    string Preference,
    string SpeedClass,
    string RuntimeKind,
    int ExpectedLatencyMs,
    bool PreferAsyncJob,
    bool PreservesStructure,
    string Reason,
    string QualityStatus = "unknown",
    string QualityNote = "")
{
    public IReadOnlyList<OcrSmokeQualityIssue> QualityIssues { get; init; } = Array.Empty<OcrSmokeQualityIssue>();
}

public sealed record OcrProviderRequest(
    byte[] ImageBytes,
    string ImageMimeType,
    string Language,
    bool NormalizeWhitespace,
    string PreprocessingPreset = "none",
    bool Realtime = false,
    string SessionKey = "");

public sealed record OcrProviderResult(
    string Text,
    IReadOnlyList<OcrTextBlock> Blocks,
    string Engine,
    OcrDocumentResult? Document = null,
    OcrProviderTiming? Timing = null);

/// <summary>
/// Provider-internal profiling for one OCR call, surfaced so a live region/workbench run can tell
/// apart the realtime cost sources at a glance: queueing (<see cref="QueueWaitMs"/>, time spent
/// waiting on the provider's single-run lock), compute (<see cref="ProviderMs"/>), a periodic/forced
/// full re-detect (<see cref="FullDetectReason"/>, e.g. "interval" / "script-flip" / "miss"; empty on
/// the cheap incremental path), preprocessing (<see cref="PreprocessMs"/>), the main DET call
/// (<see cref="FullDetectMs"/>), post-DET line assembly (<see cref="BuildLinesMs"/>), optional
/// language recognizer re-read (<see cref="LanguageRecMs"/>), sparse-glyph rescue
/// (<see cref="SparseRescueMs"/>), and the expensive side-ROI re-detect (<see cref="SideRescueMs"/>).
/// All values are best-effort and 0/empty when not applicable.
/// </summary>
public sealed record OcrProviderTiming(
    long ProviderMs = 0,
    long ServiceWaitMs = 0,
    long ServiceBuildMs = 0,
    long QueueWaitMs = 0,
    string FullDetectReason = "",
    long SideRescueMs = 0,
    long PreprocessMs = 0,
    long DetectResizeMs = 0,
    long SparsePreflightMs = 0,
    long FullDetectMs = 0,
    long BuildLinesMs = 0,
    long LanguageRecMs = 0,
    long SparseRescueMs = 0,
    long OutsideCellsMs = 0,
    long RealtimeBaseRecMs = 0,
    long RealtimeLangRecMs = 0,
    string RecCropShape = "");

public sealed record OcrTextBlock(
    string Text,
    double Confidence,
    OcrBoundingBox? BoundingBox)
{
    public string DetectedLanguage { get; init; } = string.Empty;
    public string Script { get; init; } = string.Empty;
}

public sealed record OcrBoundingBox(int X, int Y, int Width, int Height);

/// <summary>Region normalized to the source image (all values 0..1).</summary>
public sealed record OcrNormalizedRegion(double X, double Y, double Width, double Height);

public sealed record OcrPoint(double X, double Y);

public static class OcrBlockTypes
{
    public const string Text = "text";
    public const string Title = "title";
    public const string Dialogue = "dialogue";
    public const string UiLabel = "ui_label";
    public const string Table = "table";
    public const string Formula = "formula";
    public const string Code = "code";
    public const string Figure = "figure";
    public const string Unknown = "unknown";
}

public sealed record OcrDocumentResult
{
    public string Version { get; init; } = "ocr-ir-v1";
    public IReadOnlyList<OcrPageResult> Pages { get; init; } = Array.Empty<OcrPageResult>();
}

public sealed record OcrPageResult
{
    public int PageIndex { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }

    // PDF overlay editor: the rendered page backdrop + the two coordinate spaces a
    // block bbox can live in. Text-layer blocks are in PDF points@72 (PageWidthPoints/
    // PageHeightPoints); re-OCR blocks are in rendered pixels (ImageWidth/ImageHeight).
    // Normalizing a bbox by the matching pair yields one 0..1 space the editor and both
    // exporters share. Null for non-PDF sources.
    /// <summary>DPI the backdrop image was rendered at.</summary>
    public int? RenderDpi { get; init; }
    /// <summary>Rendered backdrop pixel width (matches re-OCR bbox space).</summary>
    public int? ImageWidth { get; init; }
    /// <summary>Rendered backdrop pixel height (matches re-OCR bbox space).</summary>
    public int? ImageHeight { get; init; }
    /// <summary>Logical page width in PDF points@72 (matches text-layer bbox space).</summary>
    public double? PageWidthPoints { get; init; }
    /// <summary>Logical page height in PDF points@72 (matches text-layer bbox space).</summary>
    public double? PageHeightPoints { get; init; }

    public IReadOnlyList<OcrBlock> Blocks { get; init; } = Array.Empty<OcrBlock>();
}

public sealed record OcrBlock
{
    public string Id { get; init; } = string.Empty;
    public string Type { get; init; } = OcrBlockTypes.Unknown;
    public string Text { get; init; } = string.Empty;
    public string SourceText { get; init; } = string.Empty;
    public double Confidence { get; init; } = 1.0;
    public OcrBoundingBox? BoundingBox { get; init; }
    public IReadOnlyList<OcrPoint> Polygon { get; init; } = Array.Empty<OcrPoint>();
    public int ReadingOrder { get; init; }
    public string Engine { get; init; } = string.Empty;
    public bool ShouldTranslate { get; init; } = true;
    public string DetectedLanguage { get; init; } = string.Empty;
    public string Script { get; init; } = string.Empty;
    public IReadOnlyList<OcrBlock> Children { get; init; } = Array.Empty<OcrBlock>();
    public OcrTableBlock? Table { get; init; }
    public OcrFormulaBlock? Formula { get; init; }
}

public sealed record OcrTableBlock
{
    public int RowCount { get; init; }
    public int ColumnCount { get; init; }
    public IReadOnlyList<OcrTableCell> Cells { get; init; } = Array.Empty<OcrTableCell>();
}

public sealed record OcrTableCell
{
    public string Id { get; init; } = string.Empty;
    public int RowIndex { get; init; }
    public int ColumnIndex { get; init; }
    public int RowSpan { get; init; } = 1;
    public int ColumnSpan { get; init; } = 1;
    public string Text { get; init; } = string.Empty;
    public string SourceText { get; init; } = string.Empty;
    public OcrBoundingBox? BoundingBox { get; init; }
    public IReadOnlyList<OcrPoint> Polygon { get; init; } = Array.Empty<OcrPoint>();
    public double Confidence { get; init; } = 1.0;
    public bool ShouldTranslate { get; init; } = true;
    public string DetectedLanguage { get; init; } = string.Empty;
}

public sealed record OcrFormulaBlock
{
    public string Latex { get; init; } = string.Empty;
    public string SourceText { get; init; } = string.Empty;
    public bool ShouldTranslate { get; init; }
}

public sealed record OcrStructureSummary(
    int PageCount,
    int BlockCount,
    int TextBlockCount,
    int TableBlockCount,
    int FormulaBlockCount,
    int TableCellCount,
    int TranslatableCellCount,
    int InvalidTableCellCount,
    int MissingTableCellCount,
    int OverlappingTableCellCount,
    int PassThroughBlockCount)
{
    public static readonly OcrStructureSummary Empty = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
}

public sealed record OcrExpectedStructure
{
    public int? PageCount { get; init; }
    public int? BlockCount { get; init; }
    public int? TextBlockCount { get; init; }
    public int? TableBlockCount { get; init; }
    public int? FormulaBlockCount { get; init; }
    public int? TableCellCount { get; init; }
    public int? TableRowCount { get; init; }
    public int? TableColumnCount { get; init; }
    public int? TranslatableCellCount { get; init; }
    public int? InvalidTableCellCount { get; init; }
    public int? MissingTableCellCount { get; init; }
    public int? OverlappingTableCellCount { get; init; }
    public int? PassThroughBlockCount { get; init; }
    public string? FormulaLatexContains { get; init; }
}

public sealed record OcrStructureIssue(
    string Code,
    string Severity,
    string Message,
    string Expected,
    string Actual);

public sealed record OcrStructureAssertion(
    OcrExpectedStructure? Expected,
    bool HasExpected,
    bool Passed,
    IReadOnlyList<string> Mismatches)
{
    public static readonly OcrStructureAssertion Empty = new(null, false, true, Array.Empty<string>());
    public IReadOnlyList<OcrStructureIssue> Issues { get; init; } = Array.Empty<OcrStructureIssue>();
}

public sealed record OcrResponse(
    string EventId,
    string Text,
    string RawText,
    IReadOnlyList<OcrTextBlock> Blocks,
    IReadOnlyList<AppliedOcrCorrection> AppliedCorrections,
    string Provider,
    string Engine,
    string Language,
    string ImageMimeType,
    long LatencyMs,
    OcrDocumentResult? Document = null,
    bool CacheHit = false)
{
    /// <summary>The language value the caller asked for ("auto" or an explicit tag).</summary>
    public string RequestedLanguage { get; init; } = string.Empty;

    /// <summary>The canonical language the OCR engine actually ran with.</summary>
    public string ResolvedOcrLanguage { get; init; } = string.Empty;

    /// <summary>The canonical language inferred from the recognized text.</summary>
    public string DetectedLanguage { get; init; } = string.Empty;

    public double LanguageConfidence { get; init; }
    public IReadOnlyList<OcrLanguageCandidate> LanguageCandidates { get; init; } = Array.Empty<OcrLanguageCandidate>();

    /// <summary>Realtime only: recurring text (watermarks) auto-removed from this response.</summary>
    public IReadOnlyList<string> SuppressedText { get; init; } = Array.Empty<string>();

    /// <summary>SHA-256 of the source image bytes. Stable per image; the block-workbench
    /// keys per-block annotations/history on (ImageHash, BlockId).</summary>
    public string ImageHash { get; init; } = string.Empty;

    /// <summary>Provider-internal profiling (queue wait, compute, full-detect reason, side-rescue
    /// cost) for the run that produced this response. Null for providers that don't report it.</summary>
    public OcrProviderTiming? Timing { get; init; }
}

public sealed record OcrSmokeTestResponse(
    OcrResponse Ocr,
    string ExpectedText,
    string RecognizedText,
    bool ExactMatch,
    bool ContainsExpected,
    double Similarity,
    int EditDistance,
    long LatencyMs,
    DateTimeOffset CreatedAt)
{
    public OcrStructureSummary Structure { get; init; } = OcrStructureSummary.Empty;
    public OcrStructureAssertion StructureAssertion { get; init; } = OcrStructureAssertion.Empty;
}

public sealed record OcrSmokeTestRecord(
    string Id,
    string ProfileId,
    string SessionId,
    string Language,
    string Provider,
    string Engine,
    string ContentType,
    string Preference,
    string PreprocessingPreset,
    string OcrEventId,
    string ExpectedText,
    string RecognizedText,
    bool ExactMatch,
    bool ContainsExpected,
    double Similarity,
    int EditDistance,
    long LatencyMs,
    DateTimeOffset CreatedAt)
{
    public OcrStructureSummary Structure { get; init; } = OcrStructureSummary.Empty;
    public OcrStructureAssertion StructureAssertion { get; init; } = OcrStructureAssertion.Empty;
    public bool Succeeded { get; init; } = true;
    public string ErrorCode { get; init; } = "0";
    public string ErrorMessage { get; init; } = string.Empty;
}

public sealed record OcrSmokeQualitySummary(
    string ProfileId,
    string Provider,
    string Engine,
    string Language,
    string ContentType,
    string Preference,
    int SampleCount,
    int TextExpectedCount,
    int TextPassCount,
    double TextPassRate,
    int StructureExpectedCount,
    int StructurePassCount,
    double StructurePassRate,
    int TableSampleCount,
    int TableIntegrityIssueCount,
    int RuntimeFailureCount,
    double AverageSimilarity,
    long AverageLatencyMs,
    DateTimeOffset LastSeenAt,
    string Status,
    string Note,
    string Scope = "engine")
{
    public IReadOnlyList<OcrSmokeQualityIssue> Issues { get; init; } = Array.Empty<OcrSmokeQualityIssue>();
}

public sealed record OcrSmokeQualityIssue(
    string Code,
    string Severity,
    int Count,
    string Message);

/// <summary>Language decision outcome for one OCR run, persisted alongside the cached result.</summary>
public sealed record OcrLanguageDetection(
    string RequestedLanguage,
    string ResolvedOcrLanguage,
    string DetectedLanguage,
    double LanguageConfidence,
    IReadOnlyList<OcrLanguageCandidate> Candidates)
{
    public static readonly OcrLanguageDetection Empty = new(
        string.Empty,
        string.Empty,
        string.Empty,
        0,
        Array.Empty<OcrLanguageCandidate>());
}

public sealed record OcrCachedResult(
    string Key,
    string ImageHash,
    string ImageMimeType,
    string Provider,
    string Engine,
    string EngineModelVersion,
    string Language,
    bool NormalizeWhitespace,
    string CorrectionHash,
    string RawText,
    string CorrectedText,
    IReadOnlyList<OcrTextBlock> Blocks,
    IReadOnlyList<AppliedOcrCorrection> AppliedCorrections,
    OcrDocumentResult Document,
    long LatencyMs,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastUsedAt,
    int UseCount)
{
    public OcrLanguageDetection Detection { get; init; } = OcrLanguageDetection.Empty;
}

public sealed record OcrTranslateResponse(
    OcrResponse Ocr,
    MortTranslateResponse Translation,
    OcrStructuredTranslation? Structured = null);

public sealed record OcrStructuredTranslation(
    string Text,
    OcrDocumentResult Document,
    IReadOnlyList<OcrSegmentTranslation> Segments,
    string Engine,
    long LatencyMs,
    bool CacheHit,
    TokenUsage? TokenUsage = null)
{
    public string Markdown { get; init; } = string.Empty;
    public string Html { get; init; } = string.Empty;
    public string OverlayHtml { get; init; } = string.Empty;
    public string LayoutHtml { get; init; } = string.Empty;
    public OcrLayoutDiagnostics LayoutDiagnostics { get; init; } = OcrLayoutDiagnostics.Empty;
}

public sealed record OcrLayoutDiagnostics(
    int PageCount,
    int PagesWithSize,
    int BlockCount,
    int BlocksWithBoundingBox,
    int BlocksMissingBoundingBox,
    int TableCellCount,
    int TableCellsWithBoundingBox,
    int TableCellsMissingBoundingBox,
    bool OverlayReady,
    bool LayoutReady,
    IReadOnlyList<OcrLayoutIssue> Issues)
{
    public static readonly OcrLayoutDiagnostics Empty = new(
        PageCount: 0,
        PagesWithSize: 0,
        BlockCount: 0,
        BlocksWithBoundingBox: 0,
        BlocksMissingBoundingBox: 0,
        TableCellCount: 0,
        TableCellsWithBoundingBox: 0,
        TableCellsMissingBoundingBox: 0,
        OverlayReady: false,
        LayoutReady: false,
        Issues: Array.Empty<OcrLayoutIssue>());
}

public sealed record OcrLayoutIssue(
    string Code,
    string Severity,
    string Message,
    string TargetId);

public sealed record OcrSegmentTranslation(
    string Id,
    string Type,
    string SourceText,
    string TranslatedText,
    bool Translated,
    string Engine,
    long LatencyMs,
    bool CacheHit,
    string ErrorCode,
    string ErrorMessage,
    TokenUsage? TokenUsage = null);

public sealed record AppliedOcrCorrection(
    string CorrectionId,
    string WrongText,
    string CorrectedText);

public sealed record OcrEvent(
    string Id,
    string ProfileId,
    string SessionId,
    string ImageHash,
    string ImageMimeType,
    string Language,
    string Provider,
    string Engine,
    string RawText,
    string CorrectedText,
    IReadOnlyList<OcrTextBlock> Blocks,
    IReadOnlyList<AppliedOcrCorrection> AppliedCorrections,
    long LatencyMs,
    DateTimeOffset CreatedAt,
    OcrDocumentResult? Document = null);

public sealed record OcrCorrection(
    string Id,
    string ProfileId,
    string Language,
    string WrongText,
    string CorrectedText,
    string Note,
    int Priority,
    double Confidence,
    string Source,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastUsedAt,
    int UseCount);

public sealed record OcrCorrectionRequest
{
    public string? Profile { get; init; }
    public string? Language { get; init; }
    public string? WrongText { get; init; }
    public string? CorrectedText { get; init; }
    public string? Note { get; init; }
    public int? Priority { get; init; }
    public double? Confidence { get; init; }
    public string? Source { get; init; }
}

public sealed record OcrCorrectionUpdateRequest
{
    public string? Note { get; init; }
    public int? Priority { get; init; }
    public double? Confidence { get; init; }
    public string? Source { get; init; }
    public bool? IsActive { get; init; }
}

public static class OcrBlockStatuses
{
    /// <summary>Default: the block was translated by the pipeline, untouched by the user.</summary>
    public const string Translated = "translated";

    /// <summary>The user hand-edited the block, or applied a retranslate / re-OCR result.</summary>
    public const string Edited = "edited";

    /// <summary>Excluded from overlay rendering and copy output.</summary>
    public const string Ignored = "ignored";

    /// <summary>Frozen: retranslate / re-OCR skip this block so manual edits survive re-runs.</summary>
    public const string Locked = "locked";
}

/// <summary>
/// Per-block manual state for the OCR block workbench, persisted on
/// (ProfileId, ImageHash, BlockId). Survives per-block re-OCR / retranslate
/// because those keep the original block id; a full re-OCR re-mints ids and
/// orphans these rows (documented caveat, not handled in MVP).
/// </summary>
public sealed record OcrBlockAnnotation(
    string ProfileId,
    string ImageHash,
    string BlockId,
    string Status,
    bool Locked,
    string EditedTranslation,
    DateTimeOffset UpdatedAt)
{
    public string EditedSource { get; init; } = string.Empty;
    public string Note { get; init; } = string.Empty;
    public int? ReadingOrderOverride { get; init; }
    public string TypeOverride { get; init; } = string.Empty;
}

/// <summary>One historical OCR/translation version of a block, appended on every
/// re-OCR / retranslate apply so the user can review (and later revert) prior values.</summary>
public sealed record OcrBlockHistoryEntry(
    string Id,
    string ProfileId,
    string ImageHash,
    string BlockId,
    string Kind,
    string SourceText,
    string TranslatedText,
    string Engine,
    string Provider,
    DateTimeOffset CreatedAt);

public static class OcrBlockHistoryKinds
{
    public const string Ocr = "ocr";
    public const string Translation = "translation";
}

/// <summary>Upsert payload for POST /ocr/blocks/annotations.</summary>
public sealed record OcrBlockAnnotationRequest
{
    public string? Profile { get; init; }
    public string? ImageHash { get; init; }
    public string? BlockId { get; init; }
    public string? Status { get; init; }
    public bool? Locked { get; init; }
    public string? EditedTranslation { get; init; }
    public string? EditedSource { get; init; }
    public string? Note { get; init; }
    public int? ReadingOrderOverride { get; init; }
    public string? TypeOverride { get; init; }

    /// <summary>Optional history row to append atomically with the upsert
    /// (e.g. the pre-apply value when a retranslate/re-OCR result is accepted).</summary>
    public OcrBlockHistoryInput? History { get; init; }
}

public sealed record OcrBlockHistoryInput
{
    public string? Kind { get; init; }
    public string? SourceText { get; init; }
    public string? TranslatedText { get; init; }
    public string? Engine { get; init; }
    public string? Provider { get; init; }
}

public static class OcrBlockOverflowModes
{
    /// <summary>Auto-shrink the font until the translation fits the box (default).</summary>
    public const string Shrink = "shrink";

    /// <summary>Keep the font, let the box grow vertically / text wrap.</summary>
    public const string Wrap = "wrap";

    /// <summary>Ask the model for a shorter translation that fits.</summary>
    public const string RetranslateShorter = "retranslate-shorter";
}

/// <summary>
/// Per-block geometry + text-format overrides for the PDF overlay editor, persisted on
/// (ProfileId, DocKey, BlockId) where DocKey = "{jobId}:{pageIndex}". Bbox is stored
/// normalized 0..1 of the page so it is invariant to render DPI/scale; a null geometry or
/// format field means "use the IR block's own value". Sibling to <see cref="OcrBlockAnnotation"/>,
/// which keeps status/locked/edited-text.
/// </summary>
public sealed record OcrBlockLayout(
    string ProfileId,
    string DocKey,
    string BlockId,
    DateTimeOffset UpdatedAt)
{
    public double? Nx { get; init; }
    public double? Ny { get; init; }
    public double? Nw { get; init; }
    public double? Nh { get; init; }
    public double? FontSize { get; init; }
    public double? LineHeight { get; init; }
    public string TextAlign { get; init; } = string.Empty;
    public string Overflow { get; init; } = OcrBlockOverflowModes.Shrink;
}

/// <summary>Upsert payload for POST /ocr/blocks/layout.</summary>
public sealed record OcrBlockLayoutRequest
{
    public string? Profile { get; init; }
    public string? DocKey { get; init; }
    public string? BlockId { get; init; }
    public double? Nx { get; init; }
    public double? Ny { get; init; }
    public double? Nw { get; init; }
    public double? Nh { get; init; }
    public double? FontSize { get; init; }
    public double? LineHeight { get; init; }
    public string? TextAlign { get; init; }
    public string? Overflow { get; init; }
}
