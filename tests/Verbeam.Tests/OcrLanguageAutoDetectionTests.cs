using System.Text;
using Verbeam.Core.Models;
using Verbeam.Core.Options;
using Verbeam.Core.Providers;
using Verbeam.Core.Services;
using Verbeam.Core.Storage;

namespace Verbeam.Tests;

public sealed class OcrLanguageAutoDetectionTests : IDisposable
{
    private readonly string _tempDirectory =
        Path.Combine(Path.GetTempPath(), "verbeam-ocr-auto-tests-" + Guid.NewGuid());

    /// <summary>
    /// Returns a per-language canned result so tests can simulate an engine that
    /// reads the image well in one language and produces garbage in another.
    /// </summary>
    private sealed class LanguageDependentOcrProvider : IOcrProvider
    {
        private readonly Dictionary<string, (string Text, double Confidence)> _results;

        public LanguageDependentOcrProvider(
            Dictionary<string, (string Text, double Confidence)> results,
            bool isLanguageAgnostic = false)
        {
            _results = results;
            Descriptor = new OcrProviderDescriptor(
                "mock",
                "Language Dependent Mock",
                "test",
                "ja-JP",
                RequiresExternalProcess: false,
                IsLocal: true)
            {
                IsLanguageAgnostic = isLanguageAgnostic
            };
        }

        public OcrProviderDescriptor Descriptor { get; }

        public List<string> AttemptedLanguages { get; } = [];

        public Task<OcrProviderResult> RecognizeAsync(
            OcrProviderRequest request,
            CancellationToken cancellationToken)
        {
            AttemptedLanguages.Add(request.Language);
            var (text, confidence) = _results.TryGetValue(request.Language, out var match)
                ? match
                : ("���", 0.1);
            return Task.FromResult(new OcrProviderResult(
                text,
                [new OcrTextBlock(text, confidence, null)],
                "mock"));
        }
    }

    private (OcrService Service, VerbeamOptions Options) CreateService(IOcrProvider provider)
    {
        Directory.CreateDirectory(_tempDirectory);
        var options = new VerbeamOptions
        {
            Ocr =
            {
                DefaultProvider = provider.Descriptor.Name,
                DefaultLanguage = "auto"
            }
        };
        var registry = new OcrProviderRegistry([provider]);
        var store = new SqliteOcrMemoryStore(Path.Combine(_tempDirectory, Guid.NewGuid().ToString("N") + ".sqlite"));
        var routing = new OcrRoutingService(options, registry);
        var limiter = new OcrConcurrencyLimiter(options);
        return (new OcrService(options, registry, store, routing, limiter), options);
    }

    private static string EncodeText(string text)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task Auto_DetectsLanguageAndAnnotatesBlocks()
    {
        var chinese = "編譯器會將原始碼轉換成執行檔";
        var provider = new LanguageDependentOcrProvider(new()
        {
            ["zh-Hant-TW"] = (chinese, 0.95)
        });
        var (service, _) = CreateService(provider);

        var response = await service.RecognizeAsync(new OcrRequest
        {
            ImageBase64 = EncodeText(chinese),
            Language = "auto"
        });

        Assert.Equal("auto", response.RequestedLanguage);
        Assert.Equal("zh-Hant-TW", response.DetectedLanguage);
        Assert.Equal("zh-Hant-TW", response.ResolvedOcrLanguage);
        Assert.True(response.LanguageConfidence > 0.5);
        Assert.NotEmpty(response.LanguageCandidates);
        Assert.All(response.Blocks, block => Assert.Equal("zh-Hant-TW", block.DetectedLanguage));
    }

    [Fact]
    public async Task Auto_LowConfidenceFirstPass_RerunsAndPicksBestLanguage()
    {
        var japanese = "コンパイラはソースコードを実行ファイルに変換します";
        var provider = new LanguageDependentOcrProvider(new()
        {
            // Seed language (first allowed) yields garbage; Japanese yields clean text.
            ["zh-Hant-TW"] = ("��!!��", 0.2),
            ["ja-JP"] = (japanese, 0.95)
        });
        var (service, _) = CreateService(provider);

        var response = await service.RecognizeAsync(new OcrRequest
        {
            ImageBase64 = EncodeText(japanese),
            Language = "auto",
            AllowedLanguages = ["zh-Hant-TW", "ja-JP"]
        });

        Assert.True(provider.AttemptedLanguages.Count > 1, "expected at least one re-run");
        Assert.Equal("ja-JP", response.ResolvedOcrLanguage);
        Assert.Equal("ja-JP", response.DetectedLanguage);
        Assert.Equal(japanese, response.Text);
    }

    [Fact]
    public async Task Auto_RealtimeSkipsRerun()
    {
        var provider = new LanguageDependentOcrProvider(new()
        {
            ["zh-Hant-TW"] = ("��!!��", 0.2),
            ["ja-JP"] = ("コンパイラ", 0.95)
        });
        var (service, _) = CreateService(provider);

        var response = await service.RecognizeAsync(new OcrRequest
        {
            ImageBase64 = EncodeText("text"),
            Language = "auto",
            AllowedLanguages = ["zh-Hant-TW", "ja-JP"],
            Realtime = true
        });

        Assert.Single(provider.AttemptedLanguages);
        Assert.Equal("auto", response.RequestedLanguage);
    }

    [Fact]
    public async Task Auto_LanguageAgnosticProviderNeverReruns()
    {
        var provider = new LanguageDependentOcrProvider(
            new()
            {
                ["zh-Hant-TW"] = ("��", 0.1)
            },
            isLanguageAgnostic: true);
        var (service, _) = CreateService(provider);

        await service.RecognizeAsync(new OcrRequest
        {
            ImageBase64 = EncodeText("text"),
            Language = "auto"
        });

        Assert.Single(provider.AttemptedLanguages);
    }

    [Fact]
    public async Task ExplicitLanguage_IsNormalizedToCanonical()
    {
        var japanese = "コンパイラはソースコードを変換します";
        var provider = new LanguageDependentOcrProvider(new()
        {
            ["ja-JP"] = (japanese, 0.95)
        });
        var (service, _) = CreateService(provider);

        var response = await service.RecognizeAsync(new OcrRequest
        {
            ImageBase64 = EncodeText(japanese),
            Language = "ja"
        });

        Assert.Equal("ja-JP", response.RequestedLanguage);
        Assert.Equal("ja-JP", response.ResolvedOcrLanguage);
        Assert.Equal(["ja-JP"], provider.AttemptedLanguages);
        Assert.Equal("ja-JP", response.DetectedLanguage);
    }

    [Fact]
    public async Task Auto_CacheHitPreservesDetection()
    {
        var chinese = "編譯器會將原始碼轉換成執行檔";
        var provider = new LanguageDependentOcrProvider(new()
        {
            ["zh-Hant-TW"] = (chinese, 0.95)
        });
        var (service, _) = CreateService(provider);
        var request = new OcrRequest
        {
            ImageBase64 = EncodeText(chinese),
            Language = "auto"
        };

        var first = await service.RecognizeAsync(request);
        var second = await service.RecognizeAsync(request);

        Assert.False(first.CacheHit);
        Assert.True(second.CacheHit);
        Assert.Equal(first.DetectedLanguage, second.DetectedLanguage);
        Assert.Equal(first.LanguageConfidence, second.LanguageConfidence);
        Assert.Equal("auto", second.RequestedLanguage);
        Assert.Equal(first.ResolvedOcrLanguage, second.ResolvedOcrLanguage);
    }

    [Fact]
    public async Task ExplicitAliasAndCanonical_ShareTheSameCacheEntry()
    {
        var japanese = "コンパイラはソースコードを変換します";
        var provider = new LanguageDependentOcrProvider(new()
        {
            ["ja-JP"] = (japanese, 0.95)
        });
        var (service, _) = CreateService(provider);

        var first = await service.RecognizeAsync(new OcrRequest
        {
            ImageBase64 = EncodeText(japanese),
            Language = "ja"
        });
        var second = await service.RecognizeAsync(new OcrRequest
        {
            ImageBase64 = EncodeText(japanese),
            Language = "ja-JP"
        });

        Assert.False(first.CacheHit);
        Assert.True(second.CacheHit);
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
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
