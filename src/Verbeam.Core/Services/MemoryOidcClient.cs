using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Verbeam.Core.Models;
using Verbeam.Core.Options;

namespace Verbeam.Core.Services;

public interface IMemoryOidcClient
{
    bool IsEnabled { get; }

    Task<MemoryOidcLoginStartResult?> StartLoginAsync(CancellationToken cancellationToken = default);

    Task<MemoryOidcTokenResult?> ExchangeCodeAsync(
        string code,
        string state,
        CancellationToken cancellationToken = default);

    Task<MemoryOidcTokenResult?> RefreshAsync(
        string refreshToken,
        CancellationToken cancellationToken = default);
}

public sealed class MemoryOidcClient : IMemoryOidcClient
{
    private readonly MemoryOidcOptions _options;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _discoveryGate = new(1, 1);
    private readonly ConcurrentDictionary<string, LoginState> _states = new(StringComparer.Ordinal);
    private DiscoveryDocument? _cachedDiscovery;
    private DateTimeOffset _cachedDiscoveryExpiresAt;

    public MemoryOidcClient(MemoryOidcOptions options, HttpClient httpClient)
    {
        _options = options;
        _httpClient = httpClient;
    }

    public bool IsEnabled
        => _options.Enabled &&
           !string.IsNullOrWhiteSpace(_options.ClientId) &&
           !string.IsNullOrWhiteSpace(_options.RedirectUri);

    public async Task<MemoryOidcLoginStartResult?> StartLoginAsync(
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled ||
            !TryNormalizeHttpsUrl(_options.RedirectUri, out var redirectUri))
        {
            return null;
        }

        var authorizationEndpoint = await ResolveAuthorizationEndpointAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(authorizationEndpoint))
        {
            return null;
        }

        CleanupExpiredStates();
        var state = NewBase64UrlRandom(32);
        var codeVerifier = NewBase64UrlRandom(32);
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(_options.StateTtlSeconds, 60, 1800));
        _states[state] = new LoginState(codeVerifier, expiresAt);

        var authorizationUrl = AppendQuery(authorizationEndpoint, new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = _options.ClientId.Trim(),
            ["redirect_uri"] = redirectUri,
            ["scope"] = BuildScope(),
            ["state"] = state,
            ["code_challenge"] = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier))),
            ["code_challenge_method"] = "S256"
        });

        return new MemoryOidcLoginStartResult(true, authorizationUrl, state, expiresAt);
    }

    public async Task<MemoryOidcTokenResult?> ExchangeCodeAsync(
        string code,
        string state,
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled ||
            string.IsNullOrWhiteSpace(code) ||
            string.IsNullOrWhiteSpace(state) ||
            !_states.TryRemove(state.Trim(), out var loginState) ||
            loginState.ExpiresAt <= DateTimeOffset.UtcNow ||
            !TryNormalizeHttpsUrl(_options.RedirectUri, out var redirectUri))
        {
            return null;
        }

        var tokenEndpoint = await ResolveTokenEndpointAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(tokenEndpoint))
        {
            return null;
        }

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = _options.ClientId.Trim(),
            ["code"] = code.Trim(),
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = loginState.CodeVerifier
        };
        AddClientSecret(form);
        return await SendTokenRequestAsync(tokenEndpoint, form, cancellationToken);
    }

    public async Task<MemoryOidcTokenResult?> RefreshAsync(
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(refreshToken))
        {
            return null;
        }

        var tokenEndpoint = await ResolveTokenEndpointAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(tokenEndpoint))
        {
            return null;
        }

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = _options.ClientId.Trim(),
            ["refresh_token"] = refreshToken.Trim()
        };
        AddClientSecret(form);
        return await SendTokenRequestAsync(tokenEndpoint, form, cancellationToken);
    }

    private async Task<string?> ResolveAuthorizationEndpointAsync(CancellationToken cancellationToken)
    {
        if (TryNormalizeHttpsUrl(_options.AuthorizationEndpoint, out var authorizationEndpoint))
        {
            return authorizationEndpoint;
        }

        var discovery = await ResolveDiscoveryAsync(cancellationToken);
        return discovery?.AuthorizationEndpoint;
    }

    private async Task<string?> ResolveTokenEndpointAsync(CancellationToken cancellationToken)
    {
        if (TryNormalizeHttpsUrl(_options.TokenEndpoint, out var tokenEndpoint))
        {
            return tokenEndpoint;
        }

        var discovery = await ResolveDiscoveryAsync(cancellationToken);
        return discovery?.TokenEndpoint;
    }

    private async Task<DiscoveryDocument?> ResolveDiscoveryAsync(CancellationToken cancellationToken)
    {
        if (!TryNormalizeHttpsUrl(_options.DiscoveryUrl, out var discoveryUrl))
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        if (_cachedDiscovery is not null && _cachedDiscoveryExpiresAt > now)
        {
            return _cachedDiscovery;
        }

        await _discoveryGate.WaitAsync(cancellationToken);
        try
        {
            now = DateTimeOffset.UtcNow;
            if (_cachedDiscovery is not null && _cachedDiscoveryExpiresAt > now)
            {
                return _cachedDiscovery;
            }

            using var response = await _httpClient.GetAsync(discoveryUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(json);
            if (!TryGetHttpsString(document.RootElement, "authorization_endpoint", out var authorizationEndpoint) ||
                !TryGetHttpsString(document.RootElement, "token_endpoint", out var tokenEndpoint))
            {
                return null;
            }

            _cachedDiscovery = new DiscoveryDocument(authorizationEndpoint, tokenEndpoint);
            _cachedDiscoveryExpiresAt = now.AddSeconds(Math.Clamp(_options.StateTtlSeconds, 60, 1800));
            return _cachedDiscovery;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            _discoveryGate.Release();
        }
    }

    private async Task<MemoryOidcTokenResult?> SendTokenRequestAsync(
        string tokenEndpoint,
        IReadOnlyDictionary<string, string> form,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.PostAsync(
                tokenEndpoint,
                new FormUrlEncodedContent(form),
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(json);
            var accessToken = GetString(document.RootElement, "access_token");
            var idToken = GetString(document.RootElement, "id_token");
            if (string.IsNullOrWhiteSpace(accessToken) && string.IsNullOrWhiteSpace(idToken))
            {
                return null;
            }

            return new MemoryOidcTokenResult(
                FirstNonBlank(GetString(document.RootElement, "token_type"), "Bearer") ?? "Bearer",
                accessToken,
                idToken,
                GetString(document.RootElement, "refresh_token"),
                TryGetInt(document.RootElement, "expires_in", out var expiresIn) ? expiresIn : null);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void AddClientSecret(IDictionary<string, string> form)
    {
        if (!string.IsNullOrWhiteSpace(_options.ClientSecret))
        {
            form["client_secret"] = _options.ClientSecret.Trim();
        }
    }

    private string BuildScope()
    {
        var scopes = _options.Scopes
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return scopes.Length == 0 ? "openid" : string.Join(' ', scopes);
    }

    private void CleanupExpiredStates()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var item in _states)
        {
            if (item.Value.ExpiresAt <= now)
            {
                _states.TryRemove(item.Key, out _);
            }
        }
    }

    private static string AppendQuery(string url, IReadOnlyDictionary<string, string> values)
    {
        var separator = url.Contains("?", StringComparison.Ordinal) ? "&" : "?";
        var query = string.Join("&", values.Select(item =>
            $"{Uri.EscapeDataString(item.Key)}={Uri.EscapeDataString(item.Value)}"));
        return url + separator + query;
    }

    private static bool TryGetHttpsString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        return TryNormalizeHttpsUrl(property.GetString(), out value);
    }

    private static string GetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) &&
           property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;

    private static bool TryGetInt(JsonElement element, string propertyName, out int value)
    {
        value = 0;
        return element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetInt32(out value);
    }

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string NewBase64UrlRandom(int byteCount)
        => Base64UrlEncode(RandomNumberGenerator.GetBytes(byteCount));

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

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

    private sealed record LoginState(string CodeVerifier, DateTimeOffset ExpiresAt);

    private sealed record DiscoveryDocument(string AuthorizationEndpoint, string TokenEndpoint);
}
