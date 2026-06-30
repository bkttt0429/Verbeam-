using System.Text;
using Verbeam.Core.Models;
using Verbeam.Core.Options;
using Verbeam.Core.Providers;
using Verbeam.Core.Services;
using Verbeam.Core.Storage;

namespace Verbeam.Tests;

public sealed class OcrEmptyResultFallbackTests : IDisposable
{
    private readonly string _tempDirectory =
        Path.Combine(Path.GetTempPath(), "verbeam-ocr-fallback-tests-" + Guid.NewGuid());

    private sealed class FixedOcrProvider : IOcrProvider
    {
        private readonly OcrProviderResult _result;

        public FixedOcrProvider(string name, OcrProviderResult result)
        {
            _result = result;
            Descriptor = new OcrProviderDescriptor(name, name, "test", "ja-JP", RequiresExternalProcess: false, IsLocal: true)
            {
                IsLanguageAgnostic = true
            };
        }

        public OcrProviderDescriptor Descriptor { get; }
        public int Calls { get; private set; }

        public Task<OcrProviderResult> RecognizeAsync(OcrProviderRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(_result);
        }
    }

    private (OcrService Service, FixedOcrProvider Primary, FixedOcrProvider OneOcr) CreateService(
        bool fallbackEnabled,
        OcrProviderResult? primaryResult = null,
        OcrProviderResult? oneOcrResult = null)
    {
        Directory.CreateDirectory(_tempDirectory);
        primaryResult ??= new OcrProviderResult(string.Empty, [], "rapidocr-net");
        oneOcrResult ??= new OcrProviderResult(
            "読めた", [new OcrTextBlock("読めた", 0.9, null)], "oneocr");
        var primary = new FixedOcrProvider("rapidocr-net", primaryResult);
        var oneOcr = new FixedOcrProvider("oneocr", oneOcrResult);
        var options = new VerbeamOptions
        {
            Ocr =
            {
                DefaultProvider = "rapidocr-net",
                DefaultLanguage = "ja",
                FallbackToOneOcrOnEmpty = fallbackEnabled
            }
        };
        var registry = new OcrProviderRegistry([primary, oneOcr]);
        var store = new SqliteOcrMemoryStore(Path.Combine(_tempDirectory, Guid.NewGuid().ToString("N") + ".sqlite"));
        var routing = new OcrRoutingService(options, registry);
        var limiter = new OcrConcurrencyLimiter(options);
        return (new OcrService(options, registry, store, routing, limiter), primary, oneOcr);
    }

    private static string Encode(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    private static OcrRequest Request(bool realtime = false) => new()
    {
        ImageBase64 = Encode("image"),
        Provider = "rapidocr-net",
        Language = "ja",
        Realtime = realtime
    };

    [Fact]
    public async Task EmptyPrimary_FallsBackToOneOcr()
    {
        var (service, primary, oneOcr) = CreateService(fallbackEnabled: true);

        var response = await service.RecognizeAsync(Request());

        Assert.Equal(1, primary.Calls);
        Assert.Equal(1, oneOcr.Calls);
        Assert.Equal("読めた", response.Text);
        Assert.Equal("oneocr", response.Provider);
        Assert.NotEmpty(response.Blocks);
    }

    [Fact]
    public async Task EmptyPrimary_DoesNotAcceptPunctuationOnlyOneOcrFallback()
    {
        var commaOnly = new OcrProviderResult(
            ",",
            [new OcrTextBlock(",", 1.0, new OcrBoundingBox(42, 44, 9, 12))],
            "oneocr");
        var (service, primary, oneOcr) = CreateService(fallbackEnabled: true, oneOcrResult: commaOnly);

        var response = await service.RecognizeAsync(Request());

        Assert.Equal(1, primary.Calls);
        Assert.Equal(1, oneOcr.Calls);
        Assert.Equal("rapidocr-net", response.Provider);
        Assert.Equal(string.Empty, response.Text);
        Assert.Empty(response.Blocks);
        Assert.DoesNotContain("oneocr", response.Engine, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Primary_DropsPunctuationOnlyBlocksButKeepsInlineChinesePunctuation()
    {
        var primaryResult = new OcrProviderResult(
            "你好，世界（測試）。\n,",
            [
                new OcrTextBlock("你好，世界（測試）。", 0.98, new OcrBoundingBox(5, 5, 180, 30)),
                new OcrTextBlock(",", 1.0, new OcrBoundingBox(42, 44, 9, 12))
            ],
            "rapidocr-net");
        var (service, primary, oneOcr) = CreateService(fallbackEnabled: true, primaryResult: primaryResult);

        var response = await service.RecognizeAsync(new OcrRequest
        {
            ImageBase64 = Encode("image"),
            Provider = "rapidocr-net",
            Language = "zh-TW"
        });

        Assert.Equal(1, primary.Calls);
        Assert.Equal(0, oneOcr.Calls);
        Assert.Equal("你好，世界（測試）。", response.Text);
        var block = Assert.Single(response.Blocks);
        Assert.Equal("你好，世界（測試）。", block.Text);
    }

    [Fact]
    public async Task Realtime_DoesNotFallBack()
    {
        var (service, _, oneOcr) = CreateService(fallbackEnabled: true);

        var response = await service.RecognizeAsync(Request(realtime: true));

        Assert.Equal(0, oneOcr.Calls);
        Assert.Empty(response.Blocks);
    }

    [Fact]
    public async Task Realtime_DefaultProviderStillFallsBack()
    {
        var (service, primary, oneOcr) = CreateService(fallbackEnabled: true);

        var response = await service.RecognizeAsync(new OcrRequest
        {
            ImageBase64 = Encode("image"),
            Language = "ja",
            Realtime = true
        });

        Assert.Equal(1, primary.Calls);
        Assert.Equal(1, oneOcr.Calls);
        Assert.Equal("oneocr", response.Provider);
        Assert.NotEmpty(response.Blocks);
    }

    [Fact]
    public async Task Disabled_DoesNotFallBack()
    {
        var (service, _, oneOcr) = CreateService(fallbackEnabled: false);

        var response = await service.RecognizeAsync(Request());

        Assert.Equal(0, oneOcr.Calls);
        Assert.Empty(response.Blocks);
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
