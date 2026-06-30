using Verbeam.Core.Models;
using Verbeam.Core.Options;
using Verbeam.Core.Services;

namespace Verbeam.Tests;

public sealed class TranslationConfigurationCatalogTests
{
    [Fact]
    public void ModelCatalogStore_LoadsBuiltInCatalog()
    {
        var catalog = LoadModelCatalog();

        Assert.Equal("2026-06-19-task-aware-picker", catalog.CatalogVersion);
        Assert.Contains(catalog.Models, item => item.Name == "verbeam-mort-qwen2.5-0.5b:latest");
    }

    [Fact]
    public void ModelCatalogStore_AllowsLegacySchemaWithoutRuntimeProfiles()
    {
        var model = LoadModelCatalog().Models.First(item => item.Id == "qwen2.5-1.5b");
        var legacy = new ModelCatalogDocument
        {
            SchemaVersion = 1,
            CatalogVersion = "legacy-test",
            Models =
            [
                model with
                {
                    Artifact = null,
                    Runtimes = new ModelRuntimeSet()
                }
            ]
        };

        ModelCatalogStore.Validate(legacy);
    }

    [Fact]
    public async Task ModelCatalogService_PrefersValidCacheCatalog()
    {
        var directory = Path.Combine(Path.GetTempPath(), "verbeam-model-catalog-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var builtInPath = Path.Combine(directory, "built-in.json");
            var cachePath = Path.Combine(directory, "cache.json");
            var builtIn = LoadModelCatalog();
            var cache = builtIn with { CatalogVersion = "2026-06-30-remote" };
            await new ModelCatalogStore(builtInPath).SaveAsync(builtIn);
            await new ModelCatalogStore(cachePath).SaveAsync(cache);

            var service = new ModelCatalogService(
                builtInPath,
                cachePath,
                new ModelCatalogOptions(),
                new HttpClient());

            await service.InitializeAsync();

            Assert.Equal("cache", service.GetStatus().Source);
            Assert.Equal("2026-06-30-remote", service.GetCurrent().CatalogVersion);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ModelCatalogService_DoesNotPreferOlderSchemaCache()
    {
        var directory = Path.Combine(Path.GetTempPath(), "verbeam-model-catalog-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var builtInPath = Path.Combine(directory, "built-in.json");
            var cachePath = Path.Combine(directory, "cache.json");
            var builtIn = LoadModelCatalog();
            var oldSchemaCache = builtIn with
            {
                SchemaVersion = 1,
                CatalogVersion = "2026-06-11-old-schema"
            };
            await new ModelCatalogStore(builtInPath).SaveAsync(builtIn);
            await new ModelCatalogStore(cachePath).SaveAsync(oldSchemaCache);

            var service = new ModelCatalogService(
                builtInPath,
                cachePath,
                new ModelCatalogOptions(),
                new HttpClient());

            await service.InitializeAsync();

            Assert.Equal("built-in", service.GetStatus().Source);
            Assert.Equal(2, service.GetCurrent().SchemaVersion);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ModelCatalogService_RefreshReportsDisabledByDefault()
    {
        var directory = Path.Combine(Path.GetTempPath(), "verbeam-model-catalog-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var builtInPath = Path.Combine(directory, "built-in.json");
            var cachePath = Path.Combine(directory, "cache.json");
            await new ModelCatalogStore(builtInPath).SaveAsync(LoadModelCatalog());

            var service = new ModelCatalogService(
                builtInPath,
                cachePath,
                new ModelCatalogOptions(),
                new HttpClient());

            await service.InitializeAsync();
            var result = await service.RefreshAsync();

            Assert.False(result.Updated);
            Assert.Equal("built-in", result.Source);
            Assert.Contains("disabled", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void ListLanguages_MarksConfiguredDefaults()
    {
        var catalog = CreateCatalog(new VerbeamOptions
        {
            DefaultSource = "ja-JP",
            DefaultTarget = "zh-Hant"
        });

        var languages = catalog.ListLanguages();

        Assert.Contains(languages, item => item.Code == "ja" && item.IsDefaultSource);
        Assert.Contains(languages, item => item.Code == "zh-TW" && item.IsDefaultTarget);
    }

    [Fact]
    public void EnrichModels_AddsOllamaRecommendations()
    {
        var catalog = CreateCatalog(new VerbeamOptions
        {
            Ollama = new OllamaOptions
            {
                Model = "custom-default:latest"
            }
        });

        var models = catalog.EnrichModels("ollama",
        [
            new TranslationModelDescriptor(
                "ollama",
                "custom-default:latest",
                "custom-default:latest",
                IsDefault: true,
                IsInstalled: true,
                "ollama")
        ]);

        Assert.Contains(models, item =>
            item.Name == "custom-default:latest" &&
            item.IsDefault &&
            item.IsRecommended &&
            item.RecommendedUse == "current default");
        Assert.Contains(models, item =>
            item.Name == "verbeam-mort-qwen2.5-0.5b:latest" &&
            item.IsRecommended &&
            item.Source == "recommended");
    }

    [Fact]
    public void EnrichModels_AddsLlamaCppGemmaFitRecommendation()
    {
        var catalog = CreateCatalog();

        var models = catalog.EnrichModels("llama-cpp", []);
        var gemma = Assert.Single(models, item => item.Name == "gemmafit-gemma4-e2b-iq2m");

        Assert.True(gemma.IsRecommended);
        Assert.False(gemma.IsDefault);
        Assert.Equal("quality candidate; slower for realtime OCR", gemma.RecommendedUse);
        Assert.Equal("recommended", gemma.Source);
    }

    [Fact]
    public void RecommendModelForComputer_UsesInstalledGemmaFitForLlamaCppGpu()
    {
        var catalog = CreateCatalog();
        var models = catalog.EnrichModels("llama-cpp",
        [
            new TranslationModelDescriptor(
                "llama-cpp",
                "gemmafit-gemma4-e2b-iq2m",
                "GemmaFit Gemma4 E2B IQ2_M",
                IsDefault: false,
                IsInstalled: true,
                "llama-cpp")
        ]);

        var recommendation = catalog.RecommendModelForComputer(
            new ComputerModelRecommendationRequest
            {
                Workload = "realtime_overlay",
                Preference = "quality",
                Source = "ja",
                Target = "zh-TW",
                ContextTokens = 2048,
                CpuLogicalCores = 8,
                MemoryGb = 16,
                HasDedicatedGpu = true,
                GpuVramGb = 4
            },
            "llama-cpp",
            models);

        Assert.Equal("gemmafit-gemma4-e2b-iq2m", recommendation.RecommendedModel);
        Assert.Equal("small-gpu", recommendation.Tier);
        Assert.True(recommendation.IsInstalled);
        Assert.Contains(recommendation.Candidates, item =>
            item.Name == "gemmafit-gemma4-e2b-iq2m" &&
            item.IsViable &&
            item.EstimatedMemoryGb is <= 4.1);
    }

    [Fact]
    public void RecommendModelForComputer_UsesSmallModelForRealtimeWork()
    {
        var catalog = CreateCatalog();
        var models = catalog.EnrichModels("ollama",
        [
            new TranslationModelDescriptor(
                "ollama",
                "verbeam-mort-qwen2.5-0.5b:latest",
                "Verbeam MORT Qwen2.5 0.5B",
                IsDefault: true,
                IsInstalled: true,
                "ollama")
        ]);

        var recommendation = catalog.RecommendModelForComputer(
            new ComputerModelRecommendationRequest
            {
                Workload = "realtime_overlay",
                Preference = "speed",
                Source = "ja",
                Target = "zh-TW",
                ContextTokens = 1024,
                CpuLogicalCores = 4,
                MemoryGb = 8,
                GpuVramGb = 0
            },
            "ollama",
            models);

        Assert.Equal("verbeam-mort-qwen2.5-0.5b:latest", recommendation.RecommendedModel);
        Assert.Equal("small", recommendation.Tier);
        Assert.True(recommendation.IsInstalled);
        Assert.Equal("2026-06-19-task-aware-picker", recommendation.CatalogVersion);
        Assert.True(recommendation.Candidates.Count <= 3);
        Assert.Contains(recommendation.Signals, item => item.Contains("Target tier: small"));
        Assert.Contains(recommendation.Signals, item => item.Contains("Language pair: ja->zh-TW"));
        Assert.Contains(recommendation.Candidates, item =>
            item.Name == "verbeam-mort-qwen2.5-0.5b:latest" &&
            item.IsViable &&
            item.Score > 0 &&
            item.SourceLinks.ContainsKey("ollama"));
    }

    [Fact]
    public void RecommendModelForComputer_UsesQualityModelWhenHardwareAllows()
    {
        var catalog = CreateCatalog();
        var models = catalog.EnrichModels("ollama",
        [
            new TranslationModelDescriptor(
                "ollama",
                "translategemma:latest",
                "TranslateGemma",
                IsDefault: false,
                IsInstalled: true,
                "ollama")
        ]);

        var recommendation = catalog.RecommendModelForComputer(
            new ComputerModelRecommendationRequest
            {
                Workload = "quality",
                Preference = "quality",
                Source = "ja",
                Target = "zh-TW",
                ContextTokens = 4096,
                CpuLogicalCores = 12,
                MemoryGb = 32,
                HasDedicatedGpu = true,
                GpuVramGb = 8
            },
            "ollama",
            models);

        Assert.Equal("translategemma:latest", recommendation.RecommendedModel);
        Assert.Equal("quality", recommendation.Tier);
        Assert.True(recommendation.IsInstalled);
        Assert.DoesNotContain(recommendation.Warnings, item => item.Contains("GPU VRAM was not provided"));
        Assert.Contains(recommendation.Candidates, item =>
            item.Name == "translategemma:latest" &&
            item.Score > 0.7 &&
            item.EstimatedMemoryGb is >= 7);
    }

    [Fact]
    public void RecommendModelForComputer_ExposesNonViableMemoryCandidates()
    {
        var catalog = CreateCatalog();
        var models = catalog.EnrichModels("ollama", []);

        var recommendation = catalog.RecommendModelForComputer(
            new ComputerModelRecommendationRequest
            {
                Workload = "quality",
                Preference = "quality",
                Source = "ja",
                Target = "zh-TW",
                CpuLogicalCores = 2,
                MemoryGb = 4,
                GpuVramGb = 0
            },
            "ollama",
            models);

        Assert.NotEqual("translategemma:latest", recommendation.RecommendedModel);
        Assert.True(recommendation.Candidates.Count <= 3);
        Assert.Contains(recommendation.Candidates, item =>
            !item.IsViable &&
            item.FitReason.Contains("memory", StringComparison.OrdinalIgnoreCase));
    }

    private static TranslationConfigurationCatalog CreateCatalog(VerbeamOptions? options = null)
        => new(options ?? new VerbeamOptions(), LoadModelCatalog());

    private static ModelCatalogDocument LoadModelCatalog()
    {
        var catalogPath = PathResolver.Resolve(AppContext.BaseDirectory, "models.catalog.json");
        return new ModelCatalogStore(catalogPath).LoadAsync().GetAwaiter().GetResult();
    }
}
