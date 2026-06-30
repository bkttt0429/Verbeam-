using System.Text;
using System.Text.Json;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using RapidOcrNet;
using SkiaSharp;
using Verbeam.Core.Models;
using Verbeam.Core.Options;
using Verbeam.Core.Services;

namespace Verbeam.Core.Providers;

public sealed class RapidOcrNetProvider : IOcrProvider, IShadowRepairOcrProvider, IDisposable
{
    private const int RealtimePresetDetectTargetShortSide = 1086;
    private const int RealtimePresetDetectMaxShortSide = 1440;
    private const int ArtVerticalRescueScale = 5;
    private const int ArtVerticalRescueBorder = 40;
    private const double ArtVerticalRescueMinGain = 1.0;
    private const int SparseGlyphRescueScale = 4;
    private const int SparseGlyphRedetectMinIntervalMs = 250;
    private const int RealtimeSideTextRescueTargetShortSide = 960;
    private const int RealtimeSideTextRescueMinWidth = 96;
    private const int RealtimeSideTextRescueMaxWidth = 220;

    private readonly RapidOcrNetOptions _options;
    private readonly string _contentRootPath;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private readonly RapidOcrRealtimeCache _realtimeCache = new();
    private RapidOcr? _engine;
    // Optional lightweight (tiny/small) detection-only engine used for the stage-1 coarse-locate
    // pass, so the expensive O(N^2) full-frame detect runs on narrow channels instead of medium's
    // 1792-wide convs. Region reading (stage 2) still uses _engine (medium). Toggled by env
    // VB_OCR_LOCATOR_DET (a det .onnx path relative to content root); null = use _engine (baseline).
    private RapidOcr? _locatorEngine;
    private TextRecognizer? _recognizer;
    // Catalog-driven per-language rec override: base language -> (model path, keys path), plus a lazy
    // cache of the loaded recognizer (a null cache entry = mapped but model not downloaded -> ch v5).
    private readonly Dictionary<string, (string Model, string Keys)> _langRecPaths = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OnnxRecognizer?> _langRecCache = new(StringComparer.Ordinal);
    private readonly object _langRecGate = new();
    private readonly object _defaultOnnxRecGate = new();
    private OnnxRecognizer? _defaultOnnxRec;
    private bool _defaultOnnxRecLoaded;
    private int _deviceId;
    private bool _initialized;
    private static readonly object RealtimeTraceFileLock = new();

    public RapidOcrNetProvider(
        OcrProviderDescriptor descriptor,
        RapidOcrNetOptions options,
        string contentRootPath)
    {
        Descriptor = descriptor;
        _options = options;
        _contentRootPath = contentRootPath;
    }

    public OcrProviderDescriptor Descriptor { get; }

    private string FullEngineName => Descriptor.Name.Equals("rapidocr-net-v6", StringComparison.OrdinalIgnoreCase)
        ? "rapidocr-net:ppocrv6-onnx"
        : "rapidocr-net:ppocrv5-onnx";

    private string IncrementalEngineName => FullEngineName + "-incremental";

    private string IncrementalHeldEngineName => IncrementalEngineName + "-held";

    public async Task<OcrProviderResult> RecognizeAsync(
        OcrProviderRequest request,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        using var stream = new MemoryStream(request.ImageBytes, writable: false);
        using var bitmap = SKBitmap.Decode(stream)
            ?? throw new InvalidOperationException("RapidOcrNet could not decode the OCR image.");

        // queueWait = time blocked on the single-run lock (a prior request's side-rescue or full
        // detect holds it, so a frame that ends on the cheap incremental path can still report high
        // latency ??this field separates that wait from real compute). providerMs = compute only.
        var lockSw = System.Diagnostics.Stopwatch.StartNew();
        await _runLock.WaitAsync(cancellationToken);
        var queueWaitMs = lockSw.ElapsedMilliseconds;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            OcrProviderResult result;
            if (request.Realtime && _options.RealtimeIncremental && _recognizer is not null)
            {
                result = RecognizeRealtimeFrame(bitmap, request);
                // ?寞?B temporal hold: on faint moving text the per-frame read churns (good read on an
                // occasional frame, garbage between). Hold the best recent result and emit it instead of
                // a churned downgrade, so the output stays on the good read. Cheap, lossless on accuracy
                // (only ever upgrades or holds), and cuts translation churn.
                result = ApplyRealtimeTemporalHold(request, result);
            }
            else
            {
                var detect = _engine!.Detect(bitmap, BuildOptions(request));
                var providerResult = BuildProviderResult(detect, FullEngineName);
                result = TryApplyJapaneseSparseGlyphRescue(bitmap, providerResult, request)
                    ?? TryApplyJapaneseArtVerticalRescue(bitmap, providerResult, request)
                    ?? providerResult;
            }

            return result with
            {
                Timing = (result.Timing ?? new OcrProviderTiming()) with
                {
                    ProviderMs = lockSw.ElapsedMilliseconds - queueWaitMs,
                    QueueWaitMs = queueWaitMs
                }
            };
        }
        finally
        {
            _runLock.Release();
        }
    }

    public async Task<OcrShadowRepairProviderResult?> RecognizeShadowRepairAsync(
        OcrShadowRepairProviderRequest request,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        using var stream = new MemoryStream(request.ImageBytes, writable: false);
        using var source = SKBitmap.Decode(stream)
            ?? throw new InvalidOperationException("RapidOcrNet could not decode the OCR image.");

        var crop = new SKRectI(
            request.CropX,
            request.CropY,
            request.CropX + request.CropWidth,
            request.CropY + request.CropHeight);
        crop = ClampRect(crop, source.Width, source.Height);
        if (crop.Width < 8 || crop.Height < 8)
        {
            return null;
        }

        if (request.RequireBrightRealtimeCandidate && !LooksLikeBrightRealtimeRepairCandidate(source, crop))
        {
            return null;
        }

        using var repaired = RenderClaheCandidateToBitmap(source, crop, request.Scale);
        var providerRequest = new OcrProviderRequest(
            request.ImageBytes,
            request.ImageMimeType,
            request.Language,
            request.NormalizeWhitespace,
            PreprocessingPreset: "none",
            request.Realtime,
            request.SessionKey);

        await _runLock.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = _engine!.Detect(repaired, BuildOptions(providerRequest));
            return new OcrShadowRepairProviderResult(
                BuildProviderResult(result, FullEngineName),
                request.CandidateName,
                request.Scale,
                crop.Left,
                crop.Top,
                source.Width,
                source.Height,
                repaired.Width,
                repaired.Height);
        }
        finally
        {
            _runLock.Release();
        }
    }

    private static OcrProviderResult BuildProviderResult(OcrResult result, string engine)
    {
        var blocks = result.TextBlocks
            .Where(block => HasMeaningfulContent(block.Text))
            .Select(block => new OcrTextBlock(
                block.Text,
                ConfidenceFor(block),
                BoundingBoxFrom(block.BoxPoints)))
            .ToArray();
        // Build the joined text from the meaningful blocks (not engine StrRes) so dropped
        // punctuation-only boxes don't leak back in through the concatenated string.
        var text = string.Join(Environment.NewLine, blocks.Select(block => block.Text));

        return new OcrProviderResult(text, blocks, engine);
    }

    private OcrProviderResult? TryApplyJapaneseArtVerticalRescue(
        SKBitmap bitmap,
        OcrProviderResult primary,
        OcrProviderRequest request)
    {
        if (request.Realtime ||
            !ShouldTryJapaneseArtVerticalRescue(primary, request.Language) ||
            primary.Blocks.Count is < 2 or > 8 ||
            !TryBuildArtVerticalRescueCandidate(primary.Blocks, bitmap.Width, bitmap.Height, out var candidate))
        {
            return null;
        }

        using var rescueBitmap = RenderArtVerticalRescueBitmap(bitmap, candidate);
        var rescueRequest = request with
        {
            Language = "ja-JP",
            PreprocessingPreset = "none",
            Realtime = false
        };
        var rescueRaw = _engine!.Detect(rescueBitmap, BuildOptions(rescueRequest));
        var rescue = BuildProviderResult(rescueRaw, primary.Engine);
        var rescueBlocks = rescue.Blocks
            .Where(block => HasJapaneseScript(block.Text))
            .Select(block => MapArtVerticalRescueBlock(block, candidate, bitmap.Width, bitmap.Height))
            .Where(block => block.BoundingBox is not null && HasMeaningfulContent(block.Text))
            .ToArray();
        if (rescueBlocks.Length == 0)
        {
            return null;
        }

        var replacedPrimaryBlocks = primary.Blocks
            .Where(block => block.BoundingBox is not null && ArtVerticalRescueOverlaps(block.BoundingBox!, candidate.Crop))
            .ToArray();
        var primaryJapaneseChars = replacedPrimaryBlocks.Sum(block => JapaneseCharCount(block.Text));
        var rescueJapaneseChars = rescueBlocks.Sum(block => JapaneseCharCount(block.Text));
        if (rescueJapaneseChars < primaryJapaneseChars + ArtVerticalRescueMinGain)
        {
            return null;
        }

        var merged = primary.Blocks
            .Where(block => block.BoundingBox is null || !ArtVerticalRescueOverlaps(block.BoundingBox!, candidate.Crop))
            .Concat(rescueBlocks)
            .OrderBy(block => block.BoundingBox?.Y ?? int.MaxValue)
            .ThenBy(block => block.BoundingBox?.X ?? int.MaxValue)
            .ToArray();
        if (merged.Length == 0)
        {
            return null;
        }

        var text = string.Join(Environment.NewLine, merged.Select(block => block.Text).Where(HasMeaningfulContent));
        if (JapaneseCharCount(text) <= JapaneseCharCount(primary.Text))
        {
            return null;
        }

        return new OcrProviderResult(text, merged, primary.Engine + "-art-vertical-rescue");
    }

    internal static bool ShouldTryJapaneseArtVerticalRescue(
        OcrProviderResult primary,
        string? language)
    {
        if (NormalizeLanguage(language) == "ja")
        {
            return true;
        }

        // UI and region routes can keep the global OCR language at zh-TW/auto even when the
        // frame is Japanese. A detected kana anchor is a strong enough signal to try the
        // Japanese sparse-column rescue, while plain CJK alone is not.
        return primary.Blocks.Any(block => JapaneseKanaCount(block.Text) > 0);
    }

    private OcrProviderResult? TryApplyJapaneseSparseGlyphRescue(
        SKBitmap bitmap,
        OcrProviderResult primary,
        OcrProviderRequest request)
    {
        if (request.Realtime ||
            _recognizer is null ||
            !ShouldConsiderJapaneseSparseGlyphRescue(request.Language, primary.Blocks.Select(block => block.Text)) ||
            primary.Blocks.Count > 8)
        {
            return null;
        }

        var candidates = new List<(SparseGlyphCandidate Candidate, string Tag)>(4);
        if (TryBuildSparseGlyphCandidate(bitmap, out var projectedCandidate) &&
            ShouldUseSparseGlyphRescueOrientation(projectedCandidate.Orientation))
        {
            candidates.Add((projectedCandidate, "full-proj"));
        }

        foreach (var lineCandidate in BuildSparseGlyphCandidatesFromBlocks(bitmap, primary.Blocks))
        {
            candidates.Add((lineCandidate, "full-lines"));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in candidates.Where(entry => seen.Add(SparseGlyphCandidateLineKey(entry.Candidate))))
        {
            var candidate = entry.Candidate;
            if (!ShouldAttemptSparseGlyphRescueForPrimary(primary.Text, primary.Blocks.Count, candidate))
            {
                SparseRescueDebug($"no-attempt builder={entry.Tag} centers={candidate.Centers.Count} cur=\"{primary.Text.Replace(Environment.NewLine, "|")}\"");
                continue;
            }

            var rescued = RecognizeSparseGlyphCandidate(bitmap, candidate, request.Language);
            if (rescued is null)
            {
                SparseRescueDebug($"builder={entry.Tag} centers={candidate.Centers.Count} window={candidate.WindowSize} slots={SparseGlyphCenterSummary(candidate)} cur=\"{primary.Text.Replace(Environment.NewLine, "|")}\" rescued=\"\" accepted=False");
                continue;
            }

            var accepted = ShouldAcceptSparseGlyphRescue(primary.Text, rescued.Value.Text, candidate);
            SparseRescueDebug($"builder={entry.Tag} centers={candidate.Centers.Count} window={candidate.WindowSize} slots={SparseGlyphCenterSummary(candidate)} cur=\"{primary.Text.Replace(Environment.NewLine, "|")}\" rescued=\"{rescued.Value.Text}\" accepted={accepted}");
            if (!accepted)
            {
                continue;
            }

            var block = new OcrTextBlock(
                rescued.Value.Text,
                rescued.Value.Confidence,
                BoxFromRect(candidate.Bounds));
            return new OcrProviderResult(
                rescued.Value.Text,
                [block],
                primary.Engine + "-sparse-glyph-rescue");
        }

        return null;
    }

    internal static bool ShouldAttemptSparseGlyphRescueForPrimary(
        string primaryText,
        int primaryBlockCount,
        SparseGlyphCandidate candidate)
    {
        if (!ShouldUseSparseGlyphRescueOrientation(candidate.Orientation) ||
            candidate.Centers.Count is < 3 or > 12 ||
            primaryBlockCount > 8)
        {
            return false;
        }

        var primaryJapaneseChars = JapaneseCharCount(primaryText);
        if (primaryJapaneseChars == 0)
        {
            return true;
        }

        if (candidate.Centers.Count >= Math.Max(3, primaryJapaneseChars + 2))
        {
            return true;
        }

        return candidate.Centers.Count >= primaryJapaneseChars &&
            LooksLikeSparseJapaneseGlyphText(primaryText);
    }

    internal static bool ShouldAcceptSparseGlyphRescue(
        string primaryText,
        string rescuedText,
        SparseGlyphCandidate candidate)
    {
        if (!ShouldUseSparseGlyphRescueOrientation(candidate.Orientation) ||
            !HasMeaningfulContent(rescuedText) ||
            !HasJapaneseScript(rescuedText))
        {
            return false;
        }

        var primaryJapaneseChars = JapaneseCharCount(primaryText);
        var rescuedJapaneseChars = JapaneseCharCount(rescuedText);
        var primaryKana = JapaneseKanaCount(primaryText);
        var rescuedKana = JapaneseKanaCount(rescuedText);
        var primaryIdeographs = JapaneseIdeographCount(primaryText);
        var rescuedIdeographs = JapaneseIdeographCount(rescuedText);
        if (primaryKana > 0 &&
            primaryIdeographs > 0 &&
            rescuedIdeographs > primaryIdeographs)
        {
            return false;
        }

        if (candidate.Centers.Count >= primaryJapaneseChars + 2 &&
            (rescuedJapaneseChars < primaryJapaneseChars + 3 ||
                rescuedKana < primaryKana + 2))
        {
            return false;
        }

        if (rescuedJapaneseChars > primaryJapaneseChars)
        {
            return true;
        }

        return rescuedJapaneseChars >= Math.Max(3, primaryJapaneseChars) &&
            candidate.Centers.Count >= rescuedJapaneseChars &&
            LooksLikeSparseJapaneseGlyphText(primaryText);
    }

    // "Ink" (stroke) mask for atomic glyph segmentation. The old fixed `luminance < 205` only finds
    // dark-on-light text ??it misses the failing intro columns: low-contrast pale-grey kana (strokes
    // hover near the 205 cut, so atoms come and go) and light-on-dark stylised kana (strokes are
    // BRIGHTER than the background, so `< 205` inverts and yields zero atoms). Instead, read the
    // crop's luminance distribution, decide ink polarity (dark vs light text), and threshold
    // adaptively so faint strokes of either polarity become atoms. The downstream component
    // size/aspect/orientation filters reject the extra noise an adaptive cut can admit.
    private static bool[] BuildSparseInkMask(SKBitmap bitmap, int width, int height)
    {
        var total = width * height;
        var luminance = new int[total];
        var histogram = new int[256];
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                var color = bitmap.GetPixel(x, y);
                var value = (color.Red + color.Green + color.Blue) / 3;
                luminance[row + x] = value;
                histogram[value]++;
            }
        }

        // Median ??the dominant background level.
        var median = PercentileFromHistogram(histogram, total, 0.5);
        var darkCount = 0;
        for (var i = 0; i < median; i++)
        {
            darkCount += histogram[i];
        }

        var brightCount = total - darkCount - histogram[median];
        // Bright scene ??dark ink; dark scene ??light ink; mid ??ink is the minority extreme (text is
        // a small fraction of the crop).
        var inkIsDark = median >= 128 || (median > 96 && darkCount <= brightCount);

        var mask = new bool[total];
        if (inkIsDark)
        {
            // Ink ceiling: the 18th-percentile luminance, but kept at least 16 below the background
            // so a stroke only slightly darker than a pale background still separates.
            var threshold = Math.Clamp(Math.Min(PercentileFromHistogram(histogram, total, 0.18), median - 16), 24, 235);
            for (var i = 0; i < total; i++)
            {
                mask[i] = luminance[i] <= threshold;
            }
        }
        else
        {
            var threshold = Math.Clamp(Math.Max(PercentileFromHistogram(histogram, total, 0.82), median + 16), 20, 231);
            for (var i = 0; i < total; i++)
            {
                mask[i] = luminance[i] >= threshold;
            }
        }

        return mask;
    }

    private static int PercentileFromHistogram(int[] histogram, int total, double percentile)
    {
        var rank = Math.Max(1, (int)(total * percentile));
        var accumulated = 0;
        for (var i = 0; i < 256; i++)
        {
            accumulated += histogram[i];
            if (accumulated >= rank)
            {
                return i;
            }
        }

        return 255;
    }

    internal static bool TryBuildSparseGlyphCandidate(SKBitmap bitmap, out SparseGlyphCandidate candidate)
    {
        candidate = default;
        var width = bitmap.Width;
        var height = bitmap.Height;
        if (width < 40 || height < 40 || width * height > 1_200_000)
        {
            return false;
        }

        var dark = BuildSparseInkMask(bitmap, width, height);

        var visited = new bool[dark.Length];
        var stack = new int[Math.Min(dark.Length, 8192)];
        var components = new List<SparseGlyphComponent>();
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var start = (y * width) + x;
                if (!dark[start] || visited[start])
                {
                    continue;
                }

                var count = 0;
                var minX = x;
                var maxX = x;
                var minY = y;
                var maxY = y;
                var sumX = 0L;
                var sumY = 0L;
                var top = 0;
                stack[top++] = start;
                visited[start] = true;
                while (top > 0)
                {
                    var index = stack[--top];
                    var px = index % width;
                    var py = index / width;
                    count++;
                    sumX += px;
                    sumY += py;
                    minX = Math.Min(minX, px);
                    maxX = Math.Max(maxX, px);
                    minY = Math.Min(minY, py);
                    maxY = Math.Max(maxY, py);

                    for (var dy = -1; dy <= 1; dy++)
                    {
                        var ny = py + dy;
                        if (ny < 0 || ny >= height)
                        {
                            continue;
                        }

                        for (var dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0)
                            {
                                continue;
                            }

                            var nx = px + dx;
                            if (nx < 0 || nx >= width)
                            {
                                continue;
                            }

                            var next = (ny * width) + nx;
                            if (!dark[next] || visited[next])
                            {
                                continue;
                            }

                            visited[next] = true;
                            if (top >= stack.Length)
                            {
                                Array.Resize(ref stack, Math.Min(dark.Length, stack.Length * 2));
                            }

                            stack[top++] = next;
                        }
                    }
                }

                var componentWidth = maxX - minX + 1;
                var componentHeight = maxY - minY + 1;
                if (!IsSparseGlyphComponent(width, height, minX, minY, componentWidth, componentHeight, count))
                {
                    continue;
                }

                components.Add(new SparseGlyphComponent(
                    sumX / (double)count,
                    sumY / (double)count,
                    minX,
                    minY,
                    componentWidth,
                    componentHeight,
                    count));
            }
        }

        if (components.Count < 3 || components.Count > 48)
        {
            return false;
        }

        var minCenterX = components.Min(component => component.CenterX);
        var maxCenterX = components.Max(component => component.CenterX);
        var minCenterY = components.Min(component => component.CenterY);
        var maxCenterY = components.Max(component => component.CenterY);
        var spanX = maxCenterX - minCenterX;
        var spanY = maxCenterY - minCenterY;
        var orientation = spanX > spanY * 1.25
            ? SparseGlyphOrientation.Horizontal
            : spanY > spanX * 1.25
                ? SparseGlyphOrientation.Vertical
                : SparseGlyphOrientation.Unknown;
        if (orientation == SparseGlyphOrientation.Unknown)
        {
            return false;
        }

        var tolerance = Math.Max(18, components.Select(component => Math.Max(component.Width, component.Height)).Order().ElementAt(components.Count / 2) * 2.0);
        var groups = ClusterSparseGlyphComponents(components, orientation, tolerance);
        if (groups.Count is < 3 or > 12)
        {
            return false;
        }

        var centers = groups
            .Select(group => new SKPointI(
                (int)Math.Round(group.Average(component => component.CenterX)),
                (int)Math.Round(group.Average(component => component.CenterY))))
            .Where(point => point.X > 10 && point.X < width - 10 && point.Y > 10 && point.Y < height - 10)
            .OrderBy(point => orientation == SparseGlyphOrientation.Vertical ? point.Y : point.X)
            .ToArray();
        if (centers.Length is < 3 or > 12)
        {
            return false;
        }

        var gaps = new List<int>();
        for (var i = 1; i < centers.Length; i++)
        {
            gaps.Add(orientation == SparseGlyphOrientation.Vertical
                ? centers[i].Y - centers[i - 1].Y
                : centers[i].X - centers[i - 1].X);
        }

        var medianGap = gaps.Count == 0 ? 56 : gaps.Order().ElementAt(gaps.Count / 2);
        var window = orientation == SparseGlyphOrientation.Vertical
            ? Math.Clamp((int)Math.Round(medianGap * 0.74), 34, 54)
            : Math.Clamp((int)Math.Round(medianGap * 0.68), 46, 72);
        var bounds = BoundsForSparseGlyphCenters(centers, window, width, height);
        candidate = new SparseGlyphCandidate(orientation, centers, window, bounds);
        return true;
    }

    private static bool IsSparseGlyphComponent(
        int imageWidth,
        int imageHeight,
        int x,
        int y,
        int width,
        int height,
        int area)
    {
        if (area < 6 || width < 1 || height < 3)
        {
            return false;
        }

        if ((width <= 3 && height >= 12) || (height <= 3 && width >= 12))
        {
            return false;
        }

        if (width > imageWidth * 0.4 || height > imageHeight * 0.25)
        {
            return false;
        }

        if (height > 80 && width <= 4)
        {
            return false;
        }

        if (width > 80 && height <= 4)
        {
            return false;
        }

        return y >= 12 || width <= imageWidth * 0.2;
    }

    private static List<List<SparseGlyphComponent>> ClusterSparseGlyphComponents(
        IReadOnlyList<SparseGlyphComponent> components,
        SparseGlyphOrientation orientation,
        double tolerance)
    {
        var groups = new List<List<SparseGlyphComponent>>();
        foreach (var component in components.OrderBy(component => orientation == SparseGlyphOrientation.Vertical ? component.CenterY : component.CenterX))
        {
            var axis = orientation == SparseGlyphOrientation.Vertical ? component.CenterY : component.CenterX;
            if (groups.Count == 0)
            {
                groups.Add([component]);
                continue;
            }

            var last = groups[^1];
            var lastAxis = last.Average(item => orientation == SparseGlyphOrientation.Vertical ? item.CenterY : item.CenterX);
            if (Math.Abs(axis - lastAxis) > tolerance)
            {
                groups.Add([component]);
            }
            else
            {
                last.Add(component);
            }
        }

        return groups;
    }

    private (string Text, double Confidence)? RecognizeSparseGlyphCandidate(
        SKBitmap bitmap,
        SparseGlyphCandidate candidate,
        string language,
        System.Diagnostics.Stopwatch? rescueSw = null)
    {
        // Already over budget before we even start the per-slot rec -> skip this candidate entirely.
        if (rescueSw is not null && RescueBudgetExceeded(rescueSw))
        {
            return null;
        }

        using var rendered = RenderSparseGlyphLine(bitmap, candidate);
        var line = RecognizeSparseGlyphRendered([rendered], language);
        if (candidate.Orientation != SparseGlyphOrientation.Vertical)
        {
            return line;
        }

        var glyphs = RenderSparseGlyphCharacters(bitmap, candidate);
        SKBitmap[]? alternateGlyphs = null;
        if (candidate.WindowSize < 76)
        {
            var alternateCandidate = candidate with { WindowSize = 76 };
            alternateGlyphs = RenderSparseGlyphCharacters(bitmap, alternateCandidate);
        }

        try
        {
            var chars = RecognizeSparseGlyphCharactersWithOnnx(glyphs, language, alternateGlyphs, rescueSw) ??
                RecognizeSparseGlyphCharacters(glyphs, language);
            var selected = SelectSparseGlyphRecognition(line, chars);
            SparseRescueDebug($"candidate-rec line=\"{line?.Text}\" chars=\"{chars?.Text}\" selected=\"{selected?.Text}\"");
            return selected;
        }
        finally
        {
            foreach (var glyph in glyphs)
            {
                glyph.Dispose();
            }

            if (alternateGlyphs is not null)
            {
                foreach (var glyph in alternateGlyphs)
                {
                    glyph.Dispose();
                }
            }
        }
    }

    private (string Text, double Confidence)? RecognizeSparseGlyphRendered(
        SKBitmap[] rendered,
        string language)
    {
        var recognized = _recognizer!.GetTextLines(rendered);
        if (recognized.Length == 0)
        {
            return null;
        }

        var text = RapidOcrRealtimePixels.TextFrom(recognized[0]);
        text = NormalizeJapaneseSparseGlyphText(text, language);
        if (!HasMeaningfulContent(text) || !HasJapaneseScript(text))
        {
            return null;
        }

        var confidence = RapidOcrRealtimePixels.ConfidenceFrom(recognized[0], 0);
        return confidence < 0.25 ? null : (text, confidence);
    }

    private (string Text, double Confidence)? RecognizeSparseGlyphCharacters(
        SKBitmap[] rendered,
        string language)
    {
        var recognized = _recognizer!.GetTextLines(rendered);
        if (recognized.Length == 0)
        {
            return null;
        }

        var builder = new StringBuilder(recognized.Length);
        var confidences = new List<double>(recognized.Length);
        foreach (var line in recognized)
        {
            var text = NormalizeJapaneseSparseGlyphText(RapidOcrRealtimePixels.TextFrom(line), language);
            if (!HasMeaningfulContent(text) || !HasJapaneseScript(text))
            {
                continue;
            }

            builder.Append(text);
            confidences.Add(RapidOcrRealtimePixels.ConfidenceFrom(line, 0));
        }

        if (builder.Length == 0)
        {
            return null;
        }

        var value = builder.ToString();
        var confidence = confidences.Count == 0 ? 0 : confidences.Average();
        return confidence < 0.25 ? null : (value, confidence);
    }

    private (string Text, double Confidence)? RecognizeSparseGlyphCharactersWithOnnx(
        SKBitmap[] rendered,
        string language,
        SKBitmap[]? alternateRendered = null,
        System.Diagnostics.Stopwatch? rescueSw = null)
    {
        var recognizer = ResolveLangRecognizer(language) ?? ResolveDefaultOnnxRecognizer();
        if (recognizer is null)
        {
            return null;
        }

        var builder = new StringBuilder(rendered.Length);
        var confidences = new List<double>(rendered.Length);
        for (var i = 0; i < rendered.Length; i++)
        {
            // Phase 1 budget: each per-slot ONNX rec is ~hundreds of ms; a tall column has many slots
            // (?2 with the alternate window). Bail mid-column once the rescue budget is spent so one
            // candidate can't run to 6-14s. A truncated read just fails the accept gate -> primary stands.
            if (rescueSw is not null && RescueBudgetExceeded(rescueSw))
            {
                break;
            }

            var preferKana = rendered.Length >= 3 && i > 0;
            var slot = RecognizeSparseGlyphSlotWithOnnxCore(rendered[i], recognizer, preferKana);
            if (alternateRendered is not null && i < alternateRendered.Length)
            {
                var alternate = RecognizeSparseGlyphSlotWithOnnxCore(alternateRendered[i], recognizer, preferKana);
                if (alternate is not null && (slot is null || alternate.Value.Score > slot.Value.Score))
                {
                    slot = alternate;
                }
            }

            if (slot is null)
            {
                continue;
            }

            builder.Append(slot.Value.Text);
            confidences.Add(slot.Value.Confidence);
        }

        if (builder.Length == 0)
        {
            return null;
        }

        var value = NormalizeJapaneseSparseGlyphText(builder.ToString(), language);
        if (!HasMeaningfulContent(value) || !HasJapaneseScript(value))
        {
            return null;
        }

        var confidence = confidences.Count == 0 ? 0 : confidences.Average();
        return confidence < 0.00001 ? null : (value, confidence);
    }

    internal static (string Text, double Confidence)? RecognizeSparseGlyphSlotWithOnnx(
        SKBitmap rendered,
        OnnxRecognizer recognizer,
        bool preferKana = false)
    {
        var selected = RecognizeSparseGlyphSlotWithOnnxCore(rendered, recognizer, preferKana);
        return selected is null ? null : (selected.Value.Text, selected.Value.Confidence);
    }

    private static (string Text, double Confidence, double Score)? RecognizeSparseGlyphSlotWithOnnxCore(
        SKBitmap rendered,
        OnnxRecognizer recognizer,
        bool preferKana)
    {
        var candidates = new List<(string Text, double Confidence, double Score)>();
        AddSparseGlyphSlotCandidate(candidates, recognizer, rendered, OnnxRecognizerDecodeMask.Japanese, variantScore: 0.4, preferKana);
        AddSparseGlyphSlotCandidate(candidates, recognizer, rendered, OnnxRecognizerDecodeMask.Kana, variantScore: 0.35, preferKana);

        using var center45 = CenterCrop(rendered, 0.45);
        AddSparseGlyphSlotCandidate(candidates, recognizer, center45, OnnxRecognizerDecodeMask.Kana, variantScore: 0.3, preferKana);

        using var center70 = CenterCrop(rendered, 0.70);
        AddSparseGlyphSlotCandidate(candidates, recognizer, center70, OnnxRecognizerDecodeMask.Kana, variantScore: 0.2, preferKana);

        using var center45ForThreshold = CenterCrop(rendered, 0.45);
        using var center45Threshold170 = ThresholdBitmap(center45ForThreshold, 170);
        AddSparseGlyphSlotCandidate(candidates, recognizer, center45Threshold170, OnnxRecognizerDecodeMask.Kana, variantScore: 0.1, preferKana);

        using var center70ForThreshold = CenterCrop(rendered, 0.70);
        using var center70Threshold150 = ThresholdBitmap(center70ForThreshold, 150);
        AddSparseGlyphSlotCandidate(candidates, recognizer, center70Threshold150, OnnxRecognizerDecodeMask.Kana, variantScore: 0.05, preferKana);

        if (candidates.Count == 0)
        {
            return null;
        }

        var selected = candidates.OrderByDescending(candidate => candidate.Score).First();
        SparseRescueDebug($"slot-rec preferKana={preferKana} candidates={string.Join(";", candidates.Select(candidate => $"{candidate.Text}:{candidate.Confidence:F4}:{candidate.Score:F2}"))} selected={selected.Text}");
        return selected;
    }

    private static void AddSparseGlyphSlotCandidate(
        List<(string Text, double Confidence, double Score)> candidates,
        OnnxRecognizer recognizer,
        SKBitmap bitmap,
        OnnxRecognizerDecodeMask mask,
        double variantScore,
        bool preferKana)
    {
        var result = recognizer.RecognizeConstrained(bitmap, mask, topK: 3);
        var text = NormalizeSparseGlyphSlotText(result.Text, mask);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var score = variantScore + (result.Confidence * 100.0);
        if (text.Length == 1)
        {
            score += 1.0;
        }

        if (text.All(IsJapaneseKana))
        {
            score += mask == OnnxRecognizerDecodeMask.Kana ? 4.0 : 2.5;
            if (preferKana)
            {
                score += 18.0;
            }
        }
        else if (text.All(IsCjkIdeograph))
        {
            score += mask == OnnxRecognizerDecodeMask.Japanese ? 3.3 : 1.0;
            if (preferKana)
            {
                score -= 10.0;
            }
        }
        else
        {
            score -= 2.0;
        }

        candidates.Add((text, result.Confidence, score));
    }

    private static string NormalizeSparseGlyphSlotText(string text, OnnxRecognizerDecodeMask mask)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = RemoveWhitespaceBetweenJapaneseScript(text.Trim());
        if (mask == OnnxRecognizerDecodeMask.Kana)
        {
            var kana = normalized.Where(IsJapaneseKana).ToArray();
            return kana.Length == 0 ? string.Empty : kana[^1].ToString();
        }

        var japanese = normalized.Where(ch => IsJapaneseKana(ch) || IsCjkIdeograph(ch)).ToArray();
        return japanese.Length == 0 ? string.Empty : japanese[0].ToString();
    }

    private static (string Text, double Confidence)? SelectSparseGlyphRecognition(
        (string Text, double Confidence)? line,
        (string Text, double Confidence)? chars)
    {
        if (line is null)
        {
            return chars;
        }

        if (chars is null)
        {
            return line;
        }

        var lineJapanese = JapaneseCharCount(line.Value.Text);
        var charsJapanese = JapaneseCharCount(chars.Value.Text);
        var lineKana = JapaneseKanaCount(line.Value.Text);
        var charsKana = JapaneseKanaCount(chars.Value.Text);
        if (charsJapanese >= lineJapanese && charsKana > lineKana)
        {
            return chars;
        }

        var lineScore = SparseGlyphRecognitionScore(line.Value);
        var charScore = SparseGlyphRecognitionScore(chars.Value);
        return charScore > lineScore + 0.2 ? chars : line;
    }

    private static double SparseGlyphRecognitionScore((string Text, double Confidence) candidate)
    {
        var japanese = JapaneseCharCount(candidate.Text);
        var kana = JapaneseKanaCount(candidate.Text);
        return (candidate.Confidence * 2.0) + (japanese * 1.25) + (kana * 0.35);
    }

    private static string NormalizeJapaneseSparseGlyphText(string text, string language)
    {
        var normalized = text.Trim();
        if (NormalizeLanguage(language) == "ja" || JapaneseKanaCount(normalized) > 0)
        {
            normalized = normalized.Replace('\uFF70', '\u30FC');
            normalized = RemoveWhitespaceBetweenJapaneseScript(normalized);
            normalized = NormalizeSparseJapaneseKanaConfusables(normalized);
        }

        return normalized;
    }

    private static string NormalizeSparseJapaneseKanaConfusables(string text)
    {
        if (text.Length > 12 || JapaneseKanaCount(text) == 0)
        {
            return text;
        }

        return text.Replace('\u529B', '\u30AB');
    }

    internal static string NormalizeJapaneseSparseGlyphTextForTests(string text, string language)
        => NormalizeJapaneseSparseGlyphText(text, language);

    private static bool LooksLikeSparseJapaneseGlyphText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var japaneseChars = JapaneseCharCount(text);
        if (japaneseChars is < 3 or > 12)
        {
            return false;
        }

        var compact = RemoveWhitespaceBetweenJapaneseScript(text);
        if (compact.Length > 18)
        {
            return false;
        }

        if (ContainsWhitespaceBetweenJapaneseScript(text))
        {
            return true;
        }

        return JapaneseKanaCount(text) > 0 && text.Any(IsCjkIdeograph);
    }

    private static bool ContainsWhitespaceBetweenJapaneseScript(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsWhiteSpace(text[i]) &&
                HasJapaneseScriptBefore(text, i) &&
                HasJapaneseScriptAfter(text, i))
            {
                return true;
            }
        }

        return false;
    }

    private static string RemoveWhitespaceBetweenJapaneseScript(string text)
    {
        if (text.Length < 3)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (char.IsWhiteSpace(ch) &&
                HasJapaneseScriptBefore(text, i) &&
                HasJapaneseScriptAfter(text, i))
            {
                continue;
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }

    private static bool HasJapaneseScriptBefore(string text, int index)
    {
        for (var i = index - 1; i >= 0; i--)
        {
            var ch = text[i];
            if (char.IsWhiteSpace(ch))
            {
                continue;
            }

            return IsJapaneseKana(ch) || IsCjkIdeograph(ch);
        }

        return false;
    }

    private static bool HasJapaneseScriptAfter(string text, int index)
    {
        for (var i = index + 1; i < text.Length; i++)
        {
            var ch = text[i];
            if (char.IsWhiteSpace(ch))
            {
                continue;
            }

            return IsJapaneseKana(ch) || IsCjkIdeograph(ch);
        }

        return false;
    }

    private static SKBitmap RenderSparseGlyphLine(SKBitmap source, SparseGlyphCandidate candidate)
    {
        var cropSize = candidate.WindowSize;
        var glyphGap = 8;
        var outputWidth = (cropSize * candidate.Centers.Count * SparseGlyphRescueScale) +
            (Math.Max(1, candidate.Centers.Count - 1) * glyphGap) +
            48;
        var outputHeight = (cropSize * SparseGlyphRescueScale) + 48;
        var output = new SKBitmap(new SKImageInfo(outputWidth, outputHeight, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using var canvas = new SKCanvas(output);
        canvas.Clear(SKColors.White);
#pragma warning disable CS0618 // This SkiaSharp version lacks the SKSamplingOptions DrawBitmap overload.
        using var paint = new SKPaint { IsAntialias = false, FilterQuality = SKFilterQuality.None };
#pragma warning restore CS0618
        var x = 24;
        foreach (var center in candidate.Centers)
        {
            var crop = CenteredRect(center, cropSize, source.Width, source.Height);
            var target = new SKRect(
                x,
                24,
                x + (crop.Width * SparseGlyphRescueScale),
                24 + (crop.Height * SparseGlyphRescueScale));
            canvas.DrawBitmap(source, crop, target, paint);
            x += (crop.Width * SparseGlyphRescueScale) + glyphGap;
        }

        canvas.Flush();
        return output;
    }

    private static SKBitmap[] RenderSparseGlyphCharacters(SKBitmap source, SparseGlyphCandidate candidate)
    {
        var cropSize = candidate.WindowSize;
        var outputSize = (cropSize * SparseGlyphRescueScale) + 48;
        var result = new SKBitmap[candidate.Centers.Count];
#pragma warning disable CS0618 // This SkiaSharp version lacks the SKSamplingOptions DrawBitmap overload.
        using var paint = new SKPaint { IsAntialias = false, FilterQuality = SKFilterQuality.None };
#pragma warning restore CS0618
        for (var i = 0; i < candidate.Centers.Count; i++)
        {
            var output = new SKBitmap(new SKImageInfo(outputSize, outputSize, SKColorType.Bgra8888, SKAlphaType.Opaque));
            using var canvas = new SKCanvas(output);
            canvas.Clear(SKColors.White);
            var crop = CenteredRect(candidate.Centers[i], cropSize, source.Width, source.Height);
            canvas.DrawBitmap(
                source,
                crop,
                new SKRect(24, 24, 24 + (crop.Width * SparseGlyphRescueScale), 24 + (crop.Height * SparseGlyphRescueScale)),
                paint);
            canvas.Flush();
            result[i] = output;
        }

        return result;
    }

    private static SKBitmap CenterCrop(SKBitmap source, double scale)
    {
        var clamped = Math.Clamp(scale, 0.1, 1.0);
        var width = Math.Max(1, (int)Math.Round(source.Width * clamped));
        var height = Math.Max(1, (int)Math.Round(source.Height * clamped));
        var left = Math.Max(0, (source.Width - width) / 2);
        var top = Math.Max(0, (source.Height - height) / 2);
        var output = new SKBitmap(new SKImageInfo(width, height, source.ColorType, source.AlphaType));
        using var canvas = new SKCanvas(output);
        canvas.Clear(SKColors.White);
        canvas.DrawBitmap(
            source,
            new SKRectI(left, top, left + width, top + height),
            new SKRect(0, 0, width, height));
        canvas.Flush();
        return output;
    }

    private static SKBitmap ThresholdBitmap(SKBitmap source, byte threshold)
    {
        var output = new SKBitmap(new SKImageInfo(source.Width, source.Height, source.ColorType, source.AlphaType));
        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                var pixel = source.GetPixel(x, y);
                var luminance = (int)Math.Round((pixel.Red * 0.299) + (pixel.Green * 0.587) + (pixel.Blue * 0.114));
                var value = luminance > threshold ? (byte)255 : (byte)0;
                output.SetPixel(x, y, new SKColor(value, value, value, 255));
            }
        }

        return output;
    }

    private static SKRectI BoundsForSparseGlyphCenters(IReadOnlyList<SKPointI> centers, int windowSize, int width, int height)
    {
        var bounds = CenteredRect(centers[0], windowSize, width, height);
        for (var i = 1; i < centers.Count; i++)
        {
            var rect = CenteredRect(centers[i], windowSize, width, height);
            bounds = new SKRectI(
                Math.Min(bounds.Left, rect.Left),
                Math.Min(bounds.Top, rect.Top),
                Math.Max(bounds.Right, rect.Right),
                Math.Max(bounds.Bottom, rect.Bottom));
        }

        return bounds;
    }

    private static SKRectI CenteredRect(SKPointI center, int size, int width, int height)
    {
        var left = Math.Clamp(center.X - (size / 2), 0, Math.Max(0, width - 1));
        var top = Math.Clamp(center.Y - (size / 2), 0, Math.Max(0, height - 1));
        var right = Math.Clamp(left + size, left + 1, width);
        var bottom = Math.Clamp(top + size, top + 1, height);
        left = Math.Max(0, right - size);
        top = Math.Max(0, bottom - size);
        return new SKRectI(left, top, right, bottom);
    }

    private static OcrBoundingBox BoxFromRect(SKRectI rect)
        => new(rect.Left, rect.Top, Math.Max(1, rect.Width), Math.Max(1, rect.Height));

    private static SKRectI RectFromBox(OcrBoundingBox box, int imageWidth, int imageHeight)
    {
        var left = Math.Clamp(box.X, 0, Math.Max(0, imageWidth - 1));
        var top = Math.Clamp(box.Y, 0, Math.Max(0, imageHeight - 1));
        var right = Math.Clamp(box.X + Math.Max(1, box.Width), left + 1, imageWidth);
        var bottom = Math.Clamp(box.Y + Math.Max(1, box.Height), top + 1, imageHeight);
        return new SKRectI(left, top, right, bottom);
    }

    private static bool ShouldConsiderJapaneseSparseGlyphRescue(string? language, IEnumerable<string> texts)
        => NormalizeLanguage(language) == "ja" || texts.Any(text => JapaneseKanaCount(text) > 0);

    internal static bool TryBuildArtVerticalRescueCandidate(
        IReadOnlyList<OcrTextBlock> blocks,
        int imageWidth,
        int imageHeight,
        out ArtVerticalRescueCandidate candidate)
    {
        candidate = default;
        var anchors = blocks
            .Where(block => block.BoundingBox is not null && HasJapaneseScript(block.Text))
            .Select(block => block.BoundingBox!)
            .Where(box => box.Width >= 8 && box.Height >= 8)
            .ToArray();
        if (anchors.Length < 2 || imageWidth < 48 || imageHeight < 80)
        {
            return false;
        }

        (OcrBoundingBox A, OcrBoundingBox B, double Score)? best = null;
        for (var i = 0; i < anchors.Length; i++)
        {
            for (var j = i + 1; j < anchors.Length; j++)
            {
                var a = anchors[i];
                var b = anchors[j];
                var avgWidth = (a.Width + b.Width) / 2.0;
                var avgHeight = (a.Height + b.Height) / 2.0;
                var centerXDelta = Math.Abs(BoxCenterX(a) - BoxCenterX(b));
                var centerYDelta = Math.Abs(BoxCenterY(a) - BoxCenterY(b));
                if (centerXDelta > Math.Max(36, avgWidth * 1.25) ||
                    centerYDelta < Math.Max(80, avgHeight * 2.0))
                {
                    continue;
                }

                var score = centerYDelta - (centerXDelta * 2.0);
                if (best is null || score > best.Value.Score)
                {
                    best = (a, b, score);
                }
            }
        }

        if (best is null)
        {
            return false;
        }

        var aligned = anchors
            .Where(box => Math.Abs(BoxCenterX(box) - ((BoxCenterX(best.Value.A) + BoxCenterX(best.Value.B)) / 2.0)) <=
                Math.Max(42, ((best.Value.A.Width + best.Value.B.Width) / 2.0) * 1.5))
            .ToArray();
        if (aligned.Length < 2)
        {
            return false;
        }

        var avgAnchorWidth = aligned.Average(box => (double)box.Width);
        var avgAnchorHeight = aligned.Average(box => (double)box.Height);
        var minX = aligned.Min(box => box.X);
        var minY = aligned.Min(box => box.Y);
        var maxY = aligned.Max(box => box.Y + box.Height);
        var spanY = maxY - minY;
        if (spanY < Math.Max(100, avgAnchorHeight * 3.0))
        {
            return false;
        }

        var cropWidth = Math.Clamp((int)Math.Round(avgAnchorWidth * 3.1), 120, Math.Min(imageWidth, 180));
        // For these airy art-title columns, a few pixels of extra top/left background can make
        // DBNet keep only the first kanji. Anchor off the detected glyph left edge and use
        // asymmetric vertical padding; this matches the successful column crop without
        // hand-coding coordinates.
        var left = (int)Math.Round(minX - avgAnchorWidth);
        var top = (int)Math.Round(minY - (avgAnchorHeight * 0.23));
        var bottom = (int)Math.Round(maxY + (avgAnchorHeight * 0.33));
        var crop = ClampRect(new SKRectI(left, top, left + cropWidth, bottom), imageWidth, imageHeight);
        if (crop.Width < 48 || crop.Height < 120 || crop.Height / (double)crop.Width < 1.45)
        {
            return false;
        }

        candidate = new ArtVerticalRescueCandidate(crop, ArtVerticalRescueScale, ArtVerticalRescueBorder);
        return true;
    }

    private static SKBitmap RenderArtVerticalRescueBitmap(SKBitmap source, ArtVerticalRescueCandidate candidate)
    {
        var outputWidth = (candidate.Crop.Width * candidate.Scale) + (candidate.Border * 2);
        var outputHeight = (candidate.Crop.Height * candidate.Scale) + (candidate.Border * 2);
        var output = new SKBitmap(new SKImageInfo(outputWidth, outputHeight, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using var canvas = new SKCanvas(output);
#pragma warning disable CS0618 // This SkiaSharp version lacks the SKSamplingOptions DrawBitmap overload.
        using var paint = new SKPaint
        {
            IsAntialias = false,
            FilterQuality = SKFilterQuality.None
        };
#pragma warning restore CS0618
        canvas.Clear(SKColors.White);
        canvas.DrawBitmap(
            source,
            candidate.Crop,
            new SKRect(
                candidate.Border,
                candidate.Border,
                candidate.Border + (candidate.Crop.Width * candidate.Scale),
                candidate.Border + (candidate.Crop.Height * candidate.Scale)),
            paint);
        canvas.Flush();
        return output;
    }

    private static OcrTextBlock MapArtVerticalRescueBlock(
        OcrTextBlock block,
        ArtVerticalRescueCandidate candidate,
        int imageWidth,
        int imageHeight)
    {
        if (block.BoundingBox is null)
        {
            return block;
        }

        var box = block.BoundingBox;
        var x = candidate.Crop.Left + (int)Math.Round((box.X - candidate.Border) / (double)candidate.Scale);
        var y = candidate.Crop.Top + (int)Math.Round((box.Y - candidate.Border) / (double)candidate.Scale);
        var width = Math.Max(1, (int)Math.Round(box.Width / (double)candidate.Scale));
        var height = Math.Max(1, (int)Math.Round(box.Height / (double)candidate.Scale));
        var mapped = ClampBox(new OcrBoundingBox(x, y, width, height), imageWidth, imageHeight);
        return block with { BoundingBox = mapped };
    }

    private static OcrBoundingBox ClampBox(OcrBoundingBox box, int imageWidth, int imageHeight)
    {
        var left = Math.Clamp(box.X, 0, Math.Max(0, imageWidth - 1));
        var top = Math.Clamp(box.Y, 0, Math.Max(0, imageHeight - 1));
        var right = Math.Clamp(box.X + Math.Max(1, box.Width), left + 1, imageWidth);
        var bottom = Math.Clamp(box.Y + Math.Max(1, box.Height), top + 1, imageHeight);
        return new OcrBoundingBox(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    private static bool ArtVerticalRescueOverlaps(OcrBoundingBox box, SKRectI crop)
    {
        var centerX = BoxCenterX(box);
        var centerY = BoxCenterY(box);
        return centerX >= crop.Left &&
            centerX <= crop.Right &&
            centerY >= crop.Top &&
            centerY <= crop.Bottom;
    }

    private static double BoxCenterX(OcrBoundingBox box) => box.X + (box.Width / 2.0);

    private static double BoxCenterY(OcrBoundingBox box) => box.Y + (box.Height / 2.0);

    private static int JapaneseCharCount(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var count = 0;
        foreach (var ch in text)
        {
            if (IsJapaneseKana(ch) || IsCjkIdeograph(ch))
            {
                count++;
            }
        }

        return count;
    }

    private static int JapaneseKanaCount(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var count = 0;
        foreach (var ch in text)
        {
            if (IsJapaneseKana(ch))
            {
                count++;
            }
        }

        return count;
    }

    private static int JapaneseIdeographCount(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var count = 0;
        foreach (var ch in text)
        {
            if (IsCjkIdeograph(ch))
            {
                count++;
            }
        }

        return count;
    }

    private static SKRectI ClampRect(SKRectI rect, int width, int height)
    {
        var left = Math.Clamp(rect.Left, 0, Math.Max(0, width - 1));
        var top = Math.Clamp(rect.Top, 0, Math.Max(0, height - 1));
        var right = Math.Clamp(rect.Right, left + 1, width);
        var bottom = Math.Clamp(rect.Bottom, top + 1, height);
        return new SKRectI(left, top, right, bottom);
    }

    private static bool LooksLikeBrightRealtimeRepairCandidate(SKBitmap source, SKRectI crop)
    {
        using var normalized = CopyCropToBgraBitmap(source, crop, crop.Width, crop.Height);
        var (pixels, rowBytes) = CopyBgraPixels(normalized);
        var stepX = Math.Max(1, normalized.Width / 550);
        var stepY = Math.Max(1, normalized.Height / 90);
        var samples = 0;
        var bright235 = 0;
        var bright245 = 0;
        var dark90 = 0;
        var sum = 0L;

        for (var y = 0; y < normalized.Height; y += stepY)
        {
            var row = y * rowBytes;
            for (var x = 0; x < normalized.Width; x += stepX)
            {
                var index = row + (x * 4);
                var blue = pixels[index];
                var green = pixels[index + 1];
                var red = pixels[index + 2];
                var gray = (int)Math.Round((0.299 * red) + (0.587 * green) + (0.114 * blue));

                samples++;
                sum += gray;
                if (gray >= 235)
                {
                    bright235++;
                }

                if (gray >= 245)
                {
                    bright245++;
                }

                if (gray <= 90)
                {
                    dark90++;
                }
            }
        }

        if (samples == 0)
        {
            return false;
        }

        var mean = sum / (double)samples;
        var bright235Ratio = bright235 / (double)samples;
        var bright245Ratio = bright245 / (double)samples;
        var dark90Ratio = dark90 / (double)samples;
        return mean >= 145 &&
            bright235Ratio >= 0.45 &&
            bright245Ratio >= 0.18 &&
            dark90Ratio >= 0.02;
    }

    private static SKBitmap RenderClaheCandidateToBitmap(SKBitmap source, SKRectI crop, double scale)
    {
        var width = Math.Max(1, (int)Math.Round(crop.Width * Math.Max(1.0, scale)));
        var height = Math.Max(1, (int)Math.Round(crop.Height * Math.Max(1.0, scale)));
        using var scaled = CopyCropToBgraBitmap(source, crop, width, height);
        var gray = ExtractGrayscale(scaled);
        var enhanced = ApplyClahe(gray, width, height);
        return EncodeGrayscaleBitmap(enhanced, width, height);
    }

    // A frame whose sampled luminance 5th??5th percentile spread is below this is treated as
    // low-contrast (text barely separable from background) and CLAHE-enhanced before OCR. Brightness-
    // agnostic by design: it keys off the spread, not the mean, so it also catches mid/dark scenes the
    // bright-only shadow-repair gate (LooksLikeBrightRealtimeRepairCandidate) misses.
    private const int LowContrastSpreadThreshold = 70;

    private static bool ShouldEnhanceLowContrast(SKBitmap source)
    {
        var stepX = Math.Max(1, source.Width / 160);
        var stepY = Math.Max(1, source.Height / 60);
        var histogram = new int[256];
        var count = 0;
        for (var y = 0; y < source.Height; y += stepY)
        {
            for (var x = 0; x < source.Width; x += stepX)
            {
                var color = source.GetPixel(x, y);
                var luminance = (int)Math.Round((0.299 * color.Red) + (0.587 * color.Green) + (0.114 * color.Blue));
                histogram[Math.Clamp(luminance, 0, 255)]++;
                count++;
            }
        }

        if (count < 64)
        {
            return false;
        }

        var lowRank = (int)(count * 0.05);
        var highRank = (int)(count * 0.95);
        var p5 = 0;
        var p95 = 255;
        var accumulated = 0;
        for (var i = 0; i < 256; i++)
        {
            accumulated += histogram[i];
            if (accumulated >= lowRank)
            {
                p5 = i;
                break;
            }
        }

        accumulated = 0;
        for (var i = 0; i < 256; i++)
        {
            accumulated += histogram[i];
            if (accumulated >= highRank)
            {
                p95 = i;
                break;
            }
        }

        return (p95 - p5) < LowContrastSpreadThreshold;
    }

    private static SKBitmap EnhanceLowContrast(SKBitmap source)
    {
        var rect = new SKRectI(0, 0, source.Width, source.Height);
        using var bgra = CopyCropToBgraBitmap(source, rect, source.Width, source.Height);
        var gray = ExtractGrayscale(bgra);
        var enhanced = ApplyClahe(gray, source.Width, source.Height);
        return EncodeGrayscaleBitmap(enhanced, source.Width, source.Height);
    }

    // Image preprocessing applied to the realtime frame BEFORE detect+rec, driven by the request
    // preset (the region sends it live from the preprocess dropdown). Returns null to leave the raw
    // frame. The caller shadows `bitmap` with the result so detection, recognition and the
    // incremental hash all run on the same image.
    private static SKBitmap? PreprocessForOcr(SKBitmap source, string preset)
    {
        switch ((preset ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "":
            case "none":
            case "subtitle":
                return null;
            case "threshold":
                return ApplyAdaptiveThreshold(source);
            case "isolate":
                return ApplyTextIsolation(source);
            case "contrast":
                return EnhanceLowContrast(source);
            case "denoise":
                return ApplyMedian(source);
            case "flatten":
                return ApplyRealtimeIlluminationFlatten(source);
            case "clahe-l":
            case "lab-clahe":
                return ApplyRealtimeLabClahe(source);
            case "upscale":
                return UpscaleForRealtimePreprocess(source);
            default:
                // screenshot / document / text-line and future explicit presets remain raw unless
                // they have a dedicated branch above.
                return null;
        }
    }

    private static SKBitmap UpscaleForRealtimePreprocess(SKBitmap source)
    {
        var width = source.Width;
        var height = source.Height;
        var (targetWidth, targetHeight) = RealtimePreprocessSize(width, height);
        if (targetWidth == width && targetHeight == height)
        {
            return CopyCropToBgraBitmap(source, new SKRectI(0, 0, width, height), width, height);
        }

        return ResizeForRealtimePreprocess(source, targetWidth, targetHeight);
    }

    private static (int Width, int Height) RealtimePreprocessSize(int width, int height)
        => RealtimePreprocessSize(width, height, RealtimePresetDetectTargetShortSide);

    private static (int Width, int Height) RealtimePreprocessSize(int width, int height, int targetShortSide)
    {
        var shortSide = Math.Min(width, height);
        if (shortSide <= 0)
        {
            return (width, height);
        }

        var normalizedTargetShortSide = Math.Clamp(
            targetShortSide,
            1,
            RealtimePresetDetectMaxShortSide);
        if (shortSide >= normalizedTargetShortSide)
        {
            return (width, height);
        }

        var scale = (double)normalizedTargetShortSide / shortSide;
        var targetWidth = Math.Max(1, (int)Math.Round(width * scale));
        var targetHeight = Math.Max(1, (int)Math.Round(height * scale));
        return (targetWidth, targetHeight);
    }

    private static SKBitmap ResizeForRealtimePreprocess(SKBitmap source, int width, int height)
    {
        var target = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using var canvas = new SKCanvas(target);
        using var image = SKImage.FromBitmap(source);
        using var paint = new SKPaint();
        canvas.Clear(SKColors.White);
        canvas.DrawImage(
            image,
            new SKRect(0, 0, source.Width, source.Height),
            new SKRect(0, 0, width, height),
            new SKSamplingOptions(SKCubicResampler.CatmullRom),
            paint);
        return target;
    }

    private int ResolveRealtimeDetectTargetShortSide(SKBitmap bitmap, bool usePresetDetectUpscale)
    {
        if (_options.DetTargetShortSide > 0)
        {
            return _options.DetTargetShortSide;
        }

        if (!usePresetDetectUpscale)
        {
            return 0;
        }

        var shortSide = Math.Min(bitmap.Width, bitmap.Height);
        if (shortSide <= 0)
        {
            return 0;
        }

        return shortSide < RealtimePresetDetectTargetShortSide
            ? Math.Min(RealtimePresetDetectTargetShortSide, RealtimePresetDetectMaxShortSide)
            : 0;
    }

    private static bool ShouldUsePresetDetectUpscale(string? preset, bool imageWasPreprocessed)
    {
        var normalized = (preset ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "flatten" or "upscale" => true,
            "clahe-l" or "lab-clahe" => true,
            "" or "none" or "subtitle" => imageWasPreprocessed,
            _ => false
        };
    }

    // Cheap brightness probe: subsample the luminance grid and report whether the scene is
    // predominantly light. Gates illumination flattening to bright (pale-background) frames, where a
    // smooth gradient can hide faint dark text; light-on-dark subtitles are left raw.
    private static bool IsBrightScene(SKBitmap source)
    {
        var width = source.Width;
        var height = source.Height;
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        using var bgra = CopyCropToBgraBitmap(source, new SKRectI(0, 0, width, height), width, height);
        var (pixels, rowBytes) = CopyBgraPixels(bgra);
        var stepX = Math.Max(1, width / 64);
        var stepY = Math.Max(1, height / 64);
        long total = 0;
        var samples = 0;
        for (var y = 0; y < height; y += stepY)
        {
            var row = y * rowBytes;
            for (var x = 0; x < width; x += stepX)
            {
                var index = row + (x * 4);
                total += (pixels[index] + pixels[index + 1] + pixels[index + 2]) / 3;
                samples++;
            }
        }

        return samples > 0 && (total / samples) >= 128;
    }

    private static SKBitmap ApplyRealtimeIlluminationFlatten(SKBitmap source)
        => ApplyRealtimeIlluminationFlatten(source, RealtimePresetDetectTargetShortSide);

    private static SKBitmap ApplyRealtimeIlluminationFlatten(SKBitmap source, int targetShortSide)
    {
        var width = source.Width;
        var height = source.Height;
        var (targetWidth, targetHeight) = RealtimePreprocessSize(width, height, targetShortSide);
        var gray = ExtractRealtimePreprocessGrayscale(source, targetWidth, targetHeight);
        return ApplyIlluminationFlatten(gray, targetWidth, targetHeight);
    }

    private static SKBitmap ApplyRealtimeLabClahe(SKBitmap source)
    {
        var width = source.Width;
        var height = source.Height;
        var (targetWidth, targetHeight) = RealtimePreprocessSize(width, height);
        var (red, green, blue) = ExtractRealtimePreprocessRgb(source, targetWidth, targetHeight);

        var luminance = new byte[targetWidth * targetHeight];
        var labA = new double[luminance.Length];
        var labB = new double[luminance.Length];
        for (var i = 0; i < luminance.Length; i++)
        {
            RgbToLab(red[i], green[i], blue[i], out var l, out labA[i], out labB[i]);
            luminance[i] = l;
        }

        var enhanced = ApplyClahe(luminance, targetWidth, targetHeight);
        for (var i = 0; i < enhanced.Length; i++)
        {
            LabToRgb(enhanced[i], labA[i], labB[i], out red[i], out green[i], out blue[i]);
        }

        return EncodeRgbBitmap(red, green, blue, targetWidth, targetHeight);
    }

    private static byte[] ExtractRealtimePreprocessGrayscale(SKBitmap source, int targetWidth, int targetHeight)
    {
        var width = source.Width;
        var height = source.Height;
        if (targetWidth == width && targetHeight == height)
        {
            var sourceGray = new byte[width * height];
            for (var y = 0; y < height; y++)
            {
                var row = y * width;
                for (var x = 0; x < width; x++)
                {
                    var color = source.GetPixel(x, y);
                    sourceGray[row + x] = GrayscaleFromRgb(color.Red, color.Green, color.Blue);
                }
            }

            return sourceGray;
        }

        var (red, green, blue) = ExtractRealtimePreprocessRgb(source, targetWidth, targetHeight);
        var gray = new byte[targetWidth * targetHeight];
        for (var i = 0; i < gray.Length; i++)
        {
            gray[i] = GrayscaleFromRgb(red[i], green[i], blue[i]);
        }

        return gray;
    }

    private static (byte[] Red, byte[] Green, byte[] Blue) ExtractRealtimePreprocessRgb(
        SKBitmap source,
        int targetWidth,
        int targetHeight)
    {
        var width = source.Width;
        var height = source.Height;
        var red = new byte[width * height];
        var green = new byte[red.Length];
        var blue = new byte[red.Length];
        for (var y = 0; y < height; y++)
        {
            var sourceRow = y * width;
            for (var x = 0; x < width; x++)
            {
                var index = sourceRow + x;
                var color = source.GetPixel(x, y);
                red[index] = color.Red;
                green[index] = color.Green;
                blue[index] = color.Blue;
            }
        }

        if (targetWidth == width && targetHeight == height)
        {
            return (red, green, blue);
        }

        blue = ResizeGrayscaleLanczos4(blue, width, height, targetWidth, targetHeight);
        green = ResizeGrayscaleLanczos4(green, width, height, targetWidth, targetHeight);
        red = ResizeGrayscaleLanczos4(red, width, height, targetWidth, targetHeight);
        ApplyLanczosRoundingBias(red);
        ApplyLanczosRoundingBias(green);
        ApplyLanczosRoundingBias(blue);
        return (red, green, blue);
    }

    private static byte GrayscaleFromRgb(byte red, byte green, byte blue)
        => (byte)Math.Clamp((int)Math.Round((0.299 * red) + (0.587 * green) + (0.114 * blue)), 0, 255);

    private static void ApplyLanczosRoundingBias(byte[] gray)
    {
        // OpenCV INTER_LANCZOS4 uses fixed-point interpolation tables and lands about one
        // luminance level darker than the continuous Lanczos4 math above. That one level is
        // enough to decide faint-stroke DBNet boxes, so compensate only after the upscaled path.
        for (var i = 0; i < gray.Length; i++)
        {
            if (gray[i] > 0)
            {
                gray[i]--;
            }
        }
    }

    private static void RgbToLab(byte red, byte green, byte blue, out byte luminance, out double a, out double b)
    {
        var r = SrgbToLinear(red);
        var g = SrgbToLinear(green);
        var bl = SrgbToLinear(blue);

        var x = (0.4124564 * r) + (0.3575761 * g) + (0.1804375 * bl);
        var y = (0.2126729 * r) + (0.7151522 * g) + (0.0721750 * bl);
        var z = (0.0193339 * r) + (0.1191920 * g) + (0.9503041 * bl);

        var fx = LabForward(x / 0.95047);
        var fy = LabForward(y);
        var fz = LabForward(z / 1.08883);

        var l = (116 * fy) - 16;
        a = 500 * (fx - fy);
        b = 200 * (fy - fz);
        luminance = (byte)Math.Clamp((int)Math.Round(l * 255.0 / 100.0), 0, 255);
    }

    private static void LabToRgb(byte luminance, double a, double b, out byte red, out byte green, out byte blue)
    {
        var l = luminance * 100.0 / 255.0;
        var fy = (l + 16) / 116.0;
        var fx = fy + (a / 500.0);
        var fz = fy - (b / 200.0);

        var x = 0.95047 * LabInverse(fx);
        var y = LabInverse(fy);
        var z = 1.08883 * LabInverse(fz);

        var linearRed = (3.2404542 * x) + (-1.5371385 * y) + (-0.4985314 * z);
        var linearGreen = (-0.9692660 * x) + (1.8760108 * y) + (0.0415560 * z);
        var linearBlue = (0.0556434 * x) + (-0.2040259 * y) + (1.0572252 * z);

        red = LinearToSrgbByte(linearRed);
        green = LinearToSrgbByte(linearGreen);
        blue = LinearToSrgbByte(linearBlue);
    }

    private static double SrgbToLinear(byte value)
    {
        var normalized = value / 255.0;
        return normalized <= 0.04045
            ? normalized / 12.92
            : Math.Pow((normalized + 0.055) / 1.055, 2.4);
    }

    private static byte LinearToSrgbByte(double value)
    {
        var clamped = Math.Clamp(value, 0, 1);
        var srgb = clamped <= 0.0031308
            ? 12.92 * clamped
            : (1.055 * Math.Pow(clamped, 1.0 / 2.4)) - 0.055;
        return (byte)Math.Clamp((int)Math.Round(srgb * 255), 0, 255);
    }

    private static double LabForward(double value)
    {
        const double Delta = 6.0 / 29.0;
        return value > Delta * Delta * Delta
            ? Math.Pow(value, 1.0 / 3.0)
            : (value / (3 * Delta * Delta)) + (4.0 / 29.0);
    }

    private static double LabInverse(double value)
    {
        const double Delta = 6.0 / 29.0;
        return value > Delta
            ? value * value * value
            : 3 * Delta * Delta * (value - (4.0 / 29.0));
    }

    // Illumination flattening: divide the grayscale by a large-window local mean (a background
    // estimate), so a smooth background gradient ??the thing that hides faint, low-contrast subtitle
    // text from DBNet ??normalizes to white and the strokes survive as clean dark marks. A light
    // unsharp then re-crisps strokes softened by upscaling. MEASURED to beat CLAHE/binarization on
    // gray-text-on-gradient (those over-enhance into noise or erase the strokes): on a real low-contrast
    // 蝮行??MV frame this flipped rapidocr-net's detector from reading 1 garbage char to detecting all
    // three text columns (????/ ?芰? / ?～?扼).
    private static SKBitmap ApplyIlluminationFlatten(SKBitmap source)
    {
        var width = source.Width;
        var height = source.Height;
        var rect = new SKRectI(0, 0, width, height);
        using var bgra = CopyCropToBgraBitmap(source, rect, width, height);
        var gray = ExtractGrayscale(bgra);
        return ApplyIlluminationFlatten(gray, width, height);
    }

    private static SKBitmap ApplyIlluminationFlatten(byte[] gray, int width, int height)
    {
        // Background estimate matching the measured best recipe: GaussianBlur(51x51, sigma auto).
        // This is intentionally not CLAHE/binarization; the division flattens the smooth gradient
        // while preserving faint dark strokes for DBNet.
        var background = GaussianBlur(gray, width, height, radius: 25, sigma: 8.0);

        var flat = new byte[gray.Length];
        for (var i = 0; i < gray.Length; i++)
        {
            var bg = Math.Max(1, (int)background[i]);
            flat[i] = (byte)Math.Clamp((int)Math.Round(gray[i] * 255.0 / bg), 0, 255);
        }

        // Unsharp mask matching the reference probe: flat*2 - GaussianBlur(flat, sigma=2).
        var soft = GaussianBlur(flat, width, height, radius: 6, sigma: 2.0);
        const double amount = 1.0;
        var sharp = new byte[flat.Length];
        for (var i = 0; i < flat.Length; i++)
        {
            sharp[i] = (byte)Math.Clamp((int)Math.Round(flat[i] + (amount * (flat[i] - soft[i]))), 0, 255);
        }

        return EncodeGrayscaleBitmap(sharp, width, height);
    }

    private sealed class ResizeContribution
    {
        public ResizeContribution(int[] indices, double[] weights)
        {
            Indices = indices;
            Weights = weights;
        }

        public int[] Indices { get; }

        public double[] Weights { get; }
    }

    private static byte[] ResizeGrayscaleLanczos4(
        byte[] source,
        int sourceWidth,
        int sourceHeight,
        int targetWidth,
        int targetHeight)
    {
        if (sourceWidth == targetWidth && sourceHeight == targetHeight)
        {
            return source.ToArray();
        }

        var xContrib = BuildLanczosContributions(sourceWidth, targetWidth, radius: 4);
        var temp = new double[sourceHeight * targetWidth];
        for (var y = 0; y < sourceHeight; y++)
        {
            var sourceRow = y * sourceWidth;
            var tempRow = y * targetWidth;
            for (var x = 0; x < targetWidth; x++)
            {
                var contribution = xContrib[x];
                double value = 0;
                for (var i = 0; i < contribution.Indices.Length; i++)
                {
                    value += source[sourceRow + contribution.Indices[i]] * contribution.Weights[i];
                }

                temp[tempRow + x] = value;
            }
        }

        var yContrib = BuildLanczosContributions(sourceHeight, targetHeight, radius: 4);
        var output = new byte[targetWidth * targetHeight];
        for (var y = 0; y < targetHeight; y++)
        {
            var contribution = yContrib[y];
            var outputRow = y * targetWidth;
            for (var x = 0; x < targetWidth; x++)
            {
                double value = 0;
                for (var i = 0; i < contribution.Indices.Length; i++)
                {
                    value += temp[(contribution.Indices[i] * targetWidth) + x] * contribution.Weights[i];
                }

                output[outputRow + x] = (byte)Math.Clamp((int)Math.Round(value), 0, 255);
            }
        }

        return output;
    }

    private static ResizeContribution[] BuildLanczosContributions(int sourceSize, int targetSize, int radius)
    {
        var contributions = new ResizeContribution[targetSize];
        var scale = (double)targetSize / sourceSize;
        for (var destination = 0; destination < targetSize; destination++)
        {
            var center = ((destination + 0.5) / scale) - 0.5;
            var left = (int)Math.Floor(center - radius + 1);
            var right = (int)Math.Floor(center + radius);
            var indices = new int[right - left + 1];
            var weights = new double[indices.Length];
            double sum = 0;
            for (var source = left; source <= right; source++)
            {
                var index = source - left;
                var weight = LanczosWeight(center - source, radius);
                indices[index] = Math.Clamp(source, 0, sourceSize - 1);
                weights[index] = weight;
                sum += weight;
            }

            if (Math.Abs(sum) > 1e-12)
            {
                for (var i = 0; i < weights.Length; i++)
                {
                    weights[i] /= sum;
                }
            }

            contributions[destination] = new ResizeContribution(indices, weights);
        }

        return contributions;
    }

    private static double LanczosWeight(double x, int radius)
    {
        var ax = Math.Abs(x);
        if (ax < 1e-7)
        {
            return 1;
        }

        if (ax >= radius)
        {
            return 0;
        }

        return Sinc(x) * Sinc(x / radius);
    }

    private static double Sinc(double x)
    {
        var px = Math.PI * x;
        return Math.Sin(px) / px;
    }

    private static byte[] GaussianBlur(byte[] gray, int width, int height, int radius, double sigma)
    {
        var kernel = BuildGaussianKernel(radius, sigma);
        var temp = new double[gray.Length];
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                double sum = 0;
                for (var k = -radius; k <= radius; k++)
                {
                    var xx = Reflect101Index(x + k, width);
                    sum += gray[row + xx] * kernel[k + radius];
                }

                temp[row + x] = sum;
            }
        }

        var output = new byte[gray.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                double sum = 0;
                for (var k = -radius; k <= radius; k++)
                {
                    var yy = Reflect101Index(y + k, height);
                    sum += temp[(yy * width) + x] * kernel[k + radius];
                }

                output[(y * width) + x] = (byte)Math.Clamp((int)Math.Round(sum), 0, 255);
            }
        }

        return output;
    }

    private static int Reflect101Index(int index, int length)
    {
        if (length <= 1)
        {
            return 0;
        }

        while (index < 0 || index >= length)
        {
            index = index < 0
                ? -index
                : (2 * length) - index - 2;
        }

        return index;
    }

    private static double[] BuildGaussianKernel(int radius, double sigma)
    {
        var kernel = new double[(radius * 2) + 1];
        var denom = 2 * sigma * sigma;
        double sum = 0;
        for (var i = -radius; i <= radius; i++)
        {
            var value = Math.Exp(-(i * i) / denom);
            kernel[i + radius] = value;
            sum += value;
        }

        for (var i = 0; i < kernel.Length; i++)
        {
            kernel[i] /= sum;
        }

        return kernel;
    }

    // Mean of a (2*radius+1) box window per pixel, O(N) via an integral image. Edge windows clamp to
    // the image and divide by the actual covered area.
    private static byte[] BoxBlurMean(byte[] gray, int width, int height, int radius)
    {
        var iw = width + 1;
        var sum = new long[iw * (height + 1)];
        for (var y = 1; y <= height; y++)
        {
            long rowSum = 0;
            for (var x = 1; x <= width; x++)
            {
                rowSum += gray[((y - 1) * width) + (x - 1)];
                sum[(y * iw) + x] = sum[((y - 1) * iw) + x] + rowSum;
            }
        }

        var output = new byte[gray.Length];
        for (var y = 0; y < height; y++)
        {
            var y0 = Math.Max(0, y - radius);
            var y1 = Math.Min(height - 1, y + radius);
            for (var x = 0; x < width; x++)
            {
                var x0 = Math.Max(0, x - radius);
                var x1 = Math.Min(width - 1, x + radius);
                long area = (long)(x1 - x0 + 1) * (y1 - y0 + 1);
                var s = sum[((y1 + 1) * iw) + (x1 + 1)] - sum[(y0 * iw) + (x1 + 1)] - sum[((y1 + 1) * iw) + x0] + sum[(y0 * iw) + x0];
                output[(y * width) + x] = (byte)(s / area);
            }
        }

        return output;
    }

    // Sauvola adaptive binarization: per-pixel local threshold T = m(1 + k(s/R - 1)) over a window,
    // computed in O(1) via integral images (sum + sum-of-squares). Robust to uneven / busy backgrounds
    // where a global threshold fails. Auto-detects polarity so text is black-on-white whether the
    // source is dark-on-light or light-on-dark.
    private static SKBitmap ApplyAdaptiveThreshold(SKBitmap source)
    {
        var width = source.Width;
        var height = source.Height;
        var rect = new SKRectI(0, 0, width, height);
        using var bgra = CopyCropToBgraBitmap(source, rect, width, height);
        var gray = ExtractGrayscale(bgra);

        var window = Math.Clamp((Math.Min(width, height) / 12) | 1, 15, 51); // odd window ~ text height
        const double k = 0.34;
        const double r = 128.0;

        var iw = width + 1;
        var sum = new double[iw * (height + 1)];
        var sumSq = new double[iw * (height + 1)];
        for (var y = 1; y <= height; y++)
        {
            double rowSum = 0;
            double rowSumSq = 0;
            for (var x = 1; x <= width; x++)
            {
                double v = gray[((y - 1) * width) + (x - 1)];
                rowSum += v;
                rowSumSq += v * v;
                sum[(y * iw) + x] = sum[((y - 1) * iw) + x] + rowSum;
                sumSq[(y * iw) + x] = sumSq[((y - 1) * iw) + x] + rowSumSq;
            }
        }

        var half = window / 2;
        var below = new bool[width * height];
        var belowCount = 0;
        for (var y = 0; y < height; y++)
        {
            var y0 = Math.Max(0, y - half);
            var y1 = Math.Min(height - 1, y + half);
            for (var x = 0; x < width; x++)
            {
                var x0 = Math.Max(0, x - half);
                var x1 = Math.Min(width - 1, x + half);
                double area = (x1 - x0 + 1) * (y1 - y0 + 1);
                var s = sum[((y1 + 1) * iw) + (x1 + 1)] - sum[(y0 * iw) + (x1 + 1)] - sum[((y1 + 1) * iw) + x0] + sum[(y0 * iw) + x0];
                var sq = sumSq[((y1 + 1) * iw) + (x1 + 1)] - sumSq[(y0 * iw) + (x1 + 1)] - sumSq[((y1 + 1) * iw) + x0] + sumSq[(y0 * iw) + x0];
                var mean = s / area;
                var std = Math.Sqrt(Math.Max(0, (sq / area) - (mean * mean)));
                var threshold = mean * (1 + (k * ((std / r) - 1)));
                var isBelow = gray[(y * width) + x] < threshold;
                below[(y * width) + x] = isBelow;
                if (isBelow)
                {
                    belowCount++;
                }
            }
        }

        var textIsDark = belowCount <= (width * height) / 2;
        var output = new byte[width * height];
        for (var i = 0; i < output.Length; i++)
        {
            var isText = textIsDark ? below[i] : !below[i];
            output[i] = isText ? (byte)0 : (byte)255;
        }

        return EncodeGrayscaleBitmap(output, width, height);
    }

    // Text isolation: a simpler fixed-offset local threshold with explicit global polarity ??keep the
    // strokes standing out from the local background on the text side, force everything else white.
    // Good when the subtitle colour is consistently brighter (or darker) than the busy scene.
    private static SKBitmap ApplyTextIsolation(SKBitmap source)
    {
        var width = source.Width;
        var height = source.Height;
        var rect = new SKRectI(0, 0, width, height);
        using var bgra = CopyCropToBgraBitmap(source, rect, width, height);
        var gray = ExtractGrayscale(bgra);

        long total = 0;
        for (var i = 0; i < gray.Length; i++)
        {
            total += gray[i];
        }
        var brightScene = (total / (double)gray.Length) >= 128;

        var window = Math.Clamp((Math.Min(width, height) / 10) | 1, 15, 61);
        var iw = width + 1;
        var sum = new double[iw * (height + 1)];
        for (var y = 1; y <= height; y++)
        {
            double rowSum = 0;
            for (var x = 1; x <= width; x++)
            {
                rowSum += gray[((y - 1) * width) + (x - 1)];
                sum[(y * iw) + x] = sum[((y - 1) * iw) + x] + rowSum;
            }
        }

        const double offset = 18;
        var half = window / 2;
        var output = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            var y0 = Math.Max(0, y - half);
            var y1 = Math.Min(height - 1, y + half);
            for (var x = 0; x < width; x++)
            {
                var x0 = Math.Max(0, x - half);
                var x1 = Math.Min(width - 1, x + half);
                double area = (x1 - x0 + 1) * (y1 - y0 + 1);
                var localMean = (sum[((y1 + 1) * iw) + (x1 + 1)] - sum[(y0 * iw) + (x1 + 1)] - sum[((y1 + 1) * iw) + x0] + sum[(y0 * iw) + x0]) / area;
                double value = gray[(y * width) + x];
                var isText = brightScene ? value < localMean - offset : value > localMean + offset;
                output[(y * width) + x] = isText ? (byte)0 : (byte)255;
            }
        }

        return EncodeGrayscaleBitmap(output, width, height);
    }

    // 3x3 median denoise on grayscale (kills speckle / compression noise before detection).
    private static SKBitmap ApplyMedian(SKBitmap source)
    {
        var width = source.Width;
        var height = source.Height;
        var rect = new SKRectI(0, 0, width, height);
        using var bgra = CopyCropToBgraBitmap(source, rect, width, height);
        var gray = ExtractGrayscale(bgra);
        var output = new byte[width * height];
        var win = new byte[9];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var n = 0;
                for (var dy = -1; dy <= 1; dy++)
                {
                    var yy = Math.Clamp(y + dy, 0, height - 1);
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var xx = Math.Clamp(x + dx, 0, width - 1);
                        win[n++] = gray[(yy * width) + xx];
                    }
                }
                Array.Sort(win);
                output[(y * width) + x] = win[4];
            }
        }
        return EncodeGrayscaleBitmap(output, width, height);
    }

    private static SKBitmap CopyCropToBgraBitmap(SKBitmap source, SKRectI crop, int width, int height)
    {
        var target = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using var canvas = new SKCanvas(target);
        using var image = SKImage.FromBitmap(source);
        using var paint = new SKPaint();
        canvas.Clear(SKColors.White);
        canvas.DrawImage(
            image,
            new SKRect(crop.Left, crop.Top, crop.Right, crop.Bottom),
            new SKRect(0, 0, width, height),
            new SKSamplingOptions(SKCubicResampler.Mitchell),
            paint);
        return target;
    }

    private static (byte[] Pixels, int RowBytes) CopyBgraPixels(SKBitmap bitmap)
    {
        var rowBytes = bitmap.RowBytes;
        var pixels = new byte[rowBytes * bitmap.Height];
        Marshal.Copy(bitmap.GetPixels(), pixels, 0, pixels.Length);
        return (pixels, rowBytes);
    }

    private static byte[] ExtractGrayscale(SKBitmap bitmap)
    {
        var (pixels, rowBytes) = CopyBgraPixels(bitmap);
        var gray = new byte[bitmap.Width * bitmap.Height];
        for (var y = 0; y < bitmap.Height; y++)
        {
            var sourceRow = y * rowBytes;
            var outputRow = y * bitmap.Width;
            for (var x = 0; x < bitmap.Width; x++)
            {
                var index = sourceRow + (x * 4);
                var blue = pixels[index];
                var green = pixels[index + 1];
                var red = pixels[index + 2];
                gray[outputRow + x] = (byte)Math.Clamp(
                    (int)Math.Round((0.299 * red) + (0.587 * green) + (0.114 * blue)),
                    0,
                    255);
            }
        }

        return gray;
    }

    private static byte[] ApplyClahe(byte[] gray, int width, int height)
    {
        const int bins = 256;
        const double clipLimit = 2.4;

        var tileColumns = Math.Clamp(width / 64, 1, 8);
        var tileRows = Math.Clamp(height / 32, 1, 8);
        var luts = new byte[tileColumns * tileRows][];

        for (var tileY = 0; tileY < tileRows; tileY++)
        {
            var y0 = tileY * height / tileRows;
            var y1 = (tileY + 1) * height / tileRows;
            for (var tileX = 0; tileX < tileColumns; tileX++)
            {
                var x0 = tileX * width / tileColumns;
                var x1 = (tileX + 1) * width / tileColumns;
                var histogram = new int[bins];
                for (var y = y0; y < y1; y++)
                {
                    var row = y * width;
                    for (var x = x0; x < x1; x++)
                    {
                        histogram[gray[row + x]]++;
                    }
                }

                var tileArea = Math.Max(1, (x1 - x0) * (y1 - y0));
                var clipThreshold = Math.Max(1, (int)Math.Round(clipLimit * tileArea / bins));
                var clipped = 0;
                for (var index = 0; index < bins; index++)
                {
                    if (histogram[index] <= clipThreshold)
                    {
                        continue;
                    }

                    clipped += histogram[index] - clipThreshold;
                    histogram[index] = clipThreshold;
                }

                var redistribute = clipped / bins;
                var remainder = clipped % bins;
                for (var index = 0; index < bins; index++)
                {
                    histogram[index] += redistribute;
                    if (index < remainder)
                    {
                        histogram[index]++;
                    }
                }

                var lut = new byte[bins];
                var cumulative = 0;
                for (var index = 0; index < bins; index++)
                {
                    cumulative += histogram[index];
                    lut[index] = (byte)Math.Clamp((int)Math.Round(cumulative * 255.0 / tileArea), 0, 255);
                }

                luts[(tileY * tileColumns) + tileX] = lut;
            }
        }

        var output = new byte[gray.Length];
        for (var y = 0; y < height; y++)
        {
            var tileYFloat = ((y + 0.5) * tileRows / height) - 0.5;
            var y0 = Math.Clamp((int)Math.Floor(tileYFloat), 0, tileRows - 1);
            var y1 = Math.Min(y0 + 1, tileRows - 1);
            var yWeight = Math.Clamp(tileYFloat - y0, 0, 1);

            for (var x = 0; x < width; x++)
            {
                var tileXFloat = ((x + 0.5) * tileColumns / width) - 0.5;
                var x0 = Math.Clamp((int)Math.Floor(tileXFloat), 0, tileColumns - 1);
                var x1 = Math.Min(x0 + 1, tileColumns - 1);
                var xWeight = Math.Clamp(tileXFloat - x0, 0, 1);
                var value = gray[(y * width) + x];

                var topLeft = luts[(y0 * tileColumns) + x0][value];
                var topRight = luts[(y0 * tileColumns) + x1][value];
                var bottomLeft = luts[(y1 * tileColumns) + x0][value];
                var bottomRight = luts[(y1 * tileColumns) + x1][value];
                var top = topLeft + ((topRight - topLeft) * xWeight);
                var bottom = bottomLeft + ((bottomRight - bottomLeft) * xWeight);
                output[(y * width) + x] = (byte)Math.Clamp(
                    (int)Math.Round(top + ((bottom - top) * yWeight)),
                    0,
                    255);
            }
        }

        return output;
    }

    private static SKBitmap EncodeGrayscaleBitmap(byte[] gray, int width, int height)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        var buffer = new byte[bitmap.RowBytes * height];
        for (var y = 0; y < height; y++)
        {
            var targetRow = y * bitmap.RowBytes;
            var sourceRow = y * width;
            for (var x = 0; x < width; x++)
            {
                var value = gray[sourceRow + x];
                var index = targetRow + (x * 4);
                buffer[index] = value;
                buffer[index + 1] = value;
                buffer[index + 2] = value;
                buffer[index + 3] = 255;
            }
        }

        Marshal.Copy(buffer, 0, bitmap.GetPixels(), buffer.Length);
        return bitmap;
    }

    private static SKBitmap EncodeRgbBitmap(byte[] red, byte[] green, byte[] blue, int width, int height)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        var buffer = new byte[bitmap.RowBytes * height];
        for (var y = 0; y < height; y++)
        {
            var targetRow = y * bitmap.RowBytes;
            var sourceRow = y * width;
            for (var x = 0; x < width; x++)
            {
                var sourceIndex = sourceRow + x;
                var index = targetRow + (x * 4);
                buffer[index] = blue[sourceIndex];
                buffer[index + 1] = green[sourceIndex];
                buffer[index + 2] = red[sourceIndex];
                buffer[index + 3] = 255;
            }
        }

        Marshal.Copy(buffer, 0, bitmap.GetPixels(), buffer.Length);
        return bitmap;
    }

    // ?寞?B temporal-hold state, keyed by realtime session. Guarded by _runLock (the only caller holds
    // it), so a plain dictionary is safe.
    private readonly Dictionary<string, (OcrProviderResult Result, double Score, DateTimeOffset At)> _temporalHold
        = new(StringComparer.Ordinal);
    private const double RealtimeTemporalHoldMs = 2500;

    // Score a realtime result by how much real text it carries (meaningful CJK/kana/alnum chars),
    // with confidence as a tiebreak. A churned faint-column downgrade (e.g. "瘥???) scores below the
    // committed good read ("?芰? ?～?扼") and is held; a genuine longer/clearer read scores higher and
    // commits.
    private static double ScoreRealtimeResult(OcrProviderResult result)
    {
        if (result.Blocks is not { Count: > 0 } blocks)
        {
            return 0;
        }

        double chars = 0;
        double conf = 0;
        foreach (var block in blocks)
        {
            foreach (var ch in block.Text ?? string.Empty)
            {
                if (!char.IsWhiteSpace(ch) && !char.IsPunctuation(ch) && !char.IsSymbol(ch))
                {
                    chars++;
                }
            }

            conf += block.Confidence;
        }

        return chars + (Math.Clamp(conf / blocks.Count, 0, 1) * 0.5);
    }

    private OcrProviderResult ApplyRealtimeTemporalHold(OcrProviderRequest request, OcrProviderResult result)
    {
        var key = string.IsNullOrWhiteSpace(request.SessionKey) ? "__default__" : request.SessionKey;
        var now = DateTimeOffset.UtcNow;
        var score = ScoreRealtimeResult(result);

        if (_temporalHold.TryGetValue(key, out var held) &&
            (now - held.At).TotalMilliseconds < RealtimeTemporalHoldMs &&
            score < held.Score)
        {
            // Current frame is a downgrade within the hold window -> emit the committed good read.
            // Do NOT refresh the timestamp, so a sustained genuine change still commits after the window.
            return held.Result;
        }

        _temporalHold[key] = (result, score, now);
        if (_temporalHold.Count > Math.Max(4, _options.RealtimeMaxSessions))
        {
            var oldest = _temporalHold.MinBy(entry => entry.Value.At).Key;
            _temporalHold.Remove(oldest);
        }

        return result;
    }

    private OcrProviderResult RecognizeRealtimeFrame(SKBitmap rawBitmap, OcrProviderRequest request)
    {
        var now = DateTimeOffset.UtcNow;
        var key = string.IsNullOrWhiteSpace(request.SessionKey)
            ? FormattableString.Invariant($"dims:{rawBitmap.Width}x{rawBitmap.Height}")
            : request.SessionKey;
        var layout = _realtimeCache.TryGet(key, now, _options.RealtimeSessionIdleMs);

        // Low-contrast pre-enhance (text ??background): DBNet/CRNN miss or misread faint text, so
        // CLAHE the whole frame BEFORE detect+rec when the sampled luminance spread is narrow.
        // Shadowing the param keeps the rest of this method (hash, det, rec, layout) on the SAME
        // bitmap, so the incremental hash stays consistent; high-contrast frames skip CLAHE entirely.
        // The using declaration disposes the enhanced copy on every return path.
        var preprocessSw = System.Diagnostics.Stopwatch.StartNew();
        using SKBitmap? enhanced = PreprocessForOcr(rawBitmap, request.PreprocessingPreset);
        var preprocessMs = preprocessSw.ElapsedMilliseconds;
        var bitmap = enhanced ?? rawBitmap;
        var displayScale = enhanced is not null && bitmap.Width > 0
            ? (double)rawBitmap.Width / bitmap.Width
            : 1.0;
        var usePresetDetectUpscale = displayScale >= 0.999 &&
            ShouldUsePresetDetectUpscale(request.PreprocessingPreset, enhanced is not null);

        // ROI lock (F): once a subtitle region is found, re-detect only inside it instead of
        // re-scanning the whole screen on every blank/changed frame ??that is what made a
        // whole-screen capture non-realtime. The whole frame is rescanned only periodically (to catch
        // a subtitle that moved/appeared elsewhere). Tightly-framed small crops are already cheap, so
        // scoping applies to large frames only.
        var isLargeFrame = Math.Min(bitmap.Width, bitmap.Height) > RealtimeTwoStageMinShortSide;
        var wholeScreenDue = layout is null ||
            (now - layout.LastWholeScreenScanAt).TotalMilliseconds > RealtimeWholeScreenRescanIntervalMs;
        IReadOnlyList<SKRectI>? scopedRoi =
            isLargeFrame &&
            !wholeScreenDue &&
            layout?.RoiBands is { Count: > 0 } lockedBands &&
            layout.Width == bitmap.Width &&
            layout.Height == bitmap.Height
                ? lockedBands.Select(band => band.Rect).ToArray()
                : null;

        var dimsMatch = layout is not null &&
            layout.Width == bitmap.Width &&
            layout.Height == bitmap.Height;
        var canHoldPrevious = dimsMatch && layout!.Lines.Count > 0;

        if (layout is null ||
            !dimsMatch ||
            layout.Lines.Count == 0)
        {
            // Pass the (dims-matched) layout even when its lines are empty so a momentary blank keeps
            // the locked ROI alive; a cold miss / resolution change passes null and re-scans wholescreen.
            return FullDetectFrame(
                bitmap,
                request,
                key,
                now,
                layout is null ? "miss" : "empty-layout",
                dimsMatch ? layout : null,
                usePresetDetectUpscale,
                displayScale,
                preprocessMs,
                restrictToRoi: scopedRoi);
        }

        var redetectInterval = Math.Max(
            _options.RealtimeRedetectIntervalMs,
            (long)Math.Round(layout.LastFullDetectMs * RealtimeRedetectCostMultiplier));
        // Stable-layout backoff (P3): a fixed-position subtitle re-detects to the SAME layout every time,
        // so the periodic full-detect (~400ms on a small band) is wasted re-confirmation. Stretch the
        // interval the longer the layout has held (capped); any structural change reset StableLayoutStreak
        // to 0 (in FullDetectFrame), restoring fast re-detect.
        var stabilityBackoff = 1 + Math.Min(layout.StableLayoutStreak, RealtimeRedetectStableBackoffMax);
        var effectiveRedetectInterval = redetectInterval * stabilityBackoff;
        var frameGapMs = (now - layout.LastFullDetectAt).TotalMilliseconds;
        if (frameGapMs > effectiveRedetectInterval)
        {
            Console.Error.WriteLine(
                FormattableString.Invariant($"[ocr-diag] interval fired: gap {frameGapMs:F0}ms > redetect {effectiveRedetectInterval}ms (base {redetectInterval} x{stabilityBackoff} stable={layout.StableLayoutStreak}, lastDetMs {layout.LastFullDetectMs})."));
            return FullDetectFrame(bitmap, request, key, now, "interval", layout, usePresetDetectUpscale, displayScale, preprocessMs, restrictToRoi: scopedRoi);
        }

        var lines = layout.Lines;
        var quantizeStep = RapidOcrRealtimePixels.QuantizeStep(_options.RealtimeHashTolerance);
        var newHashes = new ulong[lines.Count];
        var changed = new List<int>();
        for (var i = 0; i < lines.Count; i++)
        {
            newHashes[i] = RapidOcrRealtimePixels.LineSignature(bitmap, lines[i].CropBox, quantizeStep);
            if (newHashes[i] != lines[i].Hash)
            {
                changed.Add(i);
            }
        }

        if (changed.Count > 0 &&
            ShouldForceSparseGlyphRedetect(bitmap, request, layout, now))
        {
            return FullDetectFrame(bitmap, request, key, now, "sparse-layout-shift", layout, usePresetDetectUpscale, displayScale, preprocessMs, restrictToRoi: scopedRoi);
        }

        // These two escalations key off raw luminance (changed-line ratio and
        // outside-cell drift). On live capture with an animated background they fire
        // every frame and collapse the fast path into a full detect every frame, so
        // they are suppressed by default; layout still refreshes via the periodic
        // re-detect interval and the (text-based) rec-empty guard below.
        if (!_options.RealtimeSuppressLuminanceRedetect)
        {
            if (changed.Count > 0 &&
                (double)changed.Count / lines.Count > _options.RealtimeChangedLineRatio)
            {
                return FullDetectFrame(bitmap, request, key, now, "layout-shift", layout, usePresetDetectUpscale, displayScale, preprocessMs, restrictToRoi: scopedRoi);
            }

            if (RapidOcrRealtimePixels.CountOutsideChangedCells(bitmap, layout) >= RapidOcrRealtimePixels.MinOutsideChangedCells)
            {
                return FullDetectFrame(bitmap, request, key, now, "outside-change", layout, usePresetDetectUpscale, displayScale, preprocessMs, restrictToRoi: scopedRoi);
            }
        }

        var incrementalBaseRecMs = 0L;
        var incrementalLangRecMs = 0L;
        var recCropShape = string.Empty;
        if (changed.Count > 0)
        {
            var crops = new SKBitmap[changed.Count];
            string[] texts;
            double[]? confidences;
            try
            {
                for (var i = 0; i < changed.Count; i++)
                {
                    crops[i] = RapidOcrRealtimePixels.CropRecognitionSubset(bitmap, lines[changed[i]].CropBox);
                }
                recCropShape = string.Join("+", crops.Where(crop => crop is not null).Select(crop => $"{crop.Width}x{crop.Height}"));

                var recSw = System.Diagnostics.Stopwatch.StartNew();
                var recognized = _recognizer!.GetTextLines(crops);
                incrementalBaseRecMs = recSw.ElapsedMilliseconds;
                texts = new string[changed.Count];
                confidences = new double[changed.Count];
                for (var i = 0; i < changed.Count; i++)
                {
                    texts[i] = i < recognized.Length ? RapidOcrRealtimePixels.TextFrom(recognized[i]) : string.Empty;
                    confidences[i] = i < recognized.Length
                        ? RapidOcrRealtimePixels.ConfidenceFrom(recognized[i], lines[changed[i]].Confidence)
                        : lines[changed[i]].Confidence;
                }

                var langRec = ResolveLangRecognizer(request.Language);
                if (langRec is not null)
                {
                    var langRecSw = System.Diagnostics.Stopwatch.StartNew();
                    var languageTexts = langRec.Recognize(crops);
                    incrementalLangRecMs = langRecSw.ElapsedMilliseconds;
                    for (var i = 0; i < changed.Count; i++)
                    {
                        var languageText = i < languageTexts.Length ? languageTexts[i] : string.Empty;
                        var selected = SelectRealtimeRecognitionText(
                            texts[i],
                            confidences[i],
                            languageText,
                            lines[changed[i]].CropBox,
                            request.Language);
                        texts[i] = selected.Text;
                        confidences[i] = selected.Confidence;
                    }
                }
            }
            finally
            {
                foreach (var crop in crops)
                {
                    crop?.Dispose();
                }
            }

            var emptied = 0;
            var scriptFlipped = 0;
            var confidenceCollapsed = false;
            var collapsedBoxIdx = -1;
            for (var i = 0; i < changed.Count; i++)
            {
                var line = lines[changed[i]];
                var text = i < texts.Length ? texts[i] : string.Empty;
                if (string.IsNullOrWhiteSpace(text) && !string.IsNullOrWhiteSpace(line.Text))
                {
                    emptied++;
                }
                // A line that was CJK now decoding to NO CJK at all - pure Latin letters
                // (I/l/T/F...) or stray punctuation (嚗? ???? - almost always means the reused
                // (stale) line box clipped a 摮? since these boxes come from an earlier full detect,
                // not a fresh one. Covers both "Chinese turns into English" and the spurious "嚗?"
                // symbols that show up when the dialogue changes. (Empty text -> emptied branch.)
                else if (HasCjk(line.Text) && !HasCjk(text))
                {
                    scriptFlipped++;
                }

                // Confidence-collapse: the incremental re-rec recycled a stale box whose real
                // subtitle has MOVED ??the read confidence tanks (e.g. ?芰? 0.96????0.574).
                // Requires both absolute low AND a significant drop so stable faint text whose
                // confidence is naturally low (~0.55) never fires the escalation.
                if (!confidenceCollapsed &&
                    _options.RealtimeStaleBoxConfidence > 0 &&
                    i < confidences!.Length)
                {
                    var drop = line.Confidence - confidences[i];
                    if (confidences[i] < _options.RealtimeStaleBoxConfidence &&
                        drop >= _options.RealtimeStaleBoxConfidenceDrop)
                    {
                        confidenceCollapsed = true;
                        collapsedBoxIdx = i;
                    }
                }
            }

            // PP-OCRv6-only tracking handles collapse through hold/relocate gating.

            // Geometry check on the collapsed box: a TALL box (ar < ~0.6) means vertical 蝮行??
            // column garble (e.g. ?芰???瘚? ??DBNet merges the column, AngleNet garbles rotation.
            // Re-locating would whole-screen re-detect the SAME garble at a DIFFERENT position and
            // lock onto it, losing the previous good read (measured: t=4.5 ?芰??elocate??瘚色?lost).
            // For tall garble the correct response is HOLD (keep previous good text); only NORMAL-
            // box collapse (real horizontal movement) should relocate.
            var collapsedIsTall = confidenceCollapsed &&
                collapsedBoxIdx >= 0 && collapsedBoxIdx < changed.Count &&
                lines[changed[collapsedBoxIdx]].CropBox.Height > 0 &&
                lines[changed[collapsedBoxIdx]].CropBox.Width <
                    lines[changed[collapsedBoxIdx]].CropBox.Height * 0.6;

            // P1/P2 fix ??frame-count debounce. "Changed lines went blank" (rec-empty) or
            // "CJK flipped to Latin/punctuation" (script-flip) usually means the subtitle is
            // mid-change (karaoke per-char reveal, fade), NOT that the layout died. Escalating to a
            // full re-detect on the FIRST such frame turns every changing subtitle into a per-frame
            // detect storm (the measured root cause of "OCR ?霈眼 + ?⊥? realtime"). So hold the
            // previous good text until the signal persists RealtimeRepairDebounceFrames frames, then
            // escalate. Confidence-collapse (stale-box) follows the same cadence ??a stale read on
            // one frame may be rec jitter, so it must survive the debounce window before escalating.
            var repairSignal = (emptied * 2 > changed.Count) || scriptFlipped > 0 || confidenceCollapsed;
            if (repairSignal)
            {
                layout.RepairSignalStreak++;
                if (layout.RepairSignalStreak < Math.Max(1, _options.RealtimeRepairDebounceFrames))
                {
                    // Hold: keep the previous good text, do NOT apply this frame's empty/flipped reads.
                    layout.LastSeenAt = now;
                    return BuildRealtimeResult(lines, IncrementalHeldEngineName) with
                    {
                        Timing = new OcrProviderTiming(
                            PreprocessMs: preprocessMs,
                            LanguageRecMs: incrementalBaseRecMs + incrementalLangRecMs,
                            RealtimeBaseRecMs: incrementalBaseRecMs,
                            RealtimeLangRecMs: incrementalLangRecMs,
                            RecCropShape: recCropShape)
                    };
                }

                // Debounced ??the signal is real, not a transient flicker. Branch by geometry:
                // tall-box collapse = vertical garble (HOLD, never relocate); normal-box collapse
                // or emptied/script-flip = real movement (whole-screen relocate, throttled).
                if (collapsedIsTall)
                {
                    // Vertical JP column garble: the detector itself produces wrong output on these,
                    // so a whole-screen re-detect would return the same garble elsewhere and lock it
                    // (measured: ?芰??elocate??瘚色??芰? lost). Hold the previous good text instead;
                    // the periodic 5s whole-screen rescan will eventually catch the real new position.
                    Console.Error.WriteLine(
                        FormattableString.Invariant(
                            $"[ocr] vertical-hold: tall box conf collapse, holding previous good text."));
                    layout.LastSeenAt = now;
                    return BuildRealtimeResult(lines, IncrementalHeldEngineName) with
                    {
                        Timing = new OcrProviderTiming(
                            PreprocessMs: preprocessMs,
                            LanguageRecMs: incrementalBaseRecMs + incrementalLangRecMs,
                            RealtimeBaseRecMs: incrementalBaseRecMs,
                            RealtimeLangRecMs: incrementalLangRecMs,
                            RecCropShape: recCropShape)
                    };
                }

                // Normal-box collapse or emptied/script-flip ??the subtitle genuinely moved.
                // Check the whole-screen relocate throttle: whole-screen scans are the single most
                // expensive operation and the relocate itself is a whole-screen scan, so reuse
                // LastWholeScreenScanAt as the throttle timer (only whole-screen FullDetectFrame calls
                // refresh it). This also repurposes the existing emptied/script-flip escalation from a
                // scoped re-detect to a whole-screen one (a scoped re-detect inside the stale band
                // cannot see a subtitle that has MOVED).
                var relocateGapMs = _options.RealtimeRelocateMinGapMs;
                if (relocateGapMs > 0)
                {
                    var sinceRelocate = (now - layout.LastWholeScreenScanAt).TotalMilliseconds;
                    if (sinceRelocate < relocateGapMs)
                    {
                        // Throttled: hold the previous good text instead of either (a) showing
                        // the collapsed/empty/flipped garbage read, or (b) burning a whole-screen
                        // scan that just ran.  The next tick will re-test the elapsed window.
                        layout.LastSeenAt = now;
                        return BuildRealtimeResult(lines, IncrementalHeldEngineName) with
                        {
                            Timing = new OcrProviderTiming(
                                PreprocessMs: preprocessMs,
                                LanguageRecMs: incrementalBaseRecMs + incrementalLangRecMs,
                                RealtimeBaseRecMs: incrementalBaseRecMs,
                                RealtimeLangRecMs: incrementalLangRecMs,
                                RecCropShape: recCropShape)
                        };
                    }
                }

                layout.RepairSignalStreak = 0;
                var repairReason = confidenceCollapsed ? "stale-relocate"
                    : emptied * 2 > changed.Count ? "rec-empty"
                    : "script-flip";
                Console.Error.WriteLine(
                    FormattableString.Invariant(
                        $"[ocr] stale-relocate ({repairReason}): whole-screen relocate after {changed.Count} changed box(es)."));
                return FullDetectFrame(bitmap, request, key, now, repairReason, layout, usePresetDetectUpscale, displayScale, preprocessMs, restrictToRoi: null);
            }

            for (var i = 0; i < changed.Count; i++)
            {
                var line = lines[changed[i]];
                if (i < texts.Length)
                {
                    line.Text = texts[i];
                    line.Confidence = confidences is not null ? confidences[i] : 1.0;
                }

                line.Hash = newHashes[changed[i]];
            }

            if (ShouldEscalateRealtimeNoiseFragments(lines, bitmap, request.Language))
            {
                return FullDetectFrame(bitmap, request, key, now, "noise-fragment", layout, usePresetDetectUpscale, displayScale, preprocessMs, restrictToRoi: scopedRoi);
            }
        }

        // Clean frame (no repair signal) ??the storm is over, reset the debounce streak.
        layout.RepairSignalStreak = 0;
        layout.LastSeenAt = now;
        // DIAG (temp): confirm the cheap incremental path actually engaged (vs every frame full-detect).
        Console.Error.WriteLine(
            FormattableString.Invariant($"[ocr-diag] incremental ran: gap {frameGapMs:F0}ms, {changed.Count} changed box(es)."));
        return BuildRealtimeResult(lines, IncrementalEngineName) with
        {
            Timing = new OcrProviderTiming(
                PreprocessMs: preprocessMs,
                LanguageRecMs: incrementalBaseRecMs + incrementalLangRecMs,
                RealtimeBaseRecMs: incrementalBaseRecMs,
                RealtimeLangRecMs: incrementalLangRecMs,
                RecCropShape: recCropShape)
        };
    }

    private bool ShouldForceSparseGlyphRedetect(
        SKBitmap bitmap,
        OcrProviderRequest request,
        RapidOcrRealtimeLayout layout,
        DateTimeOffset now)
    {
        if (_recognizer is null ||
            layout.Lines.Count is < 1 or > 3 ||
            !ShouldConsiderJapaneseSparseGlyphRescue(request.Language, layout.Lines.Select(line => line.Text)) ||
            !TryBuildSparseGlyphCandidate(bitmap, out var candidate))
        {
            return false;
        }

        var layoutOrientation = EstimateRealtimeLayoutOrientation(layout.Lines);
        if (layoutOrientation != SparseGlyphOrientation.Unknown &&
            candidate.Orientation != SparseGlyphOrientation.Unknown &&
            layoutOrientation != candidate.Orientation)
        {
            return true;
        }

        if ((now - layout.LastFullDetectAt).TotalMilliseconds < SparseGlyphRedetectMinIntervalMs)
        {
            return false;
        }

        var currentText = string.Join(Environment.NewLine, layout.Lines.Select(line => line.Text));
        return ShouldAttemptSparseGlyphRescueForPrimary(currentText, layout.Lines.Count, candidate) &&
            candidate.Centers.Any(center => !layout.Lines.Any(line => Contains(line.CropBox, center)));
    }

    // Opt-in (VB_SPARSE_DEBUG=1) diagnostic for the vertical-column sparse rescue: records which
    // candidate builder ran, slot count, the pre-rescue text, the rescued text and the accept
    // decision, so a live "still 蝢??? can be root-caused (no candidate / read empty / rejected)
    // without guessing. No-op in normal runs.
    private static void SparseRescueDebug(string message)
    {
        if (Environment.GetEnvironmentVariable("VB_SPARSE_DEBUG") != "1")
        {
            return;
        }

        try
        {
            System.IO.File.AppendAllText(
                @"D:\LocalTranslateHub\app\.codex-run\sparse-rescue-debug.log",
                $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
            // diagnostics must never break the OCR path
        }
    }

    // Fallback sparse-glyph candidate built from DET-found glyph boxes instead of the ink-mask
    // projection. The projection (TryBuildSparseGlyphCandidate) needs >=3 connected ink components,
    // so a vertical column whose thin/faint middle kana never form components ??DET frames only the
    // strong end glyphs, e.g. 蝢?????with ?? dropped ??slips through it. Here we take the
    // single-glyph Japanese boxes DET DID frame, confirm they sit on a near-vertical column, and
    // INTERPOLATE the missing slot centers from the inter-glyph pitch, handing the constrained
    // per-slot decoder real ROIs to read. The estimate is only adopted if the rescue then clears
    // ShouldAttempt/ShouldAccept, so a wrong slot count degrades to a no-op, not a regression.
    internal static bool TryBuildSparseGlyphCandidateFromLines(
        SKBitmap bitmap,
        List<RapidOcrRealtimeLine> lines,
        out SparseGlyphCandidate candidate)
    {
        candidate = default;
        var width = bitmap.Width;
        var height = bitmap.Height;

        var glyphs = lines
            .Where(line =>
            {
                var t = (line.Text ?? string.Empty).Trim();
                return t.Length == 1 && (IsJapaneseKana(t[0]) || IsCjkIdeograph(t[0]));
            })
            .Select(line => line.CropBox)
            .Where(box => box.Width > 6 && box.Height > 6)
            .OrderBy(box => box.Top + box.Bottom)
            .ToList();
        if (glyphs.Count is < 2 or > 12)
        {
            return false;
        }

        var midX = glyphs.Select(b => (b.Left + b.Right) / 2).ToList();
        var midY = glyphs.Select(b => (b.Top + b.Bottom) / 2).ToList();
        var spanX = midX.Max() - midX.Min();
        var spanY = midY.Max() - midY.Min();
        if (spanY <= 0 || spanX > spanY * 0.6)
        {
            return false; // not a (near-)vertical column
        }

        int pitch;
        if (glyphs.Count >= 3)
        {
            var gaps = new List<int>(glyphs.Count - 1);
            for (var i = 1; i < glyphs.Count; i++)
            {
                gaps.Add(midY[i] - midY[i - 1]);
            }

            var medianGlyph = glyphs
                .Select(b => b.Height)
                .OrderBy(v => v)
                .ElementAt(glyphs.Count / 2);
            var minPlausibleGap = Math.Max(12, (int)Math.Round(medianGlyph * 0.8));
            var maxPlausibleGap = Math.Max(minPlausibleGap + 1, (int)Math.Round(medianGlyph * 3.0));
            var plausibleGaps = gaps
                .Where(gap => gap >= minPlausibleGap && gap <= maxPlausibleGap)
                .OrderBy(gap => gap)
                .ToArray();

            // Sparse columns often arrive as anchors like bi/a/re: the missing shi/ku make the
            // first observed gap huge, so the median gap overestimates pitch and prevents
            // interpolation. Prefer the smallest plausible real adjacent slot gap.
            pitch = plausibleGaps.Length > 0
                ? plausibleGaps[0]
                : gaps.OrderBy(v => v).ElementAt(gaps.Count / 2);
        }
        else
        {
            // Only two anchors: infer the slot pitch from glyph HEIGHT ??the column runs vertically,
            // so spacing tracks the vertical extent (kana columns sit ~1.15x the glyph height apart),
            // NOT the box width. Coarse, but the accept guard backstops a bad count.
            var medianGlyph = glyphs
                .Select(b => b.Height)
                .OrderBy(v => v)
                .ElementAt(glyphs.Count / 2);
            var gap = Math.Abs(midY[1] - midY[0]);
            pitch = gap >= medianGlyph * 5
                ? (int)Math.Round(gap / 4.0)
                : (int)Math.Round(medianGlyph * 1.15);
        }

        if (pitch < 12)
        {
            return false;
        }

        var centers = new List<SKPointI>(glyphs.Count + 6);
        for (var i = 0; i < glyphs.Count; i++)
        {
            centers.Add(new SKPointI(midX[i], midY[i]));
            if (i + 1 >= glyphs.Count)
            {
                continue;
            }

            var gap = midY[i + 1] - midY[i];
            var slots = (int)Math.Round(gap / (double)pitch);
            for (var s = 1; s < slots; s++)
            {
                var f = s / (double)slots;
                centers.Add(new SKPointI(
                    midX[i] + (int)Math.Round((midX[i + 1] - midX[i]) * f),
                    midY[i] + (int)Math.Round(gap * f)));
            }
        }

        centers = centers
            .Where(p => p.X > 6 && p.X < width - 6 && p.Y > 6 && p.Y < height - 6)
            .OrderBy(p => p.Y)
            .ToList();

        // Worthwhile only when we actually filled gaps; otherwise the normal path already had every slot.
        if (centers.Count is < 3 or > 14 || centers.Count <= glyphs.Count)
        {
            return false;
        }

        var window = Math.Clamp((int)Math.Round(pitch * 0.74), 34, 54);
        var bounds = BoundsForSparseGlyphCenters(centers, window, width, height);
        candidate = new SparseGlyphCandidate(SparseGlyphOrientation.Vertical, centers, window, bounds);
        return true;
    }

    private static IReadOnlyList<SparseGlyphCandidate> BuildSparseGlyphCandidatesFromBlocks(
        SKBitmap bitmap,
        IReadOnlyList<OcrTextBlock> blocks)
    {
        var lines = blocks
            .Where(block => block.BoundingBox is not null)
            .Select(block => new RapidOcrRealtimeLine
            {
                CropBox = RectFromBox(block.BoundingBox!, bitmap.Width, bitmap.Height),
                DisplayBox = block.BoundingBox,
                Text = block.Text,
                Confidence = block.Confidence
            })
            .ToList();

        if (lines.Count == 0)
        {
            return [];
        }

        var candidates = new List<SparseGlyphCandidate>(4);
        if (TryBuildSparseGlyphCandidateFromLines(bitmap, lines, out var primaryCandidate))
        {
            candidates.Add(primaryCandidate);
        }

        candidates.AddRange(BuildAlternativeSparseGlyphCandidatesFromLines(bitmap, lines));
        return candidates
            .GroupBy(SparseGlyphCandidateLineKey)
            .Select(group => group.First())
            .OrderByDescending(ScoreSparseGlyphLineCandidate)
            .Take(3)
            .ToArray();
    }

    internal static IReadOnlyList<SparseGlyphCandidate> BuildAlternativeSparseGlyphCandidatesFromLines(
        SKBitmap bitmap,
        List<RapidOcrRealtimeLine> lines)
    {
        var glyphLines = lines
            .Where(line =>
            {
                var t = (line.Text ?? string.Empty).Trim();
                return t.Length == 1 && (IsJapaneseKana(t[0]) || IsCjkIdeograph(t[0])) && line.CropBox.Width > 6 && line.CropBox.Height > 6;
            })
            .ToList();
        if (glyphLines.Count < 3)
        {
            return [];
        }

        var widths = glyphLines
            .Select(line => line.CropBox.Width)
            .OrderBy(width => width)
            .ToArray();
        var medianWidth = widths[widths.Length / 2];
        var tolerance = Math.Clamp((int)Math.Round(medianWidth * 1.5), 24, 56);
        var candidates = new List<SparseGlyphCandidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var seed in glyphLines)
        {
            var seedX = (seed.CropBox.Left + seed.CropBox.Right) / 2;
            var cluster = glyphLines
                .Where(line => Math.Abs(((line.CropBox.Left + line.CropBox.Right) / 2) - seedX) <= tolerance)
                .OrderBy(line => line.CropBox.Top + line.CropBox.Bottom)
                .ToList();
            if (cluster.Count < 2 || cluster.Count >= glyphLines.Count)
            {
                continue;
            }

            if (TryBuildSparseGlyphCandidateFromLines(bitmap, cluster, out var candidate) &&
                seen.Add(SparseGlyphCandidateLineKey(candidate)))
            {
                candidates.Add(candidate);
            }
        }

        return candidates
            .OrderByDescending(ScoreSparseGlyphLineCandidate)
            .Take(3)
            .ToArray();
    }

    private static string SparseGlyphCandidateLineKey(SparseGlyphCandidate candidate)
        => $"{candidate.Orientation}:{candidate.WindowSize}:{string.Join(';', candidate.Centers.Select(p => $"{p.X / 3},{p.Y / 3}"))}";

    private static double ScoreSparseGlyphLineCandidate(SparseGlyphCandidate candidate)
    {
        var xs = candidate.Centers.Select(p => p.X).ToArray();
        var ys = candidate.Centers.Select(p => p.Y).ToArray();
        var spanX = xs.Max() - xs.Min();
        var spanY = ys.Max() - ys.Min();
        var verticality = spanY / (double)Math.Max(1, spanX + 1);
        var fiveSlotBias = -Math.Abs(candidate.Centers.Count - 5) * 1.25;
        return (candidate.Centers.Count * 4.0) + verticality + fiveSlotBias - (candidate.Bounds.Width * 0.03);
    }

    // When DET merges a faint vertical kana column into ONE multi-char box (e.g. "?～?扼" / "敹策" /
    // garbage like "4?交??) instead of per-glyph boxes, TryBuildSparseGlyphCandidateFromLines finds
    // <2 single-character glyphs and bails ("no-candidate" every frame ??the measured failure on the
    // 蝢郭 MV). This builder takes the tallest narrow column box directly and lays evenly-spaced
    // per-glyph slots down it from box geometry (a CJK/kana glyph is ~square, so slot pitch ??box
    // width), feeding the SAME constrained per-slot decoder. The slot count must land in [3,12] for
    // the downstream attempt gate; shorter columns fall through to the other builders.
    internal static bool TryBuildSparseGlyphCandidateFromColumnBox(
        List<RapidOcrRealtimeLine> lines,
        double displayScale,
        int frameHeight,
        out SparseGlyphCandidate candidate)
    {
        candidate = default;
        // Use the TIGHT detect box (DisplayBox), not CropBox: BuildLines over-pads a tall box
        // horizontally (padX = height/2), which makes the column's CropBox wider than tall and hides
        // its verticality. DisplayBox = tight box * displayScale, so divide back to bitmap coords.
        var inv = displayScale > 0 ? 1.0 / displayScale : 1.0;
        SKRectI? best = null;
        foreach (var line in lines)
        {
            if (line.DisplayBox is not { } db)
            {
                continue;
            }

            var w = (int)Math.Round(db.Width * inv);
            var h = (int)Math.Round(db.Height * inv);
            if (w < 10 || h < 40)
            {
                continue;
            }

            // A vertical column: clearly taller than wide. NOTE: no text-content gate here on purpose ??
            // a GARBLED column read (e.g. "敹策" / "4?交?? for ?～?扼) is exactly what must be rescued,
            // and gating on "has kana" would skip those. The whole path is already ja-gated upstream,
            // and the attempt/accept gates reject when the current read is already complete/better.
            if (h < w * 1.8)
            {
                continue;
            }

            var left = (int)Math.Round(db.X * inv);
            var top = (int)Math.Round(db.Y * inv);
            var box = new SKRectI(left, top, left + w, top + h);
            if (best is null || (long)box.Width * box.Height > (long)best.Value.Width * best.Value.Height)
            {
                best = box;
            }
        }

        if (best is not { } col)
        {
            return false;
        }

        // DET frequently boxes only the MIDDLE of a faint column (e.g. "敹策" covering the inner glyphs
        // of ?～?扼, missing the top ??and bottom ??. Expand the column vertically around its center
        // so the slot grid spans the full column; slots landing on empty margin decode to nothing and
        // drop out. Width (same x-column) is untouched, so this never reaches a neighbour like ?芰?.
        var centerY = (col.Top + col.Bottom) / 2;
        var expandedHeight = (int)Math.Round(col.Height * 1.6);
        var expTop = Math.Max(0, centerY - expandedHeight / 2);
        var expBottom = Math.Min(frameHeight > 0 ? frameHeight : col.Bottom + expandedHeight, centerY + expandedHeight / 2);
        col = new SKRectI(col.Left, expTop, col.Right, expBottom);

        // Vertical kana cells are a touch taller than the column is wide, so bias the pitch down from
        // the width to avoid undercounting slots (e.g. a 4-glyph column reading as 3).
        var pitch = Math.Max(12, (int)Math.Round(col.Width * 0.82));
        var slots = Math.Clamp((int)Math.Round(col.Height / (double)pitch), 1, 12);
        if (slots < 3)
        {
            return false; // <3 slots is rejected downstream; let other builders try
        }

        var midX = (col.Left + col.Right) / 2;
        var centers = new List<SKPointI>(slots);
        for (var i = 0; i < slots; i++)
        {
            var y = col.Top + (int)Math.Round((i + 0.5) * col.Height / slots);
            centers.Add(new SKPointI(midX, y));
        }

        var window = Math.Max(24, (int)Math.Round(col.Width * 1.15));
        candidate = new SparseGlyphCandidate(SparseGlyphOrientation.Vertical, centers, window, col);
        return true;
    }

    // Phase 1 hard budget: the combined sparse+side rescue must abort once this elapses so a faint
    // column cannot spin per-slot rec / extra DETs to 6-14s on a cold frame.
    private bool RescueBudgetExceeded(System.Diagnostics.Stopwatch rescueSw)
        => _options.RealtimeRescueBudgetMs > 0 && rescueSw.ElapsedMilliseconds >= _options.RealtimeRescueBudgetMs;

    private void TryApplySparseGlyphRescueToRealtimeLines(
        SKBitmap bitmap,
        List<RapidOcrRealtimeLine> lines,
        string language,
        double displayScale,
        System.Diagnostics.Stopwatch rescueSw)
    {
        if (_recognizer is null ||
            lines.Count > 8 ||
            !ShouldConsiderJapaneseSparseGlyphRescue(language, lines.Select(line => line.Text)))
        {
            return;
        }

        var currentText = string.Join(Environment.NewLine, lines.Select(line => line.Text));

        // Try the ink-mask projection first; if it produces nothing acceptable ??on small live columns
        // it over-segments into the wrong slot count, which then decodes empty ??FALL THROUGH to the
        // from-DET-lines geometry (clean 蝢???axis with the middle slots interpolated). A failed
        // projection must not block the cleaner fallback.
        if (RescueBudgetExceeded(rescueSw))
        {
            return;
        }

        if (TryBuildSparseGlyphCandidate(bitmap, out var projCandidate) &&
            TryRescueColumnWithCandidate(bitmap, lines, language, displayScale, currentText, projCandidate, "proj", rescueSw))
        {
            return;
        }

        var attemptedLineCandidate = false;
        if (!RescueBudgetExceeded(rescueSw) &&
            TryBuildSparseGlyphCandidateFromLines(bitmap, lines, out var lineCandidate))
        {
            attemptedLineCandidate = true;
            if (TryRescueColumnWithCandidate(bitmap, lines, language, displayScale, currentText, lineCandidate, "lines", rescueSw))
            {
                return;
            }
        }

        // DET merged the column into one multi-char box -> rescue it directly from box geometry.
        if (!RescueBudgetExceeded(rescueSw) &&
            TryBuildSparseGlyphCandidateFromColumnBox(lines, displayScale, bitmap.Height, out var columnCandidate))
        {
            attemptedLineCandidate = true;
            if (TryRescueColumnWithCandidate(bitmap, lines, language, displayScale, currentText, columnCandidate, "column-box", rescueSw))
            {
                return;
            }
        }

        var altIndex = 0;
        // Cap alternatives: a long column yields one candidate per glyph seed, each doing per-slot ONNX
        // rec ??uncapped that is the 6-14s explosion. Two is plenty; the budget also short-circuits.
        foreach (var altCandidate in BuildAlternativeSparseGlyphCandidatesFromLines(bitmap, lines).Take(2))
        {
            if (RescueBudgetExceeded(rescueSw))
            {
                break;
            }

            attemptedLineCandidate = true;
            altIndex++;
            if (TryRescueColumnWithCandidate(bitmap, lines, language, displayScale, currentText, altCandidate, $"lines-alt{altIndex}", rescueSw))
            {
                return;
            }
        }

        if (!attemptedLineCandidate)
        {
            SparseRescueDebug($"no-candidate lines={lines.Count} cur=\"{currentText.Replace(Environment.NewLine, "|")}\"");
        }
    }

    private bool TryRescueColumnWithCandidate(
        SKBitmap bitmap,
        List<RapidOcrRealtimeLine> lines,
        string language,
        double displayScale,
        string currentText,
        SparseGlyphCandidate candidate,
        string builderTag,
        System.Diagnostics.Stopwatch? rescueSw = null)
    {
        if (!ShouldUseSparseGlyphRescueOrientation(candidate.Orientation))
        {
            SparseRescueDebug($"bad-orient={candidate.Orientation} builder={builderTag}");
            return false;
        }

        // Scope the attempt/accept gates to the line(s) the candidate actually covers, not the whole
        // frame: a per-column candidate (e.g. 4 kana slots) must be compared against the COLUMN's text,
        // not the whole subtitle's char count (which includes a separate ?芰? block). A whole-subtitle
        // candidate covers every line, so this collapses back to the original whole-frame comparison.
        var coveredLines = lines.Where(existing =>
        {
            var cx = (existing.CropBox.Left + existing.CropBox.Right) / 2;
            var cy = (existing.CropBox.Top + existing.CropBox.Bottom) / 2;
            return cx >= candidate.Bounds.Left && cx <= candidate.Bounds.Right &&
                cy >= candidate.Bounds.Top && cy <= candidate.Bounds.Bottom;
        }).ToList();
        var scopedPrimary = coveredLines.Count > 0
            ? string.Join(string.Empty, coveredLines.Select(l => l.Text))
            : currentText;
        var scopedCount = coveredLines.Count > 0 ? coveredLines.Count : lines.Count;

        if (!ShouldAttemptSparseGlyphRescueForPrimary(scopedPrimary, scopedCount, candidate))
        {
            SparseRescueDebug($"no-attempt builder={builderTag} centers={candidate.Centers.Count} scoped=\"{scopedPrimary}\"");
            return false;
        }

        var rescued = RecognizeSparseGlyphCandidate(bitmap, candidate, language, rescueSw);
        if (rescued is null)
        {
            SparseRescueDebug($"builder={builderTag} centers={candidate.Centers.Count} window={candidate.WindowSize} slots={SparseGlyphCenterSummary(candidate)} cur=\"{currentText.Replace(Environment.NewLine, "|")}\" rescued=\"\" accepted=False");
            return false;
        }

        var rescuedValue = rescued.Value;
        var accepted = ShouldAcceptSparseGlyphRescue(scopedPrimary, rescuedValue.Text, candidate);
        SparseRescueDebug($"builder={builderTag} centers={candidate.Centers.Count} window={candidate.WindowSize} slots={SparseGlyphCenterSummary(candidate)} cur=\"{currentText.Replace(Environment.NewLine, "|")}\" rescued=\"{rescuedValue.Text}\" accepted={accepted}");
        if (!accepted)
        {
            return false;
        }

        var displayBox = new OcrBoundingBox(
            Math.Max(0, (int)Math.Round(candidate.Bounds.Left * displayScale)),
            Math.Max(0, (int)Math.Round(candidate.Bounds.Top * displayScale)),
            Math.Max(1, (int)Math.Round(candidate.Bounds.Width * displayScale)),
            Math.Max(1, (int)Math.Round(candidate.Bounds.Height * displayScale)));
        // Replace ONLY the line(s) the rescued column covers (box center inside the candidate bounds),
        // keeping other elements of a 2D layout (e.g. a ?芰? kanji block beside the kana column). For a
        // whole-subtitle single column every line sits inside the bounds, so this still clears all.
        lines.RemoveAll(existing =>
        {
            var cx = (existing.CropBox.Left + existing.CropBox.Right) / 2;
            var cy = (existing.CropBox.Top + existing.CropBox.Bottom) / 2;
            return cx >= candidate.Bounds.Left && cx <= candidate.Bounds.Right &&
                cy >= candidate.Bounds.Top && cy <= candidate.Bounds.Bottom;
        });
        lines.Add(new RapidOcrRealtimeLine
        {
            CropBox = candidate.Bounds,
            DisplayBox = displayBox,
            Hash = RapidOcrRealtimePixels.LineSignature(
                bitmap,
                candidate.Bounds,
                RapidOcrRealtimePixels.QuantizeStep(_options.RealtimeHashTolerance)),
            Text = rescuedValue.Text,
            Confidence = rescuedValue.Confidence
        });
        return true;
    }

    private static string SparseGlyphCenterSummary(SparseGlyphCandidate candidate)
        => string.Join(",", candidate.Centers.Select(center => $"{center.X}:{center.Y}"));

    private bool TryBuildSparseGlyphRealtimeLine(
        SKBitmap bitmap,
        string language,
        double displayScale,
        out RapidOcrRealtimeLine? line)
    {
        line = null;
        if (_recognizer is null ||
            !ShouldConsiderJapaneseSparseGlyphRescue(language, []) ||
            !TryBuildSparseGlyphCandidate(bitmap, out var candidate) ||
            !ShouldUseSparseGlyphRescueOrientation(candidate.Orientation) ||
            candidate.Centers.Count < 3)
        {
            return false;
        }

        var rescued = RecognizeSparseGlyphCandidate(bitmap, candidate, language);
        if (rescued is null || JapaneseCharCount(rescued.Value.Text) < Math.Max(2, candidate.Centers.Count - 1))
        {
            return false;
        }

        var displayBox = new OcrBoundingBox(
            Math.Max(0, (int)Math.Round(candidate.Bounds.Left * displayScale)),
            Math.Max(0, (int)Math.Round(candidate.Bounds.Top * displayScale)),
            Math.Max(1, (int)Math.Round(candidate.Bounds.Width * displayScale)),
            Math.Max(1, (int)Math.Round(candidate.Bounds.Height * displayScale)));
        line = new RapidOcrRealtimeLine
        {
            CropBox = candidate.Bounds,
            DisplayBox = displayBox,
            Hash = RapidOcrRealtimePixels.LineSignature(
                bitmap,
                candidate.Bounds,
                RapidOcrRealtimePixels.QuantizeStep(_options.RealtimeHashTolerance)),
            Text = rescued.Value.Text,
            Confidence = rescued.Value.Confidence
        };
        return true;
    }

    private static SparseGlyphOrientation EstimateRealtimeLayoutOrientation(IReadOnlyList<RapidOcrRealtimeLine> lines)
    {
        if (lines.Count == 0)
        {
            return SparseGlyphOrientation.Unknown;
        }

        if (lines.Count == 1)
        {
            var box = lines[0].CropBox;
            if (box.Width > box.Height * 1.25)
            {
                return SparseGlyphOrientation.Horizontal;
            }

            if (box.Height > box.Width * 1.25)
            {
                return SparseGlyphOrientation.Vertical;
            }

            return SparseGlyphOrientation.Unknown;
        }

        var minX = lines.Min(line => line.CropBox.Left);
        var maxX = lines.Max(line => line.CropBox.Right);
        var minY = lines.Min(line => line.CropBox.Top);
        var maxY = lines.Max(line => line.CropBox.Bottom);
        var spanX = maxX - minX;
        var spanY = maxY - minY;
        return spanY > spanX * 1.25
            ? SparseGlyphOrientation.Vertical
            : spanX > spanY * 1.25
                ? SparseGlyphOrientation.Horizontal
                : SparseGlyphOrientation.Unknown;
    }

    private static bool Contains(SKRectI rect, SKPointI point)
        => point.X >= rect.Left &&
           point.X < rect.Right &&
           point.Y >= rect.Top &&
           point.Y < rect.Bottom;

    private OcrProviderResult FullDetectFrame(
        SKBitmap bitmap,
        OcrProviderRequest request,
        string key,
        DateTimeOffset now,
        string reason,
        RapidOcrRealtimeLayout? previousLayout = null,
        bool usePresetDetectUpscale = false,
        double displayScale = 1.0,
        long preprocessMs = 0,
        IReadOnlyList<SKRectI>? restrictToRoi = null,
        bool allowContrastRescue = true)
    {
        // Scoped re-detect: a subtitle ROI is already locked, so this frame detects ONLY inside it
        // (cheap) rather than re-scanning the whole screen. Null => a whole-screen scan (cold first
        // find or the periodic drift rescan), which also refreshes the locked ROI below.
        var scopedToRoi = restrictToRoi is { Count: > 0 };
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var detectResizeMs = 0L;
        var sparsePreflightMs = 0L;
        var fullDetectMs = 0L;
        var buildLinesMs = 0L;
        var languageRecMs = 0L;
        var sparseRescueMs = 0L;
        var outsideCellsMs = 0L;

        // L1 scale-lock / preset upscale: detect on a resized copy (PP-OCR detection is
        // scale-sensitive), but recognize from the ORIGINAL bitmap so the recognizer reads sharp
        // native pixels. Det boxes come back in scaled space and are mapped to original
        // coordinates via coordScale inside BuildLines.
        SKBitmap detBitmap = bitmap;
        SKBitmap? scaledForDet = null;
        var coordScale = 1.0;
        var target = ResolveRealtimeDetectTargetShortSide(bitmap, usePresetDetectUpscale);
        if (target > 0)
        {
            var resizeSw = System.Diagnostics.Stopwatch.StartNew();
            var shortSide = Math.Min(bitmap.Width, bitmap.Height);
            if (shortSide > 0 && shortSide != target)
            {
                var scale = (double)target / shortSide;
                var sw = Math.Max(1, (int)Math.Round(bitmap.Width * scale));
                var sh = Math.Max(1, (int)Math.Round(bitmap.Height * scale));
                scaledForDet = bitmap.Resize(
                    new SKImageInfo(sw, sh, bitmap.ColorType, bitmap.AlphaType),
                    new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
                if (scaledForDet is not null)
                {
                    detBitmap = scaledForDet;
                    coordScale = (double)bitmap.Width / sw;
                }
            }
            detectResizeMs = resizeSw.ElapsedMilliseconds;
        }

        List<RapidOcrRealtimeLine> lines;
        var sparsePreflightSw = System.Diagnostics.Stopwatch.StartNew();
        var sparsePreflight = TryBuildSparseGlyphRealtimeLine(
            bitmap,
            request.Language,
            displayScale,
            out var sparseLine);
        sparsePreflightMs = sparsePreflightSw.ElapsedMilliseconds;
        if (sparsePreflight)
        {
            scaledForDet?.Dispose();
            lines = [sparseLine!];
        }
        else
        {
            var detectSw = System.Diagnostics.Stopwatch.StartNew();
            var quantizeStep = RapidOcrRealtimePixels.QuantizeStep(_options.RealtimeHashTolerance);
            List<RapidOcrRealtimeLine>? detectedLines = null;

            if (scopedToRoi)
            {
                // Locked ROI: tile only the subtitle region(s). Keeps blank/changed frames cheap so a
                // whole-screen capture stays realtime instead of re-scanning the screen every frame.
                DetectLinesInRegions(bitmap, request, displayScale, quantizeStep, restrictToRoi!, out detectedLines);
                fullDetectMs = detectSw.ElapsedMilliseconds;
                scaledForDet?.Dispose();
            }
            // Large / mid frame: two-stage coarse-locate -> scaled-region detect (gated > RealtimeTwoStage
            // MinShortSide). One whole downscale pass loses faint/small subtitle text below the detector
            // floor, so locate then detect each region at the right scale. Full-grid fallback (crash-safe
            // only > RealtimeTiledDetectShortSide) is allowed only on the cold first scan. Recognition
            // still reads the original full-res bitmap. Small/video-sized crops take the single pass below.
            else if (TryDetectLargeFrameLines(
                bitmap,
                detBitmap,
                coordScale,
                request,
                displayScale,
                quantizeStep,
                allowFullGridFallback: previousLayout?.RoiBands is not { Count: > 0 },
                out var tiledLines))
            {
                detectedLines = tiledLines;
                fullDetectMs = detectSw.ElapsedMilliseconds;
                scaledForDet?.Dispose();
            }
            else
            {
                try
                {
                    var detect = _engine!.Detect(detBitmap, BuildOptions(request));
                    fullDetectMs = detectSw.ElapsedMilliseconds;
                    var buildLinesSw = System.Diagnostics.Stopwatch.StartNew();
                    detectedLines = RapidOcrRealtimePixels.BuildLines(
                        detect,
                        bitmap,
                        quantizeStep,
                        coordScale,
                        displayScale);
                    buildLinesMs = buildLinesSw.ElapsedMilliseconds;
                }
                catch (Exception ex)
                {
                    // DirectML can fail on a specific input SHAPE (E_INVALIDARG on a MatMul node) and
                    // POISON the ONNX session so every later detect returns 0 lines -> the realtime loop
                    // freezes on one subtitle forever. Recreate the detector so a DML crash is a single
                    // dropped frame, never a permanent freeze. This frame yields no lines; the next one
                    // runs on the fresh session.
                    fullDetectMs = detectSw.ElapsedMilliseconds;
                    Console.Error.WriteLine(
                        $"[ocr] realtime detect failed; recreating detector. {ex.Message.Replace('\r', ' ').Replace('\n', ' ')}");
                    TryRecreateDetectEngine();
                }
                finally
                {
                    scaledForDet?.Dispose();
                }
            }

            lines = detectedLines ?? [];
        }

        // Shared HARD budget clock across sparse + side rescue (Phase 1): the rescues have no internal
        // cap and can spin a faint column to 6-14s on a cold frame. Once elapsed exceeds
        // RealtimeRescueBudgetMs the rescue loops bail and the primary read stands.
        var rescueSw = System.Diagnostics.Stopwatch.StartNew();
        if (!sparsePreflight)
        {
            var langRec = ResolveLangRecognizer(request.Language);
            if (langRec is not null)
            {
                var languageRecSw = System.Diagnostics.Stopwatch.StartNew();
                ReRecognizeLines(bitmap, lines, langRec, request.Language);
                languageRecMs = languageRecSw.ElapsedMilliseconds;
            }

            // The sparse/side rescues each run extra whole-frame DETs. On a scoped ROI re-detect that
            // would defeat the point (re-scanning the screen), so they run only on whole-screen scans.
            if (!scopedToRoi)
            {
                var beforeSparse = rescueSw.ElapsedMilliseconds;
                TryApplySparseGlyphRescueToRealtimeLines(bitmap, lines, request.Language, displayScale, rescueSw);
                sparseRescueMs = rescueSw.ElapsedMilliseconds - beforeSparse;
            }
        }

        // The side-ROI rescue runs up to two extra full DETs, so it is the single biggest fixed cost
        // on this path ??time it on its own so profiling can attribute a slow full detect to it (it
        // is measured even when it finds no gain, since the wasted DETs still cost). Skipped on scoped
        // ROI re-detects so they stay cheap.
        var sideTextRescued = false;
        var sideRescueMs = 0L;
        if (!scopedToRoi)
        {
            var beforeSide = rescueSw.ElapsedMilliseconds;
            sideTextRescued = TryApplyRealtimeSideTextRescue(bitmap, lines, request, displayScale, rescueSw);
            sideRescueMs = rescueSw.ElapsedMilliseconds - beforeSide;
            if (sideTextRescued)
            {
                lines.RemoveAll(line => IsRealtimeSideTextRescueStrayLine(line, bitmap.Width, bitmap.Height));
            }
        }

        // Stamp the re-detect clock at COMPLETION, not at the tick start `now`. A full
        // detect that runs longer than RealtimeRedetectIntervalMs would otherwise look
        // "already overdue" the instant it returns, forcing a full detect on every
        // subsequent frame and starving the incremental fast path entirely (the
        // interval is meant to space detects apart, not to fire back-to-back).
        var completedAt = DateTimeOffset.UtcNow;
        TraceRealtimeFullDetect(key, reason, "raw", scopedToRoi, bitmap.Width, bitmap.Height, lines);
        lines = lines
            .Where(line => HasMeaningfulContent(line.Text))
            .Where(line => !IsRealtimeNoiseFragment(line, bitmap, request.Language))
            .ToList();
        TraceRealtimeFullDetect(key, reason, "filtered", scopedToRoi, bitmap.Width, bitmap.Height, lines);

        // Confidence-triggered contrast rescue: a least-confident line below the threshold means the
        // recognizer is unsure ??usually faint/low-contrast text the detector mangled into noise (a faint
        // vertical subtitle read as garbage). Re-detect once on a CLAHE-enhanced copy and keep whichever
        // read is more confident (or drop to empty if CLAHE shows the "text" was noise). Only on
        // whole-screen scans with no explicit preset. The recursive call passes previousLayout:null so the
        // rescue is a clean cold read (no hold), and stores its own layout: when it wins we return it as-is;
        // when the primary wins we fall through to the normal store below, which re-stores the primary
        // layout over the rescue's. allowContrastRescue:false stops it recursing again.
        var presetIsAuto = (request.PreprocessingPreset ?? string.Empty).Trim().ToLowerInvariant()
            is "" or "none" or "subtitle";
        if (allowContrastRescue &&
            !scopedToRoi &&
            presetIsAuto &&
            lines.Count > 0 &&
            _options.RealtimeContrastRescueConfidence > 0)
        {
            var primaryMinConf = lines.Min(line => line.Confidence);
            if (primaryMinConf < _options.RealtimeContrastRescueConfidence)
            {
                using var enhanced = EnhanceLowContrast(bitmap);
                var rescue = FullDetectFrame(
                    enhanced, request, key, now, reason + "-contrast", previousLayout: null,
                    usePresetDetectUpscale: false, displayScale: 1.0, preprocessMs,
                    restrictToRoi: null, allowContrastRescue: false);
                var rescueMinConf = rescue.Blocks.Count > 0 ? rescue.Blocks.Min(b => b.Confidence) : (double?)null;
                if (rescue.Blocks.Count == 0 || (rescueMinConf is double rc && rc > primaryMinConf))
                {
                    if (ShouldHoldSuspectRepairBlocks(
                            previousLayout,
                            rescue.Blocks,
                            reason,
                            bitmap.Width,
                            bitmap.Height,
                            _options.RealtimeVerticalCommitConfidence,
                            out var rescueHoldReason))
                    {
                        TraceRealtimeFullDetectBlocks(key, reason, "contrast-rejected", scopedToRoi, bitmap.Width, bitmap.Height, rescue.Blocks, rescueHoldReason);
                        Console.Error.WriteLine(
                            FormattableString.Invariant(
                                $"[ocr] contrast rescue ({reason}) rejected: {rescueHoldReason}; keeping primary candidate in tracking gate."));
                    }
                    else
                    {
                        TraceRealtimeFullDetectBlocks(key, reason, "contrast-return", scopedToRoi, bitmap.Width, bitmap.Height, rescue.Blocks, string.Empty);
                        Console.Error.WriteLine(FormattableString.Invariant(
                            $"[ocr] contrast rescue ({reason}): primary minConf {primaryMinConf:F3} -> rescue {(rescueMinConf?.ToString("F3") ?? "empty")} ({rescue.Blocks.Count} blocks); took rescue."));
                        return rescue;
                    }
                }
            }
        }

        var realtimeQuantizeStep = RapidOcrRealtimePixels.QuantizeStep(_options.RealtimeHashTolerance);
        var suspectVerticalCluster = ShouldFilterSuspectVerticalRelocateLines(
            previousLayout,
            lines,
            reason,
            bitmap.Width,
            bitmap.Height,
            out var rejectShortVerticalFragments);
        if (suspectVerticalCluster &&
            ResolveLangRecognizer("ja") is { } suspectJapaneseRec)
        {
            ReRecognizeLines(bitmap, lines, suspectJapaneseRec, "ja");
            TraceRealtimeFullDetect(
                key,
                reason,
                "vertical-ja-rerec",
                scopedToRoi,
                bitmap.Width,
                bitmap.Height,
                lines,
                "re-recognized suspect vertical candidate with PP-OCRv6 ja rec");
        }

        var rejectedVerticalLines = new List<RapidOcrRealtimeLine>();
        if (lines.Count > 0)
        {
            var keptLines = new List<RapidOcrRealtimeLine>(lines.Count);
            foreach (var line in lines)
            {
                if (IsSuspectRealtimeRelocateLine(
                    line,
                    bitmap,
                    request.Language,
                    suspectVerticalCluster,
                    rejectShortVerticalFragments))
                {
                    rejectedVerticalLines.Add(line);
                    continue;
                }

                keptLines.Add(line);
            }

            if (rejectedVerticalLines.Count > 0)
            {
                if (suspectVerticalCluster)
                {
                    var recoveredRejectedLines = new List<RapidOcrRealtimeLine>();
                    foreach (var band in BuildRoiBandsFromLines(rejectedVerticalLines, bitmap.Width, bitmap.Height, completedAt))
                    {
                        foreach (var recovered in RecoverBandLines(bitmap, request, displayScale, realtimeQuantizeStep, band.Rect))
                        {
                            if (!IsSuspectRealtimeRelocateLine(
                                recovered,
                                bitmap,
                                request.Language,
                                suspectVerticalCluster,
                                rejectShortVerticalFragments))
                            {
                                recoveredRejectedLines.Add(recovered);
                            }
                        }
                    }

                    if (recoveredRejectedLines.Count > 0)
                    {
                        keptLines.AddRange(recoveredRejectedLines);
                        keptLines = DedupRealtimeLinesByOverlap(keptLines);
                        keptLines.Sort(CompareRealtimeLinesForDisplay);
                        TraceRealtimeFullDetect(
                            key,
                            reason,
                            "vertical-line-recovered",
                            scopedToRoi,
                            bitmap.Width,
                            bitmap.Height,
                            recoveredRejectedLines,
                            "recovered suspect vertical PP-OCRv6 candidate");
                    }
                }

                lines = keptLines;
                TraceRealtimeFullDetect(
                    key,
                    reason,
                    "vertical-line-rejected",
                    scopedToRoi,
                    bitmap.Width,
                    bitmap.Height,
                    rejectedVerticalLines,
                    "filtered suspect vertical PP-OCRv6 candidate");
            }
        }

        // Reconcile full-detect output with the previous sticky bands before committing. A whole-screen
        // detect can return a non-empty subset during moving vertical subtitles; recover missing bands
        // from native crops so a single partial candidate does not clear the old track.
        var roiBands = ReconcileRealtimeRoiBands(
            bitmap,
            request,
            displayScale,
            realtimeQuantizeStep,
            previousLayout,
            lines,
            reason,
            completedAt);
        var hasLiveTrackBands = _options.RealtimeTrackRecoveryEnabled &&
            previousLayout?.RoiBands is { Count: > 0 } &&
            roiBands is { Count: > 0 };
        var liveTrackBandCount = hasLiveTrackBands ? roiBands!.Count : 0;
        if (lines.Count == 0 &&
            previousLayout is not null &&
            hasLiveTrackBands &&
            previousLayout.Lines.Any(line => HasMeaningfulContent(line.Text)))
        {
            if (rejectedVerticalLines.Count > 0 && roiBands is { Count: > 0 })
            {
                foreach (var band in roiBands)
                {
                    band.MissStreak = 0;
                    band.LastConfirmedAt = completedAt;
                }
            }

            previousLayout.RoiBands = roiBands;
            previousLayout.LastSeenAt = completedAt;
            previousLayout.FirstTransientFailureAt ??= completedAt;
            if (!scopedToRoi &&
                rejectedVerticalLines.Count > 0 &&
                _options.RealtimeRelocateMinGapMs > 0)
            {
                previousLayout.LastWholeScreenScanAt = completedAt.AddMilliseconds(-_options.RealtimeRelocateMinGapMs);
            }

            _realtimeCache.Store(
                key,
                previousLayout,
                completedAt,
                _options.RealtimeMaxSessions,
                _options.RealtimeSessionIdleMs);
            Console.Error.WriteLine(
                FormattableString.Invariant(
                    $"[ocr] track-hold ({reason}): kept previous result with {liveTrackBandCount} live band(s) after {stopwatch.ElapsedMilliseconds} ms."));
            TraceRealtimeFullDetect(key, reason, "track-hold", scopedToRoi, bitmap.Width, bitmap.Height, lines);
            return BuildRealtimeResult(previousLayout.Lines, IncrementalHeldEngineName) with
            {
                Timing = BuildRealtimeTiming(
                    reason,
                    preprocessMs,
                    detectResizeMs,
                    sparsePreflightMs,
                    fullDetectMs,
                    buildLinesMs,
                    languageRecMs,
                    sparseRescueMs,
                    sideRescueMs,
                    outsideCellsMs)
            };
        }

        var retiredAllTrackBands = _options.RealtimeTrackRecoveryEnabled &&
            previousLayout?.RoiBands is { Count: > 0 } &&
            lines.Count == 0 &&
            !hasLiveTrackBands;
        if (!retiredAllTrackBands &&
            ShouldHoldPreviousRealtimeResult(previousLayout, lines, completedAt, reason))
        {
            previousLayout!.LastSeenAt = completedAt;
            previousLayout.LastFullDetectAt = completedAt;
            previousLayout.FirstTransientFailureAt ??= completedAt;
            // Keep the locked ROI sticky; if this was a whole-screen scan, mark it so the periodic
            // rescan does not refire every frame while we hold the previous result.
            if (!scopedToRoi)
            {
                previousLayout.LastWholeScreenScanAt = completedAt;
            }
            _realtimeCache.Store(
                key,
                previousLayout,
                completedAt,
                _options.RealtimeMaxSessions,
                _options.RealtimeSessionIdleMs);
            Console.Error.WriteLine(
                $"[ocr] rapidocr-net realtime full detect ({reason}) returned empty; held previous result after {stopwatch.ElapsedMilliseconds} ms (side-rescue {sideRescueMs} ms).");
            TraceRealtimeFullDetect(key, reason, "empty-hold", scopedToRoi, bitmap.Width, bitmap.Height, lines);
            return BuildRealtimeResult(previousLayout.Lines, IncrementalHeldEngineName) with
            {
                Timing = BuildRealtimeTiming(
                    reason,
                    preprocessMs,
                    detectResizeMs,
                    sparsePreflightMs,
                    fullDetectMs,
                    buildLinesMs,
                    languageRecMs,
                    sparseRescueMs,
                    sideRescueMs,
                    outsideCellsMs)
            };
        }
        var outsideCellsSw = System.Diagnostics.Stopwatch.StartNew();
        var (mask, samples) = RapidOcrRealtimePixels.BuildOutsideCells(bitmap, lines);
        outsideCellsMs = outsideCellsSw.ElapsedMilliseconds;
        var lastWholeScreenScanAt = scopedToRoi
            ? (previousLayout?.LastWholeScreenScanAt ?? default)
            : completedAt;
        // P3 backoff bookkeeping: grow the stable streak when this detect reproduced the previous line
        // layout (structure only, text ignored); reset to 0 on any structural change.
        var stableLayoutStreak = IsStructurallyStableLayout(previousLayout, lines)
            ? previousLayout!.StableLayoutStreak + 1
            : 0;
        TraceRealtimeFullDetect(key, reason, "store", scopedToRoi, bitmap.Width, bitmap.Height, lines);
            _realtimeCache.Store(
            key,
            new RapidOcrRealtimeLayout
            {
                Width = bitmap.Width,
                Height = bitmap.Height,
                Lines = lines,
                OutsideCellMask = mask,
                OutsideCellSamples = samples,
                LastFullDetectAt = completedAt,
                LastSeenAt = completedAt,
                LastFullDetectMs = stopwatch.ElapsedMilliseconds,
                RoiBands = roiBands,
                LastWholeScreenScanAt = lastWholeScreenScanAt,
                StableLayoutStreak = stableLayoutStreak
            },
            completedAt,
            _options.RealtimeMaxSessions,
            _options.RealtimeSessionIdleMs);
        Console.Error.WriteLine(
            $"[ocr] rapidocr-net realtime full detect ({reason}, {(scopedToRoi ? "scoped-roi" : "whole-screen")}): {lines.Count} lines in {stopwatch.ElapsedMilliseconds} ms (det {fullDetectMs} ms, side-rescue {sideRescueMs} ms).");
        return BuildRealtimeResult(lines, sideTextRescued ? FullEngineName + "-side-text-rescue" : FullEngineName) with
        {
            Timing = BuildRealtimeTiming(
                reason,
                preprocessMs,
                detectResizeMs,
                sparsePreflightMs,
                fullDetectMs,
                buildLinesMs,
                languageRecMs,
                sparseRescueMs,
                sideRescueMs,
                outsideCellsMs)
        };
    }

    private void TraceRealtimeFullDetect(
        string key,
        string reason,
        string stage,
        bool scopedToRoi,
        int width,
        int height,
        IReadOnlyList<RapidOcrRealtimeLine> lines,
        string detail = "")
    {
        var path = Environment.GetEnvironmentVariable("VB_OCR_REALTIME_TRACE_PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var payload = new
        {
            ts = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            provider = Descriptor.Name,
            session = key,
            reason,
            stage,
            scopedToRoi,
            width,
            height,
            detail,
            lines = lines.Select(ToRealtimeTraceLine).ToArray()
        };
        AppendRealtimeTrace(path, payload);
    }

    private void TraceRealtimeFullDetectBlocks(
        string key,
        string reason,
        string stage,
        bool scopedToRoi,
        int width,
        int height,
        IReadOnlyList<OcrTextBlock> blocks,
        string detail)
    {
        var path = Environment.GetEnvironmentVariable("VB_OCR_REALTIME_TRACE_PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var payload = new
        {
            ts = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            provider = Descriptor.Name,
            session = key,
            reason,
            stage,
            scopedToRoi,
            width,
            height,
            detail,
            lines = blocks.Select(ToRealtimeTraceLine).ToArray()
        };
        AppendRealtimeTrace(path, payload);
    }

    private static void AppendRealtimeTrace(string path, object payload)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var line = JsonSerializer.Serialize(payload);
            lock (RealtimeTraceFileLock)
            {
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // Debug trace must never affect OCR.
        }
    }

    private static object ToRealtimeTraceLine(RapidOcrRealtimeLine line)
    {
        object? display = line.DisplayBox is { } d
            ? new { x = d.X, y = d.Y, w = d.Width, h = d.Height }
            : null;
        return new
        {
            text = line.Text,
            confidence = line.Confidence,
            crop = new
            {
                x = line.CropBox.Left,
                y = line.CropBox.Top,
                w = line.CropBox.Width,
                h = line.CropBox.Height
            },
            display
        };
    }

    private static object ToRealtimeTraceLine(OcrTextBlock block)
    {
        object? display = block.BoundingBox is { } d
            ? new { x = d.X, y = d.Y, w = d.Width, h = d.Height }
            : null;
        return new
        {
            text = block.Text,
            confidence = block.Confidence,
            crop = display,
            display
        };
    }

    private static OcrProviderTiming BuildRealtimeTiming(
        string reason,
        long preprocessMs,
        long detectResizeMs,
        long sparsePreflightMs,
        long fullDetectMs,
        long buildLinesMs,
        long languageRecMs,
        long sparseRescueMs,
        long sideRescueMs,
        long outsideCellsMs)
        => new(
            FullDetectReason: reason,
            SideRescueMs: sideRescueMs,
            PreprocessMs: preprocessMs,
            DetectResizeMs: detectResizeMs,
            SparsePreflightMs: sparsePreflightMs,
            FullDetectMs: fullDetectMs,
            BuildLinesMs: buildLinesMs,
            LanguageRecMs: languageRecMs,
            SparseRescueMs: sparseRescueMs,
            OutsideCellsMs: outsideCellsMs);

    private bool TryApplyRealtimeSideTextRescue(
        SKBitmap bitmap,
        List<RapidOcrRealtimeLine> lines,
        OcrProviderRequest request,
        double displayScale,
        System.Diagnostics.Stopwatch rescueSw)
    {
        if (!ShouldTryRealtimeSideTextRescue(bitmap, lines, request))
        {
            return false;
        }

        var primaryJapaneseChars = lines.Sum(line => JapaneseCharCount(line.Text));
        var primaryKana = lines.Sum(line => JapaneseKanaCount(line.Text));
        var quantizeStep = RapidOcrRealtimePixels.QuantizeStep(_options.RealtimeHashTolerance);
        foreach (var crop in BuildRealtimeSideTextRescueCrops(bitmap.Width, bitmap.Height))
        {
            // Each side DET is a full medium detect; bail once the shared rescue budget is spent so two
            // side columns can't blow the cold-frame budget.
            if (RescueBudgetExceeded(rescueSw))
            {
                break;
            }

            using var rescueBitmap = RenderRealtimeSideTextRescueBitmap(bitmap, crop);
            // Cost-reduction: the side DET (~400-500 ms) is the biggest fixed cost here, and both side
            // columns are DET'd unconditionally even when one (or both) is empty ??wasted on
            // single-column and horizontal-only frames. Skip the DET when this column is confidently
            // blank. The probe runs on the FLATTENED crop (so faint low-contrast kana, whose strokes
            // flatten boosts, are not mistaken for blank) and is biased to DET when unsure, so it
            // never drops a real column ??it only reclaims time on genuinely empty sides. Every frame
            // still detects, so there is no staleness / accuracy trade (unlike skipping by frequency).
            if (!SideCropHasTextLikeContent(rescueBitmap))
            {
                continue;
            }

            var rescueRequest = request with
            {
                Language = "ja-JP",
                PreprocessingPreset = "none",
                Realtime = false
            };
            var detect = _engine!.Detect(rescueBitmap, BuildOptions(rescueRequest));
            var rescueLines = RapidOcrRealtimePixels.BuildLines(
                    detect,
                    rescueBitmap,
                    quantizeStep)
                .Where(line => HasMeaningfulContent(line.Text) && HasJapaneseScript(line.Text))
                .Select(line => MapRealtimeSideTextRescueLine(
                    bitmap,
                    line,
                    crop,
                    rescueBitmap.Width,
                    rescueBitmap.Height,
                    displayScale,
                    quantizeStep))
                .Where(line => line.DisplayBox is not null)
                .ToArray();
            if (rescueLines.Length == 0)
            {
                continue;
            }

            var rescueText = string.Join(Environment.NewLine, rescueLines.Select(line => line.Text));
            var rescueJapaneseChars = JapaneseCharCount(rescueText);
            var rescueKana = JapaneseKanaCount(rescueText);
            var hasClearGain = rescueJapaneseChars >= primaryJapaneseChars + 3 ||
                (primaryKana == 0 && rescueKana >= 3) ||
                (primaryJapaneseChars <= 1 && rescueJapaneseChars >= 4);
            if (!hasClearGain)
            {
                continue;
            }

            lines.RemoveAll(line => RealtimeLineCenterInside(line, crop));
            lines.AddRange(rescueLines);
            lines.Sort(CompareRealtimeLinesForDisplay);
            return true;
        }

        return false;
    }

    private static bool ShouldEscalateRealtimeNoiseFragments(
        IReadOnlyList<RapidOcrRealtimeLine> lines,
        SKBitmap bitmap,
        string? language)
    {
        if (NormalizeLanguage(language) != "ja" || lines.Count == 0)
        {
            return false;
        }

        var meaningful = lines
            .Where(line => HasMeaningfulContent(line.Text))
            .ToArray();
        if (meaningful.Length == 0 || meaningful.Length > 2)
        {
            return false;
        }

        var contentLength = meaningful.Sum(line => MeaningfulCharacterCount(line.Text));
        return contentLength <= 2 &&
            meaningful.All(line => IsRealtimeNoiseFragment(line, bitmap, language));
    }

    // Conservative blank-column probe for a FLATTENED side crop: counts subsampled pixels whose
    // luminance differs from a right/down neighbor by more than a small step (= a stroke edge). Text
    // produces many such edges; a smooth gradient/flat background produces almost none. Returns true
    // (=> run the side DET) unless the edge density is below a low floor, so a textured/streaky
    // background still detects ??it only skips genuinely empty columns. Subsampled GetPixel matches
    // the other realtime luminance probes (ShouldEnhanceLowContrast); a few ms vs the ~400-500 ms DET.
    private const int SideContentEdgeStep = 18;
    private const double SideContentMinEdgeRatio = 0.004;

    private static bool SideCropHasTextLikeContent(SKBitmap flattened)
    {
        var width = flattened.Width;
        var height = flattened.Height;
        if (width < 4 || height < 4)
        {
            return true;
        }

        var stepX = Math.Max(1, width / 160);
        var stepY = Math.Max(1, height / 240);
        var samples = 0;
        var edges = 0;
        for (var y = 0; y + stepY < height; y += stepY)
        {
            for (var x = 0; x + stepX < width; x += stepX)
            {
                var center = flattened.GetPixel(x, y);
                var luminance = (center.Red + center.Green + center.Blue) / 3;
                var rightColor = flattened.GetPixel(x + stepX, y);
                var rightLuminance = (rightColor.Red + rightColor.Green + rightColor.Blue) / 3;
                var downColor = flattened.GetPixel(x, y + stepY);
                var downLuminance = (downColor.Red + downColor.Green + downColor.Blue) / 3;
                samples++;
                if (Math.Abs(luminance - rightLuminance) > SideContentEdgeStep ||
                    Math.Abs(luminance - downLuminance) > SideContentEdgeStep)
                {
                    edges++;
                }
            }
        }

        return samples == 0 || edges / (double)samples >= SideContentMinEdgeRatio;
    }

    private static bool ShouldTryRealtimeSideTextRescue(
        SKBitmap bitmap,
        IReadOnlyList<RapidOcrRealtimeLine> lines,
        OcrProviderRequest request)
    {
        if (!request.Realtime ||
            NormalizeLanguage(request.Language) != "ja" ||
            bitmap.Width < 640 ||
            bitmap.Height < 300 ||
            lines.Count > 2)
        {
            return false;
        }

        var text = string.Join("", lines.Select(line => line.Text));
        var japaneseChars = JapaneseCharCount(text);
        if (japaneseChars >= 4)
        {
            return false;
        }

        return japaneseChars > 0 || lines.Count == 0;
    }

    private static IEnumerable<SKRectI> BuildRealtimeSideTextRescueCrops(int width, int height)
    {
        var cropWidth = Math.Clamp(
            (int)Math.Round(width * 0.19),
            RealtimeSideTextRescueMinWidth,
            Math.Min(RealtimeSideTextRescueMaxWidth, Math.Max(1, width / 3)));
        var marginX = Math.Clamp((int)Math.Round(width * 0.058), 0, Math.Max(0, width / 6));
        var top = Math.Clamp((int)Math.Round(height * 0.20), 0, Math.Max(0, height - 1));
        var bottom = Math.Clamp((int)Math.Round(height * 0.71), top + 1, height);
        var left = ClampRect(new SKRectI(marginX, top, marginX + cropWidth, bottom), width, height);
        if (left.Width >= 32 && left.Height >= 80)
        {
            yield return left;
        }

        var rightX = Math.Max(0, width - marginX - cropWidth);
        var right = ClampRect(new SKRectI(rightX, top, rightX + cropWidth, bottom), width, height);
        if (right.Width >= 32 && right.Height >= 80 && right.Left != left.Left)
        {
            yield return right;
        }
    }

    private static SKBitmap RenderRealtimeSideTextRescueBitmap(SKBitmap source, SKRectI crop)
    {
        using var cropped = CopyCropToBgraBitmap(source, crop, crop.Width, crop.Height);
        return ApplyRealtimeIlluminationFlatten(cropped, RealtimeSideTextRescueTargetShortSide);
    }

    private static RapidOcrRealtimeLine MapRealtimeSideTextRescueLine(
        SKBitmap source,
        RapidOcrRealtimeLine line,
        SKRectI crop,
        int rescueWidth,
        int rescueHeight,
        double displayScale,
        int quantizeStep)
    {
        var scaleX = crop.Width / (double)Math.Max(1, rescueWidth);
        var scaleY = crop.Height / (double)Math.Max(1, rescueHeight);
        var left = crop.Left + (int)Math.Round(line.CropBox.Left * scaleX);
        var top = crop.Top + (int)Math.Round(line.CropBox.Top * scaleY);
        var right = crop.Left + (int)Math.Round(line.CropBox.Right * scaleX);
        var bottom = crop.Top + (int)Math.Round(line.CropBox.Bottom * scaleY);
        var mappedCrop = ClampRect(new SKRectI(left, top, right, bottom), source.Width, source.Height);

        OcrBoundingBox? displayBox = null;
        if (line.DisplayBox is not null)
        {
            var box = line.DisplayBox;
            displayBox = new OcrBoundingBox(
                Math.Max(0, (int)Math.Round((crop.Left + (box.X * scaleX)) * displayScale)),
                Math.Max(0, (int)Math.Round((crop.Top + (box.Y * scaleY)) * displayScale)),
                Math.Max(1, (int)Math.Round(box.Width * scaleX * displayScale)),
                Math.Max(1, (int)Math.Round(box.Height * scaleY * displayScale)));
        }

        return new RapidOcrRealtimeLine
        {
            CropBox = mappedCrop,
            DisplayBox = displayBox,
            Hash = RapidOcrRealtimePixels.LineSignature(source, mappedCrop, quantizeStep),
            Text = line.Text,
            Confidence = Math.Max(line.Confidence, 0.72)
        };
    }

    private static bool RealtimeLineCenterInside(RapidOcrRealtimeLine line, SKRectI crop)
    {
        var box = line.CropBox;
        var centerX = box.Left + (box.Width / 2.0);
        var centerY = box.Top + (box.Height / 2.0);
        return centerX >= crop.Left &&
            centerX <= crop.Right &&
            centerY >= crop.Top &&
            centerY <= crop.Bottom;
    }

    private static int CompareRealtimeLinesForDisplay(RapidOcrRealtimeLine left, RapidOcrRealtimeLine right)
    {
        var y = left.CropBox.Top.CompareTo(right.CropBox.Top);
        return y != 0 ? y : left.CropBox.Left.CompareTo(right.CropBox.Left);
    }

    private static bool IsRealtimeSideTextRescueStrayLine(
        RapidOcrRealtimeLine line,
        int imageWidth,
        int imageHeight)
    {
        var text = line.Text.Trim();
        if (text.Length is 0 or > 2 ||
            HasJapaneseScript(text) ||
            HasAsciiLetter(text))
        {
            return false;
        }

        var box = line.DisplayBox;
        var largeBox = box is not null &&
            (box.Width >= imageWidth * 0.45 || box.Height >= imageHeight * 0.45);
        return largeBox || line.Confidence < 0.65;
    }

    private bool ShouldHoldPreviousRealtimeResult(
        RapidOcrRealtimeLayout? previousLayout,
        IReadOnlyList<RapidOcrRealtimeLine> currentLines,
        DateTimeOffset completedAt,
        string reason)
    {
        if (_options.RealtimeTransientHoldMs <= 0 ||
            previousLayout is null ||
            currentLines.Count > 0 ||
            !previousLayout.Lines.Any(line => HasMeaningfulContent(line.Text)))
        {
            return false;
        }

        // If the repair was triggered because the old boxes themselves stopped
        // recognizing meaningful text, holding that same old text creates stale
        // hallucinations on no-subtitle transition frames. Keep hold for ordinary
        // detector misses. Vertical subtitle tracks are the exception: during motion,
        // PP-OCRv6 often returns an empty repair detect before the new column is stable;
        // clearing the cache there lets the next low-quality column commit as a new track.
        if (IsStaleRealtimeLayoutRepairReason(reason) &&
            !LooksLikeVerticalRealtimeCandidate(previousLayout.Lines, previousLayout.Width, previousLayout.Height))
        {
            return false;
        }

        var firstFailureAt = previousLayout.FirstTransientFailureAt ?? completedAt;
        return (completedAt - firstFailureAt).TotalMilliseconds <= _options.RealtimeTransientHoldMs;
    }

    private static bool ShouldHoldSuspectRelocateCandidate(
        RapidOcrRealtimeLayout? previousLayout,
        IReadOnlyList<RapidOcrRealtimeLine> currentLines,
        string reason,
        int imageWidth,
        int imageHeight,
        out string holdReason)
    {
        holdReason = string.Empty;
        // Reason-agnostic: any full-detect commit (interval / relocate / layout-shift / cold whole-screen)
        // must pass this gate, because PP-OCRv6 garbles a moving vertical column the same way regardless of
        // which reason triggered the detect (敹策 came via stale-relocate, ?‧敹? via interval). The guards
        // below (no previous track / empty / not vertical-or-scattered) already exclude cold starts and
        // normal horizontal subtitles, so widening the reason set never holds a legitimate read.
        if (previousLayout is null ||
            currentLines.Count == 0 ||
            !previousLayout.Lines.Any(line => HasMeaningfulContent(line.Text)))
        {
            return false;
        }

        var vertical = LooksLikeVerticalRealtimeCandidate(currentLines, imageWidth, imageHeight);
        var scattered = LooksLikeScatteredRealtimeCandidate(currentLines, imageWidth, imageHeight);
        if (!vertical && !scattered)
        {
            return false;
        }

        var text = string.Concat(currentLines.Select(line => line.Text?.Trim() ?? string.Empty));
        var minConfidence = currentLines.Min(line => line.Confidence);
        var asciiOrDigit = HasAsciiLetter(text) || HasDigit(text);
        var japaneseChars = JapaneseCharCount(text);
        var kana = JapaneseKanaCount(text);
        var shortFragments = currentLines.Count > 1 &&
            currentLines.Count(line => MeaningfulCharacterCount(line.Text) <= 2) >= Math.Max(2, currentLines.Count - 1);

        if (asciiOrDigit && (vertical || minConfidence < 0.75))
        {
            holdReason = FormattableString.Invariant(
                $"{(vertical ? "vertical" : "scattered")} relocate candidate contains ASCII/digits (minConf {minConfidence:F3})");
            return true;
        }

        if (vertical && minConfidence < 0.86 && (shortFragments || kana == 0 || japaneseChars < 4))
        {
            holdReason = FormattableString.Invariant(
                $"low-confidence vertical relocate candidate (minConf {minConfidence:F3}, chars {japaneseChars}, kana {kana}, shortFragments {shortFragments})");
            return true;
        }

        if (minConfidence < 0.75)
        {
            holdReason = FormattableString.Invariant(
                $"very-low-confidence vertical relocate candidate (minConf {minConfidence:F3})");
            return true;
        }

        return false;
    }

    private static bool IsRealtimeRepairDetectReason(string reason)
        => string.Equals(reason, "stale-relocate", StringComparison.Ordinal) ||
           string.Equals(reason, "empty-layout", StringComparison.Ordinal) ||
           IsStaleRealtimeLayoutRepairReason(reason);

    private static bool ShouldHoldSuspectRepairBlocks(
        RapidOcrRealtimeLayout? previousLayout,
        IReadOnlyList<OcrTextBlock> blocks,
        string reason,
        int imageWidth,
        int imageHeight,
        double realtimeVerticalCommitConfidence,
        out string holdReason)
    {
        holdReason = string.Empty;
        if (previousLayout is null ||
            blocks.Count == 0 ||
            !IsRealtimeRepairDetectReason(reason) ||
            !previousLayout.Lines.Any(line => HasMeaningfulContent(line.Text)))
        {
            return false;
        }

        var vertical = LooksLikeVerticalOcrBlocks(blocks, imageWidth, imageHeight);
        var scattered = LooksLikeScatteredOcrBlocks(blocks, imageWidth, imageHeight);
        if (!vertical && !scattered)
        {
            return false;
        }

        var text = string.Concat(blocks.Select(block => block.Text?.Trim() ?? string.Empty));
        var minConfidence = blocks.Min(block => block.Confidence);
        var asciiOrDigit = HasAsciiLetter(text) || HasDigit(text);
        var japaneseChars = JapaneseCharCount(text);
        var kana = JapaneseKanaCount(text);
        var shortFragments = blocks.Count > 1 &&
            blocks.Count(block => MeaningfulCharacterCount(block.Text) <= 2) >= Math.Max(2, blocks.Count - 1);
        var commitConfidence = realtimeVerticalCommitConfidence > 0
            ? realtimeVerticalCommitConfidence
            : 0.90;
        if (LooksLikeVerticalRealtimeCandidate(previousLayout.Lines, previousLayout.Width, previousLayout.Height) &&
            shortFragments &&
            minConfidence < commitConfidence &&
            (kana == 0 || japaneseChars < 4 || blocks.Count <= 2))
        {
            holdReason = FormattableString.Invariant(
                $"short-fragment vertical repair rescue (minConf {minConfidence:F3}, chars {japaneseChars}, kana {kana}, fragments {blocks.Count})");
            return true;
        }

        if (asciiOrDigit && (vertical || minConfidence < 0.75))
        {
            holdReason = FormattableString.Invariant(
                $"{(vertical ? "vertical" : "scattered")} repair rescue contains ASCII/digits (minConf {minConfidence:F3})");
            return true;
        }

        if (vertical && minConfidence < 0.86 && (shortFragments || kana == 0 || japaneseChars < 4))
        {
            holdReason = FormattableString.Invariant(
                $"low-confidence vertical repair rescue (minConf {minConfidence:F3}, chars {japaneseChars}, kana {kana}, shortFragments {shortFragments})");
            return true;
        }

        if (minConfidence < 0.75)
        {
            holdReason = FormattableString.Invariant(
                $"very-low-confidence vertical repair rescue (minConf {minConfidence:F3})");
            return true;
        }

        return false;
    }

    private static bool LooksLikeVerticalRealtimeCandidate(
        IReadOnlyList<RapidOcrRealtimeLine> lines,
        int imageWidth,
        int imageHeight)
    {
        if (lines.Count == 0)
        {
            return false;
        }

        var boxes = lines
            .Select(GetRealtimeTightBox)
            .Where(box => box.Width > 0 && box.Height > 0)
            .ToArray();
        if (boxes.Length == 0)
        {
            return false;
        }

        if (boxes.Any(box => box.Height >= Math.Max(72, box.Width * 1.45)))
        {
            return true;
        }

        var minX = boxes.Min(box => box.Left);
        var maxX = boxes.Max(box => box.Right);
        var minY = boxes.Min(box => box.Top);
        var maxY = boxes.Max(box => box.Bottom);
        var spanX = maxX - minX;
        var spanY = maxY - minY;
        return spanY >= Math.Max(96, spanX * 1.35) &&
            spanY >= Math.Max(1, imageHeight) * 0.16 &&
            spanX <= Math.Max(1, imageWidth) * 0.18;
    }

    private static bool LooksLikeScatteredRealtimeCandidate(
        IReadOnlyList<RapidOcrRealtimeLine> lines,
        int imageWidth,
        int imageHeight)
    {
        if (lines.Count < 3)
        {
            return false;
        }

        var boxes = lines
            .Select(GetRealtimeTightBox)
            .Where(box => box.Width > 0 && box.Height > 0)
            .ToArray();
        if (boxes.Length < 3)
        {
            return false;
        }

        var minX = boxes.Min(box => box.Left);
        var maxX = boxes.Max(box => box.Right);
        var minY = boxes.Min(box => box.Top);
        var maxY = boxes.Max(box => box.Bottom);
        var spanX = maxX - minX;
        var spanY = maxY - minY;
        return spanX >= Math.Max(1, imageWidth) * 0.45 &&
            spanY >= Math.Max(1, imageHeight) * 0.20;
    }

    private static bool LooksLikeVerticalOcrBlocks(
        IReadOnlyList<OcrTextBlock> blocks,
        int imageWidth,
        int imageHeight)
    {
        var boxes = blocks
            .Select(block => block.BoundingBox)
            .Where(box => box is not null && box.Width > 0 && box.Height > 0)
            .Select(box => box!)
            .Select(box => new SKRectI(box.X, box.Y, box.X + box.Width, box.Y + box.Height))
            .ToArray();
        if (boxes.Length == 0)
        {
            return false;
        }

        if (boxes.Any(box => box.Height >= Math.Max(72, box.Width * 1.45)))
        {
            return true;
        }

        var minX = boxes.Min(box => box.Left);
        var maxX = boxes.Max(box => box.Right);
        var minY = boxes.Min(box => box.Top);
        var maxY = boxes.Max(box => box.Bottom);
        var spanX = maxX - minX;
        var spanY = maxY - minY;
        return spanY >= Math.Max(96, spanX * 1.35) &&
            spanY >= Math.Max(1, imageHeight) * 0.16 &&
            spanX <= Math.Max(1, imageWidth) * 0.18;
    }

    private static bool LooksLikeScatteredOcrBlocks(
        IReadOnlyList<OcrTextBlock> blocks,
        int imageWidth,
        int imageHeight)
    {
        if (blocks.Count < 3)
        {
            return false;
        }

        var boxes = blocks
            .Select(block => block.BoundingBox)
            .Where(box => box is not null && box.Width > 0 && box.Height > 0)
            .Select(box => box!)
            .Select(box => new SKRectI(box.X, box.Y, box.X + box.Width, box.Y + box.Height))
            .ToArray();
        if (boxes.Length < 3)
        {
            return false;
        }

        var minX = boxes.Min(box => box.Left);
        var maxX = boxes.Max(box => box.Right);
        var minY = boxes.Min(box => box.Top);
        var maxY = boxes.Max(box => box.Bottom);
        var spanX = maxX - minX;
        var spanY = maxY - minY;
        return spanX >= Math.Max(1, imageWidth) * 0.45 &&
            spanY >= Math.Max(1, imageHeight) * 0.20;
    }

    private static SKRectI GetRealtimeTightBox(RapidOcrRealtimeLine line)
    {
        if (line.DisplayBox is { } box)
        {
            return new SKRectI(box.X, box.Y, box.X + box.Width, box.Y + box.Height);
        }

        return line.CropBox;
    }

    private static bool IsStaleRealtimeLayoutRepairReason(string reason)
        => string.Equals(reason, "rec-empty", StringComparison.Ordinal) ||
           string.Equals(reason, "script-flip", StringComparison.Ordinal) ||
           string.Equals(reason, "noise-fragment", StringComparison.Ordinal);

    private static void ReRecognizeLines(
        SKBitmap bitmap,
        List<RapidOcrRealtimeLine> lines,
        OnnxRecognizer recognizer,
        string? language)
    {
        if (lines.Count == 0)
        {
            return;
        }

        // Orientation-aware recognizer choice. The custom japan recognizer blind-rotates tall crops
        // -90deg with NO angle classifier, which garbles upright-stacked 蝮行??CJK (measured: ?芰?->??
        // ??>瞍?. The built-in detect text already in line.Text (RapidOcrNet's AngleNet pipeline)
        // reads those vertical columns correctly. So re-recognize ONLY the WIDE (horizontal) lines ??
        // where the built-in ch-v5 rec garbles Japanese kana (mo->L) and the japan rec is the fix ??
        // and leave tall vertical lines on the built-in result. (A square box counts as horizontal.)
        var recognizeCandidates = new List<int>(lines.Count);
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].CropBox.Width >= lines[i].CropBox.Height ||
                ShouldTryTallJapaneseRecognizer(lines[i], language))
            {
                recognizeCandidates.Add(i);
            }
        }

        if (recognizeCandidates.Count == 0)
        {
            return;
        }

        var crops = new SKBitmap[recognizeCandidates.Count];
        try
        {
            for (var j = 0; j < recognizeCandidates.Count; j++)
            {
                crops[j] = RapidOcrRealtimePixels.CropRecognitionSubset(bitmap, lines[recognizeCandidates[j]].CropBox);
            }

            var texts = recognizer.Recognize(crops);
            for (var j = 0; j < recognizeCandidates.Count; j++)
            {
                if (j < texts.Length)
                {
                    var line = lines[recognizeCandidates[j]];
                    var selected = SelectRealtimeRecognitionText(
                        line.Text,
                        line.Confidence,
                        texts[j],
                        line.CropBox,
                        language);
                    line.Text = selected.Text;
                    line.Confidence = selected.Confidence;
                }
            }
        }
        finally
        {
            foreach (var crop in crops)
            {
                crop?.Dispose();
            }
        }
    }

    private static bool ShouldTryTallJapaneseRecognizer(RapidOcrRealtimeLine line, string? language)
    {
        if (NormalizeLanguage(language) != "ja" || line.CropBox.Width >= line.CropBox.Height)
        {
            return false;
        }

        var text = line.Text?.Trim() ?? string.Empty;
        return text.Length > 0 &&
            !HasJapaneseScript(text) &&
            (HasAsciiLetter(text) || line.Confidence < 0.78);
    }

    internal static (string Text, double Confidence) SelectRealtimeRecognitionText(
        string? builtInText,
        double builtInConfidence,
        string? languageText,
        SKRectI cropBox,
        string? language)
    {
        var builtIn = builtInText ?? string.Empty;
        var candidate = languageText?.Trim() ?? string.Empty;
        var tallJapaneseRescue = cropBox.Width < cropBox.Height &&
            NormalizeLanguage(language) == "ja" &&
            !HasJapaneseScript(builtIn) &&
            HasJapaneseScript(candidate);
        if ((!tallJapaneseRescue && cropBox.Width < cropBox.Height) ||
            string.IsNullOrWhiteSpace(candidate))
        {
            return (builtIn, builtInConfidence);
        }

        if (string.IsNullOrWhiteSpace(builtIn))
        {
            return (candidate, Math.Max(builtInConfidence, 0.9));
        }

        var candidateConfidence = Math.Max(builtInConfidence, 0.75);
        var builtInScore = RecognitionCandidateScore(builtIn, builtInConfidence, language);
        var candidateScore = RecognitionCandidateScore(candidate, candidateConfidence, language);
        return candidateScore > builtInScore + 0.25
            ? (candidate, Math.Max(builtInConfidence, 0.9))
            : (builtIn, builtInConfidence);
    }

    private static double RecognitionCandidateScore(string text, double confidence, string? language)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return double.NegativeInfinity;
        }

        var kana = 0;
        var ideographs = 0;
        var asciiLetters = 0;
        var digits = 0;
        var questionable = 0;
        foreach (var ch in trimmed)
        {
            if (IsJapaneseKana(ch))
            {
                kana++;
            }
            else if (IsCjkIdeograph(ch))
            {
                ideographs++;
            }
            else if ((ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z'))
            {
                asciiLetters++;
            }
            else if (ch >= '0' && ch <= '9')
            {
                digits++;
            }

            if (ch is '?' or '\uFFFD')
            {
                questionable++;
            }
        }

        var score = (confidence * 2.0) + (Math.Min(trimmed.Length, 24) * 0.05) - (questionable * 1.5);
        if (NormalizeLanguage(language) == "ja")
        {
            score += (kana * 2.0) + (ideographs * 0.9) + (digits * 0.1);
            if (kana == 0 && ideographs == 0 && asciiLetters > 0)
            {
                score -= asciiLetters * 0.75;
            }
        }
        else
        {
            score += (kana + ideographs + asciiLetters + digits) * 0.2;
        }

        return score;
    }

    private static bool IsJapaneseKana(char ch)
        => (ch >= '\u3040' && ch <= '\u30ff') ||
           (ch >= '\uff66' && ch <= '\uff9f');

    private static bool IsCjkIdeograph(char ch)
        => (ch >= '\u3400' && ch <= '\u4dbf') ||
           (ch >= '\u4e00' && ch <= '\u9fff') ||
           (ch >= '\uf900' && ch <= '\ufaff');

    private static bool HasJapaneseScript(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        foreach (var ch in value)
        {
            if (IsJapaneseKana(ch) || IsCjkIdeograph(ch))
            {
                return true;
            }
        }

        return false;
    }

    private static OcrProviderResult BuildRealtimeResult(IReadOnlyList<RapidOcrRealtimeLine> lines, string engine)
    {
        var blocks = lines
            .Where(line => HasMeaningfulContent(line.Text))
            .Select(line => new OcrTextBlock(line.Text, line.Confidence, line.DisplayBox))
            .ToArray();
        var text = string.Join(Environment.NewLine, blocks.Select(block => block.Text));
        return new OcrProviderResult(text, blocks, engine);
    }

    // CJK ideographs (incl. Ext-A + compatibility), Japanese kana, and Hangul - i.e. the scripts
    // the ch PP-OCRv5 recognizer reads besides Latin. Used by the realtime script-flip guard.
    private static bool HasCjk(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        foreach (var ch in value)
        {
            if ((ch >= '\u4e00' && ch <= '\u9fff') ||   // CJK Unified Ideographs
                (ch >= '\u3400' && ch <= '\u4dbf') ||   // CJK Extension A
                (ch >= '\uf900' && ch <= '\ufaff') ||   // CJK Compatibility Ideographs
                (ch >= '\u3040' && ch <= '\u30ff') ||   // Hiragana + Katakana
                (ch >= '\uac00' && ch <= '\ud7af'))     // Hangul syllables
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasAsciiLetter(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        foreach (var ch in value)
        {
            if ((ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z'))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasDigit(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        foreach (var ch in value)
        {
            if (ch >= '0' && ch <= '9')
            {
                return true;
            }
        }

        return false;
    }

    // A block/line is worth showing only if it carries real content - a CJK char, a Latin letter,
    // or a digit. Boxes the detector drops on UI borders/icons/gradients recognize as a lone
    // punctuation mark (嚗? ????...); filtering those out removes that spurious-symbol noise.
    private static bool HasMeaningfulContent(string? value)
        => HasCjk(value) || HasAsciiLetter(value) || HasDigit(value);

    private static bool IsRealtimeNoiseFragment(RapidOcrRealtimeLine line, SKBitmap bitmap, string? language)
    {
        if (NormalizeLanguage(language) != "ja")
        {
            return false;
        }

        var text = line.Text?.Trim() ?? string.Empty;
        var contentLength = MeaningfulCharacterCount(text);
        if (contentLength == 0)
        {
            return true;
        }

        if (contentLength >= 3)
        {
            return false;
        }

        var hasJapanese = HasJapaneseScript(text);
        var kanaCount = JapaneseKanaCount(text);
        if (kanaCount > 0 && line.Confidence >= 0.72)
        {
            return false;
        }

        var box = line.DisplayBox;
        var boxWidth = box?.Width ?? line.CropBox.Width;
        var boxHeight = box?.Height ?? line.CropBox.Height;
        var boxArea = Math.Max(1, boxWidth) * Math.Max(1, boxHeight);
        var imageArea = Math.Max(1, bitmap.Width * bitmap.Height);
        var areaRatio = boxArea / (double)imageArea;
        var smallBox = boxArea < 12_000 || areaRatio < 0.012;

        if (!hasJapanese)
        {
            return contentLength <= 2;
        }

        if (contentLength == 1 && kanaCount == 0)
        {
            return smallBox || line.Confidence < 0.9;
        }

        return smallBox && line.Confidence < 0.86;
    }

    private static int MeaningfulCharacterCount(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return 0;
        }

        var count = 0;
        foreach (var ch in value)
        {
            if (IsJapaneseKana(ch) ||
                IsCjkIdeograph(ch) ||
                (ch >= 'A' && ch <= 'Z') ||
                (ch >= 'a' && ch <= 'z') ||
                (ch >= '0' && ch <= '9'))
            {
                count++;
            }
        }

        return count;
    }

    public Task<LocalOcrEngineStatus> CheckAsync(CancellationToken cancellationToken = default)
    {
        var paths = ResolveModelPaths();
        var missing = paths.AllFiles()
            .Where(path => !File.Exists(path))
            .Select(Path.GetFileName)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToArray();

        if (missing.Length > 0)
        {
            return Task.FromResult(new LocalOcrEngineStatus(
                Descriptor.Name,
                IsAvailable: false,
                missing,
                $"RapidOcrNet model files were not found in {paths.ModelDirectory}.",
                "missing_dependency"));
        }

        var note = paths.UsesCustomModels
            ? "Uses RapidOcrNet with configured custom PP-OCR model paths."
            : "Uses RapidOcrNet bundled PP-OCRv5 latin recognizer models. Configure Ocr:RapidOcrNet RecModelPath and KeysPath for CJK recognizers.";
        return Task.FromResult(new LocalOcrEngineStatus(
            Descriptor.Name,
            IsAvailable: true,
            Missing: [],
            note,
            "available"));
    }

    public void Dispose()
    {
        _engine?.Dispose();
        _recognizer?.Dispose();
        lock (_defaultOnnxRecGate)
        {
            _defaultOnnxRec?.Dispose();
        }
        lock (_langRecGate)
        {
            foreach (var recognizer in _langRecCache.Values)
            {
                recognizer?.Dispose();
            }
        }
        _initLock.Dispose();
        _runLock.Dispose();
    }

    private async ValueTask EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            var paths = ResolveModelPaths();
            var missing = paths.AllFiles().Where(path => !File.Exists(path)).ToArray();
            if (missing.Length > 0)
            {
                throw new InvalidOperationException(
                    "RapidOcrNet model files are missing: " + string.Join(", ", missing));
            }

            _deviceId = ResolveDeviceId();

            var engine = new RapidOcr();
            using var sessionOptions = CreateSessionOptions();
            engine.InitModels(
                paths.DetModelPath,
                paths.ClsModelPath,
                paths.RecModelPath,
                paths.KeysPath,
                sessionOptions);

            if (_options.RealtimeIncremental)
            {
                // Dedicated rec-only session for the realtime incremental path;
                // RapidOcr does not expose its internal recognizer.
                var recognizer = new TextRecognizer();
                using var recognizerSessionOptions = CreateSessionOptions();
                recognizer.InitModel(paths.RecModelPath, paths.KeysPath, recognizerSessionOptions);
                _recognizer = recognizer;
            }

            BuildLanguageRecMap(paths.RecModelPath);

            _engine = engine;

            TryInitLocatorEngine(paths);

            if (_options.WarmupOnInit)
            {
                WarmUp();
            }

            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    // Rebuild the detector after a DirectML failure poisoned its ONNX session. Mirrors the engine
    // construction in InitializeAsync so a recovered session is identical to a cold start.
    private void TryRecreateDetectEngine()
    {
        try
        {
            var paths = ResolveModelPaths();
            var fresh = new RapidOcr();
            using var opts = CreateSessionOptions();
            fresh.InitModels(
                paths.DetModelPath,
                paths.ClsModelPath,
                paths.RecModelPath,
                paths.KeysPath,
                opts);
            _engine = fresh;
            Console.Error.WriteLine("[ocr] detector recreated after DirectML failure.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ocr] detector recreate failed: {ex.Message}");
        }
    }

    // Build the optional coarse-locator engine (tiny/small det). Only detection runs on it; the
    // rec/cls args are reused from the main engine purely because RapidOcr.InitModels requires them
    // and they are never invoked (we only call Detect). Accuracy-neutral: medium still reads regions.
    private void TryInitLocatorEngine(RapidOcrNetModelPaths paths)
    {
        // Coarse-locator det: VB_OCR_LOCATOR_DET env override wins, else the configured
        // LocatorDetModelPath (the shipped "TINY locate + MEDIUM refine" default). Empty = off.
        var envLocatorDet = Environment.GetEnvironmentVariable("VB_OCR_LOCATOR_DET");
        var configuredLocatorDet = string.IsNullOrWhiteSpace(envLocatorDet)
            ? _options.LocatorDetModelPath
            : envLocatorDet;
        if (string.IsNullOrWhiteSpace(configuredLocatorDet))
        {
            return;
        }

        string locatorDetPath;
        try
        {
            locatorDetPath = PathResolver.Resolve(_contentRootPath, configuredLocatorDet);
        }
        catch
        {
            locatorDetPath = configuredLocatorDet;
        }

        if (!File.Exists(locatorDetPath))
        {
            Console.Error.WriteLine(
                $"[ocr] locator det not found ({configuredLocatorDet}); coarse-locate stays on main engine.");
            return;
        }

        if (string.Equals(locatorDetPath, paths.DetModelPath, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("[ocr] locator det equals main det; skipping separate locator engine.");
            return;
        }

        try
        {
            var locator = new RapidOcr();
            using var locatorSessionOptions = CreateSessionOptions();
            locator.InitModels(
                locatorDetPath,
                paths.ClsModelPath,
                paths.RecModelPath,
                paths.KeysPath,
                locatorSessionOptions);
            _locatorEngine = locator;
            Console.Error.WriteLine(
                $"[ocr] locator engine loaded: {Path.GetFileName(locatorDetPath)} for coarse-locate.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[ocr] locator engine init failed ({ex.Message.Replace('\r', ' ').Replace('\n', ' ')}); coarse-locate stays on main engine.");
        }
    }

    // Above this short side a whole-frame detect either over-downscales small subtitle text below the
    // detector floor or risks the iGPU MatMul crash; such frames are detected in tiles instead.
    private const int RealtimeTiledDetectShortSide = 760;
    // Two-stage coarse-locate (tiny locate -> scaled-region detect) entry threshold. Lower than the
    // full-grid tiling threshold above (760, kept for iGPU MatMul crash safety) because coarse-locate +
    // scaled-region detect are bounded/safe at any size. Mid frames the frontend did NOT upscale (short
    // side >= REGION_MIN_CROP_SHORT_SIDE=700) ??e.g. a 1258x711 region ??fall in the 700..760 gap: too
    // big to be upscaled, too small to trigger the old two-stage, so a single pass downscales their sparse
    // subtitle below the detector floor (faint vertical kana lost). Routing them through coarse-locate
    // detects the located region at the right scale instead.
    private const int RealtimeTwoStageMinShortSide = 700;
    // Caps the stage-2 region-detect input long side. On a small frame the frame-matched scale barely
    // shrinks (640/711=0.9), so a wide located band would be detected near-native = slow (~0.5-1.4s).
    // Capping the REGION's own long side keeps MEDIUM's input small (fast) regardless of frame size;
    // REC still reads the original full-res. Only bites mid frames ??large frames already downscale more.
    private const int RealtimeRegionDetectMaxLong = 800;
    private const int RealtimeDetectTileSide = 600;   // <= the iGPU's safe per-detect side
    private const int RealtimeDetectTileOverlap = 96; // a glyph on a seam survives whole in one tile
    private const double RealtimeRedetectCostMultiplier = 4.0;
    // DISABLED (=0): the stable-layout backoff caused dropped/clipped subtitles ("瞍") ??the periodic
    // full-detect is NOT wasted re-confirmation, it RE-FRAMES the line box so a changed subtitle reads
    // without clipping. Backing it off delayed re-framing. Kept at 0 (no backoff = original cadence)
    // pending a re-frame-preserving P3 approach.
    private const int RealtimeRedetectStableBackoffMax = 0;
    // Stage-1 coarse-locate runs on the already-downscaled detect bitmap; this is the safe short side
    // the small-frame single pass already uses without crashing the iGPU.
    private const int RealtimeCoarseLocateShortSide = 640;
    // Once a subtitle ROI is locked, the whole frame is re-scanned only this often (to catch a
    // subtitle that moved/appeared outside the ROI); every other re-detect stays scoped to the ROI.
    private const double RealtimeWholeScreenRescanIntervalMs = 5000;

    // Full-resolution tiled detection for large frames. Each tile is small enough to detect on the
    // iGPU without crashing AND keeps small text at native scale, so a whole-screen capture no longer
    // loses thin vertical kana. Tile boxes are offset into frame space and de-duplicated across the
    // overlap seams; recognition downstream still reads the original full-res bitmap.
    private bool TryDetectTiledLines(
        SKBitmap bitmap,
        OcrProviderRequest request,
        double displayScale,
        int quantizeStep,
        out List<RapidOcrRealtimeLine> lines)
    {
        lines = [];
        if (_engine is null || Math.Min(bitmap.Width, bitmap.Height) <= RealtimeTiledDetectShortSide)
        {
            return false;
        }

        var collected = new List<RapidOcrRealtimeLine>();
        var tileCount = 0;
        var tileSw = System.Diagnostics.Stopwatch.StartNew();
        DetectTilesInBounds(
            bitmap,
            request,
            displayScale,
            quantizeStep,
            new SKRectI(0, 0, bitmap.Width, bitmap.Height),
            collected,
            ref tileCount);
        lines = DedupRealtimeLinesByOverlap(collected);
        tileSw.Stop();
        Console.Error.WriteLine(
            $"[ocr] tiled detect: {tileCount} tiles -> {lines.Count} lines over {bitmap.Width}x{bitmap.Height} in {tileSw.ElapsedMilliseconds} ms.");
        return true;
    }

    // Two-stage large-frame detect. Stage 1: one cheap downscaled detect locates WHERE the text is.
    // Stage 2: tile only those regions at full resolution (typically 1-2 tiles for a subtitle instead
    // of the whole 8-12 tile grid) so small text survives without paying the full grid's per-tile DML
    // launch cost. Falls back to full-grid tiling when the coarse pass finds nothing (faint
    // low-contrast text the downscaled pass cannot see), preserving the prior accuracy floor.
    private bool TryDetectLargeFrameLines(
        SKBitmap bitmap,
        SKBitmap detBitmap,
        double coordScale,
        OcrProviderRequest request,
        double displayScale,
        int quantizeStep,
        bool allowFullGridFallback,
        out List<RapidOcrRealtimeLine> lines)
    {
        lines = [];
        if (_engine is null || Math.Min(bitmap.Width, bitmap.Height) <= RealtimeTwoStageMinShortSide)
        {
            return false; // genuinely small / frontend-upscaled crop -> caller's cheaper single pass
        }

        // Debug A/B: VB_OCR_FORCE_FULLGRID=1 skips stage 1/2 so the old full-grid path can be measured
        // against the two-stage path on identical frames.
        if (string.Equals(Environment.GetEnvironmentVariable("VB_OCR_FORCE_FULLGRID"), "1", StringComparison.Ordinal))
        {
            return TryDetectTiledLines(bitmap, request, displayScale, quantizeStep, out lines);
        }

        if (TryLocateTextRegions(detBitmap, coordScale, bitmap, request, out var regions) &&
            regions.Count > 0 &&
            DetectLinesInRegions(bitmap, request, displayScale, quantizeStep, regions, out var regionLines) &&
            regionLines.Count > 0)
        {
            lines = regionLines;
            return true;
        }

        // Coarse locate saw nothing usable. The full-grid 8-tile scan (~2s) catches faint kana the
        // downscaled locate missed, but it is far too costly to run on every blank/periodic realtime
        // frame -- so it is allowed ONLY on the cold first scan (no ROI yet). Once an ROI is locked,
        // returning empty here keeps the loop realtime; the periodic whole-screen rescan retries.
        if (allowFullGridFallback)
        {
            return TryDetectTiledLines(bitmap, request, displayScale, quantizeStep, out lines);
        }

        return true; // handled the large frame; no text found in the located regions
    }

    // Stage-1 coarse locator: a single detect on the already-downscaled detect bitmap (short side
    // ~RealtimeCoarseLocateShortSide, the same safe size the small-frame single pass uses) finds the
    // text regions. The boxes can be imprecise -- only their extent matters, since stage 2 re-tiles
    // them at full resolution. Returns false (caller full-grids) on a DML crash or no regions.
    private bool TryLocateTextRegions(
        SKBitmap detBitmap,
        double coordScale,
        SKBitmap fullBitmap,
        OcrProviderRequest request,
        out List<SKRectI> regions)
    {
        regions = [];
        if (_engine is null)
        {
            return false;
        }

        // The caller's detBitmap is usually already downscaled to DetTargetShortSide, but when that is
        // 0 (auto) it can be the full frame -- which is exactly the size that crashes the iGPU. Cap the
        // coarse pass at a safe short side and compose the extra scale into the box mapping.
        var coarse = detBitmap;
        SKBitmap? scaledCoarse = null;
        var mapScale = coordScale;
        var shortSide = Math.Min(detBitmap.Width, detBitmap.Height);
        if (shortSide > RealtimeCoarseLocateShortSide)
        {
            var s = (double)RealtimeCoarseLocateShortSide / shortSide;
            var cw = Math.Max(1, (int)Math.Round(detBitmap.Width * s));
            var ch = Math.Max(1, (int)Math.Round(detBitmap.Height * s));
            scaledCoarse = detBitmap.Resize(
                new SKImageInfo(cw, ch, detBitmap.ColorType, detBitmap.AlphaType),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
            if (scaledCoarse is not null)
            {
                coarse = scaledCoarse;
                mapScale = coordScale * detBitmap.Width / cw;
            }
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        OcrResult detect;
        var usedLocator = _locatorEngine is not null;
        try
        {
            detect = (_locatorEngine ?? _engine)!.Detect(coarse, BuildOptions(request));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[ocr] coarse locate failed ({(usedLocator ? "locator" : "main")}); {ex.Message.Replace('\r', ' ').Replace('\n', ' ')}");
            // If the dedicated locator threw, drop it and retry once on the main engine before
            // falling through to the recreate/full-grid recovery path.
            if (usedLocator)
            {
                _locatorEngine = null;
                try
                {
                    detect = _engine!.Detect(coarse, BuildOptions(request));
                }
                catch (Exception ex2)
                {
                    Console.Error.WriteLine(
                        $"[ocr] main coarse locate also failed; recreating. {ex2.Message.Replace('\r', ' ').Replace('\n', ' ')}");
                    TryRecreateDetectEngine();
                    scaledCoarse?.Dispose();
                    return false;
                }
            }
            else
            {
                TryRecreateDetectEngine();
                scaledCoarse?.Dispose();
                return false;
            }
        }

        scaledCoarse?.Dispose();
        var boxes = new List<SKRectI>();
        foreach (var block in detect.TextBlocks)
        {
            if (block.BoxPoints is not { Length: > 0 } points)
            {
                continue;
            }

            var minX = (int)Math.Round(points.Min(point => point.X) * mapScale);
            var minY = (int)Math.Round(points.Min(point => point.Y) * mapScale);
            var maxX = (int)Math.Round(points.Max(point => point.X) * mapScale);
            var maxY = (int)Math.Round(points.Max(point => point.Y) * mapScale);
            if (maxX > minX && maxY > minY)
            {
                boxes.Add(new SKRectI(minX, minY, maxX, maxY));
            }
        }

        regions = ClusterBoxesIntoRegions(boxes, fullBitmap.Width, fullBitmap.Height);
        sw.Stop();
        Console.Error.WriteLine(
            $"[ocr] coarse locate: {boxes.Count} boxes -> {regions.Count} regions in {sw.ElapsedMilliseconds} ms.");
        return regions.Count > 0;
    }

    // Groups coarse detect boxes into a few axis-aligned regions: each box is inflated by ~0.6x its
    // larger side so neighboring glyphs/columns of one subtitle merge, then overlapping inflated boxes
    // are unioned. Returns the largest few regions; a noisy locate that yields many scattered regions
    // degrades to the caller's full-grid fallback rather than tiling the whole screen piecemeal.
    private static List<SKRectI> ClusterBoxesIntoRegions(List<SKRectI> boxes, int frameWidth, int frameHeight)
    {
        var regions = new List<SKRectI>();
        if (boxes.Count == 0)
        {
            return regions;
        }

        var inflated = new List<SKRectI>(boxes.Count);
        foreach (var b in boxes)
        {
            var pad = (int)Math.Round(Math.Max(b.Width, b.Height) * 0.6) + 8;
            inflated.Add(new SKRectI(
                Math.Max(0, b.Left - pad),
                Math.Max(0, b.Top - pad),
                Math.Min(frameWidth, b.Right + pad),
                Math.Min(frameHeight, b.Bottom + pad)));
        }

        var used = new bool[inflated.Count];
        for (var i = 0; i < inflated.Count; i++)
        {
            if (used[i])
            {
                continue;
            }

            var region = inflated[i];
            used[i] = true;
            bool grew;
            do
            {
                grew = false;
                for (var j = 0; j < inflated.Count; j++)
                {
                    if (used[j] || !RectsOverlap(region, inflated[j]))
                    {
                        continue;
                    }

                    region = new SKRectI(
                        Math.Min(region.Left, inflated[j].Left),
                        Math.Min(region.Top, inflated[j].Top),
                        Math.Max(region.Right, inflated[j].Right),
                        Math.Max(region.Bottom, inflated[j].Bottom));
                    used[j] = true;
                    grew = true;
                }
            }
            while (grew);
            regions.Add(region);
        }

        return regions
            .OrderByDescending(r => (long)r.Width * r.Height)
            .Take(3)
            .ToList();
    }

    private static bool RectsOverlap(SKRectI a, SKRectI b) =>
        a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;

    // Builds the sticky ROI bands from the lines a successful detect produced: cluster the line boxes
    // into 1-3 regions, then pad generously (extra vertical room) so a subtitle that grows a line or
    // shifts slightly stays inside the locked ROI and keeps taking the cheap scoped re-detect path.
    // Structural-stability check for the periodic interval-redetect backoff (P3): same line count and
    // each line's vertical band (center + height) within tolerance. Text changes do NOT count ??only the
    // box layout. A fixed-position subtitle stays "stable" across text swaps so the periodic full-detect
    // can back off; a new / moved / removed line breaks it and resets to fast re-detect.
    private static bool IsStructurallyStableLayout(
        RapidOcrRealtimeLayout? previousLayout,
        IReadOnlyList<RapidOcrRealtimeLine> currentLines)
    {
        if (previousLayout is null ||
            currentLines.Count == 0 ||
            previousLayout.Lines.Count != currentLines.Count)
        {
            return false;
        }

        for (var i = 0; i < currentLines.Count; i++)
        {
            var a = previousLayout.Lines[i].CropBox;
            var b = currentLines[i].CropBox;
            var tol = Math.Max(16, b.Height / 3);
            if (Math.Abs((a.Top + a.Bottom) / 2 - (b.Top + b.Bottom) / 2) > tol ||
                Math.Abs(a.Height - b.Height) > tol)
            {
                return false;
            }
        }

        return true;
    }

    private List<RealtimeRoiBand>? ReconcileRealtimeRoiBands(
        SKBitmap bitmap,
        OcrProviderRequest request,
        double displayScale,
        int quantizeStep,
        RapidOcrRealtimeLayout? previousLayout,
        List<RapidOcrRealtimeLine> lines,
        string reason,
        DateTimeOffset completedAt)
    {
        if (previousLayout is null ||
            previousLayout.Width != bitmap.Width ||
            previousLayout.Height != bitmap.Height)
        {
            return lines.Count > 0
                ? BuildRoiBandsFromLines(lines, bitmap.Width, bitmap.Height, completedAt)
                : null;
        }

        var previousBands = previousLayout.RoiBands;
        if (!_options.RealtimeTrackRecoveryEnabled || previousBands is not { Count: > 0 })
        {
            return lines.Count > 0
                ? BuildRoiBandsFromLines(lines, bitmap.Width, bitmap.Height, completedAt)
                : previousBands;
        }

        var carriedBands = new List<RealtimeRoiBand>(previousBands.Count);
        var recoveredLines = new List<RapidOcrRealtimeLine>();
        var remainingRecoveries = Math.Max(0, _options.RealtimeTrackRecoveryMaxBands);
        var retireMissStreak = Math.Max(1, _options.RealtimeTrackRetireMissStreak);
        var assocIoU = Math.Clamp(_options.RealtimeTrackAssocIoU, 0.0, 1.0);

        foreach (var previousBand in previousBands)
        {
            var rect = ClampRect(previousBand.Rect, bitmap.Width, bitmap.Height);
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                continue;
            }

            if (IsRealtimeBandCovered(rect, lines, assocIoU, displayScale))
            {
                carriedBands.Add(new RealtimeRoiBand
                {
                    Rect = rect,
                    MissStreak = 0,
                    LastConfirmedAt = completedAt
                });
                continue;
            }

            List<RapidOcrRealtimeLine> recovered = [];
            if (remainingRecoveries > 0)
            {
                remainingRecoveries--;
                recovered = RecoverBandLines(bitmap, request, displayScale, quantizeStep, rect);
            }

            if (recovered.Count > 0)
            {
                recoveredLines.AddRange(recovered);
                carriedBands.Add(new RealtimeRoiBand
                {
                    Rect = rect,
                    MissStreak = 0,
                    LastConfirmedAt = completedAt
                });
                Console.Error.WriteLine(
                    FormattableString.Invariant(
                        $"[ocr] track-recover ({reason}): band @({rect.Left},{rect.Top},{rect.Width},{rect.Height}) -> {recovered.Count} lines."));
                continue;
            }

            var missStreak = previousBand.MissStreak + 1;
            if (missStreak < retireMissStreak)
            {
                carriedBands.Add(new RealtimeRoiBand
                {
                    Rect = rect,
                    MissStreak = missStreak,
                    LastConfirmedAt = previousBand.LastConfirmedAt == default
                        ? previousLayout.LastFullDetectAt
                        : previousBand.LastConfirmedAt
                });
                continue;
            }

            Console.Error.WriteLine(
                FormattableString.Invariant(
                    $"[ocr] track-retire ({reason}): band @({rect.Left},{rect.Top},{rect.Width},{rect.Height}) empty x{missStreak}."));
        }

        if (recoveredLines.Count > 0)
        {
            var mergedLines = DedupRealtimeLinesByOverlap(lines.Concat(recoveredLines).ToList());
            mergedLines.Sort(CompareRealtimeLinesForDisplay);
            lines.Clear();
            lines.AddRange(mergedLines);
        }

        var lineBands = lines.Count > 0
            ? BuildRoiBandsFromLines(lines, bitmap.Width, bitmap.Height, completedAt)
            : [];
        return MergeRealtimeRoiBands(lineBands, carriedBands, bitmap.Width, bitmap.Height);
    }

    private List<RapidOcrRealtimeLine> RecoverBandLines(
        SKBitmap bitmap,
        OcrProviderRequest request,
        double displayScale,
        int quantizeStep,
        SKRectI band)
    {
        if (_engine is null)
        {
            return [];
        }

        var rect = ClampRect(band, bitmap.Width, bitmap.Height);
        if (rect.Width < 8 || rect.Height < 8)
        {
            return [];
        }

        var minShortSide = Math.Max(1, _options.RealtimeTrackRecoveryMinShortSide);
        var shortSide = Math.Min(rect.Width, rect.Height);
        var scale = shortSide > 0 && shortSide < minShortSide
            ? Math.Min(4.0, minShortSide / (double)shortSide)
            : 1.0;
        var width = Math.Max(rect.Width, (int)Math.Round(rect.Width * scale));
        var height = Math.Max(rect.Height, (int)Math.Round(rect.Height * scale));

        using var crop = CopyCropToBgraBitmap(bitmap, rect, width, height);
        var mapped = MapAndFilterRecoveredBandLines(
            bitmap,
            request,
            displayScale,
            quantizeStep,
            rect,
            crop,
            DetectTrackRecoveryLocalLines(crop, request, quantizeStep));
        if (mapped.Count == 0)
        {
            using var enhanced = EnhanceLowContrast(crop);
            mapped = MapAndFilterRecoveredBandLines(
                bitmap,
                request,
                displayScale,
                quantizeStep,
                rect,
                enhanced,
                DetectTrackRecoveryLocalLines(enhanced, request, quantizeStep));
        }

        if (mapped.Count == 0)
        {
            return [];
        }

        var langRec = ResolveLangRecognizer(request.Language);
        if (langRec is not null)
        {
            ReRecognizeLines(bitmap, mapped, langRec, request.Language);
        }

        return DedupRealtimeLinesByOverlap(mapped);
    }

    private List<RapidOcrRealtimeLine> DetectTrackRecoveryLocalLines(
        SKBitmap bitmap,
        OcrProviderRequest request,
        int quantizeStep)
    {
        try
        {
            var detect = _engine!.Detect(bitmap, BuildOptions(request));
            return RapidOcrRealtimePixels.BuildLines(detect, bitmap, quantizeStep, 1.0, 1.0);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[ocr] track-recover detect failed; recreating detector. {ex.Message.Replace('\r', ' ').Replace('\n', ' ')}");
            TryRecreateDetectEngine();
            return [];
        }
    }

    private List<RapidOcrRealtimeLine> MapAndFilterRecoveredBandLines(
        SKBitmap source,
        OcrProviderRequest request,
        double displayScale,
        int quantizeStep,
        SKRectI band,
        SKBitmap detectedBitmap,
        IReadOnlyList<RapidOcrRealtimeLine> localLines)
    {
        if (localLines.Count == 0)
        {
            return [];
        }

        var mapped = new List<RapidOcrRealtimeLine>(localLines.Count);
        foreach (var localLine in localLines)
        {
            var line = MapTrackRecoveryLine(
                source,
                localLine,
                band,
                detectedBitmap.Width,
                detectedBitmap.Height,
                displayScale,
                quantizeStep);
            if (line.CropBox.Width <= 0 ||
                line.CropBox.Height <= 0 ||
                !HasMeaningfulContent(line.Text) ||
                IsRealtimeNoiseFragment(line, source, request.Language) ||
                IsSuspectRecoveredBandLine(line, source, request.Language))
            {
                continue;
            }

            mapped.Add(line);
        }

        return mapped;
    }

    private bool IsSuspectRecoveredBandLine(
        RapidOcrRealtimeLine line,
        SKBitmap source,
        string? language)
    {
        if (!LooksLikeVerticalRealtimeCandidate([line], source.Width, source.Height))
        {
            return false;
        }

        var text = line.Text?.Trim() ?? string.Empty;
        var asciiOrDigit = HasAsciiLetter(text) || HasDigit(text);
        var japaneseChars = JapaneseCharCount(text);
        var kana = JapaneseKanaCount(text);
        var commitConfidence = _options.RealtimeVerticalCommitConfidence > 0
            ? _options.RealtimeVerticalCommitConfidence
            : 0.90;

        if (asciiOrDigit && line.Confidence < commitConfidence)
        {
            return true;
        }

        var japaneseLike = NormalizeLanguage(language) == "ja" ||
            japaneseChars > 0 ||
            kana > 0 ||
            HasJapaneseScript(text);
        if (japaneseLike && line.Confidence < commitConfidence)
        {
            return kana == 0 || japaneseChars < 2;
        }

        return false;
    }

    private static bool ShouldFilterSuspectVerticalRelocateLines(
        RapidOcrRealtimeLayout? previousLayout,
        IReadOnlyList<RapidOcrRealtimeLine> lines,
        string reason,
        int imageWidth,
        int imageHeight,
        out bool rejectShortFragments)
    {
        rejectShortFragments = false;
        if (previousLayout is null ||
            lines.Count == 0 ||
            !IsSuspectVerticalRelocateReason(reason) ||
            !previousLayout.Lines.Any(line => HasMeaningfulContent(line.Text)) ||
            !LooksLikeVerticalRealtimeCandidate(previousLayout.Lines, previousLayout.Width, previousLayout.Height) ||
            !LooksLikeVerticalRealtimeCandidate(lines, imageWidth, imageHeight))
        {
            return false;
        }

        var candidateText = string.Concat(lines.Select(line => line.Text?.Trim() ?? string.Empty));
        var hasAsciiOrDigit = HasAsciiLetter(candidateText) || HasDigit(candidateText);
        var hasNoKanaMultiKanjiLine = lines.Any(line =>
        {
            var text = line.Text?.Trim() ?? string.Empty;
            return JapaneseKanaCount(text) == 0 && JapaneseCharCount(text) >= 2;
        });
        var shortFragments = lines.Count > 1 &&
            lines.Count(line => MeaningfulCharacterCount(line.Text) <= 2) >= Math.Max(2, lines.Count - 1);

        rejectShortFragments = shortFragments && (hasAsciiOrDigit || hasNoKanaMultiKanjiLine);
        return hasAsciiOrDigit || hasNoKanaMultiKanjiLine || rejectShortFragments;
    }

    private static bool IsSuspectVerticalRelocateReason(string reason)
        => string.Equals(reason, "interval", StringComparison.Ordinal) ||
           IsRealtimeRepairDetectReason(reason);

    private bool IsSuspectRealtimeRelocateLine(
        RapidOcrRealtimeLine line,
        SKBitmap source,
        string? language,
        bool suspectVerticalCluster,
        bool rejectShortFragments)
    {
        if (IsSuspectRecoveredBandLine(line, source, language))
        {
            return true;
        }

        if (!suspectVerticalCluster)
        {
            return false;
        }

        var text = line.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        if (HasAsciiLetter(text) || HasDigit(text))
        {
            return true;
        }

        var japaneseChars = JapaneseCharCount(text);
        var kana = JapaneseKanaCount(text);
        var japaneseLike = NormalizeLanguage(language) == "ja" ||
            japaneseChars > 0 ||
            kana > 0 ||
            HasJapaneseScript(text);
        if (!japaneseLike)
        {
            return false;
        }

        if (kana == 0 && japaneseChars >= 2)
        {
            return true;
        }

        return rejectShortFragments && MeaningfulCharacterCount(text) <= 2;
    }

    private static RapidOcrRealtimeLine MapTrackRecoveryLine(
        SKBitmap source,
        RapidOcrRealtimeLine line,
        SKRectI band,
        int detectedWidth,
        int detectedHeight,
        double displayScale,
        int quantizeStep)
    {
        var scaleX = band.Width / (double)Math.Max(1, detectedWidth);
        var scaleY = band.Height / (double)Math.Max(1, detectedHeight);
        var mappedCrop = ClampRect(new SKRectI(
            band.Left + (int)Math.Round(line.CropBox.Left * scaleX),
            band.Top + (int)Math.Round(line.CropBox.Top * scaleY),
            band.Left + (int)Math.Round(line.CropBox.Right * scaleX),
            band.Top + (int)Math.Round(line.CropBox.Bottom * scaleY)),
            source.Width,
            source.Height);

        OcrBoundingBox? displayBox = null;
        if (line.DisplayBox is not null)
        {
            var box = line.DisplayBox;
            displayBox = new OcrBoundingBox(
                Math.Max(0, (int)Math.Round((band.Left + (box.X * scaleX)) * displayScale)),
                Math.Max(0, (int)Math.Round((band.Top + (box.Y * scaleY)) * displayScale)),
                Math.Max(1, (int)Math.Round(box.Width * scaleX * displayScale)),
                Math.Max(1, (int)Math.Round(box.Height * scaleY * displayScale)));
        }

        return new RapidOcrRealtimeLine
        {
            CropBox = mappedCrop,
            DisplayBox = displayBox,
            Hash = mappedCrop.Width > 0 && mappedCrop.Height > 0
                ? RapidOcrRealtimePixels.LineSignature(source, mappedCrop, quantizeStep)
                : 0UL,
            Text = line.Text,
            Confidence = line.Confidence
        };
    }

    private static bool IsRealtimeBandCovered(
        SKRectI band,
        IReadOnlyList<RapidOcrRealtimeLine> lines,
        double minIoU,
        double displayScale)
    {
        foreach (var line in lines)
        {
            var box = GetRealtimeTightBoxInBitmap(line, displayScale);
            if (box.Width <= 0 || box.Height <= 0)
            {
                continue;
            }

            if (RealtimeBoxIoU(band, box) >= minIoU)
            {
                return true;
            }

            var ix = Math.Max(0, Math.Min(band.Right, box.Right) - Math.Max(band.Left, box.Left));
            var iy = Math.Max(0, Math.Min(band.Bottom, box.Bottom) - Math.Max(band.Top, box.Top));
            var intersection = (double)ix * iy;
            var smallerArea = Math.Min((double)band.Width * band.Height, (double)box.Width * box.Height);
            if (smallerArea > 0 && intersection / smallerArea >= 0.65)
            {
                return true;
            }
        }

        return false;
    }

    private static SKRectI GetRealtimeTightBoxInBitmap(RapidOcrRealtimeLine line, double displayScale)
    {
        if (line.DisplayBox is not { } box)
        {
            return line.CropBox;
        }

        var scale = displayScale > 0 ? displayScale : 1.0;
        return new SKRectI(
            (int)Math.Round(box.X / scale),
            (int)Math.Round(box.Y / scale),
            (int)Math.Round((box.X + box.Width) / scale),
            (int)Math.Round((box.Y + box.Height) / scale));
    }

    private static List<RealtimeRoiBand>? MergeRealtimeRoiBands(
        IReadOnlyList<RealtimeRoiBand> lineBands,
        IReadOnlyList<RealtimeRoiBand> carriedBands,
        int frameWidth,
        int frameHeight)
    {
        var merged = new List<RealtimeRoiBand>(lineBands.Count + carriedBands.Count);
        foreach (var band in lineBands.Concat(carriedBands))
        {
            var rect = ClampRect(band.Rect, frameWidth, frameHeight);
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                continue;
            }

            if (merged.Any(existing => RealtimeBoxIoU(existing.Rect, rect) > 0.45))
            {
                continue;
            }

            merged.Add(new RealtimeRoiBand
            {
                Rect = rect,
                MissStreak = band.MissStreak,
                LastConfirmedAt = band.LastConfirmedAt
            });
        }

        return merged.Count > 0 ? merged : null;
    }

    private static List<RealtimeRoiBand> BuildRoiBandsFromLines(
        IReadOnlyList<RapidOcrRealtimeLine> lines,
        int frameWidth,
        int frameHeight,
        DateTimeOffset confirmedAt)
    {
        var boxes = lines
            .Where(line => line.CropBox.Width > 0 && line.CropBox.Height > 0)
            .Select(line => line.CropBox)
            .ToList();
        if (boxes.Count == 0)
        {
            return [];
        }

        var bands = ClusterBoxesIntoRegions(boxes, frameWidth, frameHeight);
        var padded = new List<RealtimeRoiBand>(bands.Count);
        foreach (var b in bands)
        {
            var mx = Math.Max(24, b.Width / 6);
            var my = Math.Max(32, b.Height / 2); // generous vertical: subtitles add/shift lines
            padded.Add(new RealtimeRoiBand
            {
                Rect = new SKRectI(
                    Math.Max(0, b.Left - mx),
                    Math.Max(0, b.Top - my),
                    Math.Min(frameWidth, b.Right + mx),
                    Math.Min(frameHeight, b.Bottom + my)),
                MissStreak = 0,
                LastConfirmedAt = confirmedAt
            });
        }

        return padded;
    }

    // Stage-2: full-resolution tiled detect confined to the located regions. Mirrors the full-grid
    // tiler but only walks the region bounds, so a subtitle costs 1-2 tiles instead of the whole grid.
    private bool DetectLinesInRegions(
        SKBitmap bitmap,
        OcrProviderRequest request,
        double displayScale,
        int quantizeStep,
        IReadOnlyList<SKRectI> regions,
        out List<RapidOcrRealtimeLine> lines)
    {
        lines = [];
        if (_engine is null)
        {
            return false;
        }

        var collected = new List<RapidOcrRealtimeLine>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var region in regions)
        {
            DetectScaledRegionLines(bitmap, request, displayScale, quantizeStep, region, collected);
        }

        lines = DedupRealtimeLinesByOverlap(collected);
        sw.Stop();
        Console.Error.WriteLine(
            $"[ocr] roi detect (scaled): {regions.Count} regions -> {lines.Count} lines in {sw.ElapsedMilliseconds} ms.");
        return true;
    }

    // The data-proven recipe: detect a region by DOWNSCALING the whole band (with its context) to the
    // SAME scale the full-frame detect uses (faint vertical kana reads correctly at column ~60px, not
    // at native ~102px), then map boxes back and let REC read the ORIGINAL full-res pixels. One small
    // detect per region (fast, compute-bound model loves small input) instead of N full-res tiles.
    private void DetectScaledRegionLines(
        SKBitmap bitmap,
        OcrProviderRequest request,
        double displayScale,
        int quantizeStep,
        SKRectI region,
        List<RapidOcrRealtimeLine> collected)
    {
        var x0 = Math.Clamp(region.Left, 0, bitmap.Width);
        var y0 = Math.Clamp(region.Top, 0, bitmap.Height);
        var x1 = Math.Clamp(region.Right, 0, bitmap.Width);
        var y1 = Math.Clamp(region.Bottom, 0, bitmap.Height);
        var rw = x1 - x0;
        var rh = y1 - y0;
        if (rw < 8 || rh < 8)
        {
            return;
        }

        var frameShort = Math.Min(bitmap.Width, bitmap.Height);
        var frameScale = _options.DetTargetShortSide > 0 && frameShort > 0
            ? (double)_options.DetTargetShortSide / frameShort
            : 1.0;
        // Also cap the REGION's own long side: on a small frame frameScale barely shrinks (0.9), so a
        // wide located band is detected near-native = slow. Take the more-aggressive downscale; never
        // upscale. Small regions / large frames are unaffected (frameScale dominates there).
        var regionLong = Math.Max(rw, rh);
        var regionScale = regionLong > RealtimeRegionDetectMaxLong
            ? (double)RealtimeRegionDetectMaxLong / regionLong
            : 1.0;
        var scale = Math.Min(1.0, Math.Min(frameScale, regionScale));
        var sw2 = Math.Max(8, (int)Math.Round(rw * scale));
        var sh2 = Math.Max(8, (int)Math.Round(rh * scale));

        List<RapidOcrRealtimeLine> regionLines;
        using (var scaled = new SKBitmap(sw2, sh2, bitmap.ColorType, bitmap.AlphaType))
        {
            using (var canvas = new SKCanvas(scaled))
            {
                canvas.DrawBitmap(bitmap, new SKRect(x0, y0, x1, y1), new SKRect(0, 0, sw2, sh2));
            }

            try
            {
                var detect = _engine!.Detect(scaled, BuildOptions(request));
                regionLines = RapidOcrRealtimePixels.BuildLines(detect, scaled, quantizeStep, 1.0, displayScale);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[ocr] scaled-region detect failed at ({x0},{y0}); recreating detector. {ex.Message.Replace('\r', ' ').Replace('\n', ' ')}");
                TryRecreateDetectEngine();
                return;
            }
        }

        var inv = scale > 0 ? 1.0 / scale : 1.0;
        foreach (var line in regionLines)
        {
            var box = new SKRectI(
                Math.Clamp(x0 + (int)Math.Round(line.CropBox.Left * inv), 0, bitmap.Width),
                Math.Clamp(y0 + (int)Math.Round(line.CropBox.Top * inv), 0, bitmap.Height),
                Math.Clamp(x0 + (int)Math.Round(line.CropBox.Right * inv), 0, bitmap.Width),
                Math.Clamp(y0 + (int)Math.Round(line.CropBox.Bottom * inv), 0, bitmap.Height));
            if (box.Width <= 0 || box.Height <= 0)
            {
                continue;
            }

            OcrBoundingBox? display = line.DisplayBox is { } d
                ? new OcrBoundingBox(
                    Math.Max(0, (int)Math.Round(x0 * displayScale + d.X * inv)),
                    Math.Max(0, (int)Math.Round(y0 * displayScale + d.Y * inv)),
                    Math.Max(1, (int)Math.Round(d.Width * inv)),
                    Math.Max(1, (int)Math.Round(d.Height * inv)))
                : null;
            collected.Add(new RapidOcrRealtimeLine
            {
                CropBox = box,
                DisplayBox = display,
                Text = line.Text,
                Confidence = line.Confidence,
                Hash = RapidOcrRealtimePixels.LineSignature(bitmap, box, quantizeStep)
            });
        }
    }

    // Tiles the given frame-space bounds into iGPU-safe squares, detecting each and mapping boxes back
    // to frame space. Shared by the full-grid tiler and the region tiler. A DML crash on one tile
    // recreates the detector and skips that tile rather than poisoning the whole frame.
    private void DetectTilesInBounds(
        SKBitmap bitmap,
        OcrProviderRequest request,
        double displayScale,
        int quantizeStep,
        SKRectI bounds,
        List<RapidOcrRealtimeLine> collected,
        ref int tileCount)
    {
        var step = Math.Max(1, RealtimeDetectTileSide - RealtimeDetectTileOverlap);
        var x0 = Math.Clamp(bounds.Left, 0, bitmap.Width);
        var y0 = Math.Clamp(bounds.Top, 0, bitmap.Height);
        var x1 = Math.Clamp(bounds.Right, 0, bitmap.Width);
        var y1 = Math.Clamp(bounds.Bottom, 0, bitmap.Height);
        if (x1 <= x0 || y1 <= y0)
        {
            return;
        }

        for (var ty = y0; ty < y1; ty += step)
        {
            var th = Math.Min(RealtimeDetectTileSide, y1 - ty);
            for (var tx = x0; tx < x1; tx += step)
            {
                var tw = Math.Min(RealtimeDetectTileSide, x1 - tx);
                List<RapidOcrRealtimeLine> tileLines;
                using (var tile = new SKBitmap(tw, th, bitmap.ColorType, bitmap.AlphaType))
                {
                    using (var canvas = new SKCanvas(tile))
                    {
                        canvas.DrawBitmap(bitmap, new SKRect(tx, ty, tx + tw, ty + th), new SKRect(0, 0, tw, th));
                    }

                    tileCount++;
                    try
                    {
                        var detect = _engine!.Detect(tile, BuildOptions(request));
                        tileLines = RapidOcrRealtimePixels.BuildLines(detect, tile, quantizeStep, 1.0, displayScale);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine(
                            $"[ocr] tiled detect failed at ({tx},{ty}); recreating detector. {ex.Message.Replace('\r', ' ').Replace('\n', ' ')}");
                        TryRecreateDetectEngine();
                        if (tx + tw >= x1)
                        {
                            break;
                        }

                        continue;
                    }
                }

                foreach (var line in tileLines)
                {
                    var box = new SKRectI(
                        line.CropBox.Left + tx,
                        line.CropBox.Top + ty,
                        line.CropBox.Right + tx,
                        line.CropBox.Bottom + ty);
                    OcrBoundingBox? display = line.DisplayBox is { } d
                        ? new OcrBoundingBox(
                            d.X + (int)Math.Round(tx * displayScale),
                            d.Y + (int)Math.Round(ty * displayScale),
                            d.Width,
                            d.Height)
                        : null;
                    collected.Add(new RapidOcrRealtimeLine
                    {
                        CropBox = box,
                        DisplayBox = display,
                        Text = line.Text,
                        Confidence = line.Confidence,
                        Hash = RapidOcrRealtimePixels.LineSignature(bitmap, box, quantizeStep)
                    });
                }

                if (tx + tw >= x1)
                {
                    break;
                }
            }

            if (ty + th >= y1)
            {
                break;
            }
        }
    }

    private static List<RapidOcrRealtimeLine> DedupRealtimeLinesByOverlap(List<RapidOcrRealtimeLine> lines)
    {
        var kept = new List<RapidOcrRealtimeLine>(lines.Count);
        foreach (var line in lines.OrderByDescending(candidate => candidate.Confidence))
        {
            var duplicate = false;
            foreach (var existing in kept)
            {
                if (RealtimeBoxIoU(existing.CropBox, line.CropBox) > 0.45)
                {
                    duplicate = true;
                    break;
                }
            }

            if (!duplicate)
            {
                kept.Add(line);
            }
        }

        return kept;
    }

    private static double RealtimeBoxIoU(SKRectI a, SKRectI b)
    {
        var ix = Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left));
        var iy = Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));
        var intersection = (double)ix * iy;
        if (intersection <= 0)
        {
            return 0;
        }

        var union = ((double)a.Width * a.Height) + ((double)b.Width * b.Height) - intersection;
        return union <= 0 ? 0 : intersection / union;
    }

    /// <summary>
    /// Runs a throwaway detect (+ recognize) so DirectML compiles its per-shape kernels now
    /// rather than stalling the user's first realtime frame by ~20s. No-op-cheap on CPU.
    /// </summary>
    private void WarmUp()
    {
        try
        {
            var w = Math.Max(32, _options.WarmupWidth);
            var h = Math.Max(32, _options.WarmupHeight);
            using var blank = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
            using (var canvas = new SKCanvas(blank))
            {
                canvas.Clear(SKColors.Black);
                using var paint = new SKPaint { Color = SKColors.White, IsAntialias = false, StrokeWidth = 3 };
                // A few vertical strokes give detect plausible text-shaped content to trace.
                for (var x = w / 4; x < w; x += Math.Max(8, w / 8))
                {
                    canvas.DrawLine(x, h / 8f, x, h * 7 / 8f, paint);
                }
            }

            var warmOptions = (_options.UsePythonCompat ? RapidOcrOptions.PythonCompat : RapidOcrOptions.Default) with
            {
                DoAngle = _options.DoAngle,
                ReturnWordBox = false,
                ReturnSingleCharBox = false
            };
            if (_options.DetMaxSideLen > 0)
            {
                warmOptions = warmOptions with { MaxSideLen = _options.DetMaxSideLen };
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            _engine!.Detect(blank, warmOptions);
            // Per-language rec recognizers (ja/ko/?? load lazily on first use; only the det session
            // needs the per-shape DirectML warmup above.

            Console.Error.WriteLine(
                $"[ocr] rapidocr-net warmup (det={_options.ExecutionProvider}/rec={_options.RecExecutionProvider}) " +
                $"{w}x{h} in {sw.ElapsedMilliseconds} ms.");

            var tw = RealtimeDetectTileSide;
            using var tileBlank = new SKBitmap(tw, tw, SKColorType.Bgra8888, SKAlphaType.Premul);
            using (var canvas = new SKCanvas(tileBlank))
            {
                canvas.Clear(SKColors.Black);
                using var paint = new SKPaint { Color = SKColors.White, IsAntialias = false, StrokeWidth = 3 };
                for (var x = tw / 4; x < tw; x += Math.Max(8, tw / 8))
                {
                    canvas.DrawLine(x, tw / 8f, x, tw * 7 / 8f, paint);
                }
            }
            var tileSw = System.Diagnostics.Stopwatch.StartNew();
            _engine!.Detect(tileBlank, warmOptions);
            Console.Error.WriteLine(
                $"[ocr] rapidocr-net warmup tile {tw}x{tw} in {tileSw.ElapsedMilliseconds} ms.");

            // Stage-1 coarse-locate shape: a 16:9 whole-screen frame downscales to ~1138x640, a shape
            // distinct from the warmups above. Pre-compile it so the first large-frame capture doesn't
            // stall ~1.5s on the DML kernel compile (measured cold 1774ms -> warm ~300ms).
            var cw = (int)Math.Round(RealtimeCoarseLocateShortSide * 16.0 / 9.0);
            var ch = RealtimeCoarseLocateShortSide;
            using var coarseBlank = new SKBitmap(cw, ch, SKColorType.Bgra8888, SKAlphaType.Premul);
            using (var canvas = new SKCanvas(coarseBlank))
            {
                canvas.Clear(SKColors.Black);
                using var paint = new SKPaint { Color = SKColors.White, IsAntialias = false, StrokeWidth = 3 };
                for (var x = cw / 4; x < cw; x += Math.Max(8, cw / 8))
                {
                    canvas.DrawLine(x, ch / 8f, x, ch * 7 / 8f, paint);
                }
            }
            var coarseSw = System.Diagnostics.Stopwatch.StartNew();
            _engine!.Detect(coarseBlank, warmOptions);
            Console.Error.WriteLine(
                $"[ocr] rapidocr-net warmup coarse {cw}x{ch} in {coarseSw.ElapsedMilliseconds} ms.");

            // Pre-compile the locator engine on the same coarse shape so the first stage-1 locate
            // does not stall on a fresh DirectML kernel compile.
            if (_locatorEngine is not null)
            {
                var locatorSw = System.Diagnostics.Stopwatch.StartNew();
                _locatorEngine.Detect(coarseBlank, warmOptions);
                Console.Error.WriteLine(
                    $"[ocr] rapidocr-net warmup locator coarse {cw}x{ch} in {locatorSw.ElapsedMilliseconds} ms.");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ocr] rapidocr-net warmup skipped: {ex.Message}");
        }
    }

    private int ResolveDeviceId()
    {
        var ep = (_options.ExecutionProvider ?? "cpu").Trim().ToLowerInvariant();
        if (!_options.PreferIntegratedGpu || ep is not ("dml" or "directml"))
        {
            return _options.DeviceId;
        }

        var adapters = DmlAdapterResolver.Enumerate();
        if (adapters.Count > 0)
        {
            Console.Error.WriteLine(
                "[ocr] DirectML adapters: " +
                string.Join(", ", adapters.Select(a => $"#{a.Index} {a.Description} ({a.DedicatedVideoMemoryBytes / (1024 * 1024)}MB)")));
        }

        var integrated = DmlAdapterResolver.FindIntegratedDeviceId();
        if (integrated is int id)
        {
            Console.Error.WriteLine($"[ocr] PreferIntegratedGpu -> DirectML device {id} (OCR off the discrete card).");
            return id;
        }

        Console.Error.WriteLine($"[ocr] PreferIntegratedGpu: no distinct integrated GPU found; using device {_options.DeviceId}.");
        return _options.DeviceId;
    }

    private SessionOptions CreateSessionOptions()
    {
        var threads = Math.Max(0, _options.SessionThreadCount);
        var options = RapidOcr.GetDefaultSessionOptions(threads);
        switch ((_options.ExecutionProvider ?? "cpu").Trim().ToLowerInvariant())
        {
            case "dml":
            case "directml":
                // DirectML is a Windows-only EP. Off-Windows (Linux/macOS) the native EP is absent
                // and AppendExecutionProvider_DML throws; because this provider registers
                // unconditionally and the realtime route prefers it, an unguarded call would make a
                // non-Windows run pick-then-throw. Skip the GPU EP off-Windows so it falls back to
                // the default CPU EP. (The shipped appsettings hard-set "dml" for the Windows build.)
                if (OperatingSystem.IsWindows())
                {
                    options.EnableMemoryPattern = false;
                    options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                    options.AppendExecutionProvider_DML(_deviceId);
                }
                else
                {
                    Console.Error.WriteLine(
                        "[ocr] ExecutionProvider 'dml' is Windows-only; using the CPU EP on this OS.");
                }
                break;
            case "cuda":
                try
                {
                    options.AppendExecutionProvider_CUDA(_deviceId);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(
                        $"[ocr] CUDA EP unavailable ({ex.Message}); falling back to the CPU EP.");
                }
                break;
        }

        return options;
    }

    private static string NormalizeLanguage(string? language)
        => (language ?? string.Empty).Trim().ToLowerInvariant().Split('-')[0];

    /// <summary>
    /// Builds the catalog-driven language -> rec-model map from ocr_rec_catalog.json next to the rec
    /// model, then seeds the legacy single OnnxRecModelPath (japan) as the "ja" entry for back-compat.
    /// </summary>
    private void BuildLanguageRecMap(string recModelPath)
    {
        var modelsDir = Path.GetDirectoryName(recModelPath);
        if (!string.IsNullOrEmpty(modelsDir))
        {
            var catalogPath = Path.Combine(modelsDir, "ocr_rec_catalog.json");
            if (File.Exists(catalogPath))
            {
                try
                {
                    LoadLanguageCatalog(catalogPath, modelsDir);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(
                        $"[ocr] rapidocr-net language catalog parse failed ({ex.Message}); ch v5 only.");
                }
            }
        }

        if (!_langRecPaths.ContainsKey("ja") &&
            !string.IsNullOrWhiteSpace(_options.OnnxRecModelPath) &&
            !string.IsNullOrWhiteSpace(_options.OnnxRecKeysPath))
        {
            _langRecPaths["ja"] = (
                PathResolver.Resolve(_contentRootPath, _options.OnnxRecModelPath),
                PathResolver.Resolve(_contentRootPath, _options.OnnxRecKeysPath));
        }

        if (_langRecPaths.Count > 0)
        {
            Console.Error.WriteLine(
                $"[ocr] rapidocr-net per-language rec: {string.Join(", ", _langRecPaths.Keys)} (else ch v5).");
        }
    }

    /// <summary>
    /// Parses ocr_rec_catalog.json into the language map. ch / chinese_cht / en are skipped (the ch
    /// PP-OCRv5 model is the default and reads zh + en), and when several models list a language the
    /// one covering the fewest languages wins (e.g. ru -> eslav over cyrillic).
    /// </summary>
    private void LoadLanguageCatalog(string catalogPath, string modelsDir)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(catalogPath));
        if (!doc.RootElement.TryGetProperty("recognizers", out var recs) ||
            recs.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var entries = new List<(int Count, string Model, string Dict, List<string> Langs)>();
        foreach (var rec in recs.EnumerateArray())
        {
            var key = rec.TryGetProperty("key", out var k) ? k.GetString() ?? string.Empty : string.Empty;
            if (key is "ch" or "chinese_cht" or "en")
            {
                continue;
            }

            var model = rec.TryGetProperty("model", out var m) ? m.GetString() ?? string.Empty : string.Empty;
            var dict = rec.TryGetProperty("dict", out var d) ? d.GetString() ?? string.Empty : string.Empty;
            if (string.IsNullOrEmpty(model) || string.IsNullOrEmpty(dict) ||
                !rec.TryGetProperty("languages", out var langsEl) || langsEl.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var langs = langsEl.EnumerateArray()
                .Select(x => x.GetString() ?? string.Empty)
                .Where(s => s.Length > 0)
                .ToList();
            if (langs.Count > 0)
            {
                entries.Add((langs.Count, model, dict, langs));
            }
        }

        foreach (var entry in entries.OrderBy(e => e.Count))
        {
            foreach (var lang in entry.Langs)
            {
                var b = NormalizeLanguage(lang);
                if (b is "" or "zh" or "en" || _langRecPaths.ContainsKey(b))
                {
                    continue;
                }

                _langRecPaths[b] = (Path.Combine(modelsDir, entry.Model), Path.Combine(modelsDir, entry.Dict));
            }
        }
    }

    /// <summary>
    /// Returns the per-language rec recognizer for the request language, or null to fall back to the
    /// default ch PP-OCRv5 recognizer (zh / en / unmapped languages, or when the language's model is
    /// not downloaded). Recognizers are loaded once and cached.
    /// </summary>
    private OnnxRecognizer? ResolveDefaultOnnxRecognizer()
    {
        lock (_defaultOnnxRecGate)
        {
            if (_defaultOnnxRecLoaded)
            {
                return _defaultOnnxRec;
            }

            _defaultOnnxRecLoaded = true;
            var paths = ResolveModelPaths();
            if (!File.Exists(paths.RecModelPath) || !File.Exists(paths.KeysPath))
            {
                return null;
            }

            try
            {
                _defaultOnnxRec = new OnnxRecognizer(
                    paths.RecModelPath,
                    paths.KeysPath,
                    _options.RecExecutionProvider,
                    _deviceId);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[ocr] rapidocr-net default ONNX rec failed to load ({ex.Message}); sparse slot CTC decode disabled.");
            }

            return _defaultOnnxRec;
        }
    }

    private OnnxRecognizer? ResolveLangRecognizer(string? language)
    {
        var b = NormalizeLanguage(language);
        if (b is "" or "zh" or "en" || !_langRecPaths.TryGetValue(b, out var files))
        {
            return null;
        }

        lock (_langRecGate)
        {
            if (_langRecCache.TryGetValue(b, out var cached))
            {
                return cached;
            }

            OnnxRecognizer? recognizer = null;
            if (File.Exists(files.Model) && File.Exists(files.Keys))
            {
                try
                {
                    recognizer = new OnnxRecognizer(files.Model, files.Keys, _options.RecExecutionProvider, _deviceId);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[ocr] rapidocr-net rec for '{b}' failed to load ({ex.Message}); using ch v5.");
                }
            }
            else
            {
                Console.Error.WriteLine(
                    $"[ocr] rapidocr-net rec for '{b}' not present ({Path.GetFileName(files.Model)}); using ch v5.");
            }

            _langRecCache[b] = recognizer;
            return recognizer;
        }
    }

    private RapidOcrOptions BuildOptions(OcrProviderRequest request)
    {
        var options = _options.UsePythonCompat
            ? RapidOcrOptions.PythonCompat
            : RapidOcrOptions.Default;
        var doAngle = _options.DoAngle && !IsUprightRealtimePreset(request.PreprocessingPreset);
        options = options with
        {
            DoAngle = doAngle,
            ReturnWordBox = false,
            ReturnSingleCharBox = false
        };
        if (_options.DetMaxSideLen > 0)
        {
            options = options with { MaxSideLen = _options.DetMaxSideLen };
        }

        if (UsesLowContrastDetectPreset(request.PreprocessingPreset))
        {
            options = options with
            {
                BoxScoreThresh = 0.28f,
                BoxThresh = 0.18f,
                UnClipRatio = 1.85f
            };
        }

        return options;
    }

    private static bool UsesLowContrastDetectPreset(string value)
        => value.Equals("clahe-l", StringComparison.OrdinalIgnoreCase) ||
           value.Equals("lab-clahe", StringComparison.OrdinalIgnoreCase);

    private RapidOcrNetModelPaths ResolveModelPaths()
    {
        var defaultModelDirectory = Path.Combine(
            AppContext.BaseDirectory,
            RapidOcr.ModelsFolderName,
            RapidOcr.ModelsVersion);
        var detPath = ResolveModelPath(_options.DetModelPath, Path.Combine(defaultModelDirectory, RapidOcr.DefaultDetModelPath));
        var clsPath = ResolveModelPath(_options.ClsModelPath, Path.Combine(defaultModelDirectory, RapidOcr.DefaultClsModelPath));
        var recPath = ResolveModelPath(_options.RecModelPath, Path.Combine(defaultModelDirectory, RapidOcr.DefaultRecModelPath));
        var keysPath = ResolveModelPath(_options.KeysPath, Path.Combine(defaultModelDirectory, RapidOcr.DefaultKeysFilePath));
        var usesCustomModels =
            !string.IsNullOrWhiteSpace(_options.DetModelPath) ||
            !string.IsNullOrWhiteSpace(_options.ClsModelPath) ||
            !string.IsNullOrWhiteSpace(_options.RecModelPath) ||
            !string.IsNullOrWhiteSpace(_options.KeysPath);

        return new RapidOcrNetModelPaths(
            defaultModelDirectory,
            detPath,
            clsPath,
            recPath,
            keysPath,
            usesCustomModels);
    }

    private string ResolveModelPath(string configuredPath, string fallbackPath)
        => string.IsNullOrWhiteSpace(configuredPath)
            ? fallbackPath
            : PathResolver.Resolve(_contentRootPath, configuredPath);

    private static bool IsUprightRealtimePreset(string value)
        => value.Equals("text-line", StringComparison.OrdinalIgnoreCase) ||
           value.Equals("subtitle", StringComparison.OrdinalIgnoreCase) ||
           value.Equals("screenshot", StringComparison.OrdinalIgnoreCase) ||
           value.Equals("threshold", StringComparison.OrdinalIgnoreCase) ||
           value.Equals("isolate", StringComparison.OrdinalIgnoreCase) ||
           value.Equals("contrast", StringComparison.OrdinalIgnoreCase) ||
           value.Equals("denoise", StringComparison.OrdinalIgnoreCase);

    private static double ConfidenceFor(TextBlock block)
    {
        if (block.CharScores is { Length: > 0 })
        {
            return Math.Clamp(block.CharScores.Average(score => (double)score), 0, 1);
        }

        return Math.Clamp(block.BoxScore, 0, 1);
    }

    private static OcrBoundingBox? BoundingBoxFrom(SKPointI[]? points)
    {
        if (points is null || points.Length == 0)
        {
            return null;
        }

        var minX = points.Min(point => point.X);
        var minY = points.Min(point => point.Y);
        var maxX = points.Max(point => point.X);
        var maxY = points.Max(point => point.Y);
        var width = maxX - minX;
        var height = maxY - minY;
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        return new OcrBoundingBox(
            Math.Max(0, minX),
            Math.Max(0, minY),
            Math.Max(1, width),
            Math.Max(1, height));
    }

    internal readonly record struct ArtVerticalRescueCandidate(SKRectI Crop, int Scale, int Border);

    internal enum SparseGlyphOrientation
    {
        Unknown,
        Horizontal,
        Vertical
    }

    internal static bool ShouldUseSparseGlyphRescueOrientation(SparseGlyphOrientation orientation)
        => orientation is SparseGlyphOrientation.Horizontal or SparseGlyphOrientation.Vertical;

    internal readonly record struct SparseGlyphCandidate(
        SparseGlyphOrientation Orientation,
        IReadOnlyList<SKPointI> Centers,
        int WindowSize,
        SKRectI Bounds);

    private readonly record struct SparseGlyphComponent(
        double CenterX,
        double CenterY,
        int X,
        int Y,
        int Width,
        int Height,
        int Area);

    private sealed record RapidOcrNetModelPaths(
        string ModelDirectory,
        string DetModelPath,
        string ClsModelPath,
        string RecModelPath,
        string KeysPath,
        bool UsesCustomModels)
    {
        public IEnumerable<string> AllFiles()
        {
            yield return DetModelPath;
            yield return ClsModelPath;
            yield return RecModelPath;
            yield return KeysPath;
        }
    }
}
