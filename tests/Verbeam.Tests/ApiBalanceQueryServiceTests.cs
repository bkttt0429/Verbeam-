using System.Net;
using Verbeam.Core.Models;
using Verbeam.Core.Options;
using Verbeam.Core.Services;

namespace Verbeam.Tests;

public sealed class ApiBalanceQueryServiceTests
{
    [Fact]
    public void BuildBalanceUrlCandidates_KnownTemplatesAndOverride()
    {
        Assert.Equal(
            ["https://api.deepseek.com/user/balance"],
            ApiBalanceQueryService.BuildBalanceUrlCandidates("deepseek-balance", "https://api.deepseek.com", ""));

        Assert.Equal(
            ["https://openrouter.ai/api/v1/key", "https://openrouter.ai/api/key"],
            ApiBalanceQueryService.BuildBalanceUrlCandidates("openrouter-balance", "https://openrouter.ai/api/v1", ""));

        // Explicit balanceUrl override wins for any template.
        Assert.Equal(
            ["https://relay.example.com/billing"],
            ApiBalanceQueryService.BuildBalanceUrlCandidates("generic", "https://api.example.com/v1", "https://relay.example.com/billing"));

        // Unknown / off templates yield nothing.
        Assert.Empty(ApiBalanceQueryService.BuildBalanceUrlCandidates("off", "https://api.example.com", ""));
        Assert.Empty(ApiBalanceQueryService.BuildBalanceUrlCandidates("generic", "https://api.example.com", ""));
    }

    [Theory]
    [InlineData("""{ "balance_infos": [ { "currency": "USD", "total_balance": "12.34" } ] }""", 12.34, "USD")]
    [InlineData("""{ "total_granted": 100, "total_used": 40, "total_available": 60 }""", 60.0, "USD")]
    public void ParsePlans_DeepSeekAndCreditGrantShapes(string body, double remaining, string unit)
    {
        var plans = ApiBalanceQueryService.ParsePlans("deepseek-balance", body);
        var plan = Assert.Single(plans);
        Assert.Equal(remaining, plan.Remaining);
        Assert.Equal(unit, plan.Unit);
    }

    [Fact]
    public void ParsePlans_OpenRouterShape()
    {
        var plans = ApiBalanceQueryService.ParsePlans(
            "openrouter-balance",
            """{ "data": { "limit": 20, "usage": 8, "limit_remaining": 12 } }""");
        var plan = Assert.Single(plans);
        Assert.Equal(20, plan.Total);
        Assert.Equal(8, plan.Used);
        Assert.Equal(12, plan.Remaining);
        Assert.Equal("USD", plan.Unit);
    }

    [Fact]
    public void ParsePlans_SiliconFlowShape()
    {
        var plans = ApiBalanceQueryService.ParsePlans(
            "siliconflow-balance",
            """{ "code": 20000, "data": { "balance": "3.50", "totalBalance": "5.00" } }""");
        var plan = Assert.Single(plans);
        Assert.Equal(5.00, plan.Remaining);
        Assert.Equal("CNY", plan.Unit);
    }

    [Fact]
    public void ParsePlans_UnknownShapeReturnsEmpty()
    {
        Assert.Empty(ApiBalanceQueryService.ParsePlans("generic", """{ "something": "else" }"""));
        Assert.Empty(ApiBalanceQueryService.ParsePlans("generic", "not json"));
    }

    [Fact]
    public async Task QueryAsync_ReadyWithPlan()
    {
        var result = await RunQueryAsync(
            template: "deepseek-balance",
            baseUrl: "https://api.deepseek.com",
            (HttpStatusCode.OK, """{ "balance_infos": [ { "currency": "USD", "total_balance": "9.99" } ] }"""));

        Assert.Equal("ready", result.Status);
        var plan = Assert.Single(result.Plans);
        Assert.Equal(9.99, plan.Remaining);
        Assert.NotNull(result.CheckedAt);
    }

    [Fact]
    public async Task QueryAsync_AuthErrorOn401()
    {
        var result = await RunQueryAsync(
            template: "deepseek-balance",
            baseUrl: "https://api.deepseek.com",
            (HttpStatusCode.Unauthorized, """{ "error": "bad key" }"""));

        Assert.Equal("auth_error", result.Status);
        Assert.Empty(result.Plans);
    }

    [Fact]
    public async Task QueryAsync_UnsupportedWhenNoTemplate()
    {
        var result = await RunQueryAsync(
            template: "",
            baseUrl: "https://api.deepseek.com",
            (HttpStatusCode.OK, "{}"));

        Assert.Equal("unsupported", result.Status);
    }

    private static async Task<ApiSupplierBalance> RunQueryAsync(
        string template,
        string baseUrl,
        params (HttpStatusCode Status, string Body)[] responses)
    {
        var directory = Path.Combine(Path.GetTempPath(), "verbeam-balance-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var catalogPath = Path.Combine(directory, "catalog.json");
            await new ApiSupplierPresetCatalogStore(catalogPath).SaveAsync(new ApiSupplierPresetCatalogDocument
            {
                CatalogVersion = "2026-06-11-test",
                Presets =
                [
                    new ApiSupplierPreset
                    {
                        Id = "balance-test",
                        DisplayName = "Balance Test",
                        Protocol = "openai_chat",
                        BaseUrl = baseUrl,
                        DefaultModel = "m",
                        SupportsBalance = template.Length > 0,
                        BalanceTemplate = template
                    }
                ]
            });
            var presetService = new ApiSupplierPresetCatalogService(
                catalogPath,
                Path.Combine(directory, "cache.json"),
                new ApiSupplierOptions(),
                new HttpClient());
            await presetService.InitializeAsync();

            var secrets = new ApiSecretStore(Path.Combine(directory, "secrets.json"));
            var keyRef = await secrets.SaveApiKeyAsync("supplier-test", "sk-test");

            using var httpClient = new HttpClient(new QueueJsonHandler(responses));
            var service = new ApiBalanceQueryService(httpClient, new ApiSupplierOptions(), secrets, presetService);

            return await service.QueryAsync(new ApiSupplierProfile
            {
                Id = "supplier-test",
                PresetId = "balance-test",
                Name = "Test",
                BaseUrl = baseUrl,
                ApiKeyRef = keyRef
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class QueueJsonHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _responses;

        public QueueJsonHandler((HttpStatusCode Status, string Body)[] responses)
        {
            _responses = new Queue<(HttpStatusCode, string)>(responses);
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var (status, body) = _responses.Count > 0
                ? _responses.Dequeue()
                : (HttpStatusCode.NotFound, "{}");
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}
