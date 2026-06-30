using Verbeam.Core.Models;
using Verbeam.Core.Services;

namespace Verbeam.Tests;

public sealed class LanguageRegistryTests
{
    [Theory]
    [InlineData("ja", "ja-JP")]
    [InlineData("JP", "ja-JP")]
    [InlineData("ja-JP", "ja-JP")]
    [InlineData("jpn", "ja-JP")]
    [InlineData("zh-TW", "zh-Hant-TW")]
    [InlineData("zh-Hant", "zh-Hant-TW")]
    [InlineData("zh-hant-tw", "zh-Hant-TW")]
    [InlineData("chi_tra", "zh-Hant-TW")]
    [InlineData("zh", "zh-Hans-CN")]
    [InlineData("zh-CN", "zh-Hans-CN")]
    [InlineData("en", "en-US")]
    [InlineData("eng", "en-US")]
    [InlineData("ko", "ko-KR")]
    [InlineData("korean", "ko-KR")]
    public void Normalize_MapsAliasesToCanonical(string value, string expected)
    {
        Assert.Equal(expected, LanguageRegistry.Normalize(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("auto")]
    [InlineData("AUTO")]
    public void Normalize_BlankOrAutoStaysAuto(string? value)
    {
        Assert.Equal(LanguageRegistry.Auto, LanguageRegistry.Normalize(value));
        Assert.True(LanguageRegistry.IsAuto(value));
    }

    [Fact]
    public void Normalize_UnknownLanguagePassesThrough()
    {
        Assert.Equal("th-TH", LanguageRegistry.Normalize(" th-TH "));
    }

    [Fact]
    public void Normalize_LongerTagsFallBackToKnownPrefix()
    {
        Assert.Equal("ja-JP", LanguageRegistry.Normalize("ja-JP-x-custom"));
    }

    [Theory]
    [InlineData("zh-Hant-TW", LanguageRegistry.Providers.Tesseract, "chi_tra")]
    [InlineData("zh-Hant-TW", LanguageRegistry.Providers.Windows, "zh-Hant")]
    [InlineData("zh-Hant-TW", LanguageRegistry.Providers.EasyOcr, "ch_tra")]
    [InlineData("zh-Hant-TW", LanguageRegistry.Providers.PaddleOcr, "chinese_cht")]
    [InlineData("ja", LanguageRegistry.Providers.Tesseract, "jpn")]
    [InlineData("ja", LanguageRegistry.Providers.PaddleOcr, "japan")]
    [InlineData("en", LanguageRegistry.Providers.Windows, "en-US")]
    public void ProviderCode_MapsCanonicalToEngineCode(string language, string provider, string expected)
    {
        Assert.Equal(expected, LanguageRegistry.ProviderCode(language, provider));
    }

    [Theory]
    [InlineData("zh-Hant-TW", "zh-TW")]
    [InlineData("zh-Hans-CN", "zh-CN")]
    [InlineData("ja-JP", "ja")]
    [InlineData("en-US", "en")]
    [InlineData("ko-KR", "ko")]
    public void ToTranslationCode_MapsToTranslationSideCodes(string canonical, string expected)
    {
        Assert.Equal(expected, LanguageRegistry.ToTranslationCode(canonical));
    }

    [Fact]
    public void ResolveAllowedLanguages_DefaultsToCjkPlusEnglish()
    {
        var resolved = LanguageRegistry.ResolveAllowedLanguages(null);
        Assert.Equal(LanguageRegistry.DefaultAllowedLanguages, resolved);
        Assert.Contains("zh-Hant-TW", resolved);
        Assert.Contains("en-US", resolved);
    }

    [Fact]
    public void ResolveAllowedLanguages_NormalizesAndDeduplicates()
    {
        var resolved = LanguageRegistry.ResolveAllowedLanguages(["ja", "jpn", "zh-tw", "auto", ""]);
        Assert.Equal(["ja-JP", "zh-Hant-TW"], resolved);
    }
}

public sealed class UnicodeScriptDetectorTests
{
    [Fact]
    public void Detect_TraditionalChinese()
    {
        var result = UnicodeScriptDetector.Detect("編譯器會將原始碼轉換成執行檔");
        Assert.Equal("zh-Hant-TW", result.DetectedLanguage);
        Assert.StartsWith("Hant", result.Script);
        Assert.True(result.Confidence > 0.5, $"confidence={result.Confidence}");
    }

    [Fact]
    public void Detect_SimplifiedChinese()
    {
        var result = UnicodeScriptDetector.Detect("编译器会将源代码转换成可执行文件");
        Assert.Equal("zh-Hans-CN", result.DetectedLanguage);
        Assert.StartsWith("Hans", result.Script);
        Assert.True(result.Confidence > 0.5, $"confidence={result.Confidence}");
    }

    [Fact]
    public void Detect_JapaneseWithKanji()
    {
        var result = UnicodeScriptDetector.Detect("コンパイラはソースコードを実行ファイルに変換します");
        Assert.Equal("ja-JP", result.DetectedLanguage);
        Assert.Contains("Kana", result.Script);
        Assert.True(result.Confidence > 0.6, $"confidence={result.Confidence}");
    }

    [Fact]
    public void Detect_Korean()
    {
        var result = UnicodeScriptDetector.Detect("컴파일러는 소스 코드를 실행 파일로 변환합니다");
        Assert.Equal("ko-KR", result.DetectedLanguage);
        Assert.Contains("Hang", result.Script);
    }

    [Fact]
    public void Detect_English()
    {
        var result = UnicodeScriptDetector.Detect("The compiler translates source code into an executable.");
        Assert.Equal("en-US", result.DetectedLanguage);
        Assert.Equal("Latn", result.Script);
        Assert.True(result.Confidence > 0.6, $"confidence={result.Confidence}");
    }

    [Fact]
    public void Detect_MixedChineseWithEnglishTerms_StaysChinese()
    {
        // The motivating example: auxiliary Latin terms must not flip the language.
        var result = UnicodeScriptDetector.Detect("編譯 (Compile)\n組譯 (Assemble)\n預先處理 (Preprocess)");
        Assert.Equal("zh-Hant-TW", result.DetectedLanguage);
        Assert.Contains("Latn", result.Script);
        var english = result.Candidates.FirstOrDefault(item => item.Language == "en-US");
        if (english is not null)
        {
            Assert.True(english.Score < result.Confidence, "English must rank below Chinese");
        }
    }

    [Fact]
    public void Detect_LatinDominantWithStrayCjk_IsEnglish()
    {
        var result = UnicodeScriptDetector.Detect(
            "This is a long English sentence about compilers and assemblers that mentions 編譯 once.");
        Assert.Equal("en-US", result.DetectedLanguage);
    }

    [Fact]
    public void Detect_HanOnly_KeepsJapaneseAsCandidate()
    {
        var result = UnicodeScriptDetector.Detect("編集設定");
        Assert.Contains(result.Candidates, item => item.Language == "ja-JP");
    }

    [Fact]
    public void Detect_MojibakeLowersConfidence()
    {
        var clean = UnicodeScriptDetector.Detect("編譯器會將原始碼轉換");
        var noisy = UnicodeScriptDetector.Detect("編譯器��會將�原始碼轉換��");
        Assert.True(noisy.MojibakeRatio > 0);
        Assert.True(noisy.Confidence < clean.Confidence);
    }

    [Fact]
    public void Detect_EmptyOrSymbolsOnly_ReturnsEmpty()
    {
        Assert.Equal(ScriptDetectionResult.Empty, UnicodeScriptDetector.Detect(""));
        Assert.Equal(string.Empty, UnicodeScriptDetector.Detect("123 -- !!").DetectedLanguage);
    }

    [Fact]
    public void Aggregate_WeightsLargeBlocksOverStrayOnes()
    {
        var chinese = UnicodeScriptDetector.Detect("編譯器會將原始碼轉換成執行檔,連結器再連結函式庫");
        var english = UnicodeScriptDetector.Detect("OK");
        var aggregated = UnicodeScriptDetector.Aggregate(
        [
            (chinese, chinese.EffectiveCharCount * 1.0),
            (english, english.EffectiveCharCount * 1.0)
        ]);
        Assert.Equal("zh-Hant-TW", aggregated.DetectedLanguage);
        Assert.True(aggregated.Candidates.Count >= 2);
    }

    [Fact]
    public void Aggregate_EmptyInput_ReturnsEmpty()
    {
        Assert.Equal(ScriptDetectionResult.Empty, UnicodeScriptDetector.Aggregate([]));
    }
}

public sealed class OcrLanguageDetectionPersistenceTests : IDisposable
{
    private readonly string _tempDirectory =
        Path.Combine(Path.GetTempPath(), "verbeam-lang-detect-tests-" + Guid.NewGuid());

    [Fact]
    public async Task OcrCachedResult_RoundTripsDetection()
    {
        Directory.CreateDirectory(_tempDirectory);
        var store = new Verbeam.Core.Storage.SqliteOcrMemoryStore(Path.Combine(_tempDirectory, "ocr.sqlite"));
        await store.InitializeAsync();

        var detection = new OcrLanguageDetection(
            "auto",
            "zh-Hant-TW",
            "zh-Hant-TW",
            0.87,
            [new OcrLanguageCandidate("zh-Hant-TW", 0.87), new OcrLanguageCandidate("ja-JP", 0.22)]);
        var entry = new OcrCachedResult(
            "key-detect-1",
            "image-hash",
            "image/png",
            "windows",
            "windows:media-ocr",
            "v1",
            "zh-Hant-TW",
            true,
            "correction-hash",
            "編譯 (Compile)",
            "編譯 (Compile)",
            [new OcrTextBlock("編譯 (Compile)", 0.95, null) { DetectedLanguage = "zh-Hant-TW", Script = "Hant+Latn" }],
            [],
            new OcrDocumentResult(),
            12,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            0)
        {
            Detection = detection
        };

        await store.SetCachedResultAsync(entry);
        var loaded = await store.GetCachedResultAsync("key-detect-1");

        Assert.NotNull(loaded);
        Assert.Equal("auto", loaded.Detection.RequestedLanguage);
        Assert.Equal("zh-Hant-TW", loaded.Detection.DetectedLanguage);
        Assert.Equal(0.87, loaded.Detection.LanguageConfidence);
        Assert.Equal(2, loaded.Detection.Candidates.Count);
        Assert.Equal("zh-Hant-TW", loaded.Blocks[0].DetectedLanguage);
        Assert.Equal("Hant+Latn", loaded.Blocks[0].Script);
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

public sealed class OcrLanguageScorerTests
{
    [Fact]
    public void Score_PrefersRunWhoseDetectionMatchesCandidate()
    {
        var text = "編譯器會將原始碼轉換成執行檔";
        var blocks = new[] { new OcrTextBlock(text, 0.9, null) };
        var detection = UnicodeScriptDetector.Detect(text);

        var matching = OcrLanguageScorer.Score("zh-Hant-TW", blocks, detection);
        var mismatched = OcrLanguageScorer.Score("ko-KR", blocks, detection);

        Assert.True(matching > mismatched, $"matching={matching} mismatched={mismatched}");
    }

    [Fact]
    public void Score_PenalizesMojibake()
    {
        var cleanText = "編譯器會將原始碼轉換成執行檔案內容";
        var noisyText = "編譯��器會�將原始�碼轉換��";
        var clean = OcrLanguageScorer.Score(
            "zh-Hant-TW",
            [new OcrTextBlock(cleanText, 0.9, null)],
            UnicodeScriptDetector.Detect(cleanText));
        var noisy = OcrLanguageScorer.Score(
            "zh-Hant-TW",
            [new OcrTextBlock(noisyText, 0.9, null)],
            UnicodeScriptDetector.Detect(noisyText));

        Assert.True(clean > noisy, $"clean={clean} noisy={noisy}");
    }

    [Fact]
    public void Score_EmptyRunScoresZero()
    {
        Assert.Equal(0, OcrLanguageScorer.Score("ja-JP", [], ScriptDetectionResult.Empty));
    }
}
