namespace Verbeam.Core.Models;

public sealed record OcrRequest
{
    public string? ImageBase64 { get; init; }
    public string? ImageMimeType { get; init; }
    public string? Provider { get; init; }
    public string? Language { get; init; }
    public string? Profile { get; init; }
    public string? SessionId { get; init; }
    public bool? NormalizeWhitespace { get; init; }
}

public sealed record OcrTranslateRequest
{
    public string? ImageBase64 { get; init; }
    public string? ImageMimeType { get; init; }
    public string? OcrProvider { get; init; }
    public string? Language { get; init; }
    public string? Profile { get; init; }
    public string? SessionId { get; init; }
    public bool? NormalizeWhitespace { get; init; }

    public string? Target { get; init; }
    public string? Source { get; init; }
    public string? Mode { get; init; }
    public string? Glossary { get; init; }
    public string? TranslationProvider { get; init; }
    public string? Model { get; init; }
}

public sealed record OcrProviderDescriptor(
    string Name,
    string DisplayName,
    string Kind,
    string DefaultLanguage,
    bool RequiresExternalProcess,
    bool IsLocal);

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
    string Note);

public sealed record OcrProviderRequest(
    byte[] ImageBytes,
    string ImageMimeType,
    string Language,
    bool NormalizeWhitespace);

public sealed record OcrProviderResult(
    string Text,
    IReadOnlyList<OcrTextBlock> Blocks,
    string Engine);

public sealed record OcrTextBlock(
    string Text,
    double Confidence,
    OcrBoundingBox? BoundingBox);

public sealed record OcrBoundingBox(int X, int Y, int Width, int Height);

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
    long LatencyMs);

public sealed record OcrTranslateResponse(
    OcrResponse Ocr,
    MortTranslateResponse Translation);

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
    DateTimeOffset CreatedAt);

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
