using Verbeam.Core.Models;
using Verbeam.Core.Options;

namespace Verbeam.Core.Providers;

/// <summary>
/// macOS realtime OCR via Apple's Vision framework (VNRecognizeTextRequest), invoked through a tiny
/// Swift helper CLI (<c>ocr-helpers/vision-ocr</c>) that emits the same stdout-JSON block format the
/// local OCR set uses. Vision is the macOS analog of OneOCR: NPU-accelerated, ~tens of ms, and on
/// macOS 13+ it natively recognizes ja / zh-Hans / zh-Hant / yue / ko plus ru / uk / th / vi.
/// <para>
/// Registered ONLY on macOS when the helper binary resolves (mirrors <see cref="OneOcrProvider"/>'s
/// availability-probe gate), so the registration-based realtime router (<c>SelectRegisteredProvider</c>)
/// naturally prefers it on macOS and falls back to <c>rapidocr-net</c> elsewhere — never pick-then-fail.
/// </para>
/// </summary>
public sealed class AppleVisionOcrProvider : IOcrProvider
{
    public const string ProviderName = "apple-vision";

    private static readonly string[] HelperRelativeCandidates =
    [
        Path.Combine("ocr-helpers", "vision-ocr", "verbeam-vision-ocr"),
        Path.Combine("ocr-helpers", "verbeam-vision-ocr"),
    ];

    private readonly IOcrProvider _inner;

    // internal (not private) so Verbeam.Tests can inject a stub/fake inner provider and exercise the
    // delegation + engine re-tag on Windows without a macOS helper. Production goes through Create().
    internal AppleVisionOcrProvider(OcrProviderDescriptor descriptor, IOcrProvider inner)
    {
        Descriptor = descriptor;
        _inner = inner;
    }

    public OcrProviderDescriptor Descriptor { get; }

    public async Task<OcrProviderResult> RecognizeAsync(
        OcrProviderRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _inner.RecognizeAsync(request, cancellationToken);
        // Re-tag the engine so latency labels/telemetry report the real provider, not "external:...".
        return new OcrProviderResult(result.Text, result.Blocks, ProviderName, result.Document);
    }

    /// <summary>
    /// Builds a provider bound to a resolved helper binary. The helper is driven through the shared
    /// <see cref="ExternalCommandOcrProvider"/> subprocess+JSON-parse machinery.
    /// </summary>
    public static AppleVisionOcrProvider Create(string helperPath, string workingDirectory, string defaultLanguage)
    {
        var options = new ExternalOcrOptions
        {
            FileName = helperPath,
            Arguments = "--image {image} --language {language}",
            TimeoutSeconds = 15,
        };

        var descriptor = new OcrProviderDescriptor(
            ProviderName,
            "Apple Vision (macOS)",
            "local-native",
            defaultLanguage,
            RequiresExternalProcess: true,
            IsLocal: true)
        {
            IsLanguageAgnostic = true,
        };

        return new AppleVisionOcrProvider(descriptor, new ExternalCommandOcrProvider(options, workingDirectory));
    }

    /// <summary>
    /// Resolves the Vision helper binary (macOS only). Returns false off-macOS or when the helper is
    /// absent so callers skip registration. Resolution order: explicit <paramref name="configuredPath"/>,
    /// the <c>VERBEAM_VISION_OCR_PATH</c> env var, then the conventional <c>ocr-helpers/</c> locations.
    /// </summary>
    public static bool TryProbeAvailability(
        string contentRootPath,
        string? configuredPath,
        out string resolvedPath,
        out string note)
    {
        resolvedPath = string.Empty;

        if (!OperatingSystem.IsMacOS())
        {
            note = "Apple Vision OCR is only available on macOS.";
            return false;
        }

        foreach (var candidate in EnumerateCandidates(contentRootPath, configuredPath))
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
            {
                resolvedPath = candidate;
                note = $"Apple Vision helper found at {candidate}.";
                return true;
            }
        }

        note = "Apple Vision helper binary 'verbeam-vision-ocr' was not found; build app/ocr-helpers/vision-ocr "
            + "or set VERBEAM_VISION_OCR_PATH.";
        return false;
    }

    private static IEnumerable<string> EnumerateCandidates(string contentRootPath, string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            yield return ResolveAgainstRoot(contentRootPath, configuredPath);
        }

        var fromEnv = Environment.GetEnvironmentVariable("VERBEAM_VISION_OCR_PATH");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            yield return fromEnv;
        }

        foreach (var relative in HelperRelativeCandidates)
        {
            yield return ResolveAgainstRoot(contentRootPath, relative);
        }
    }

    private static string ResolveAgainstRoot(string contentRootPath, string path)
        => Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(contentRootPath, path));
}
