using System.Net;
using System.Text.Json;
using Verbeam.Core.Models;
using Verbeam.Core.Options;
using Verbeam.Core.Services;

namespace Verbeam.Tests;

public sealed class ApiSupplierTests
{
    [Fact]
    public async Task ApiSupplierPresetCatalogStore_LoadsBuiltInCatalog()
    {
        var catalog = await new ApiSupplierPresetCatalogStore("api-suppliers.catalog.json").LoadAsync();

        Assert.Equal("2026-06-12-official-core-and-coding-plan", catalog.CatalogVersion);
        Assert.Contains(catalog.Presets, item => item.Id == "deepseek");
        Assert.Contains(catalog.Presets, item => item.Id == "openrouter");
        Assert.Contains(catalog.Presets, item => item.Protocol == "anthropic");
        Assert.All(catalog.Presets, item => Assert.Contains(item.Protocol, new[] { "openai_chat", "anthropic" }));
    }

    [Fact]
    public void ApiSupplierPresetCatalogStore_RejectsUnsupportedProtocol()
    {
        var catalog = CreateCatalog() with
        {
            Presets =
            [
                CreatePreset() with { Protocol = "unsupported" }
            ]
        };

        var error = Assert.Throws<InvalidOperationException>(() => ApiSupplierPresetCatalogStore.Validate(catalog));
        Assert.Contains("Unsupported", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildModelsUrlCandidates_HandlesVersionRootAndOverride()
    {
        Assert.Equal(
            ["https://api.example.com/v1/models"],
            ApiModelDiscoveryService.BuildModelsUrlCandidates("https://api.example.com", false, null));
        Assert.Equal(
            ["https://api.example.com/v1/models"],
            ApiModelDiscoveryService.BuildModelsUrlCandidates("https://api.example.com/v1", false, null));
        Assert.Equal(
            ["https://custom.example.com/models"],
            ApiModelDiscoveryService.BuildModelsUrlCandidates("https://api.example.com", false, "https://custom.example.com/models"));
    }

    [Fact]
    public async Task ApiSecretStore_DoesNotWritePlaintextKey()
    {
        var directory = CreateTempDirectory();
        try
        {
            var path = Path.Combine(directory, "secrets.json");
            var store = new ApiSecretStore(path);

            var keyRef = await store.SaveApiKeyAsync("supplier-test", "sk-secret-value");

            Assert.Equal("sk-secret-value", await store.GetApiKeyAsync(keyRef));
            var raw = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain("sk-secret-value", raw);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ApiModelDiscoveryService_FetchesAndSortsOpenAiModels()
    {
        var directory = CreateTempDirectory();
        try
        {
            var catalogPath = Path.Combine(directory, "catalog.json");
            await new ApiSupplierPresetCatalogStore(catalogPath).SaveAsync(CreateCatalog());
            var presetService = new ApiSupplierPresetCatalogService(
                catalogPath,
                Path.Combine(directory, "cache.json"),
                new ApiSupplierOptions(),
                new HttpClient());
            await presetService.InitializeAsync();
            var secrets = new ApiSecretStore(Path.Combine(directory, "secrets.json"));
            var keyRef = await secrets.SaveApiKeyAsync("supplier-test", "sk-test");
            using var httpClient = new HttpClient(new StaticJsonHandler("""
                {
                  "data": [
                    { "id": "z-model", "owned_by": "owner-z" },
                    { "id": "a-model", "owned_by": "owner-a" }
                  ]
                }
                """));
            var discovery = new ApiModelDiscoveryService(
                httpClient,
                new ApiSupplierOptions(),
                secrets,
                presetService);

            var models = await discovery.FetchModelsAsync(new ApiSupplierProfile
            {
                Id = "supplier-test",
                PresetId = "test",
                Name = "Test",
                BaseUrl = "https://api.example.com",
                ApiKeyRef = keyRef
            });

            Assert.Equal(["a-model", "z-model"], models.Select(item => item.Id).ToArray());
            Assert.Equal("owner-a", models[0].OwnedBy);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ApiSupplierPresetCatalogDocument CreateCatalog()
        => new()
        {
            CatalogVersion = "2026-06-11-test",
            Presets = [CreatePreset()]
        };

    private static ApiSupplierPreset CreatePreset()
        => new()
        {
            Id = "test",
            DisplayName = "Test",
            Protocol = "openai_chat",
            BaseUrl = "https://api.example.com",
            DefaultModel = "test-model",
            RecommendedModels =
            [
                new ApiSupplierRecommendedModel
                {
                    Id = "test-model",
                    DisplayName = "Test Model"
                }
            ]
        };

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "verbeam-api-supplier-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed class StaticJsonHandler : HttpMessageHandler
    {
        private readonly string _json;

        public StaticJsonHandler(string json)
        {
            _json = json;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://api.example.com/v1/models", request.RequestUri?.ToString());
            Assert.True(request.Headers.TryGetValues("Authorization", out var values));
            Assert.Equal("Bearer sk-test", Assert.Single(values));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_json, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}
