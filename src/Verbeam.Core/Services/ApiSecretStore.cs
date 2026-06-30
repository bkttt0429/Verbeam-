using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Verbeam.Core.Services;

public sealed class ApiSecretStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ApiSecretStore(string path)
    {
        _path = path;
    }

    public async Task<string> SaveApiKeyAsync(
        string supplierId,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(supplierId))
        {
            throw new InvalidOperationException("Supplier id is required before saving an API key.");
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("API key cannot be blank.");
        }

        var keyRef = BuildKeyRef(supplierId);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var document = await LoadDocumentAsync(cancellationToken);
            var secrets = new Dictionary<string, string>(document.Secrets, StringComparer.OrdinalIgnoreCase)
            {
                [keyRef] = Protect(apiKey)
            };
            await SaveDocumentAsync(new ApiSecretStoreDocument { Secrets = secrets }, cancellationToken);
            return keyRef;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> GetApiKeyAsync(
        string keyRef,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(keyRef))
        {
            return string.Empty;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var document = await LoadDocumentAsync(cancellationToken);
            return document.Secrets.TryGetValue(keyRef, out var protectedValue)
                ? Unprotect(protectedValue)
                : string.Empty;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(
        string keyRef,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(keyRef))
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var document = await LoadDocumentAsync(cancellationToken);
            if (!document.Secrets.ContainsKey(keyRef))
            {
                return;
            }

            var secrets = new Dictionary<string, string>(document.Secrets, StringComparer.OrdinalIgnoreCase);
            secrets.Remove(keyRef);
            await SaveDocumentAsync(new ApiSecretStoreDocument { Secrets = secrets }, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public static string BuildKeyRef(string supplierId)
        => $"verbeam://secret/api-suppliers/{supplierId}/api-key";

    private async Task<ApiSecretStoreDocument> LoadDocumentAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return new ApiSecretStoreDocument();
        }

        await using var stream = File.OpenRead(_path);
        var document = await JsonSerializer.DeserializeAsync<ApiSecretStoreDocument>(
            stream,
            JsonOptions,
            cancellationToken);
        return document ?? new ApiSecretStoreDocument();
    }

    private async Task SaveDocumentAsync(
        ApiSecretStoreDocument document,
        CancellationToken cancellationToken)
    {
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

    private static string Protect(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var protectedBytes = ProtectedData.Protect(bytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    private static string Unprotect(string value)
    {
        var protectedBytes = Convert.FromBase64String(value);
        var bytes = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(bytes);
    }

    private sealed record ApiSecretStoreDocument
    {
        public int SchemaVersion { get; init; } = 1;
        public IReadOnlyDictionary<string, string> Secrets { get; init; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }
}
