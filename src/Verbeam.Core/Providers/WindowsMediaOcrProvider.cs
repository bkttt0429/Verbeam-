using System.Runtime.InteropServices.WindowsRuntime;
using Verbeam.Core.Models;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace Verbeam.Core.Providers;

/// <summary>
/// In-process Windows.Media.Ocr provider. Replaces the PowerShell-spawning
/// external command path for realtime region captures: no process startup,
/// the WinRT engine is cached per language, and a frame costs ~50-100ms.
/// </summary>
public sealed class WindowsMediaOcrProvider : IOcrProvider
{
    private readonly object _engineLock = new();
    private readonly Dictionary<string, OcrEngine?> _engines = new(StringComparer.OrdinalIgnoreCase);

    public WindowsMediaOcrProvider(OcrProviderDescriptor descriptor)
    {
        Descriptor = descriptor;
    }

    public OcrProviderDescriptor Descriptor { get; }

    public static bool TryProbeAvailability(out string note)
    {
        try
        {
            var languages = OcrEngine.AvailableRecognizerLanguages;
            if (languages.Count == 0)
            {
                note = "No Windows OCR recognizer languages are installed.";
                return false;
            }

            note = $"Windows OCR languages: {string.Join(", ", languages.Select(language => language.LanguageTag))}.";
            return true;
        }
        catch (Exception ex)
        {
            note = $"Windows OCR runtime is unavailable: {ex.Message}";
            return false;
        }
    }

    public async Task<OcrProviderResult> RecognizeAsync(
        OcrProviderRequest request,
        CancellationToken cancellationToken)
    {
        var engine = ResolveEngine(request.Language)
            ?? throw new InvalidOperationException(
                $"No Windows OCR engine is available for '{ConvertLanguageTag(request.Language)}'. Install the OCR language pack in Windows Settings.");

        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(request.ImageBytes.AsBuffer()).AsTask(cancellationToken);
        stream.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken);
        var transform = new BitmapTransform();
        var maxDimension = OcrEngine.MaxImageDimension;
        if (decoder.PixelWidth > maxDimension || decoder.PixelHeight > maxDimension)
        {
            var scale = Math.Min(
                (double)maxDimension / decoder.PixelWidth,
                (double)maxDimension / decoder.PixelHeight);
            transform.ScaledWidth = (uint)Math.Max(1, Math.Floor(decoder.PixelWidth * scale));
            transform.ScaledHeight = (uint)Math.Max(1, Math.Floor(decoder.PixelHeight * scale));
        }

        using var bitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            transform,
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage).AsTask(cancellationToken);

        var result = await engine.RecognizeAsync(bitmap).AsTask(cancellationToken);
        var blocks = result.Lines
            .Select(line => new OcrTextBlock(line.Text, 1.0, BoundingBoxForLine(line)))
            .ToArray();

        return new OcrProviderResult(result.Text ?? string.Empty, blocks, "windows:media-ocr");
    }

    private OcrEngine? ResolveEngine(string language)
    {
        var languageTag = ConvertLanguageTag(language);
        lock (_engineLock)
        {
            if (_engines.TryGetValue(languageTag, out var cached))
            {
                return cached;
            }

            OcrEngine? engine = null;
            try
            {
                engine = OcrEngine.TryCreateFromLanguage(new Language(languageTag));
            }
            catch (ArgumentException)
            {
                // Invalid BCP-47 tag; fall through to the user profile languages.
            }

            engine ??= OcrEngine.TryCreateFromUserProfileLanguages();
            _engines[languageTag] = engine;
            return engine;
        }
    }

    private static OcrBoundingBox? BoundingBoxForLine(OcrLine line)
    {
        if (line.Words.Count == 0)
        {
            return null;
        }

        var left = double.MaxValue;
        var top = double.MaxValue;
        var right = double.MinValue;
        var bottom = double.MinValue;
        foreach (var word in line.Words)
        {
            var rect = word.BoundingRect;
            left = Math.Min(left, rect.Left);
            top = Math.Min(top, rect.Top);
            right = Math.Max(right, rect.Right);
            bottom = Math.Max(bottom, rect.Bottom);
        }

        return new OcrBoundingBox(
            (int)Math.Floor(left),
            (int)Math.Floor(top),
            (int)Math.Ceiling(right - left),
            (int)Math.Ceiling(bottom - top));
    }

    private static string ConvertLanguageTag(string value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized.StartsWith("ja", StringComparison.Ordinal) ||
            normalized.StartsWith("jp", StringComparison.Ordinal))
        {
            return "ja-JP";
        }

        if (normalized.StartsWith("zh-tw", StringComparison.Ordinal) ||
            normalized.StartsWith("zh-hant", StringComparison.Ordinal))
        {
            return "zh-Hant";
        }

        if (normalized.StartsWith("zh", StringComparison.Ordinal))
        {
            return "zh-Hans";
        }

        if (normalized.StartsWith("en", StringComparison.Ordinal))
        {
            return "en-US";
        }

        if (normalized.StartsWith("ko", StringComparison.Ordinal))
        {
            return "ko-KR";
        }

        return string.IsNullOrWhiteSpace(value) ? "ja-JP" : value.Trim();
    }
}
