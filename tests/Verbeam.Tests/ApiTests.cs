using System.Net.WebSockets;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Verbeam.Core.Models;
using Verbeam.Core.Storage;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;

namespace Verbeam.Tests;

public sealed class ApiTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), "verbeam-tests-" + Guid.NewGuid());
    private readonly string? _previousCachePath;
    private readonly string? _previousDefaultProvider;
    private readonly string? _previousDefaultSpeechProvider;
    private readonly string? _previousUrls;
    private readonly WebApplicationFactory<Program> _factory;

    public ApiTests()
    {
        Directory.CreateDirectory(_tempDirectory);
        _previousCachePath = Environment.GetEnvironmentVariable("VB_Verbeam__CachePath");
        _previousDefaultProvider = Environment.GetEnvironmentVariable("VB_Verbeam__DefaultProvider");
        _previousDefaultSpeechProvider = Environment.GetEnvironmentVariable("VB_Verbeam__Speech__DefaultProvider");
        _previousUrls = Environment.GetEnvironmentVariable("VB_Urls");

        Environment.SetEnvironmentVariable(
            "VB_Verbeam__CachePath",
            Path.Combine(_tempDirectory, "translations.sqlite"));
        Environment.SetEnvironmentVariable("VB_Verbeam__DefaultProvider", "mock");
        Environment.SetEnvironmentVariable("VB_Verbeam__Speech__DefaultProvider", "mock");
        Environment.SetEnvironmentVariable("VB_Urls", "http://localhost:0");

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Verbeam:DefaultProvider"] = "mock",
                        ["Verbeam:Speech:DefaultProvider"] = "mock",
                        ["Verbeam:CachePath"] = Path.Combine(_tempDirectory, "translations.sqlite")
                    });
                });
            });
    }

    [Fact]
    public async Task Translate_ReturnsMortCompatibleShape()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/translate", new
        {
            text = "hello",
            source = "en",
            target = "zh-TW",
            mode = "game_dialogue",
            provider = "mock"
        });

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<MortTranslateResponse>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(result);
        Assert.Equal("0", result.ErrorCode);
        Assert.Equal(string.Empty, result.ErrorMessage);
        Assert.Contains("[mock en->zh-TW game_dialogue] hello", result.Result);
    }

    [Fact]
    public async Task Translate_RecordsSuccessfulTranslationEvents()
    {
        var client = _factory.CreateClient();

        for (var index = 0; index < 2; index++)
        {
            var response = await client.PostAsJsonAsync("/translate", new
            {
                name = "line-42",
                text = "hello tracked",
                source = "en",
                target = "zh-TW",
                mode = "game_dialogue",
                provider = "mock",
                profile = "test-game",
                sessionId = "session-a"
            });

            response.EnsureSuccessStatusCode();
        }

        var events = await client.GetFromJsonAsync<TranslationEvent[]>("/translation/events?profile=test-game&limit=5");

        Assert.NotNull(events);
        Assert.Equal(2, events.Length);
        Assert.All(events, recorded =>
        {
            Assert.Equal("test-game", recorded.ProfileId);
            Assert.Equal("session-a", recorded.SessionId);
            Assert.Equal("line-42", recorded.RequestName);
            Assert.Equal("hello tracked", recorded.SourceText);
            Assert.Contains("[mock en->zh-TW game_dialogue] hello tracked", recorded.TranslatedText);
            Assert.Equal("en", recorded.SourceLanguage);
            Assert.Equal("zh-TW", recorded.TargetLanguage);
            Assert.Equal("game_dialogue", recorded.Mode);
            Assert.Equal("mock", recorded.Provider);
            Assert.Equal("mock", recorded.Engine);
            Assert.Equal("mock", recorded.Model);
            Assert.Equal("0", recorded.ErrorCode);
            Assert.Equal(string.Empty, recorded.ErrorMessage);
            Assert.False(string.IsNullOrWhiteSpace(recorded.TranslationKey));
        });
        Assert.Contains(events, recorded => !recorded.CacheHit);
        Assert.Contains(events, recorded => recorded.CacheHit);
    }

    [Fact]
    public async Task Translate_UnknownProvider_ReturnsMortErrorWithoutBlankResult()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/translate", new
        {
            text = "keep me visible",
            source = "en",
            target = "zh-TW",
            provider = "missing"
        });

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<MortTranslateResponse>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(result);
        Assert.NotEqual("0", result.ErrorCode);
        Assert.Equal("keep me visible", result.Result);
        Assert.Contains("Unknown provider", result.ErrorMessage);

        var events = await client.GetFromJsonAsync<TranslationEvent[]>("/translation/events?limit=5");
        Assert.NotNull(events);
        var recorded = Assert.Single(events);
        Assert.Equal("keep me visible", recorded.SourceText);
        Assert.Equal(string.Empty, recorded.TranslatedText);
        Assert.Equal("missing", recorded.Provider);
        Assert.Equal("1", recorded.ErrorCode);
        Assert.Contains("Unknown provider", recorded.ErrorMessage);
        Assert.Null(recorded.TranslationKey);
    }

    [Fact]
    public async Task TranslationCorrection_CreatesMemoryAndExactMemoryOverridesProvider()
    {
        var client = _factory.CreateClient();

        var initialResponse = await client.PostAsJsonAsync("/translate", new
        {
            text = "remember this line",
            source = "en",
            target = "zh-TW",
            mode = "game_dialogue",
            provider = "mock",
            profile = "memory-game",
            sessionId = "session-a"
        });
        initialResponse.EnsureSuccessStatusCode();

        var events = await client.GetFromJsonAsync<TranslationEvent[]>("/translation/events?profile=memory-game&limit=5");
        Assert.NotNull(events);
        var sourceEvent = Assert.Single(events);

        var correctionResponse = await client.PostAsJsonAsync("/translation/corrections", new
        {
            profile = "memory-game",
            sessionId = "session-a",
            eventId = sourceEvent.Id,
            correctedText = "記住這一句",
            note = "user approved"
        });
        correctionResponse.EnsureSuccessStatusCode();
        var memory = await correctionResponse.Content.ReadFromJsonAsync<MemoryItem>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(memory);
        Assert.Equal("memory-game", memory.ProfileId);
        Assert.Equal("translation", memory.MemoryKind);
        Assert.Equal("remember this line", memory.SourceText);
        Assert.Equal("記住這一句", memory.TargetText);
        Assert.Contains(sourceEvent.Id, memory.MetadataJson);

        var memories = await client.GetFromJsonAsync<MemoryItem[]>("/memories?profile=memory-game&type=translation");
        Assert.NotNull(memories);
        var listed = Assert.Single(memories);
        Assert.Equal(memory.Id, listed.Id);

        var memoryHitResponse = await client.PostAsJsonAsync("/translate", new
        {
            text = "remember this line",
            source = "en",
            target = "zh-TW",
            mode = "game_dialogue",
            provider = "missing",
            profile = "memory-game",
            sessionId = "session-a"
        });
        memoryHitResponse.EnsureSuccessStatusCode();
        var memoryHit = await memoryHitResponse.Content.ReadFromJsonAsync<MortTranslateResponse>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(memoryHit);
        Assert.Equal("0", memoryHit.ErrorCode);
        Assert.Equal("記住這一句", memoryHit.Result);

        var otherProfileResponse = await client.PostAsJsonAsync("/translate", new
        {
            text = "remember this line",
            source = "en",
            target = "zh-TW",
            mode = "game_dialogue",
            provider = "mock",
            profile = "other-game"
        });
        otherProfileResponse.EnsureSuccessStatusCode();
        var otherProfile = await otherProfileResponse.Content.ReadFromJsonAsync<MortTranslateResponse>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(otherProfile);
        Assert.Contains("[mock en->zh-TW game_dialogue] remember this line", otherProfile.Result);

        var updatedMemories = await client.GetFromJsonAsync<MemoryItem[]>("/memories?profile=memory-game&type=translation");
        Assert.NotNull(updatedMemories);
        Assert.Equal(1, Assert.Single(updatedMemories).UseCount);
    }

    [Fact]
    public async Task Memories_CanBeCreatedAndListedByProfile()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/memories", new
        {
            profile = "manual-profile",
            memoryKind = "term",
            source = "ja",
            target = "zh-TW",
            sourceText = "勇者",
            targetText = "勇者大人",
            note = "title-like honorific",
            priority = 10,
            confidence = 0.95
        });
        response.EnsureSuccessStatusCode();

        var created = await response.Content.ReadFromJsonAsync<MemoryItem>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(created);
        Assert.Equal("manual-profile", created.ProfileId);
        Assert.Equal("term", created.MemoryKind);
        Assert.Equal("勇者", created.SourceText);
        Assert.Equal("勇者大人", created.TargetText);
        Assert.Equal(10, created.Priority);

        var listed = await client.GetFromJsonAsync<MemoryItem[]>("/memories?profile=manual-profile&type=term");
        Assert.NotNull(listed);
        Assert.Equal(created.Id, Assert.Single(listed).Id);

        var otherProfile = await client.GetFromJsonAsync<MemoryItem[]>("/memories?profile=default&type=term");
        Assert.NotNull(otherProfile);
        Assert.Empty(otherProfile);
    }

    [Fact]
    public async Task Translate_DoesNotUseUntrustedExactMemory()
    {
        var client = _factory.CreateClient();

        var memoryResponse = await client.PostAsJsonAsync("/memories", new
        {
            profile = "untrusted-profile",
            memoryKind = "translation",
            source = "en",
            target = "zh-TW",
            sourceText = "shared poison",
            targetText = "MALICIOUS OVERRIDE",
            trustLevel = "untrusted_import",
            sourceUri = "import://external"
        });
        memoryResponse.EnsureSuccessStatusCode();

        var memory = await memoryResponse.Content.ReadFromJsonAsync<MemoryItem>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(memory);
        Assert.Equal("untrusted_import", memory.TrustLevel);
        Assert.Equal("import://external", memory.SourceUri);

        var translateResponse = await client.PostAsJsonAsync("/translate", new
        {
            text = "shared poison",
            source = "en",
            target = "zh-TW",
            mode = "game_dialogue",
            provider = "mock",
            profile = "untrusted-profile"
        });
        translateResponse.EnsureSuccessStatusCode();

        var translated = await translateResponse.Content.ReadFromJsonAsync<MortTranslateResponse>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(translated);
        Assert.Equal("0", translated.ErrorCode);
        Assert.Contains("[mock en->zh-TW game_dialogue] shared poison", translated.Result);
        Assert.DoesNotContain("MALICIOUS OVERRIDE", translated.Result);
    }

    [Fact]
    public async Task Translate_UsesMemoryContextHashForGeneratedCache()
    {
        var client = _factory.CreateClient();
        const string profile = "rag-cache-game";
        const string sourceText = "Use Star Key now";

        var memoryResponse = await client.PostAsJsonAsync("/memories", new
        {
            profile,
            memoryKind = "term",
            source = "en",
            target = "zh-TW",
            sourceText = "Star Key",
            targetText = "Star Key TW v1",
            priority = 100,
            confidence = 1.0
        });
        memoryResponse.EnsureSuccessStatusCode();

        await TranslateAsync("rag-1");
        await TranslateAsync("rag-2");

        var firstEvents = await client.GetFromJsonAsync<TranslationEvent[]>($"/translation/events?profile={profile}&limit=5");
        Assert.NotNull(firstEvents);
        var first = Assert.Single(firstEvents, item => item.RequestName == "rag-1");
        var second = Assert.Single(firstEvents, item => item.RequestName == "rag-2");
        Assert.False(first.CacheHit);
        Assert.True(second.CacheHit);
        Assert.False(string.IsNullOrWhiteSpace(first.TranslationKey));
        Assert.Equal(first.TranslationKey, second.TranslationKey);

        var updateResponse = await client.PostAsJsonAsync("/memories", new
        {
            profile,
            memoryKind = "term",
            source = "en",
            target = "zh-TW",
            sourceText = "Star Key",
            targetText = "Star Key TW v2",
            priority = 100,
            confidence = 1.0
        });
        updateResponse.EnsureSuccessStatusCode();

        await TranslateAsync("rag-3");

        var updatedEvents = await client.GetFromJsonAsync<TranslationEvent[]>($"/translation/events?profile={profile}&limit=5");
        Assert.NotNull(updatedEvents);
        var third = Assert.Single(updatedEvents, item => item.RequestName == "rag-3");
        Assert.False(third.CacheHit);
        Assert.False(string.IsNullOrWhiteSpace(third.TranslationKey));
        Assert.NotEqual(first.TranslationKey, third.TranslationKey);

        var auditStore = new SqliteMemoryContextAuditStore(Path.Combine(_tempDirectory, "translations.sqlite"));
        var audit = await auditStore.ListAsync(profile, limit: 10);
        Assert.Equal(3, audit.Count);
        Assert.All(audit, entry =>
        {
            Assert.Equal(profile, entry.ProfileId);
            Assert.Equal("term", entry.MemoryKind);
            Assert.Equal("user_verified", entry.TrustLevel);
            Assert.Equal("memory-context-v1", entry.PolicyVersion);
            Assert.False(string.IsNullOrWhiteSpace(entry.SnippetHash));
            Assert.False(string.IsNullOrWhiteSpace(entry.ContextHash));
            Assert.False(string.IsNullOrWhiteSpace(entry.RequestId));
            Assert.False(string.IsNullOrWhiteSpace(entry.TranslationKey));
        });
        Assert.Contains(audit, entry => entry.RequestId == first.Id && entry.TranslationKey == first.TranslationKey);
        Assert.Contains(audit, entry => entry.RequestId == second.Id && entry.TranslationKey == second.TranslationKey);
        Assert.Contains(audit, entry => entry.RequestId == third.Id && entry.TranslationKey == third.TranslationKey);

        async Task TranslateAsync(string name)
        {
            var response = await client.PostAsJsonAsync("/translate", new
            {
                name,
                text = sourceText,
                source = "en",
                target = "zh-TW",
                mode = "game_dialogue",
                provider = "mock",
                profile
            });

            response.EnsureSuccessStatusCode();
        }
    }

    [Fact]
    public async Task DiscoveryEndpoints_ReturnConfiguredData()
    {
        var client = _factory.CreateClient();

        var providers = await client.GetFromJsonAsync<JsonElement>("/providers");
        var ocrProviders = await client.GetFromJsonAsync<JsonElement>("/ocr/providers");
        var ocrEngines = await client.GetFromJsonAsync<OcrEngineDescriptor[]>("/ocr/engines");
        var asrProviders = await client.GetFromJsonAsync<JsonElement>("/asr/providers");
        var asrEngines = await client.GetFromJsonAsync<SpeechEngineDescriptor[]>("/asr/engines");
        var models = await client.GetFromJsonAsync<TranslationModelDescriptor[]>("/translation/models?provider=mock");
        var languages = await client.GetFromJsonAsync<TranslationLanguageDescriptor[]>("/translation/languages");
        var presets = await client.GetFromJsonAsync<JsonElement>("/presets");
        var glossaries = await client.GetFromJsonAsync<JsonElement>("/glossaries");

        Assert.True(providers.GetArrayLength() >= 2);
        Assert.True(ocrProviders.GetArrayLength() >= 2);
        Assert.True(asrProviders.GetArrayLength() >= 3);
        Assert.NotNull(ocrEngines);
        Assert.Contains(ocrEngines, item => item.Name == "mock" && item.IsAvailable);
        Assert.Contains(ocrEngines, item => item.Name == "external");
        Assert.Contains(ocrEngines, item => item.Name == "tesseract" && !item.RequiresApiConfiguration);
        Assert.Contains(ocrEngines, item => item.Name == "easyocr" && !item.RequiresApiConfiguration);
        Assert.Contains(ocrEngines, item => item.Name == "paddleocr" && !item.RequiresApiConfiguration);
        Assert.Contains(ocrEngines, item => item.Name == "pix2text" && !item.RequiresApiConfiguration);
        Assert.Contains(ocrEngines, item => item.Name == "pp-structure-v3" && !item.RequiresApiConfiguration);
        Assert.Contains(ocrEngines, item => item.Name == "paddleocr-vl" && !item.RequiresApiConfiguration);
        Assert.Contains(ocrEngines, item => item.Name == "dots-ocr" && !item.RequiresApiConfiguration);
        Assert.Contains(ocrEngines, item => item.Name == "google-cloud-vision" && item.RequiresApiConfiguration && !item.IsAvailable);
        Assert.Contains(ocrEngines, item => item.Name == "deepseek-ocr-vlm" && item.RequiresApiConfiguration && !item.IsAvailable);
        Assert.Contains(ocrEngines, item => item.Name == "mathpix" && item.RequiresApiConfiguration && !item.IsAvailable);
        Assert.NotNull(asrEngines);
        Assert.Contains(asrEngines, item => item.Name == "mock" && item.IsAvailable && item.IsDefault);
        Assert.Contains(asrEngines, item => item.Name == "funasr-http");
        Assert.NotNull(models);
        Assert.Equal("mock", Assert.Single(models).Name);
        Assert.NotNull(languages);
        Assert.Contains(languages, item => item.Code == "ja" && item.IsDefaultSource);
        Assert.Contains(languages, item => item.Code == "zh-TW" && item.IsDefaultTarget);
        Assert.True(presets.GetArrayLength() >= 6);
        Assert.True(glossaries.GetArrayLength() >= 1);
    }

    [Fact]
    public async Task Ocr_ReturnsRecognizedText()
    {
        var client = _factory.CreateClient();
        var imageBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("こんにちは OCR"));

        var response = await client.PostAsJsonAsync("/ocr", new
        {
            imageBase64,
            provider = "mock",
            language = "ja"
        });

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<OcrResponse>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(result);
        Assert.False(string.IsNullOrWhiteSpace(result.EventId));
        Assert.Equal("こんにちは OCR", result.Text);
        Assert.Equal("こんにちは OCR", result.RawText);
        Assert.Equal("mock", result.Provider);
        Assert.Equal("mock", result.Engine);
        Assert.Equal("ja", result.Language);
        Assert.Single(result.Blocks);
    }

    [Fact]
    public async Task Ocr_AppliesCorrectionsAndRecordsEvent()
    {
        var client = _factory.CreateClient();
        var correctionResponse = await client.PostAsJsonAsync("/ocr/corrections", new
        {
            language = "ja",
            wrongText = "グランぺル",
            correctedText = "グランベル",
            note = "OCR confusion"
        });
        correctionResponse.EnsureSuccessStatusCode();

        var imageBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("王都グランぺル"));
        var ocrResponse = await client.PostAsJsonAsync("/ocr", new
        {
            imageBase64,
            provider = "mock",
            language = "ja"
        });
        ocrResponse.EnsureSuccessStatusCode();
        var ocr = await ocrResponse.Content.ReadFromJsonAsync<OcrResponse>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(ocr);
        Assert.Equal("王都グランぺル", ocr.RawText);
        Assert.Equal("王都グランベル", ocr.Text);
        Assert.Single(ocr.AppliedCorrections);

        var events = await client.GetFromJsonAsync<OcrEvent[]>("/ocr/events?limit=5");
        Assert.NotNull(events);
        var recorded = Assert.Single(events, item => item.Id == ocr.EventId);
        Assert.Equal("王都グランぺル", recorded.RawText);
        Assert.Equal("王都グランベル", recorded.CorrectedText);
        Assert.Single(recorded.AppliedCorrections);

        var corrections = await client.GetFromJsonAsync<OcrCorrection[]>("/ocr/corrections?language=ja");
        Assert.NotNull(corrections);
        var stored = Assert.Single(corrections, item => item.WrongText == "グランぺル");
        Assert.Equal(1, stored.UseCount);
    }

    [Fact]
    public async Task Ocr_InvalidBase64_ReturnsBadRequest()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/ocr", new
        {
            imageBase64 = "not-base64",
            provider = "mock"
        });

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("invalid_ocr_request", body.GetProperty("errorCode").GetString());
        Assert.Contains("valid base64", body.GetProperty("errorMessage").GetString());
    }

    [Fact]
    public async Task OcrTranslate_RunsOcrThenTranslation()
    {
        var client = _factory.CreateClient();
        var imageBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("hello from image"));

        var response = await client.PostAsJsonAsync("/ocr/translate", new
        {
            imageBase64,
            ocrProvider = "mock",
            translationProvider = "mock",
            source = "en",
            target = "zh-TW",
            mode = "game_dialogue"
        });

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<OcrTranslateResponse>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(result);
        Assert.Equal("hello from image", result.Ocr.Text);
        Assert.Equal("0", result.Translation.ErrorCode);
        Assert.Contains("[mock en->zh-TW game_dialogue] hello from image", result.Translation.Result);
    }

    [Fact]
    public async Task Asr_ReturnsSegmentsAndRecordsEvent()
    {
        var client = _factory.CreateClient();
        var audioBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("first line\nsecond line"));

        var response = await client.PostAsJsonAsync("/asr", new
        {
            audioBase64,
            provider = "mock",
            language = "en"
        });

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<SpeechResponse>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(result);
        Assert.False(string.IsNullOrWhiteSpace(result.EventId));
        Assert.Equal("mock", result.Provider);
        Assert.Equal("mock", result.Engine);
        Assert.Equal("en", result.Language);
        Assert.Equal("upload", result.SourceKind);
        Assert.Equal(2, result.Segments.Count);
        Assert.Equal("first line", result.Segments[0].Text);
        Assert.Equal("second line", result.Segments[1].Text);

        var events = await client.GetFromJsonAsync<SpeechEvent[]>("/asr/events?limit=5");
        Assert.NotNull(events);
        var recorded = Assert.Single(events, item => item.Id == result.EventId);
        Assert.Equal(2, recorded.Segments.Count);
        Assert.Equal(result.Text, recorded.Text);
    }

    [Fact]
    public async Task AsrTranslate_TranslatesEachSegment()
    {
        var client = _factory.CreateClient();
        var audioBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("hello\nworld"));

        var response = await client.PostAsJsonAsync("/asr/translate", new
        {
            audioBase64,
            speechProvider = "mock",
            language = "en",
            translationProvider = "mock",
            source = "en",
            target = "zh-TW",
            mode = "subtitle"
        });

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<SpeechTranslateResponse>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(result);
        Assert.Equal(2, result.Speech.Segments.Count);
        Assert.Equal(2, result.Translations.Count);
        Assert.All(result.Translations, item => Assert.Equal("0", item.ErrorCode));
        Assert.Contains("[mock en->zh-TW subtitle] hello", result.Translations[0].TranslatedText);
        Assert.Contains("[mock en->zh-TW subtitle] world", result.Translations[1].TranslatedText);
    }

    [Fact]
    public async Task AsrJob_RecordsStatusAndStreamsEvents()
    {
        var client = _factory.CreateClient();
        var audioBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("job first\njob second"));

        var response = await client.PostAsJsonAsync("/asr/jobs", new
        {
            audioBase64,
            provider = "mock",
            language = "en"
        });

        Assert.Equal(System.Net.HttpStatusCode.Accepted, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<SpeechJobStatus>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(created);
        Assert.Equal("queued", created.Status);

        SpeechJobStatus? status = null;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            status = await client.GetFromJsonAsync<SpeechJobStatus>($"/asr/jobs/{created.Id}");
            if (status?.Status == "succeeded")
            {
                break;
            }

            await Task.Delay(100);
        }

        Assert.NotNull(status);
        Assert.Equal("succeeded", status.Status);
        Assert.Equal(2, status.SegmentCount);
        Assert.False(string.IsNullOrWhiteSpace(status.ResultEventId));

        var jobs = await client.GetFromJsonAsync<SpeechJobStatus[]>("/asr/jobs?limit=5");
        Assert.NotNull(jobs);
        Assert.Contains(jobs, item => item.Id == created.Id);

        var events = await client.GetStringAsync($"/asr/jobs/{created.Id}/events");
        Assert.Contains("event: job_started", events);
        Assert.Contains("event: segment", events);
        Assert.Contains("event: job_done", events);
        Assert.Contains("job first", events);
        Assert.Contains("job second", events);
    }

    [Fact]
    public async Task Asr_InvalidBase64_ReturnsBadRequest()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/asr", new
        {
            audioBase64 = "not-base64",
            provider = "mock"
        });

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("invalid_asr_request", body.GetProperty("errorCode").GetString());
        Assert.Contains("valid base64", body.GetProperty("errorMessage").GetString());
    }

    [Fact]
    public async Task Viewer_ReturnsMobileDisplayPage()
    {
        var client = _factory.CreateClient();

        var html = await client.GetStringAsync("/viewer");

        Assert.Contains("Verbeam Viewer", html);
        Assert.Contains("/broadcast", html);
    }

    [Fact]
    public async Task App_ReturnsWorkbenchPage()
    {
        var client = _factory.CreateClient();

        var html = await client.GetStringAsync("/app");

        Assert.Contains("Verbeam App", html);
        Assert.Contains("id=\"runButton\"", html);
        Assert.Contains("id=\"dropZone\"", html);
        Assert.Contains("id=\"translationModel\"", html);
        Assert.Contains("id=\"applyRecommendedModelButton\"", html);
        Assert.Contains("id=\"recommendedModel\"", html);
        Assert.Contains("id=\"promptPresetName\"", html);
        Assert.Contains("id=\"ocrEngineAvailability\"", html);
        Assert.Contains("id=\"ocrEngineStatus\"", html);
        Assert.Contains("/ocr/engines", html);
        Assert.Contains("id=\"tabAudio\"", html);
        Assert.Contains("id=\"audioPane\"", html);
        Assert.Contains("id=\"audioFile\"", html);
        Assert.Contains("id=\"audioSourceUrl\"", html);
        Assert.Contains("id=\"speechProvider\"", html);
        Assert.Contains("id=\"speechSegmentsTable\"", html);
        Assert.Contains("id=\"copySrtButton\"", html);
        Assert.Contains("/asr/engines", html);
        Assert.Contains("/asr/translate", html);
        Assert.Contains("id=\"tabRegion\"", html);
        Assert.Contains("id=\"regionStage\"", html);
        Assert.Contains("id=\"startRegionCaptureButton\"", html);
        Assert.Contains("getDisplayMedia", html);
        Assert.Contains("Translate OCR Text", html);
        Assert.Contains("/ocr/translate", html);
        Assert.Contains("/translation/models", html);
        Assert.Contains("/translation/languages", html);
        Assert.Contains("/broadcast", html);
    }

    [Fact]
    public async Task Translate_BroadcastsSuccessfulTranslationToWebSocketClients()
    {
        var client = _factory.CreateClient();
        var webSocketClient = _factory.Server.CreateWebSocketClient();
        using var socket = await webSocketClient.ConnectAsync(new Uri("ws://localhost/broadcast"), CancellationToken.None);

        var response = await client.PostAsJsonAsync("/translate", new
        {
            text = "hello broadcast",
            source = "en",
            target = "zh-TW",
            mode = "game_dialogue",
            provider = "mock"
        });

        response.EnsureSuccessStatusCode();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var message = await ReceiveTextAsync(socket, timeout.Token);
        using var document = JsonDocument.Parse(message);
        var root = document.RootElement;

        Assert.Equal("translation", root.GetProperty("type").GetString());
        Assert.Equal("hello broadcast", root.GetProperty("sourceText").GetString());
        Assert.Contains("[mock en->zh-TW game_dialogue] hello broadcast", root.GetProperty("translatedText").GetString());
        Assert.Equal("en", root.GetProperty("source").GetString());
        Assert.Equal("zh-TW", root.GetProperty("target").GetString());
        Assert.Equal("mock", root.GetProperty("provider").GetString());
    }

    [Fact]
    public async Task AsrLive_WebSocketReturnsSegment()
    {
        var webSocketClient = _factory.Server.CreateWebSocketClient();
        using var socket = await webSocketClient.ConnectAsync(new Uri("ws://localhost/asr/live"), CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var ready = await ReceiveTextAsync(socket, timeout.Token);
        Assert.Contains("\"type\":\"ready\"", ready);

        await SendTextAsync(socket, """{"type":"start","provider":"mock","language":"en"}""", timeout.Token);
        var started = await ReceiveTextAsync(socket, timeout.Token);
        Assert.Contains("\"type\":\"started\"", started);

        var pcm = new byte[3200];
        await socket.SendAsync(new ArraySegment<byte>(pcm), WebSocketMessageType.Binary, endOfMessage: true, timeout.Token);
        await SendTextAsync(socket, """{"type":"stop"}""", timeout.Token);

        var sawSegment = false;
        var sawDone = false;
        for (var attempt = 0; attempt < 4 && !sawDone; attempt++)
        {
            var message = await ReceiveTextAsync(socket, timeout.Token);
            using var document = JsonDocument.Parse(message);
            var type = document.RootElement.GetProperty("type").GetString();
            sawSegment |= type == "segment";
            sawDone |= type == "done";
        }

        Assert.True(sawSegment);
        Assert.True(sawDone);
    }

    public void Dispose()
    {
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        Environment.SetEnvironmentVariable("VB_Verbeam__CachePath", _previousCachePath);
        Environment.SetEnvironmentVariable("VB_Verbeam__DefaultProvider", _previousDefaultProvider);
        Environment.SetEnvironmentVariable("VB_Verbeam__Speech__DefaultProvider", _previousDefaultSpeechProvider);
        Environment.SetEnvironmentVariable("VB_Urls", _previousUrls);

        if (Directory.Exists(_tempDirectory))
        {
            DeleteDirectoryWithRetry(_tempDirectory);
        }
    }

    private static void DeleteDirectoryWithRetry(string path)
    {
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(100);
            }
        }
    }

    private static async Task<string> ReceiveTextAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        using var stream = new MemoryStream();

        while (true)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
            Assert.Equal(WebSocketMessageType.Text, result.MessageType);
            stream.Write(buffer, 0, result.Count);

            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }
    }

    private static Task SendTextAsync(WebSocket socket, string text, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
    }
}
