using Verbeam.Core.Models;
using Verbeam.Core.Options;
using Verbeam.Core.Services;

namespace Verbeam.Tests;

public sealed class TranslationConfigurationCatalogTests
{
    [Fact]
    public void ListLanguages_MarksConfiguredDefaults()
    {
        var catalog = new TranslationConfigurationCatalog(new VerbeamOptions
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
        var catalog = new TranslationConfigurationCatalog(new VerbeamOptions
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
}
