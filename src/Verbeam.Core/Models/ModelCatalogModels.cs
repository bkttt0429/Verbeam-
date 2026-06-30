namespace Verbeam.Core.Models;

public sealed record ModelCatalogDocument
{
    public int SchemaVersion { get; init; } = 1;
    public string CatalogVersion { get; init; } = string.Empty;
    public string MinAppVersion { get; init; } = string.Empty;
    public DateTimeOffset? ExpiresAt { get; init; }
    public IReadOnlyList<LlamaCppBinaryArtifact> LlamaCppBinaries { get; init; } = [];
    public IReadOnlyList<ModelCatalogEntry> Models { get; init; } = [];
}

public sealed record ModelCatalogEntry
{
    public required string Id { get; init; }
    public required string Provider { get; init; }
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public string Status { get; init; } = "candidate";
    public string Audience { get; init; } = "beginner";
    public required string Tier { get; init; }
    public required string RecommendedUse { get; init; }
    public int TierRank { get; init; }
    public double EstimatedMemoryGb { get; init; }
    public int DefaultContextTokens { get; init; } = 1024;
    public double KvCacheGbPer4096Tokens { get; init; } = 0.25;
    public double QualityScore { get; init; }
    public double LatencyScore { get; init; }
    public double ContextScore { get; init; }
    public double StabilityScore { get; init; }
    public IReadOnlyList<string> LanguagePairs { get; init; } = [];
    public IReadOnlyList<string> BestFor { get; init; } = [];
    public required ModelCatalogInstallPlan Install { get; init; }
    public ModelArtifact? Artifact { get; init; }
    public ModelRuntimeSet Runtimes { get; init; } = new();
    public ModelCatalogSource Source { get; init; } = new();
    public DateOnly? LastVerified { get; init; }
    public int Rank { get; init; } = 1000;
}

public sealed record ModelArtifact
{
    public string Format { get; init; } = string.Empty;
    public string Quant { get; init; } = string.Empty;
    public string DownloadUrl { get; init; } = string.Empty;
    public string HuggingFace { get; init; } = string.Empty;
    public string Filename { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public string License { get; init; } = string.Empty;
    public IReadOnlyList<string> LocalPaths { get; init; } = [];
}

public sealed record ModelRuntimeSet
{
    public ModelOllamaRuntime? Ollama { get; init; }
    public ModelLlamaCppRuntime? LlamaCpp { get; init; }
}

public sealed record ModelOllamaRuntime
{
    public string ModelName { get; init; } = string.Empty;
    public string InstallCommand { get; init; } = string.Empty;
}

public sealed record ModelLlamaCppRuntime
{
    public string ModelAlias { get; init; } = string.Empty;
    public string MinLlamaCppVersion { get; init; } = string.Empty;
    public string BinaryFlavor { get; init; } = "vulkan";
    public IReadOnlyList<string> RecommendedFallback { get; init; } = [];
    public IReadOnlyList<ModelLlamaCppProfile> Profiles { get; init; } = [];
    public ModelLlamaCppSampling Sampling { get; init; } = new();
}

public sealed record ModelLlamaCppProfile
{
    public string Name { get; init; } = "realtime-ocr";
    public string DisplayName { get; init; } = "Realtime OCR";
    public int ContextSize { get; init; } = 2048;
    public int Parallel { get; init; } = 1;
    public int GpuLayers { get; init; } = 999;
    public int BatchSize { get; init; } = 256;
    public int MicroBatchSize { get; init; } = 128;
    public bool? Fit { get; init; }
    public string? Reasoning { get; init; }
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
    public string? CacheTypeK { get; init; }
    public string? CacheTypeV { get; init; }
    public bool? FlashAttention { get; init; }
    public int? CacheReuse { get; init; }
}

public sealed record ModelLlamaCppSampling
{
    public double Temperature { get; init; } = 0;
    public int MaxTokens { get; init; } = 128;
    public int TimeoutSeconds { get; init; } = 20;
    public bool CachePrompt { get; init; } = true;
}

public sealed record LlamaCppBinaryArtifact
{
    public string Version { get; init; } = string.Empty;
    public string Flavor { get; init; } = string.Empty;
    public string Platform { get; init; } = string.Empty;
    public string Architecture { get; init; } = string.Empty;
    public string DownloadUrl { get; init; } = string.Empty;
    public string Filename { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public string ExecutableRelativePath { get; init; } = "llama-server.exe";

    /// <summary>
    /// Extra archives extracted into the same install directory as the main one.
    /// Used for backends whose runtime ships separately — notably the Windows CUDA
    /// build, where the cudart DLLs are a second zip without which llama-server.exe
    /// cannot start.
    /// </summary>
    public IReadOnlyList<LlamaCppArchiveRef> DependencyArchives { get; init; } = Array.Empty<LlamaCppArchiveRef>();
}

/// <summary>A supplemental archive (e.g. cudart) extracted alongside a binary artifact.</summary>
public sealed record LlamaCppArchiveRef
{
    public string DownloadUrl { get; init; } = string.Empty;
    public string Filename { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
}

public sealed record ModelCatalogInstallPlan
{
    public string Type { get; init; } = "manual";
    public string Command { get; init; } = string.Empty;
}

public sealed record ModelCatalogSource
{
    public string? Ollama { get; init; }
    public string? HuggingFace { get; init; }
}

public sealed record ModelCatalogStatus(
    string Source,
    string BuiltInPath,
    string CachePath,
    string UpdateUrl,
    bool RemoteRefreshEnabled,
    string CatalogVersion,
    DateTimeOffset? ExpiresAt,
    int ModelCount,
    DateTimeOffset LoadedAt,
    DateTimeOffset? LastRefreshAttemptAt,
    string LastRefreshMessage);

public sealed record ModelCatalogRefreshResult(
    bool Updated,
    string Source,
    string CatalogVersion,
    int ModelCount,
    DateTimeOffset LoadedAt,
    string Message);

public sealed record LlamaCppRuntimeEndpoint(
    Uri OpenAiBaseUrl,
    string ModelAlias,
    ModelLlamaCppSampling Sampling,
    bool CachePromptEnabled);

public sealed record LlamaCppRuntimeStatus(
    string Mode,
    string BaseUrl,
    bool IsManagedRunning,
    int? Port,
    string ModelAlias,
    DateTimeOffset? StartedAt,
    DateTimeOffset LastUsedAt,
    bool CachePromptEnabled,
    string LastError);

public sealed record LlamaCppArtifactStatus(
    string ModelId,
    string ModelAlias,
    string DisplayName,
    string Filename,
    string LocalPath,
    string DownloadUrl,
    long SizeBytes,
    string Sha256,
    bool Exists,
    bool SizeMatches,
    bool Sha256Matches,
    bool Verified,
    long? ActualSizeBytes,
    string ActualSha256,
    string Message);

public sealed record LlamaCppArtifactDownloadResult(
    string ModelId,
    string LocalPath,
    bool Verified,
    long BytesWritten,
    string Sha256,
    string Message);

public sealed record LlamaCppBinaryStatus(
    string Version,
    string Flavor,
    string Platform,
    string Architecture,
    string Filename,
    string ArchivePath,
    string ExecutablePath,
    string DownloadUrl,
    long SizeBytes,
    string Sha256,
    bool ArchiveExists,
    bool ArchiveSizeMatches,
    bool ArchiveSha256Matches,
    bool ExecutableExists,
    bool Ready,
    long? ActualSizeBytes,
    string ActualSha256,
    string Message);

public sealed record LlamaCppBinaryDownloadResult(
    string Version,
    string Flavor,
    string ExecutablePath,
    bool Ready,
    long BytesWritten,
    string Sha256,
    string Message);

public sealed record LlamaCppDownloadProgress(
    string Key,
    string Kind,
    string Id,
    string Status,
    long TotalBytes,
    long ReceivedBytes,
    double BytesPerSecond,
    string Message,
    DateTimeOffset UpdatedAt);
