using System.Net;
using System.Text.Json;
using Verbeam.Core.Models;
using Verbeam.Core.Options;

namespace Verbeam.Core.Services;

/// <summary>
/// Queries a cloud supplier's account balance / quota using a <em>declarative</em> template — never by
/// running user-supplied code (unlike cc-switch's JS usage scripts). Mirrors the HTTP/auth/status pattern
/// of <see cref="ApiModelDiscoveryService"/> and reuses its <see cref="ApiModelDiscoveryService.ApplyAuthHeader"/>
/// plus the supplier's existing DPAPI-stored API key.
/// </summary>
public sealed class ApiBalanceQueryService
{
    private readonly HttpClient _httpClient;
    private readonly ApiSupplierOptions _options;
    private readonly ApiSecretStore _secrets;
    private readonly ApiSupplierPresetCatalogService _presets;

    public ApiBalanceQueryService(
        HttpClient httpClient,
        ApiSupplierOptions options,
        ApiSecretStore secrets,
        ApiSupplierPresetCatalogService presets)
    {
        _httpClient = httpClient;
        _options = options;
        _secrets = secrets;
        _presets = presets;
    }

    public async Task<ApiSupplierBalance> QueryAsync(
        ApiSupplierProfile supplier,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        try
        {
            var plans = await QueryPlansAsync(supplier, cancellationToken);
            return new ApiSupplierBalance(
                "ready",
                plans.Count == 0 ? "Balance query returned no plans." : $"Fetched {plans.Count} plan(s).",
                plans,
                now);
        }
        catch (ApiBalanceQueryException ex)
        {
            return new ApiSupplierBalance(ex.Status, ex.Message, [], now);
        }
    }

    private async Task<IReadOnlyList<ApiSupplierBalancePlan>> QueryPlansAsync(
        ApiSupplierProfile supplier,
        CancellationToken cancellationToken)
    {
        var preset = _presets.GetRequiredPreset(supplier.PresetId);
        var template = ApiSupplierStore.ResolveBalanceTemplate(supplier, preset.BalanceTemplate);
        if (template.Length == 0 || template.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            throw new ApiBalanceQueryException("unsupported", "Balance check is not configured for this supplier.");
        }

        var apiKey = await _secrets.GetApiKeyAsync(supplier.ApiKeyRef, cancellationToken);
        if (preset.RequiresApiKey && string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ApiBalanceQueryException("missing_key", "API key is required before checking the balance.");
        }

        var candidates = BuildBalanceUrlCandidates(template, supplier.BaseUrl, supplier.BalanceUrl);
        if (candidates.Count == 0)
        {
            throw new ApiBalanceQueryException("unsupported", "No balance endpoint could be derived for this supplier.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.DiscoveryTimeoutSeconds, 1, 120)));

        ApiBalanceQueryException? lastEndpointError = null;
        foreach (var url in candidates)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            ApiModelDiscoveryService.ApplyAuthHeader(request, preset, apiKey);

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(request, timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ApiBalanceQueryException("timeout", "Balance query timed out.");
            }
            catch (HttpRequestException ex)
            {
                throw new ApiBalanceQueryException("unreachable", $"Balance request failed: {ex.Message}");
            }

            using (response)
            {
                if (response.IsSuccessStatusCode)
                {
                    string body;
                    try
                    {
                        body = await response.Content.ReadAsStringAsync(timeout.Token);
                    }
                    catch (Exception ex)
                    {
                        throw new ApiBalanceQueryException("parse_error", $"Could not read balance response: {ex.Message}");
                    }

                    var plans = ParsePlans(template, body);
                    if (plans.Count == 0)
                    {
                        // A 200 with an unrecognized shape — try the next candidate before giving up.
                        lastEndpointError = new ApiBalanceQueryException("parse_error", "Balance response had no recognizable fields.");
                        continue;
                    }
                    return plans;
                }

                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    throw new ApiBalanceQueryException("auth_error", $"Balance query returned {(int)response.StatusCode}. Check the API key.");
                }

                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
                {
                    lastEndpointError = new ApiBalanceQueryException("unsupported", "This supplier endpoint does not expose a balance endpoint.");
                    continue;
                }

                var error = await response.Content.ReadAsStringAsync(timeout.Token);
                throw new ApiBalanceQueryException("http_error", $"Balance query returned {(int)response.StatusCode}: {TrimForError(error)}");
            }
        }

        throw lastEndpointError ?? new ApiBalanceQueryException("unsupported", "No balance endpoint candidate was available.");
    }

    /// <summary>
    /// Declarative URL candidates per template. The per-supplier <paramref name="balanceUrl"/> wins for the
    /// generic template and acts as an override for the known ones.
    /// </summary>
    public static IReadOnlyList<string> BuildBalanceUrlCandidates(
        string template,
        string baseUrl,
        string balanceUrl)
    {
        var overrideUrl = (balanceUrl ?? string.Empty).Trim();
        if (overrideUrl.Length > 0)
        {
            return [overrideUrl];
        }

        var root = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (root.Length == 0)
        {
            return [];
        }

        // Strip a trailing version segment (…/v1) so we can build sibling billing paths.
        var withoutVersion = StripTrailingVersion(root);

        return template.Trim().ToLowerInvariant() switch
        {
            // DeepSeek: GET https://api.deepseek.com/user/balance → { balance_infos: [...] }.
            "deepseek-balance" or "deepseek" =>
            [
                $"{withoutVersion}/user/balance"
            ],
            // OpenRouter: GET https://openrouter.ai/api/v1/key → { data: { limit, usage } }.
            "openrouter-balance" or "openrouter" =>
            [
                $"{root}/key",
                $"{withoutVersion}/key"
            ],
            // SiliconFlow: GET https://api.siliconflow.cn/v1/user/info → { data: { balance, ... } }.
            "siliconflow-balance" or "siliconflow" =>
            [
                $"{root}/user/info",
                $"{withoutVersion}/user/info"
            ],
            "openai_credit_grants" =>
            [
                $"{withoutVersion}/dashboard/billing/credit_grants",
                $"{withoutVersion}/v1/dashboard/billing/credit_grants"
            ],
            "balance" =>
            [
                $"{withoutVersion}/user/balance",
                $"{withoutVersion}/v1/user/balance",
                $"{withoutVersion}/dashboard/billing/credit_grants"
            ],
            "newapi" =>
            [
                $"{withoutVersion}/api/user/self"
            ],
            "generic" => [], // generic requires an explicit balanceUrl (handled above)
            _ => []
        };
    }

    /// <summary>
    /// Parse a balance response into plans. Recognizes a small set of well-known field shapes; unknown
    /// shapes yield no plans (caller treats that as parse_error / try-next-candidate).
    /// </summary>
    public static IReadOnlyList<ApiSupplierBalancePlan> ParsePlans(string template, string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return [];
        }

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(body);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return [];
        }

        // DeepSeek: { balance_infos: [ { currency, total_balance, granted_balance, topped_up_balance } ] }.
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("balance_infos", out var balanceInfos) &&
            balanceInfos.ValueKind == JsonValueKind.Array)
        {
            var plans = new List<ApiSupplierBalancePlan>();
            foreach (var info in balanceInfos.EnumerateArray())
            {
                if (info.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                var currency = info.TryGetProperty("currency", out var cur) && cur.ValueKind == JsonValueKind.String
                    ? cur.GetString() ?? "USD"
                    : "USD";
                if (TryGetNumber(info, out var totalBalance, "total_balance"))
                {
                    plans.Add(new ApiSupplierBalancePlan(
                        Name: "Balance",
                        Total: null,
                        Used: null,
                        Remaining: totalBalance,
                        Unit: currency,
                        Extra: string.Empty,
                        IsValid: true));
                }
            }
            if (plans.Count > 0)
            {
                return plans;
            }
        }

        var hasData = TryGetNested(root, out var dataElement, "data") && dataElement.ValueKind == JsonValueKind.Object;

        // OpenRouter: { data: { limit, usage, limit_remaining } } in USD (limit may be null = unlimited).
        if (hasData && (dataElement.TryGetProperty("usage", out _) || dataElement.TryGetProperty("limit_remaining", out _)))
        {
            var hasUsage = TryGetNumber(dataElement, out var usage, "usage");
            var hasLimit = TryGetNumber(dataElement, out var limit, "limit");
            var hasRemaining = TryGetNumber(dataElement, out var remaining, "limit_remaining");
            if (hasUsage || hasLimit || hasRemaining)
            {
                double? remainingValue = hasRemaining
                    ? remaining
                    : hasLimit && hasUsage ? Math.Max(0, limit - usage) : null;
                return [new ApiSupplierBalancePlan(
                    Name: "Credits",
                    Total: hasLimit ? limit : null,
                    Used: hasUsage ? usage : null,
                    Remaining: remainingValue,
                    Unit: "USD",
                    Extra: hasLimit ? string.Empty : "no spend limit set",
                    IsValid: true)];
            }
        }

        // SiliconFlow: { data: { balance, totalBalance, chargeBalance } } — balance is a string CNY amount.
        if (hasData && (dataElement.TryGetProperty("totalBalance", out _) || dataElement.TryGetProperty("balance", out _)))
        {
            if (TryGetNumber(dataElement, out var sfBalance, "totalBalance") ||
                TryGetNumber(dataElement, out sfBalance, "balance"))
            {
                return [new ApiSupplierBalancePlan(
                    Name: "Balance",
                    Total: null,
                    Used: null,
                    Remaining: sfBalance,
                    Unit: "CNY",
                    Extra: string.Empty,
                    IsValid: true)];
            }
        }

        // NewAPI-style: { data: { quota, used_quota } } in token units; convert to "credits".
        if (hasData)
        {
            if (TryGetNumber(dataElement, out var quota, "quota") &&
                TryGetNumber(dataElement, out var usedQuota, "used_quota"))
            {
                // NewAPI quota is in 1/500000 USD units by convention; keep raw + label as credits.
                var remainingCredits = Math.Max(0, quota - usedQuota);
                return [new ApiSupplierBalancePlan(
                    Name: "Quota",
                    Total: quota,
                    Used: usedQuota,
                    Remaining: remainingCredits,
                    Unit: "credits",
                    Extra: string.Empty,
                    IsValid: true)];
            }
        }

        // OpenAI credit grants: { total_granted, total_used, total_available }.
        if (TryGetNumber(root, out var granted, "total_granted") &&
            TryGetNumber(root, out var used, "total_used"))
        {
            var available = TryGetNumber(root, out var avail, "total_available")
                ? avail
                : Math.Max(0, granted - used);
            return [new ApiSupplierBalancePlan(
                Name: "Credit grants",
                Total: granted,
                Used: used,
                Remaining: available,
                Unit: "USD",
                Extra: string.Empty,
                IsValid: true)];
        }

        // Simple balance: { balance } or { data: { balance } } or { is_available, total_balance }.
        if (TryGetNumber(root, out var balance, "balance") ||
            TryGetNumber(root, out balance, "total_balance") ||
            (dataElement.ValueKind == JsonValueKind.Object && TryGetNumber(dataElement, out balance, "balance")))
        {
            return [new ApiSupplierBalancePlan(
                Name: "Balance",
                Total: null,
                Used: null,
                Remaining: balance,
                Unit: "USD",
                Extra: string.Empty,
                IsValid: true)];
        }

        return [];
    }

    private static bool TryGetNested(JsonElement root, out JsonElement value, string property)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(property, out var found))
        {
            value = found;
            return true;
        }
        value = default;
        return false;
    }

    private static bool TryGetNumber(JsonElement element, out double value, string property)
    {
        value = 0;
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var prop))
        {
            return false;
        }

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out var number))
        {
            value = number;
            return true;
        }

        if (prop.ValueKind == JsonValueKind.String &&
            double.TryParse(prop.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            value = parsed;
            return true;
        }

        return false;
    }

    private static string StripTrailingVersion(string url)
    {
        var last = url.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;
        var isVersion = last.Length > 1 && (last[0] is 'v' or 'V') && last[1..].All(char.IsAsciiDigit);
        return isVersion ? url[..^(last.Length + 1)] : url;
    }

    private static string TrimForError(string value)
    {
        value = value.ReplaceLineEndings(" ").Trim();
        return value.Length <= 300 ? value : value[..300];
    }
}

public sealed class ApiBalanceQueryException : InvalidOperationException
{
    public ApiBalanceQueryException(string status, string message)
        : base(message)
    {
        Status = status;
    }

    public string Status { get; }
}
