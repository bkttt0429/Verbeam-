using System.Net.Http.Headers;
using LocalTranslateHub.Core.Models;
using LocalTranslateHub.Core.Options;

namespace LocalTranslateHub.Core.Providers;

public sealed class FunAsrHttpSpeechProvider : ISpeechProvider
{
    private readonly HttpClient _httpClient;
    private readonly FunAsrHttpOptions _options;

    public FunAsrHttpSpeechProvider(HttpClient httpClient, FunAsrHttpOptions options)
    {
        _httpClient = httpClient;
        _options = options;
    }

    public SpeechProviderDescriptor Descriptor => new(
        "funasr-http",
        "FunASR HTTP",
        "openai-compatible-http",
        "ja",
        RequiresExternalProcess: false,
        IsLocal: true);

    public async Task<SpeechProviderResult> TranscribeAsync(
        SpeechProviderRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
        {
            throw new InvalidOperationException("FunASR HTTP provider is not configured. Set LocalTranslateHub:Speech:FunAsrHttp:BaseUrl.");
        }

        using var content = new MultipartFormDataContent();
        var audio = new ByteArrayContent(request.AudioBytes);
        audio.Headers.ContentType = new MediaTypeHeaderValue(Pick(request.AudioMimeType, "application/octet-stream"));
        content.Add(audio, "file", BuildFileName(request.AudioMimeType));
        content.Add(new StringContent(Pick(_options.Model, "sensevoice")), "model");
        content.Add(new StringContent(Pick(_options.ResponseFormat, "verbose_json")), "response_format");
        content.Add(new StringContent(request.Language), "language");

        if (request.Hotwords.Count > 0)
        {
            var hotwords = string.Join(Environment.NewLine, request.Hotwords.Keys.Where(value => !string.IsNullOrWhiteSpace(value)));
            if (!string.IsNullOrWhiteSpace(hotwords))
            {
                content.Add(new StringContent(hotwords), "hotword");
                content.Add(new StringContent(hotwords), "hotwords");
            }
        }

        using var response = await _httpClient.PostAsync(BuildEndpoint(_options.BaseUrl), content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"FunASR HTTP returned {(int)response.StatusCode}: {TrimForError(body)}");
        }

        return SpeechJsonResultParser.Parse(body, $"funasr:{Pick(_options.Model, "sensevoice")}");
    }

    private static Uri BuildEndpoint(string baseUrl)
    {
        var normalized = baseUrl.TrimEnd('/') + "/";
        return new Uri(new Uri(normalized), "v1/audio/transcriptions");
    }

    private static string BuildFileName(string mimeType)
        => "audio" + (mimeType.ToLowerInvariant() switch
        {
            "audio/wav" or "audio/wave" or "audio/x-wav" => ".wav",
            "audio/mpeg" or "audio/mp3" => ".mp3",
            "audio/mp4" or "audio/m4a" => ".m4a",
            "audio/webm" => ".webm",
            "audio/ogg" => ".ogg",
            "audio/flac" => ".flac",
            _ => ".audio"
        });

    private static string Pick(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string TrimForError(string value)
    {
        value = value.ReplaceLineEndings(" ").Trim();
        return value.Length <= 300 ? value : value[..300];
    }
}
