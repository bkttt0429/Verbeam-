using System.Text.Json;
using Verbeam.Core.Models;

namespace Verbeam.Core.Services;

public sealed class ApiSupplierStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ApiSupplierStore(string path)
    {
        _path = path;
    }

    public async Task<IReadOnlyList<ApiSupplierProfile>> ListAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return (await LoadDocumentAsync(cancellationToken)).Suppliers;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ApiSupplierProfile?> GetAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var suppliers = await ListAsync(cancellationToken);
        return suppliers.FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<ApiSupplierProfile> UpsertAsync(
        ApiSupplierProfile profile,
        CancellationToken cancellationToken = default)
    {
        Validate(profile);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var document = await LoadDocumentAsync(cancellationToken);
            var profiles = document.Suppliers.ToList();
            var index = profiles.FindIndex(item => item.Id.Equals(profile.Id, StringComparison.OrdinalIgnoreCase));
            var now = DateTimeOffset.UtcNow;
            var saved = profile with
            {
                CreatedAt = index >= 0 ? profiles[index].CreatedAt : profile.CreatedAt,
                UpdatedAt = now
            };

            if (index >= 0)
            {
                profiles[index] = saved;
            }
            else
            {
                profiles.Add(saved);
            }

            await SaveDocumentAsync(new ApiSupplierStoreDocument { Suppliers = profiles }, cancellationToken);
            return saved;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> DeleteAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var document = await LoadDocumentAsync(cancellationToken);
            var profiles = document.Suppliers
                .Where(item => !item.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (profiles.Length == document.Suppliers.Count)
            {
                return false;
            }

            await SaveDocumentAsync(new ApiSupplierStoreDocument { Suppliers = profiles }, cancellationToken);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Maps a stored profile to its UI DTO. <paramref name="presetBalanceTemplate"/> is the balance
    /// template declared by the supplier's preset (empty when unknown); the per-supplier override on the
    /// profile wins. A non-empty, non-"off" effective template means the card may show a balance row.
    /// </summary>
    public static ApiSupplierProfileResponse ToResponse(
        ApiSupplierProfile profile,
        string presetBalanceTemplate = "")
    {
        var effectiveTemplate = ResolveBalanceTemplate(profile, presetBalanceTemplate);
        var supportsBalance = effectiveTemplate.Length > 0
            && !effectiveTemplate.Equals("off", StringComparison.OrdinalIgnoreCase);
        return new ApiSupplierProfileResponse(
            profile.Id,
            profile.PresetId,
            profile.Name,
            profile.Protocol,
            profile.BaseUrl,
            profile.ModelsUrl,
            !string.IsNullOrWhiteSpace(profile.ApiKeyRef),
            profile.ActiveModel,
            profile.ModelCatalog,
            profile.LastHealth,
            profile.CreatedAt,
            profile.UpdatedAt)
        {
            SupportsBalance = supportsBalance,
            BalanceTemplate = profile.BalanceTemplate,
            BalanceUrl = profile.BalanceUrl,
            BalanceAutoIntervalMinutes = profile.BalanceAutoIntervalMinutes,
            LastBalance = profile.LastBalance
        };
    }

    /// <summary>Per-supplier balance template override wins over the preset default.</summary>
    public static string ResolveBalanceTemplate(ApiSupplierProfile profile, string presetBalanceTemplate)
    {
        var profileTemplate = (profile.BalanceTemplate ?? string.Empty).Trim();
        if (profileTemplate.Length > 0)
        {
            return profileTemplate;
        }
        return (presetBalanceTemplate ?? string.Empty).Trim();
    }

    private async Task<ApiSupplierStoreDocument> LoadDocumentAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return new ApiSupplierStoreDocument();
        }

        await using var stream = File.OpenRead(_path);
        var document = await JsonSerializer.DeserializeAsync<ApiSupplierStoreDocument>(
            stream,
            JsonOptions,
            cancellationToken);
        return document ?? new ApiSupplierStoreDocument();
    }

    private async Task SaveDocumentAsync(
        ApiSupplierStoreDocument document,
        CancellationToken cancellationToken)
    {
        foreach (var profile in document.Suppliers)
        {
            Validate(profile);
        }

        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = _path + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken);
        }

        if (File.Exists(_path))
        {
            File.Replace(tempPath, _path, destinationBackupFileName: null);
        }
        else
        {
            File.Move(tempPath, _path);
        }
    }

    private static void Validate(ApiSupplierProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Id) ||
            string.IsNullOrWhiteSpace(profile.PresetId) ||
            string.IsNullOrWhiteSpace(profile.Name) ||
            string.IsNullOrWhiteSpace(profile.Protocol) ||
            string.IsNullOrWhiteSpace(profile.BaseUrl))
        {
            throw new InvalidOperationException("API supplier profile requires id, presetId, name, protocol, and baseUrl.");
        }

        if (!profile.Protocol.Equals("openai_chat", StringComparison.OrdinalIgnoreCase) &&
            !profile.Protocol.Equals("anthropic", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Unsupported API supplier protocol: {profile.Protocol}");
        }

        if (!Uri.TryCreate(profile.BaseUrl, UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException($"API supplier baseUrl must be an absolute HTTP URL: {profile.Id}");
        }

        if (!string.IsNullOrWhiteSpace(profile.ModelsUrl) &&
            (!Uri.TryCreate(profile.ModelsUrl, UriKind.Absolute, out var modelsUri) ||
             modelsUri.Scheme is not ("http" or "https")))
        {
            throw new InvalidOperationException($"API supplier modelsUrl must be an absolute HTTP URL: {profile.Id}");
        }
    }

    private sealed record ApiSupplierStoreDocument
    {
        public int SchemaVersion { get; init; } = 1;
        public IReadOnlyList<ApiSupplierProfile> Suppliers { get; init; } = [];
    }
}
