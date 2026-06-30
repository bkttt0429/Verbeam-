using Verbeam.Core.Models;

namespace Verbeam.Core.Providers;

public interface IShadowRepairOcrProvider
{
    Task<OcrShadowRepairProviderResult?> RecognizeShadowRepairAsync(
        OcrShadowRepairProviderRequest request,
        CancellationToken cancellationToken);
}

public sealed record OcrShadowRepairProviderRequest(
    byte[] ImageBytes,
    string ImageMimeType,
    string Language,
    bool NormalizeWhitespace,
    bool Realtime,
    string SessionKey,
    int CropX,
    int CropY,
    int CropWidth,
    int CropHeight,
    double Scale,
    string CandidateName,
    bool RequireBrightRealtimeCandidate);

public sealed record OcrShadowRepairProviderResult(
    OcrProviderResult Result,
    string CandidateName,
    double Scale,
    int OffsetX,
    int OffsetY,
    int OriginalWidth,
    int OriginalHeight,
    int Width,
    int Height);
