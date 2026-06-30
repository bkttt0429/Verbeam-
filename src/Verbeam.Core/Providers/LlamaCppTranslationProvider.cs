using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Verbeam.Core.Models;
using Verbeam.Core.Options;
using Verbeam.Core.Services;

namespace Verbeam.Core.Providers;

public sealed class LlamaCppTranslationProvider : ITranslationProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;
    private readonly LlamaCppOptions _options;
    private readonly LlamaCppRuntimeManager _runtimeManager;

    public LlamaCppTranslationProvider(
        HttpClient httpClient,
        LlamaCppOptions options,
        LlamaCppRuntimeManager runtimeManager)
    {
        _httpClient = httpClient;
        _options = options;
        _runtimeManager = runtimeManager;
    }

    public ProviderDescriptor Descriptor => new(
        "llama-cpp",
        "llama.cpp",
        "local-llm",
        _options.Model,
        RequiresNetwork: false,
        IsLocal: true);

    public async Task<ProviderTranslationResult> TranslateAsync(
        ProviderTranslationRequest request,
        CancellationToken cancellationToken)
    {
        var providerTimings = new Dictionary<string, double>();
        var endpoint = await MeasureAsync(
            providerTimings,
            "provider.endpoint_ms",
            () => _runtimeManager.GetEndpointAsync(request.Model, cancellationToken));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(endpoint.Sampling.TimeoutSeconds, 1, 600)));

        var rendered = Measure(providerTimings, "provider.prompt_render_ms", () => PromptRenderer.RenderForPrefixCache(request));
        AddTiming(providerTimings, "provider.stable_prefix_chars", rendered.StablePrefix.Length);
        AddTiming(providerTimings, "provider.suffix_chars", rendered.Suffix.Length);
        var httpStopwatch = Stopwatch.StartNew();
        var response = await SendAsync(endpoint, rendered, includeCachePrompt: endpoint.CachePromptEnabled, timeout.Token);
        if (!response.IsSuccessStatusCode &&
            endpoint.CachePromptEnabled &&
            response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity)
        {
            var error = await response.Content.ReadAsStringAsync(timeout.Token);
            response.Dispose();
            _runtimeManager.RecordCachePromptRejected($"llama.cpp rejected cache_prompt: {TrimForError(error)}");
            AddTiming(providerTimings, "provider.cache_prompt_rejected", 1);
            response = await SendAsync(endpoint, rendered, includeCachePrompt: false, timeout.Token);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            httpStopwatch.Stop();
            AddTiming(providerTimings, "provider.http_ms", httpStopwatch.Elapsed.TotalMilliseconds);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"llama.cpp returned {(int)response.StatusCode}: {TrimForError(body)}");
            }

            var chat = JsonSerializer.Deserialize<LlamaCppChatResponse>(body, JsonOptions);
            var translated = chat?.Choices.FirstOrDefault()?.Message.Content?.Trim();
            if (string.IsNullOrWhiteSpace(translated))
            {
                throw new InvalidOperationException("llama.cpp returned an empty translation.");
            }

            return new ProviderTranslationResult(translated, $"llama-cpp:{endpoint.ModelAlias}")
            {
                Timings = MergeTimings(chat?.Timings, providerTimings),
                TokenUsage = chat?.Usage?.ToTokenUsage("llama-cpp:exact")
                    ?? TokenUsageFromTimings(chat?.Timings, "llama-cpp:timings")
                    ?? TokenUsageEstimator.EstimateProviderRequest(request, translated, "llama-cpp:estimated")
            };
        }
    }

    public async IAsyncEnumerable<ProviderStreamChunk> TranslateStreamAsync(
        ProviderTranslationRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var providerTimings = new Dictionary<string, double>();
        var endpoint = await MeasureAsync(
            providerTimings,
            "provider.endpoint_ms",
            () => _runtimeManager.GetEndpointAsync(request.Model, cancellationToken));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(endpoint.Sampling.TimeoutSeconds, 1, 600)));

        var rendered = Measure(providerTimings, "provider.prompt_render_ms", () => PromptRenderer.RenderForPrefixCache(request));
        AddTiming(providerTimings, "provider.stable_prefix_chars", rendered.StablePrefix.Length);
        AddTiming(providerTimings, "provider.suffix_chars", rendered.Suffix.Length);
        var payload = new LlamaCppChatRequest(
            endpoint.ModelAlias,
            Stream: true,
            [
                new LlamaCppChatMessage("system", rendered.StablePrefix),
                new LlamaCppChatMessage("user", rendered.Suffix)
            ],
            endpoint.Sampling.Temperature,
            endpoint.Sampling.MaxTokens,
            endpoint.CachePromptEnabled ? true : null);

        using var requestMessage = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint.OpenAiBaseUrl, "chat/completions"))
        {
            Content = JsonContent.Create(payload)
        };

        HttpResponseMessage response;
        var httpStopwatch = Stopwatch.StartNew();
        try
        {
            response = await _httpClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException(
                $"Cannot reach llama.cpp server at {endpoint.OpenAiBaseUrl} ({TrimForError(ex.Message)}).", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(timeout.Token);
                httpStopwatch.Stop();
                AddTiming(providerTimings, "provider.http_ms", httpStopwatch.Elapsed.TotalMilliseconds);
                throw new InvalidOperationException($"llama.cpp returned {(int)response.StatusCode}: {TrimForError(body)}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var accumulated = new StringBuilder();
            IReadOnlyDictionary<string, double>? timings = null;
            TokenUsage? tokenUsage = null;

            while (!reader.EndOfStream)
            {
                var line = await reader.ReadLineAsync(timeout.Token);
                if (string.IsNullOrEmpty(line) || !line.StartsWith("data:", StringComparison.Ordinal))
                {
                    continue;
                }

                var data = line["data:".Length..].Trim();
                if (data == "[DONE]")
                {
                    break;
                }

                LlamaCppStreamResponse? chunk;
                try
                {
                    chunk = JsonSerializer.Deserialize<LlamaCppStreamResponse>(data, JsonOptions);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (chunk?.Timings is not null)
                {
                    timings = chunk.Timings;
                }
                if (chunk?.Usage is not null)
                {
                    tokenUsage = chunk.Usage.ToTokenUsage("llama-cpp:exact");
                }

                var delta = chunk?.Choices.FirstOrDefault()?.Delta?.Content;
                if (!string.IsNullOrEmpty(delta))
                {
                    accumulated.Append(delta);
                    yield return new ProviderStreamChunk(delta, null);
                }
            }

            var full = accumulated.ToString().Trim();
            httpStopwatch.Stop();
            AddTiming(providerTimings, "provider.http_ms", httpStopwatch.Elapsed.TotalMilliseconds);
            if (string.IsNullOrWhiteSpace(full))
            {
                throw new InvalidOperationException("llama.cpp returned an empty translation.");
            }

            yield return new ProviderStreamChunk(
                string.Empty,
                new ProviderTranslationResult(full, $"llama-cpp:{endpoint.ModelAlias}")
                {
                    Timings = MergeTimings(timings, providerTimings),
                    TokenUsage = tokenUsage
                        ?? TokenUsageFromTimings(timings, "llama-cpp:timings")
                        ?? TokenUsageEstimator.EstimateProviderRequest(request, full, "llama-cpp:estimated")
                });
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        LlamaCppRuntimeEndpoint endpoint,
        RenderedCachedPrompt rendered,
        bool includeCachePrompt,
        CancellationToken cancellationToken)
    {
        var payload = new LlamaCppChatRequest(
            endpoint.ModelAlias,
            Stream: false,
            [
                new LlamaCppChatMessage("system", rendered.StablePrefix),
                new LlamaCppChatMessage("user", rendered.Suffix)
            ],
            endpoint.Sampling.Temperature,
            endpoint.Sampling.MaxTokens,
            includeCachePrompt ? true : null);

        try
        {
            return await _httpClient.PostAsJsonAsync(
                new Uri(endpoint.OpenAiBaseUrl, "chat/completions"),
                payload,
                cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            var hint = _options.Mode.Equals("managed", StringComparison.OrdinalIgnoreCase)
                ? "The managed llama-server is not responding; retry, or reinstall it via Install and Use on the llama.cpp provider card."
                : "llama.cpp is in remote mode, which expects an already-running llama-server. Click Install and Use on the llama.cpp provider card to switch to managed mode, or start llama-server yourself.";
            throw new InvalidOperationException(
                $"Cannot reach llama.cpp server at {endpoint.OpenAiBaseUrl} ({TrimForError(ex.Message)}). {hint}",
                ex);
        }
    }

    private static string TrimForError(string value)
    {
        value = value.ReplaceLineEndings(" ").Trim();
        return value.Length <= 300 ? value : value[..300];
    }

    private static TokenUsage? TokenUsageFromTimings(IReadOnlyDictionary<string, double>? timings, string source)
    {
        if (timings is null || timings.Count == 0)
        {
            return null;
        }

        var input = ReadTimingCount(timings, "prompt_n", "prompt_tokens", "prompt_eval_count", "n_prompt_tokens");
        var output = ReadTimingCount(timings, "predicted_n", "completion_tokens", "eval_count", "n_decoded", "n_predict");
        if (input is null && output is null)
        {
            return null;
        }

        var safeInput = Math.Max(0, input ?? 0);
        var safeOutput = Math.Max(0, output ?? 0);
        return new TokenUsage(safeInput, safeOutput, safeInput + safeOutput, source, IsEstimated: false);
    }

    private static long? ReadTimingCount(IReadOnlyDictionary<string, double> timings, params string[] names)
    {
        foreach (var name in names)
        {
            if (timings.TryGetValue(name, out var value) && double.IsFinite(value) && value >= 0)
            {
                return (long)Math.Round(value);
            }
        }

        return null;
    }

    private static T Measure<T>(IDictionary<string, double> timings, string name, Func<T> action)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            return action();
        }
        finally
        {
            stopwatch.Stop();
            AddTiming(timings, name, stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    private static async Task<T> MeasureAsync<T>(IDictionary<string, double> timings, string name, Func<Task<T>> action)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            return await action();
        }
        finally
        {
            stopwatch.Stop();
            AddTiming(timings, name, stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    private static IReadOnlyDictionary<string, double> MergeTimings(
        IReadOnlyDictionary<string, double>? providerTimings,
        IReadOnlyDictionary<string, double> localTimings)
    {
        var merged = new Dictionary<string, double>();
        if (providerTimings is not null)
        {
            foreach (var item in providerTimings)
            {
                if (double.IsFinite(item.Value))
                {
                    merged[item.Key] = item.Value;
                }
            }
        }

        foreach (var item in localTimings)
        {
            if (double.IsFinite(item.Value))
            {
                merged[item.Key] = item.Value;
            }
        }

        return merged;
    }

    private static void AddTiming(IDictionary<string, double> timings, string name, double value)
    {
        if (double.IsFinite(value))
        {
            timings[name] = value;
        }
    }

    private sealed record LlamaCppChatRequest(
        [property: JsonPropertyName("model")]
        string Model,
        [property: JsonPropertyName("stream")]
        bool Stream,
        [property: JsonPropertyName("messages")]
        IReadOnlyList<LlamaCppChatMessage> Messages,
        [property: JsonPropertyName("temperature")]
        double Temperature,
        [property: JsonPropertyName("max_tokens")]
        int MaxTokens,
        [property: JsonPropertyName("cache_prompt")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        bool? CachePrompt);

    private sealed record LlamaCppChatMessage(
        [property: JsonPropertyName("role")]
        string Role,
        [property: JsonPropertyName("content")]
        string Content);

    private sealed record LlamaCppChatResponse(
        [property: JsonPropertyName("choices")]
        IReadOnlyList<LlamaCppChoice> Choices,
        [property: JsonPropertyName("usage")]
        LlamaCppUsage? Usage = null,
        [property: JsonPropertyName("timings")]
        IReadOnlyDictionary<string, double>? Timings = null);

    private sealed record LlamaCppChoice(
        [property: JsonPropertyName("message")]
        LlamaCppChatMessage Message);

    private sealed record LlamaCppStreamResponse(
        [property: JsonPropertyName("choices")]
        IReadOnlyList<LlamaCppStreamChoice> Choices,
        [property: JsonPropertyName("usage")]
        LlamaCppUsage? Usage = null,
        [property: JsonPropertyName("timings")]
        IReadOnlyDictionary<string, double>? Timings = null);

    private sealed record LlamaCppUsage(
        [property: JsonPropertyName("prompt_tokens")]
        long PromptTokens,
        [property: JsonPropertyName("completion_tokens")]
        long CompletionTokens,
        [property: JsonPropertyName("total_tokens")]
        long TotalTokens)
    {
        public TokenUsage ToTokenUsage(string source)
        {
            var input = Math.Max(0, PromptTokens);
            var output = Math.Max(0, CompletionTokens);
            var total = TotalTokens > 0 ? TotalTokens : input + output;
            return new TokenUsage(input, output, total, source, IsEstimated: false);
        }
    }

    private sealed record LlamaCppStreamChoice(
        [property: JsonPropertyName("delta")]
        LlamaCppStreamDelta? Delta);

    private sealed record LlamaCppStreamDelta(
        [property: JsonPropertyName("content")]
        string? Content);
}
