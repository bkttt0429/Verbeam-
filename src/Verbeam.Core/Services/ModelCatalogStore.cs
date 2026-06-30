using System.Text.Json;
using Verbeam.Core.Models;

namespace Verbeam.Core.Services;

public sealed class ModelCatalogStore
{
    private readonly string _path;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public ModelCatalogStore(string path)
    {
        _path = path;
    }

    public string Path => _path;

    public async Task<ModelCatalogDocument> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            throw new FileNotFoundException($"Model catalog was not found: {_path}", _path);
        }

        await using var stream = File.OpenRead(_path);
        return await LoadAsync(stream, _path, cancellationToken);
    }

    public async Task SaveAsync(
        ModelCatalogDocument catalog,
        CancellationToken cancellationToken = default)
    {
        Validate(catalog);

        var directory = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = _path + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, catalog, JsonOptions, cancellationToken);
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

    public static async Task<ModelCatalogDocument> LoadAsync(
        Stream stream,
        string sourceName,
        CancellationToken cancellationToken = default)
    {
        var catalog = await JsonSerializer.DeserializeAsync<ModelCatalogDocument>(
            stream,
            JsonOptions,
            cancellationToken);

        if (catalog is null)
        {
            throw new InvalidOperationException($"Model catalog is empty or invalid: {sourceName}");
        }

        Validate(catalog);
        return catalog;
    }

    public static void Validate(ModelCatalogDocument catalog)
    {
        if (catalog.SchemaVersion <= 0)
        {
            throw new InvalidOperationException("Model catalog schemaVersion must be positive.");
        }

        if (string.IsNullOrWhiteSpace(catalog.CatalogVersion))
        {
            throw new InvalidOperationException("Model catalog catalogVersion is required.");
        }

        if (catalog.Models.Count == 0)
        {
            throw new InvalidOperationException("Model catalog must contain at least one model.");
        }

        ValidateLlamaCppBinaries(catalog);

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in catalog.Models)
        {
            if (string.IsNullOrWhiteSpace(model.Id) ||
                string.IsNullOrWhiteSpace(model.Provider) ||
                string.IsNullOrWhiteSpace(model.Name) ||
                string.IsNullOrWhiteSpace(model.DisplayName))
            {
                throw new InvalidOperationException("Each model catalog entry requires id, provider, name, and displayName.");
            }

            if (!ids.Add(model.Id))
            {
                throw new InvalidOperationException($"Duplicate model catalog id: {model.Id}");
            }

            if (model.EstimatedMemoryGb <= 0 ||
                model.QualityScore is < 0 or > 1 ||
                model.LatencyScore is < 0 or > 1 ||
                model.ContextScore is < 0 or > 1 ||
                model.StabilityScore is < 0 or > 1)
            {
                throw new InvalidOperationException($"Model catalog entry has invalid scores or memory estimate: {model.Id}");
            }

            ValidateArtifact(model);
            ValidateLlamaCppRuntime(model);
        }
    }

    private static void ValidateLlamaCppBinaries(ModelCatalogDocument catalog)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var binary in catalog.LlamaCppBinaries)
        {
            if (string.IsNullOrWhiteSpace(binary.Version) ||
                string.IsNullOrWhiteSpace(binary.Flavor) ||
                string.IsNullOrWhiteSpace(binary.Platform) ||
                string.IsNullOrWhiteSpace(binary.Architecture) ||
                string.IsNullOrWhiteSpace(binary.DownloadUrl) ||
                string.IsNullOrWhiteSpace(binary.Filename) ||
                string.IsNullOrWhiteSpace(binary.ExecutableRelativePath))
            {
                throw new InvalidOperationException("llama.cpp binary artifacts require version, flavor, platform, architecture, downloadUrl, filename, and executableRelativePath.");
            }

            RejectAuto(binary.Version, "llama-cpp-binary", "version");
            RejectAuto(binary.Flavor, "llama-cpp-binary", "flavor");
            RejectAuto(binary.Platform, "llama-cpp-binary", "platform");
            RejectAuto(binary.Architecture, "llama-cpp-binary", "architecture");
            RejectAuto(binary.Filename, "llama-cpp-binary", "filename");
            RejectAuto(binary.ExecutableRelativePath, "llama-cpp-binary", "executableRelativePath");

            // Empty sha256 = trust-on-first-use (size-only verification) for
            // cross-platform entries whose hash was not pre-pinned; a non-empty
            // value must still be a proper digest.
            if (!string.IsNullOrEmpty(binary.Sha256) && !IsSha256(binary.Sha256))
            {
                throw new InvalidOperationException($"llama.cpp binary artifact sha256 must be empty or a 64 character hex digest: {binary.Version}/{binary.Flavor}/{binary.Platform}/{binary.Architecture}");
            }

            if (binary.SizeBytes <= 0)
            {
                throw new InvalidOperationException($"llama.cpp binary artifact sizeBytes must be positive: {binary.Version}/{binary.Flavor}/{binary.Platform}/{binary.Architecture}");
            }

            foreach (var dependency in binary.DependencyArchives)
            {
                if (string.IsNullOrWhiteSpace(dependency.DownloadUrl) ||
                    string.IsNullOrWhiteSpace(dependency.Filename) ||
                    dependency.SizeBytes <= 0)
                {
                    throw new InvalidOperationException($"llama.cpp dependency archive requires downloadUrl, filename, and positive sizeBytes: {binary.Version}/{binary.Flavor}");
                }

                if (!string.IsNullOrEmpty(dependency.Sha256) && !IsSha256(dependency.Sha256))
                {
                    throw new InvalidOperationException($"llama.cpp dependency archive sha256 must be empty or a 64 character hex digest: {dependency.Filename}");
                }
            }

            var id = $"{binary.Version}/{binary.Flavor}/{binary.Platform}/{binary.Architecture}";
            if (!ids.Add(id))
            {
                throw new InvalidOperationException($"Duplicate llama.cpp binary artifact: {id}");
            }
        }
    }

    private static void ValidateArtifact(ModelCatalogEntry model)
    {
        if (model.Artifact is null)
        {
            return;
        }

        var artifact = model.Artifact;
        if (string.IsNullOrWhiteSpace(artifact.Format) ||
            string.IsNullOrWhiteSpace(artifact.Quant) ||
            string.IsNullOrWhiteSpace(artifact.Filename) ||
            string.IsNullOrWhiteSpace(artifact.License))
        {
            throw new InvalidOperationException($"Model catalog artifact requires format, quant, filename, sha256, and license: {model.Id}");
        }

        if (!IsSha256(artifact.Sha256))
        {
            throw new InvalidOperationException($"Model catalog artifact sha256 must be a 64 character hex digest: {model.Id}");
        }

        if (artifact.SizeBytes <= 0)
        {
            throw new InvalidOperationException($"Model catalog artifact sizeBytes must be positive: {model.Id}");
        }

        foreach (var localPath in artifact.LocalPaths ?? [])
        {
            if (string.IsNullOrWhiteSpace(localPath))
            {
                throw new InvalidOperationException($"Model catalog artifact localPaths cannot contain empty values: {model.Id}");
            }

            RejectAuto(localPath, model.Id, "artifact.localPaths");
        }
    }

    private static void ValidateLlamaCppRuntime(ModelCatalogEntry model)
    {
        var runtime = model.Runtimes.LlamaCpp;
        if (runtime is null)
        {
            return;
        }

        if (model.Artifact is null)
        {
            throw new InvalidOperationException($"llama.cpp runtime requires a GGUF artifact: {model.Id}");
        }

        if (!model.Artifact.Format.Equals("gguf", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"llama.cpp runtime only supports GGUF artifacts: {model.Id}");
        }

        if (string.IsNullOrWhiteSpace(runtime.MinLlamaCppVersion) ||
            string.IsNullOrWhiteSpace(runtime.BinaryFlavor) ||
            runtime.Profiles.Count == 0)
        {
            throw new InvalidOperationException($"llama.cpp runtime requires minLlamaCppVersion, binaryFlavor, and profiles: {model.Id}");
        }

        RejectAuto(runtime.ModelAlias, model.Id, "modelAlias");
        RejectAuto(runtime.MinLlamaCppVersion, model.Id, "minLlamaCppVersion");
        RejectAuto(runtime.BinaryFlavor, model.Id, "binaryFlavor");
        ValidateRecommendedFallback(model.Id, runtime.RecommendedFallback);

        if (runtime.Sampling.Temperature < 0 ||
            runtime.Sampling.MaxTokens <= 0 ||
            runtime.Sampling.TimeoutSeconds <= 0)
        {
            throw new InvalidOperationException($"llama.cpp sampling has invalid values: {model.Id}");
        }

        foreach (var profile in runtime.Profiles)
        {
            if (string.IsNullOrWhiteSpace(profile.Name) ||
                profile.ContextSize <= 0 ||
                profile.Parallel <= 0 ||
                profile.GpuLayers < 0 ||
                profile.BatchSize <= 0 ||
                profile.MicroBatchSize <= 0)
            {
                throw new InvalidOperationException($"llama.cpp profile has invalid values: {model.Id}/{profile.Name}");
            }

            RejectAuto(profile.Name, model.Id, "profile.name");
            RejectAuto(profile.DisplayName, model.Id, "profile.displayName");
            RejectAuto(profile.CacheTypeK, model.Id, "profile.cacheTypeK");
            RejectAuto(profile.CacheTypeV, model.Id, "profile.cacheTypeV");
            RejectAuto(profile.Reasoning, model.Id, "profile.reasoning");

            foreach (var pair in profile.Environment ?? new Dictionary<string, string>())
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value))
                {
                    throw new InvalidOperationException($"llama.cpp profile environment cannot contain empty keys or values: {model.Id}/{profile.Name}");
                }

                RejectAuto(pair.Key, model.Id, "profile.environment");
                RejectAuto(pair.Value, model.Id, "profile.environment");
            }

            if (!string.IsNullOrWhiteSpace(profile.CacheTypeV) &&
                profile.CacheTypeV.StartsWith('q') &&
                profile.FlashAttention != true)
            {
                throw new InvalidOperationException($"Quantized V cache requires flashAttention=true: {model.Id}/{profile.Name}");
            }
        }
    }

    private static void ValidateRecommendedFallback(
        string modelId,
        IReadOnlyList<string> fallbacks)
    {
        foreach (var fallback in fallbacks)
        {
            if (string.IsNullOrWhiteSpace(fallback))
            {
                throw new InvalidOperationException($"llama.cpp recommendedFallback cannot contain blank values: {modelId}");
            }

            RejectAuto(fallback, modelId, "recommendedFallback");
            if (!fallback.StartsWith("llama-cpp:", StringComparison.OrdinalIgnoreCase) &&
                !fallback.StartsWith("ollama:", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"llama.cpp recommendedFallback must name an explicit runtime path: {modelId}/{fallback}");
            }
        }
    }

    private static void RejectAuto(string? value, string modelId, string field)
    {
        if (value is not null && value.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Model catalog field cannot use auto: {modelId}/{field}");
        }
    }

    private static bool IsSha256(string value)
        => value.Length == 64 && value.All(Uri.IsHexDigit);
}
