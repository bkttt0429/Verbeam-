using System.Text.Json;
using Verbeam.Core.Models;
using Verbeam.Core.Options;

namespace Verbeam.Core.Services;

public sealed class ModelCatalogService
{
    private readonly string _builtInPath;
    private readonly string _cachePath;
    private readonly string _updateUrl;
    private readonly bool _remoteRefreshEnabled;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private ModelCatalogDocument _current = new()
    {
        CatalogVersion = "uninitialized",
        Models = []
    };
    private string _source = "uninitialized";
    private DateTimeOffset _loadedAt = DateTimeOffset.UtcNow;
    private DateTimeOffset? _lastRefreshAttemptAt;
    private string _lastRefreshMessage = string.Empty;

    public ModelCatalogService(
        string builtInPath,
        string cachePath,
        ModelCatalogOptions options,
        HttpClient httpClient)
    {
        _builtInPath = builtInPath;
        _cachePath = cachePath;
        _updateUrl = options.UpdateUrl.Trim();
        _remoteRefreshEnabled = options.RemoteRefreshEnabled;
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromSeconds(Math.Clamp(options.RefreshTimeoutSeconds, 1, 60));
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var builtIn = await new ModelCatalogStore(_builtInPath).LoadAsync(cancellationToken);
        var source = "built-in";
        var selected = builtIn;

        if (File.Exists(_cachePath))
        {
            try
            {
                var cache = await new ModelCatalogStore(_cachePath).LoadAsync(cancellationToken);
                if (IsPreferredCatalog(cache, builtIn))
                {
                    selected = cache;
                    source = "cache";
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
            {
                _lastRefreshMessage = $"Ignored invalid cached model catalog: {ex.Message}";
            }
        }

        SetCurrent(selected, source);
    }

    public ModelCatalogDocument GetCurrent() => _current;

    public ModelCatalogStatus GetStatus()
        => new(
            _source,
            _builtInPath,
            _cachePath,
            _updateUrl,
            _remoteRefreshEnabled,
            _current.CatalogVersion,
            _current.ExpiresAt,
            _current.Models.Count,
            _loadedAt,
            _lastRefreshAttemptAt,
            _lastRefreshMessage);

    public async Task<ModelCatalogRefreshResult> RefreshAsync(CancellationToken cancellationToken = default)
    {
        _lastRefreshAttemptAt = DateTimeOffset.UtcNow;

        if (!_remoteRefreshEnabled)
        {
            _lastRefreshMessage = "Remote model catalog refresh is disabled.";
            return BuildRefreshResult(updated: false, _lastRefreshMessage);
        }

        if (string.IsNullOrWhiteSpace(_updateUrl))
        {
            _lastRefreshMessage = "Remote model catalog updateUrl is not configured.";
            return BuildRefreshResult(updated: false, _lastRefreshMessage);
        }

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            using var response = await _httpClient.GetAsync(_updateUrl, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var downloaded = await ModelCatalogStore.LoadAsync(stream, _updateUrl, cancellationToken);
            if (!IsPreferredCatalog(downloaded, _current))
            {
                _lastRefreshMessage = $"Downloaded model catalog {downloaded.CatalogVersion} is older than current {_current.CatalogVersion}.";
                return BuildRefreshResult(updated: false, _lastRefreshMessage);
            }

            await new ModelCatalogStore(_cachePath).SaveAsync(downloaded, cancellationToken);
            SetCurrent(downloaded, "remote-cache");
            _lastRefreshMessage = $"Updated model catalog to {downloaded.CatalogVersion}.";
            return BuildRefreshResult(updated: true, _lastRefreshMessage);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidOperationException or TaskCanceledException)
        {
            _lastRefreshMessage = $"Could not refresh model catalog: {ex.Message}";
            return BuildRefreshResult(updated: false, _lastRefreshMessage);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private void SetCurrent(ModelCatalogDocument catalog, string source)
    {
        _current = catalog;
        _source = source;
        _loadedAt = DateTimeOffset.UtcNow;
    }

    private ModelCatalogRefreshResult BuildRefreshResult(bool updated, string message)
        => new(
            updated,
            _source,
            _current.CatalogVersion,
            _current.Models.Count,
            _loadedAt,
            message);

    private static bool IsPreferredCatalog(ModelCatalogDocument candidate, ModelCatalogDocument baseline)
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
