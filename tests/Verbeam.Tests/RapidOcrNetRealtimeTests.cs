using SkiaSharp;
using Verbeam.Core.Models;
using Verbeam.Core.Options;
using Verbeam.Core.Providers;

namespace Verbeam.Tests;

/// <summary>
/// Exercises the rapidocr-net realtime incremental path (layout cache +
/// batch rec-only). Tests no-op when the bundled ONNX models are missing.
/// </summary>
[Collection(NonParallelTestCollection.Name)]
public sealed class RapidOcrNetRealtimeTests : IClassFixture<RapidOcrNetRealtimeTests.ProviderFixture>
{
    private const string FullEngine = "rapidocr-net:ppocrv5-onnx";
    private const string IncrementalEngine = "rapidocr-net:ppocrv5-onnx-incremental";

    private readonly ProviderFixture _fixture;

    public RapidOcrNetRealtimeTests(ProviderFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void RealtimeDualRec_PrefersJapaneseKanaCandidateOverLatinNoise()
    {
        var selected = RapidOcrNetProvider.SelectRealtimeRecognitionText(
            "L",
            0.95,
            "\u3082",
            new SKRectI(0, 0, 80, 20),
            "ja-JP");

        Assert.Equal("\u3082", selected.Text);
        Assert.True(selected.Confidence >= 0.95);
    }

    [Fact]
    public void RealtimeDualRec_KeepsBuiltInKanjiWhenLanguageCandidateIsLatin()
    {
        var selected = RapidOcrNetProvider.SelectRealtimeRecognitionText(
            "\u7121",
            0.9,
            "abc",
            new SKRectI(0, 0, 80, 20),
            "ja-JP");

        Assert.Equal("\u7121", selected.Text);
        Assert.Equal(0.9, selected.Confidence);
    }

    [Fact]
    public void RealtimeDualRec_KeepsVerticalColumnsOnBuiltInRecognizer()
    {
        var selected = RapidOcrNetProvider.SelectRealtimeRecognitionText(
            "\u7121",
            0.9,
            "\u3082",
            new SKRectI(0, 0, 20, 80),
            "ja-JP");

        Assert.Equal("\u7121", selected.Text);
    }

    [Fact]
    public async Task RealtimeFrames_UnchangedAndChangedLines_UseIncrementalPath()
    {
        if (!await _fixture.IsAvailableAsync())
        {
            return;
        }

        var frameA = RenderLines("HELLO WORLD", "AAAA BBBB CCCC");
        var frameB = RenderLines("HELLO WORLD", "XXXX YYYY ZZZZ");

        var first = await RecognizeAsync(_fixture.Provider, frameA, realtime: true, sessionKey: "rt-incremental");
        Assert.Equal(FullEngine, first.Engine);
        Assert.Contains("HELLO", first.Text, StringComparison.OrdinalIgnoreCase);

        var unchanged = await RecognizeAsync(_fixture.Provider, frameA, realtime: true, sessionKey: "rt-incremental");
        Assert.Equal(IncrementalEngine, unchanged.Engine);
        Assert.Equal(first.Text, unchanged.Text);

        var changed = await RecognizeAsync(_fixture.Provider, frameB, realtime: true, sessionKey: "rt-incremental");
        Assert.Equal(IncrementalEngine, changed.Engine);
        Assert.Contains("HELLO", changed.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ZZZZ", changed.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SingleTrailingCharacterChange_IsDetectedIncrementally()
    {
        if (!await _fixture.IsAvailableAsync())
        {
            return;
        }

        // Regression: a coarse 8x8 average hash sampled wide dialogue lines so
        // sparsely that a one-digit update slipped through and stale text was
        // served. The dense line signature must catch it.
        var frameA = RenderLines("SYSTEM MESSAGE WINDOW", "DIALOGUE TEXT NUMBER 000");
        var frameB = RenderLines("SYSTEM MESSAGE WINDOW", "DIALOGUE TEXT NUMBER 001");

        var first = await RecognizeAsync(_fixture.Provider, frameA, realtime: true, sessionKey: "rt-digit");
        Assert.Equal(FullEngine, first.Engine);
        Assert.Contains("000", first.Text, StringComparison.Ordinal);

        var changed = await RecognizeAsync(_fixture.Provider, frameB, realtime: true, sessionKey: "rt-digit");
        Assert.Equal(IncrementalEngine, changed.Engine);
        Assert.Contains("001", changed.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RealtimeFrame_SizeChange_RebuildsLayoutWithFullDetect()
    {
        if (!await _fixture.IsAvailableAsync())
        {
            return;
        }

        var frameA = RenderLines("HELLO WORLD", "AAAA BBBB CCCC");
        var smaller = RenderLines(500, 160, "HELLO WORLD");

        var first = await RecognizeAsync(_fixture.Provider, frameA, realtime: true, sessionKey: "rt-resize");
        Assert.Equal(FullEngine, first.Engine);

        var resized = await RecognizeAsync(_fixture.Provider, smaller, realtime: true, sessionKey: "rt-resize");
        Assert.Equal(FullEngine, resized.Engine);
    }

    [Fact]
    public async Task NonRealtimeRequest_KeepsStandardPath()
    {
        if (!await _fixture.IsAvailableAsync())
        {
            return;
        }

        var frame = RenderLines("HELLO WORLD", "AAAA BBBB CCCC");
        var first = await RecognizeAsync(_fixture.Provider, frame, realtime: false, sessionKey: "rt-standard");
        var second = await RecognizeAsync(_fixture.Provider, frame, realtime: false, sessionKey: "rt-standard");
        Assert.Equal(FullEngine, first.Engine);
        Assert.Equal(FullEngine, second.Engine);
    }

    [Fact]
    public async Task RedetectIntervalElapsed_ForcesFullDetect()
    {
        if (!await _fixture.IsAvailableAsync())
        {
            return;
        }

        using var provider = CreateProvider(options => options.RealtimeRedetectIntervalMs = 0);
        var frame = RenderLines("HELLO WORLD", "AAAA BBBB CCCC");

        var first = await RecognizeAsync(provider, frame, realtime: true, sessionKey: "rt-interval");
        var second = await RecognizeAsync(provider, frame, realtime: true, sessionKey: "rt-interval");
        Assert.Equal(FullEngine, first.Engine);
        Assert.Equal(FullEngine, second.Engine);
    }

    [Fact]
    public async Task RealtimeIncrementalDisabled_UsesStandardPath()
    {
        if (!await _fixture.IsAvailableAsync())
        {
            return;
        }

        using var provider = CreateProvider(options => options.RealtimeIncremental = false);
        var frame = RenderLines("HELLO WORLD", "AAAA BBBB CCCC");

        var first = await RecognizeAsync(provider, frame, realtime: true, sessionKey: "rt-disabled");
        var second = await RecognizeAsync(provider, frame, realtime: true, sessionKey: "rt-disabled");
        Assert.Equal(FullEngine, first.Engine);
        Assert.Equal(FullEngine, second.Engine);
    }

    private static Task<OcrProviderResult> RecognizeAsync(
        RapidOcrNetProvider provider,
        byte[] imageBytes,
        bool realtime,
        string sessionKey)
        => provider.RecognizeAsync(
            new OcrProviderRequest(
                imageBytes,
                "image/png",
                "en",
                NormalizeWhitespace: false,
                PreprocessingPreset: "screenshot",
                Realtime: realtime,
                SessionKey: sessionKey),
            CancellationToken.None);

    private static byte[] RenderLines(params string[] lines)
        => RenderLines(600, 200, lines);

    private static byte[] RenderLines(int width, int height, params string[] lines)
    {
        var info = new SKImageInfo(width, height);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);
        using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        using var font = new SKFont(SKTypeface.Default, 32);
        for (var i = 0; i < lines.Length; i++)
        {
            canvas.DrawText(lines[i], 24, 56 + i * 64, SKTextAlign.Left, font, paint);
        }

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    internal static RapidOcrNetProvider CreateProvider(Action<RapidOcrNetOptions>? configure = null)
    {
        var options = new RapidOcrNetOptions();
        configure?.Invoke(options);
        return new RapidOcrNetProvider(
            new OcrProviderDescriptor(
                "rapidocr-net",
                "RapidOcrNet / PP-OCRv5 ONNX",
                "local-dotnet-onnx",
                "en",
                RequiresExternalProcess: false,
                IsLocal: true)
            {
                IsLanguageAgnostic = true
            },
            options,
            AppContext.BaseDirectory);
    }

    public sealed class ProviderFixture : IDisposable
    {
        private bool? _available;

        public RapidOcrNetProvider Provider { get; } = CreateProvider();

        public async Task<bool> IsAvailableAsync()
        {
            _available ??= (await Provider.CheckAsync()).IsAvailable;
            return _available.Value;
        }

        public void Dispose()
            => Provider.Dispose();
    }
}
