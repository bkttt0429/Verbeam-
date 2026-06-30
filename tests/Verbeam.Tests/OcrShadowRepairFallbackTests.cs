using System.Drawing;
using System.Drawing.Imaging;
using Verbeam.Core.Models;
using Verbeam.Core.Options;
using Verbeam.Core.Providers;
using Verbeam.Core.Services;
using Verbeam.Core.Storage;

namespace Verbeam.Tests;

public sealed class OcrShadowRepairFallbackTests : IDisposable
{
    private readonly string _tempDirectory =
        Path.Combine(Path.GetTempPath(), "verbeam-ocr-shadow-repair-tests-" + Guid.NewGuid());

    private sealed class ScriptedOcrProvider : IOcrProvider, IShadowRepairOcrProvider
    {
        private readonly Func<OcrProviderRequest, OcrProviderResult> _recognize;
        private readonly Func<OcrShadowRepairProviderRequest, OcrShadowRepairProviderResult?>? _recognizeShadowRepair;

        public ScriptedOcrProvider(
            string name,
            Func<OcrProviderRequest, OcrProviderResult> recognize,
            Func<OcrShadowRepairProviderRequest, OcrShadowRepairProviderResult?>? recognizeShadowRepair = null)
        {
            _recognize = recognize;
            _recognizeShadowRepair = recognizeShadowRepair;
            Descriptor = new OcrProviderDescriptor(
                name,
                name,
                "test",
                "en",
                RequiresExternalProcess: false,
                IsLocal: true)
            {
                IsLanguageAgnostic = true
            };
        }

        public OcrProviderDescriptor Descriptor { get; }
        public List<OcrProviderRequest> Requests { get; } = [];
        public List<OcrShadowRepairProviderRequest> ShadowRepairRequests { get; } = [];

        public Task<OcrProviderResult> RecognizeAsync(
            OcrProviderRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(_recognize(request));
        }

        public Task<OcrShadowRepairProviderResult?> RecognizeShadowRepairAsync(
            OcrShadowRepairProviderRequest request,
            CancellationToken cancellationToken)
        {
            ShadowRepairRequests.Add(request);
            return Task.FromResult(_recognizeShadowRepair?.Invoke(request));
        }
    }

    private (OcrService Service, ScriptedOcrProvider Primary, ScriptedOcrProvider OneOcr) CreateService(
        OcrProviderResult? primaryResult = null,
        bool realtimeRepairEnabled = true,
        Func<OcrProviderRequest, OcrProviderResult>? primaryRecognize = null,
        Func<OcrShadowRepairProviderRequest, OcrShadowRepairProviderResult?>? primaryShadowRepairRecognize = null)
    {
        Directory.CreateDirectory(_tempDirectory);
        primaryResult ??= new OcrProviderResult(
            "YOU ARATHEONE. OUR RISINGSTAR",
            [new OcrTextBlock("YOU ARATHEONE. OUR RISINGSTAR", 0.7, new OcrBoundingBox(10, 10, 180, 20))],
            "rapidocr-net:test");
        var primary = new ScriptedOcrProvider(
            "rapidocr-net",
            primaryRecognize ?? (_ => primaryResult),
            primaryShadowRepairRecognize);
        var oneOcr = new ScriptedOcrProvider(
            "oneocr",
            _ => new OcrProviderResult(
                "YOU ARE THE ONE, OUR RISING STAR",
                [new OcrTextBlock("YOU ARE THE ONE, OUR RISING STAR", 0.95, new OcrBoundingBox(30, 12, 180, 24))],
                "oneocr:test"));
        var options = new VerbeamOptions
        {
            Ocr =
            {
                DefaultProvider = "rapidocr-net",
                DefaultLanguage = "en",
                ShadowRepair =
                {
                    Enabled = true,
                    RealtimeEnabled = realtimeRepairEnabled,
                    PreferredProvider = "oneocr",
                    Scale = 3,
                    MinQualityGain = 2
                }
            }
        };
        var registry = new OcrProviderRegistry([primary, oneOcr]);
        var store = new SqliteOcrMemoryStore(Path.Combine(_tempDirectory, Guid.NewGuid().ToString("N") + ".sqlite"));
        var routing = new OcrRoutingService(options, registry);
        var limiter = new OcrConcurrencyLimiter(options);
        return (new OcrService(options, registry, store, routing, limiter), primary, oneOcr);
    }

    [Fact]
    public async Task LowContrastWideSubtitle_UsesShadowRepairAndMapsBoxes()
    {
        var (service, _, oneOcr) = CreateService();

        var response = await service.RecognizeAsync(new OcrRequest
        {
            ImageBase64 = CreateWidePngBase64(),
            ImageMimeType = "image/png",
            Provider = "rapidocr-net",
            Language = "en",
            NormalizeWhitespace = false
        });

        Assert.NotEmpty(oneOcr.Requests);
        Assert.Equal("oneocr", response.Provider);
        Assert.Contains("shadow-repair:main-clahe", response.Engine, StringComparison.Ordinal);
        Assert.Equal("YOU ARE THE ONE, OUR RISING STAR", response.Text);
        var box = Assert.Single(response.Blocks).BoundingBox;
        Assert.NotNull(box);
        Assert.Equal(10, box!.X);
        Assert.InRange(box.Y, 5, 7);
        Assert.Equal(60, box.Width);
        Assert.Equal(8, box.Height);
        Assert.NotNull(response.Document);
        var page = Assert.Single(response.Document!.Pages);
        Assert.Equal(300, page.Width);
        Assert.Equal(50, page.Height);
    }

    [Fact]
    public async Task RealtimeLowContrastWideSubtitle_UsesSingleShadowRepairCandidate()
    {
        var (service, primary, oneOcr) = CreateService(
            primaryRecognize: _ => new OcrProviderResult(
                "YOUARETHEONE OUR RISINCATAR",
                [new OcrTextBlock("YOUARETHEONE OUR RISINCATAR", 0.82, new OcrBoundingBox(30, 12, 210, 24))],
                "rapidocr-net:test"));

        var response = await service.RecognizeAsync(new OcrRequest
        {
            ImageBase64 = CreateBrightMixedWidePngBase64(),
            ImageMimeType = "image/png",
            Provider = "rapidocr-net",
            Language = "en",
            NormalizeWhitespace = false,
            Realtime = true
        });

        var request = Assert.Single(primary.Requests);
        Assert.True(request.Realtime);
        Assert.Empty(oneOcr.Requests);
        Assert.Equal("rapidocr-net", response.Provider);
        Assert.Contains("shadow-repair:main-clahe", response.Engine, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("YOU ARE THE ONE, OUR RISING STAR", response.Text);
    }

    [Fact]
    public async Task RealtimeBrightWideLatinSubtitle_UsesShadowRepairBeforePrimary()
    {
        var (service, primary, oneOcr) = CreateService(
            primaryRecognize: _ => new OcrProviderResult(
                "YOUARETHEONE OUR RISINCATAR",
                [new OcrTextBlock("YOUARETHEONE OUR RISINCATAR", 0.82, new OcrBoundingBox(30, 12, 210, 24))],
                "rapidocr-net:test"));

        var response = await service.RecognizeAsync(new OcrRequest
        {
            ImageBase64 = CreateBrightMixedWidePngBase64(),
            ImageMimeType = "image/png",
            Provider = "rapidocr-net",
            Language = "en",
            NormalizeWhitespace = false,
            Realtime = true
        });

        Assert.Single(primary.Requests);
        Assert.Empty(oneOcr.Requests);
        var request = Assert.Single(primary.Requests);
        Assert.True(request.Realtime);
        Assert.Equal("rapidocr-net", response.Provider);
        Assert.Contains("shadow-repair:main-clahe", response.Engine, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("YOU ARE THE ONE, OUR RISING STAR", response.Text);
    }

    [Fact]
    public async Task RealtimeNativeShadowRepair_ReordersWideSingleLineBlocksBeforeCleanup()
    {
        var (service, primary, oneOcr) = CreateService(
            primaryRecognize: _ => throw new InvalidOperationException("Native repair should run before primary OCR."),
            primaryShadowRepairRecognize: request =>
            {
                var scale = request.Scale;
                var width = Math.Max(1, (int)Math.Round(request.CropWidth * scale));
                var height = Math.Max(1, (int)Math.Round(request.CropHeight * scale));
                var result = new OcrProviderResult(
                    "IE. OUR RISING\nTHE\nYOu ArE\nTAR",
                    [
                        new OcrTextBlock("IE. OUR RISING", 0.93, new OcrBoundingBox(430, 35, 300, 40)),
                        new OcrTextBlock("THE", 0.98, new OcrBoundingBox(230, 35, 120, 40)),
                        new OcrTextBlock("YOu ArE", 0.81, new OcrBoundingBox(50, 35, 160, 40)),
                        new OcrTextBlock("TAR", 0.99, new OcrBoundingBox(760, 35, 110, 40))
                    ],
                    "rapidocr-net:test");
                return new OcrShadowRepairProviderResult(
                    result,
                    request.CandidateName,
                    scale,
                    request.CropX,
                    request.CropY,
                    300,
                    50,
                    width,
                    height);
            });

        var response = await service.RecognizeAsync(new OcrRequest
        {
            ImageBase64 = CreateBrightMixedWidePngBase64(),
            ImageMimeType = "image/png",
            Provider = "rapidocr-net",
            Language = "en",
            NormalizeWhitespace = false,
            Realtime = true
        });

        Assert.Single(primary.ShadowRepairRequests);
        Assert.Empty(primary.Requests);
        Assert.Empty(oneOcr.Requests);
        Assert.Equal("rapidocr-net", response.Provider);
        Assert.Contains("shadow-repair:main-clahe", response.Engine, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("YOU ARE THE ONE, OUR RISING STAR", response.Text);
        Assert.Single(response.Blocks);
    }

    [Fact]
    public async Task RealtimeCleanPrimary_DoesNotRunShadowRepair()
    {
        var cleanPrimary = new OcrProviderResult(
            "YOU ARE THE ONE, OUR RISING STAR",
            [new OcrTextBlock("YOU ARE THE ONE, OUR RISING STAR", 0.96, new OcrBoundingBox(10, 10, 240, 24))],
            "rapidocr-net:test");
        var (service, _, oneOcr) = CreateService(cleanPrimary);

        var response = await service.RecognizeAsync(new OcrRequest
        {
            ImageBase64 = CreateWidePngBase64(),
            ImageMimeType = "image/png",
            Provider = "rapidocr-net",
            Language = "en",
            NormalizeWhitespace = false,
            Realtime = true
        });

        Assert.Empty(oneOcr.Requests);
        Assert.Equal("rapidocr-net", response.Provider);
        Assert.DoesNotContain("shadow-repair", response.Engine, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("YOU ARE THE ONE, OUR RISING STAR", response.Text);
    }

    [Fact]
    public async Task RealtimeRequest_IgnoresNonRealtimeShadowRepairCache()
    {
        var (service, primary, oneOcr) = CreateService(
            primaryRecognize: _ => new OcrProviderResult(
                "YOUARETHEONE OUR RISINCATAR",
                [new OcrTextBlock("YOUARETHEONE OUR RISINCATAR", 0.82, new OcrBoundingBox(30, 12, 210, 24))],
                "rapidocr-net:test"));
        var imageBase64 = CreateBrightMixedWidePngBase64();

        var repaired = await service.RecognizeAsync(new OcrRequest
        {
            ImageBase64 = imageBase64,
            ImageMimeType = "image/png",
            Provider = "rapidocr-net",
            Language = "en",
            NormalizeWhitespace = false
        });
        Assert.Contains("shadow-repair", repaired.Engine, StringComparison.OrdinalIgnoreCase);

        primary.Requests.Clear();
        oneOcr.Requests.Clear();
        var realtime = await service.RecognizeAsync(new OcrRequest
        {
            ImageBase64 = imageBase64,
            ImageMimeType = "image/png",
            Provider = "rapidocr-net",
            Language = "en",
            NormalizeWhitespace = false,
            Realtime = true
        });

        Assert.NotEmpty(primary.Requests);
        Assert.Empty(oneOcr.Requests);
        Assert.False(realtime.CacheHit);
        Assert.Equal("rapidocr-net", realtime.Provider);
        Assert.Contains("shadow-repair:main-clahe", realtime.Engine, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("YOU ARE THE ONE, OUR RISING STAR", realtime.Text);
    }

    private static string CreateWidePngBase64()
    {
        using var bitmap = new Bitmap(300, 50, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.White);
            using var brush = new SolidBrush(Color.FromArgb(245, 245, 245));
            graphics.FillRectangle(brush, 0, 0, 300, 50);
        }

        using var output = new MemoryStream();
        bitmap.Save(output, ImageFormat.Png);
        return Convert.ToBase64String(output.ToArray());
    }

    private static string CreateBrightMixedWidePngBase64()
    {
        using var bitmap = new Bitmap(300, 50, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.FromArgb(248, 248, 248));
            using var shadow = new SolidBrush(Color.FromArgb(60, 60, 60));
            graphics.FillRectangle(shadow, 0, 6, 90, 20);
        }

        using var output = new MemoryStream();
        bitmap.Save(output, ImageFormat.Png);
        return Convert.ToBase64String(output.ToArray());
    }

    public void Dispose()
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
