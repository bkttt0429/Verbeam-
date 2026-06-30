using System.Text.Json;
using Verbeam.Core.Options;

namespace Verbeam.Core.Services;

public sealed class MemoryBearerJwtKeyStore
{
    private readonly MemoryBearerJwtOptions _options;
    private readonly string _contentRootPath;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _cachedJwksJson;
    private DateTimeOffset _cachedJwksExpiresAt;
    private string? _cachedDiscoveryJwksUrl;
    private DateTimeOffset _cachedDiscoveryExpiresAt;

    public MemoryBearerJwtKeyStore(
        MemoryBearerJwtOptions options,
        string contentRootPath,
        HttpClient httpClient)
    {
        _options = options;
        _contentRootPath = contentRootPath;
        _httpClient = httpClient;
    }

    public async Task<string?> GetJwksJsonAsync(CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(_options.JwksJson))
        {
            return _options.JwksJson;
        }

        var fileJson = ReadJwksFile();
        if (!string.IsNullOrWhiteSpace(fileJson))
        {
            return fileJson;
        }

        var now = DateTimeOffset.UtcNow;
        if (!string.IsNullOrWhiteSpace(_cachedJwksJson) && _cachedJwksExpiresAt > now)
        {
            return _cachedJwksJson;
        }

        var jwksUrl = await ResolveJwksUrlAsync(now, cancellationToken);
        if (string.IsNullOrWhiteSpace(jwksUrl))
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            now = DateTimeOffset.UtcNow;
            if (!string.IsNullOrWhiteSpace(_cachedJwksJson) && _cachedJwksExpiresAt > now)
            {
                return _cachedJwksJson;
            }

            var jwksJson = await FetchJsonAsync(jwksUrl, cancellationToken);
            if (string.IsNullOrWhiteSpace(jwksJson) || !LooksLikeJwks(jwksJson))
            {
                return null;
            }

            _cachedJwksJson = jwksJson;
            _cachedJwksExpiresAt = now.AddSeconds(Math.Clamp(_options.JwksRefreshSeconds, 30, 86400));
            return _cachedJwksJson;
        }
        finally
        {
            _gate.Release();
        }
    }

    private string? ReadJwksFile()
    {
        if (string.IsNullOrWhiteSpace(_options.JwksPath))
        {
            return null;
        }

        var path = PathResolver.Resolve(_contentRootPath, _options.JwksPath);
        return File.Exists(path)
            ? File.ReadAllText(path)
            : null;
    }

    private async Task<string?> ResolveJwksUrlAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (TryNormalizeHttpsUrl(_options.JwksUrl, out var jwksUrl))
        {
            return jwksUrl;
        }

        if (!TryNormalizeHttpsUrl(_options.OidcDiscoveryUrl, out var discoveryUrl))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(_cachedDiscoveryJwksUrl) && _cachedDiscoveryExpiresAt > now)
        {
            return _cachedDiscoveryJwksUrl;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            now = DateTimeOffset.UtcNow;
            if (!string.IsNullOrWhiteSpace(_cachedDiscoveryJwksUrl) && _cachedDiscoveryExpiresAt > now)
            {
                return _cachedDiscoveryJwksUrl;
            }

            var discoveryJson = await FetchJsonAsync(discoveryUrl, cancellationToken);
            if (string.IsNullOrWhiteSpace(discoveryJson))
            {
                return null;
            }

            using var document = JsonDocument.Parse(discoveryJson);
            if (!document.RootElement.TryGetProperty("jwks_uri", out var jwksUriElement) ||
                jwksUriElement.ValueKind != JsonValueKind.String ||
                !TryNormalizeHttpsUrl(jwksUriElement.GetString(), out var discoveredJwksUrl))
            {
                return null;
            }

            _cachedDiscoveryJwksUrl = discoveredJwksUrl;
            _cachedDiscoveryExpiresAt = now.AddSeconds(Math.Clamp(_options.JwksRefreshSeconds, 30, 86400));
            return _cachedDiscoveryJwksUrl;
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string?> FetchJsonAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private static bool LooksLikeJwks(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("keys", out var keys) &&
                   keys.ValueKind == JsonValueKind.Array;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryNormalizeHttpsUrl(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        normalized = uri.ToString();
        return true;
    }
}
