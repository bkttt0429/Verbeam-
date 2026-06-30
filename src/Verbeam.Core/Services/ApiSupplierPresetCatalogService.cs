using System.Text.Json;
using Verbeam.Core.Models;
using Verbeam.Core.Options;

namespace Verbeam.Core.Services;

public sealed class ApiSupplierPresetCatalogService
{
    private static readonly ApiSupplierPreset CustomPreset = new()
    {
        Id = "custom",
        DisplayName = "Custom API",
        Category = "custom",
        Protocol = "openai_chat",
        BaseUrl = string.Empty,
        ModelsUrl = string.Empty,
        RequiresApiKey = false,
        DefaultModel = string.Empty
    };

    private readonly string _builtInPath;
    private readonly string _cachePath;
    private readonly ApiSupplierOptions _options;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private ApiSupplierPresetCatalogDocument _current = new()
    {
        CatalogVersion = "uninitialized",
        Presets = []
    };
    private string _source = "uninitialized";
    private DateTimeOffset _loadedAt = DateTimeOffset.UtcNow;
    private DateTimeOffset? _lastRefreshAttemptAt;
    private string _lastRefreshMessage = string.Empty;

    public ApiSupplierPresetCatalogService(
        string builtInPath,
        string cachePath,
        ApiSupplierOptions options,
        HttpClient httpClient)
    {
        _builtInPath = builtInPath;
        _cachePath = cachePath;
        _options = options;
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromSeconds(Math.Clamp(options.RefreshTimeoutSeconds, 1, 60));
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var builtIn = await new ApiSupplierPresetCatalogStore(_builtInPath).LoadAsync(cancellationToken);
        var source = "built-in";
        var selected = builtIn;

        if (File.Exists(_cachePath))
        {
            try
            {
                var cache = await new ApiSupplierPresetCatalogStore(_cachePath).LoadAsync(cancellationToken);
                if (IsPreferredCatalog(cache, builtIn))
                {
                    selected = cache;
                    source = "cache";
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
            {
                _lastRefreshMessage = $"Ignored invalid cached API supplier preset catalog: {ex.Message}";
            }
        }

        SetCurrent(selected, source);
    }

    public ApiSupplierPresetCatalogDocument GetCurrent() => _current;

    public ApiSupplierPreset GetRequiredPreset(string presetId)
    {
        if (string.IsNullOrWhiteSpace(presetId) ||
            presetId.Equals(CustomPreset.Id, StringComparison.OrdinalIgnoreCase))
        {
            return CustomPreset;
        }

        var preset = _current.Presets.FirstOrDefault(item =>
            item.Id.Equals(presetId, StringComparison.OrdinalIgnoreCase));
        return preset ?? throw new InvalidOperationException($"Unknown API supplier preset: {presetId}");
    }

    /// <summary>Balance template declared by a preset; empty when the preset is unknown or unset.</summary>
    public string GetBalanceTemplate(string presetId)
    {
        if (string.IsNullOrWhiteSpace(presetId))
        {
            return string.Empty;
        }
        var preset = _current.Presets.FirstOrDefault(item =>
            item.Id.Equals(presetId, StringComparison.OrdinalIgnoreCase));
        return preset?.BalanceTemplate ?? string.Empty;
    }

    public ApiSupplierCatalogStatus GetStatus()
        => new(
            _source,
            _builtInPath,
            _cachePath,
            _options.UpdateUrl.Trim(),
            _options.RemoteRefreshEnabled,
            _current.CatalogVersion,
            _current.ExpiresAt,
            _current.Presets.Count,
            _loadedAt,
            _lastRefreshAttemptAt,
            _lastRefreshMessage);

    public async Task<ApiSupplierCatalogRefreshResult> RefreshAsync(CancellationToken cancellationToken = default)
    {
        _lastRefreshAttemptAt = DateTimeOffset.UtcNow;

        if (!_options.RemoteRefreshEnabled)
        {
            _lastRefreshMessage = "Remote API supplier preset catalog refresh is disabled.";
            return BuildRefreshResult(updated: false, _lastRefreshMessage);
        }

        var updateUrl = _options.UpdateUrl.Trim();
        if (string.IsNullOrWhiteSpace(updateUrl))
        {
            _lastRefreshMessage = "Remote API supplier preset catalog updateUrl is not configured.";
            return BuildRefreshResult(updated: false, _lastRefreshMessage);
        }

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            using var response = await _httpClient.GetAsync(updateUrl, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var downloaded = await ApiSupplierPresetCatalogStore.LoadAsync(stream, updateUrl, cancellationToken);
            if (!IsPreferredCatalog(downloaded, _current))
            {
                _lastRefreshMessage = $"Downloaded API supplier preset catalog {downloaded.CatalogVersion} is older than current {_current.CatalogVersion}.";
                return BuildRefreshResult(updated: false, _lastRefreshMessage);
            }

            await new ApiSupplierPresetCatalogStore(_cachePath).SaveAsync(downloaded, cancellationToken);
            SetCurrent(downloaded, "remote-cache");
            _lastRefreshMessage = $"Updated API supplier preset catalog to {downloaded.CatalogVersion}.";
            return BuildRefreshResult(updated: true, _lastRefreshMessage);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidOperationException or TaskCanceledException)
        {
            _lastRefreshMessage = $"Could not refresh API supplier preset catalog: {ex.Message}";
            return BuildRefreshResult(updated: false, _lastRefreshMessage);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private void SetCurrent(ApiSupplierPresetCatalogDocument catalog, string source)
    {
        _current = EnsureCustomPreset(catalog);
        _source = source;
        _loadedAt = DateTimeOffset.UtcNow;
    }

    private static ApiSupplierPresetCatalogDocument EnsureCustomPreset(ApiSupplierPresetCatalogDocument catalog)
    {
        if (catalog.Presets.Any(item => item.Id.Equals(CustomPreset.Id, StringComparison.OrdinalIgnoreCase)))
        {
            return catalog;
        }

        return catalog with
        {
            Presets = catalog.Presets.Concat([CustomPreset]).ToArray()
        };
    }

    private ApiSupplierCatalogRefreshResult BuildRefreshResult(bool updated, string message)
        => new(
            updated,
            _source,
            _current.CatalogVersion,
            _current.Presets.Count,
            _loadedAt,
            message);

    private static bool IsPreferredCatalog(
        ApiSupplierPresetCatalogDocument candidate,
        ApiSupplierPresetCatalogDocument baseline)
    {
        if (candidate.SchemaVersion < baseline.SchemaVersion)
        {
            return false;
        }

        if (candidate.ExpiresAt is not null && candidate.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            return false;
        }

        return CompareCatalogVersions(candidate.CatalogVersion, baseline.CatalogVersion) >= 0;
    }

    private static int CompareCatalogVersions(string left, string right)
    {
        var leftDate = ParseLeadingDate(left);
        var rightDate = ParseLeadingDate(right);
        if (leftDate is not null && rightDate is not null)
        {
            var comparison = leftDate.Value.CompareTo(rightDate.Value);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static DateOnly? ParseLeadingDate(string value)
    {
        if (value.Length < 10)
        {
            return null;
        }

        return DateOnly.TryParseExact(value[..10], "yyyy-MM-dd", out var date)
            ? date
            : null;
    }
}
