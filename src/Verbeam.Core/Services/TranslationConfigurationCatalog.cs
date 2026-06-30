using Verbeam.Core.Models;
using Verbeam.Core.Options;
using System.Runtime.InteropServices;

namespace Verbeam.Core.Services;

public sealed class TranslationConfigurationCatalog
{
    private static readonly IReadOnlyList<LanguageDefinition> LanguageDefinitions =
    [
        new(
            "ja",
            "Japanese",
            "日本語",
            "Japanese",
            IsOcrSupported: true,
            IsSpeechSupported: true,
            ["jp", "ja-jp"]),
        new(
            "en",
            "English",
            "English",
            "English",
            IsOcrSupported: true,
            IsSpeechSupported: true,
            ["en-us", "en-gb"]),
        new(
            "zh-TW",
            "Traditional Chinese (Taiwan)",
            "繁體中文（台灣）",
            "Traditional Chinese (Taiwan) / 繁體中文（台灣）, using Traditional Chinese characters only and never Simplified Chinese",
            IsOcrSupported: true,
            IsSpeechSupported: true,
            ["zh-hant", "traditional chinese", "traditional chinese taiwan"]),
        new(
            "zh-CN",
            "Simplified Chinese (China)",
            "简体中文（中国）",
            "Simplified Chinese (China), using Simplified Chinese characters",
            IsOcrSupported: true,
            IsSpeechSupported: true,
            ["zh-hans", "simplified chinese"]),
        new(
            "zh",
            "Chinese",
            "中文",
            "Chinese",
            IsOcrSupported: true,
            IsSpeechSupported: true,
            []),
        new(
            "ko",
            "Korean",
            "한국어",
            "Korean",
            IsOcrSupported: true,
            IsSpeechSupported: true,
            ["ko-kr"])
    ];

    private readonly VerbeamOptions _options;
    private readonly Func<ModelCatalogDocument> _getModelCatalog;

    public TranslationConfigurationCatalog(VerbeamOptions options)
        : this(options, CreateFallbackModelCatalog())
    {
    }

    public TranslationConfigurationCatalog(VerbeamOptions options, ModelCatalogDocument modelCatalog)
        : this(options, () => modelCatalog)
    {
    }

    public TranslationConfigurationCatalog(VerbeamOptions options, ModelCatalogService modelCatalogService)
        : this(options, modelCatalogService.GetCurrent)
    {
    }

    private TranslationConfigurationCatalog(VerbeamOptions options, Func<ModelCatalogDocument> getModelCatalog)
    {
        _options = options;
        _getModelCatalog = getModelCatalog;
    }

    public IReadOnlyList<TranslationLanguageDescriptor> ListLanguages()
    {
        return LanguageDefinitions
            .Select(language => new TranslationLanguageDescriptor(
                language.Code,
                language.DisplayName,
                language.NativeName,
                language.PromptName,
                IsLanguageMatch(language.Code, _options.DefaultSource),
                IsLanguageMatch(language.Code, _options.DefaultTarget),
                language.IsOcrSupported,
                language.IsSpeechSupported))
            .ToArray();
    }

    public IReadOnlyList<TranslationModelDescriptor> EnrichModels(
        string provider,
        IReadOnlyList<TranslationModelDescriptor> models)
    {
        var recommendations = BuildProviderRecommendations(provider)
            .ToDictionary(recommendation => recommendation.Name, StringComparer.OrdinalIgnoreCase);
        if (recommendations.Count == 0)
        {
            return models
                .Select(model => model with
                {
                    IsRecommended = model.IsDefault,
                    RecommendationReason = model.IsDefault
                        ? "Provider default for this runtime."
                        : model.RecommendationReason,
                    RecommendedUse = model.IsDefault ? "provider default" : model.RecommendedUse
                })
                .ToArray();
        }

        var values = models.ToDictionary(model => model.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var recommendation in recommendations.Values)
        {
            if (values.TryGetValue(recommendation.Name, out var existing))
            {
                values[recommendation.Name] = existing with
                {
                    DisplayName = HasGenericDisplayName(existing.DisplayName, existing.Name)
                        ? recommendation.DisplayName
                        : existing.DisplayName,
                    IsRecommended = true,
                    RecommendationReason = recommendation.Reason,
                    RecommendedUse = recommendation.UseCase
                };
                continue;
            }

            values[recommendation.Name] = new TranslationModelDescriptor(
                provider,
                recommendation.Name,
                recommendation.DisplayName,
                IsDefault: IsConfiguredDefault(provider, recommendation.Name),
                IsInstalled: false,
                "recommended",
                IsRecommended: true,
                recommendation.Reason,
                recommendation.UseCase);
        }

        return values.Values
            .OrderByDescending(model => model.IsDefault)
            .ThenByDescending(model => model.IsRecommended)
            .ThenByDescending(model => model.IsInstalled)
            .ThenBy(model => RecommendationRank(model.Name, recommendations))
            .ThenBy(model => model.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public ComputerModelRecommendation RecommendModelForComputer(
        ComputerModelRecommendationRequest request,
        string provider,
        IReadOnlyList<TranslationModelDescriptor> models)
    {
        var profile = BuildHardwareProfile(request);
        var workload = NormalizeChoice(request.Workload, "realtime_overlay");
        var preference = NormalizeChoice(request.Preference, "balanced");
        var source = NormalizeCode(Pick(request.Source, _options.DefaultSource));
        var target = NormalizeCode(Pick(request.Target, _options.DefaultTarget));
        var contextTokens = Math.Max(128, request.ContextTokens ?? _options.Ollama.NumContext);
        var capacityTier = EstimateCapacityTier(profile);
        var targetTier = PickTargetTier(capacityTier, workload, preference);
        var weights = BuildScoringWeights(workload, preference);
        var candidates = BuildComputerCandidates(
            provider,
            models,
            profile,
            workload,
            source,
            target,
            contextTokens,
            targetTier,
            weights);
        var viable = candidates
            .Where(candidate => candidate.IsViable)
            .OrderByDescending(candidate => candidate.IsInstalled)
            .ThenByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.EstimatedMemoryGb ?? double.MaxValue)
            .ToArray();
        var selected = viable.FirstOrDefault()
            ?? candidates.OrderBy(candidate => candidate.EstimatedMemoryGb ?? double.MaxValue).First();
        var signals = BuildSignals(profile, capacityTier, targetTier, workload, preference, source, target, contextTokens);
        var warnings = BuildWarnings(profile, selected);

        var visibleCandidates = BuildVisibleCandidates(selected, candidates);

        return new ComputerModelRecommendation(
            provider,
            selected.Name,
            selected.DisplayName,
            selected.Tier,
            workload,
            preference,
            selected.IsInstalled,
            selected.InstallHint,
            BuildSelectionReason(selected, capacityTier, targetTier),
            profile,
            signals,
            warnings,
            ModelCatalog.CatalogVersion,
            visibleCandidates
                .Select(candidate => new ComputerModelCandidateDescriptor(
                    provider,
                    candidate.Name,
                    candidate.DisplayName,
                    candidate.Tier,
                    candidate.RecommendedUse,
                    candidate.IsInstalled,
                    candidate.InstallHint,
                    Math.Round(candidate.Score, 3),
                    candidate.IsViable,
                    candidate.EstimatedMemoryGb,
                    candidate.FitReason,
                    candidate.SourceLinks))
                .ToArray());
    }

    public static string FormatLanguageForPrompt(string language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return language;
        }

        var normalized = NormalizeCode(language);
        var definition = LanguageDefinitions.FirstOrDefault(item =>
            item.Code.Equals(normalized, StringComparison.OrdinalIgnoreCase));

        return definition?.PromptName ?? language.Trim();
    }

    private static ComputerHardwareProfile BuildHardwareProfile(ComputerModelRecommendationRequest request)
    {
        return new ComputerHardwareProfile(
            RuntimeInformation.OSDescription,
            RuntimeInformation.OSArchitecture.ToString(),
            Math.Max(1, request.CpuLogicalCores ?? Environment.ProcessorCount),
            request.MemoryGb ?? DetectAvailableMemoryGb(),
            request.HasDedicatedGpu,
            request.GpuVramGb);
    }

    private static double? DetectAvailableMemoryGb()
    {
        var bytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return bytes > 0
            ? Math.Round(bytes / 1024d / 1024d / 1024d, 1)
            : null;
    }

    private static string NormalizeChoice(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().ToLowerInvariant();

    private static int EstimateCapacityTier(ComputerHardwareProfile profile)
    {
        var memory = profile.MemoryGb ?? 0;
        var vram = profile.GpuVramGb ?? 0;
        var cores = profile.CpuLogicalCores;

        if (memory >= 16 && cores >= 6 && vram >= 6)
        {
            return 3;
        }

        if ((memory >= 12 && cores >= 6) || vram >= 4)
        {
            return 2;
        }

        if (memory >= 6 && cores >= 4)
        {
            return 1;
        }

        return 0;
    }

    private static int PickTargetTier(int capacityTier, string workload, string preference)
    {
        var preferredTier = preference switch
        {
            "speed" or "latency" => 1,
            "quality" => capacityTier,
            _ => workload switch
            {
                "quality" or "document" or "batch" => Math.Min(capacityTier, 3),
                "balanced" => Math.Min(capacityTier, 2),
                _ => Math.Min(capacityTier, 1)
            }
        };

        return Math.Clamp(preferredTier, 0, capacityTier);
    }

    private IReadOnlyList<ComputerModelCandidate> BuildComputerCandidates(
        string provider,
        IReadOnlyList<TranslationModelDescriptor> models,
        ComputerHardwareProfile profile,
        string workload,
        string source,
        string target,
        int contextTokens,
        int targetTier,
        ScoringWeights weights)
    {
        var metadata = BuildModelMetadata(provider);
        if (metadata.Count == 0)
        {
            return models
                .Select((model, index) => new ComputerModelCandidate(
                    model.Name,
                    model.DisplayName,
                    "provider-default",
                    model.RecommendedUse,
                    TierRank: 0,
                    EstimatedMemoryGb: null,
                    model.IsInstalled,
                    model.IsInstalled ? "already available" : "configure this provider model",
                    new Dictionary<string, string>(),
                    index,
                    Score: model.IsInstalled ? 1 : 0.88,
                    IsViable: true,
                    "Provider-owned model; Verbeam cannot estimate local memory pressure."))
                .ToArray();
        }

        var modelLookup = models.ToDictionary(model => model.Name, StringComparer.OrdinalIgnoreCase);

        return metadata
            .Select(item =>
            {
                modelLookup.TryGetValue(item.Name, out var model);
                var estimatedMemoryGb = EstimateRuntimeMemoryGb(item, contextTokens);
                var memoryFit = MemoryFit(profile.MemoryGb, estimatedMemoryGb);
                var isViable = memoryFit > 0;
                var languageFit = LanguageFit(item, source, target);
                var workloadFit = WorkloadFit(item, workload);
                var contextFit = ContextFit(item, contextTokens);
                var tierFit = TierFit(item.TierRank, targetTier);
                var score = isViable
                    ? ScoreCandidate(
                        item,
                        weights,
                        memoryFit,
                        languageFit,
                        workloadFit,
                        contextFit,
                        tierFit,
                        model?.IsInstalled ?? false)
                    : 0;

                return new ComputerModelCandidate(
                    item.Name,
                    SelectDisplayName(model, item),
                    item.Tier,
                    model?.RecommendedUse ?? item.RecommendedUse,
                    item.TierRank,
                    estimatedMemoryGb,
                    model?.IsInstalled ?? false,
                    model?.IsInstalled == true ? "already installed" : item.InstallHint,
                    item.SourceLinks,
                    item.Rank,
                    score,
                    isViable,
                    BuildFitReason(item, score, memoryFit, languageFit, workloadFit, contextFit, tierFit));
            })
            .OrderBy(candidate => candidate.Rank)
            .ToArray();
    }

    private static ScoringWeights BuildScoringWeights(string workload, string preference)
    {
        var weights = workload switch
        {
            "quality" or "document" or "batch" => new ScoringWeights(
                Latency: 0.08,
                Quality: 0.35,
                Context: 0.22,
                Language: 0.18,
                Stability: 0.10,
                Workload: 0.07),
            "balanced" => new ScoringWeights(
                Latency: 0.22,
                Quality: 0.24,
                Context: 0.16,
                Language: 0.16,
                Stability: 0.10,
                Workload: 0.12),
            _ => new ScoringWeights(
                Latency: 0.42,
                Quality: 0.16,
                Context: 0.08,
                Language: 0.14,
                Stability: 0.10,
                Workload: 0.10)
        };

        return preference switch
        {
            "speed" or "latency" => weights with
            {
                Latency = weights.Latency + 0.12,
                Quality = Math.Max(0.05, weights.Quality - 0.08),
                Context = Math.Max(0.05, weights.Context - 0.04)
            },
            "quality" => weights with
            {
                Quality = weights.Quality + 0.12,
                Context = weights.Context + 0.04,
                Latency = Math.Max(0.05, weights.Latency - 0.10)
            },
            _ => weights
        };
    }

    private static double ScoreCandidate(
        ModelMetadata metadata,
        ScoringWeights weights,
        double memoryFit,
        double languageFit,
        double workloadFit,
        double contextFit,
        double tierFit,
        bool isInstalled)
    {
        var baseScore =
            weights.Latency * metadata.LatencyScore +
            weights.Quality * metadata.QualityScore +
            weights.Context * contextFit +
            weights.Language * languageFit +
            weights.Stability * metadata.StabilityScore +
            weights.Workload * workloadFit;
        var installedBonus = isInstalled ? 0.08 : 0;
        var memoryPenalty = 1 - memoryFit;
        var tierPenalty = (1 - tierFit) * 0.12;

        return Math.Clamp(baseScore + installedBonus - memoryPenalty - tierPenalty, 0, 1);
    }

    private static double EstimateRuntimeMemoryGb(ModelMetadata metadata, int contextTokens)
    {
        var contextMultiplier = Math.Max(0, contextTokens - metadata.DefaultContextTokens) / 4096d;
        return Math.Round(metadata.EstimatedMemoryGb + contextMultiplier * metadata.KvCacheGbPer4096Tokens, 2);
    }

    private static double MemoryFit(double? memoryGb, double estimatedMemoryGb)
    {
        if (memoryGb is null)
        {
            return 0.82;
        }

        var available = memoryGb.Value * 0.72;
        if (available < estimatedMemoryGb)
        {
            return 0;
        }

        var pressure = estimatedMemoryGb / Math.Max(1, available);
        return Math.Clamp(1 - Math.Max(0, pressure - 0.55), 0.45, 1);
    }

    private static double LanguageFit(ModelMetadata metadata, string source, string target)
    {
        var pair = $"{source}->{target}";
        if (metadata.LanguagePairs.Any(item => item.Equals(pair, StringComparison.OrdinalIgnoreCase)))
        {
            return 1;
        }

        if (metadata.LanguagePairs.Any(item => item.Equals($"{source}->*", StringComparison.OrdinalIgnoreCase) ||
                                               item.Equals($"*->{target}", StringComparison.OrdinalIgnoreCase)))
        {
            return 0.82;
        }

        return metadata.LanguagePairs.Contains("*", StringComparer.OrdinalIgnoreCase) ? 0.72 : 0.48;
    }

    private static double WorkloadFit(ModelMetadata metadata, string workload)
    {
        if (metadata.BestFor.Any(item => item.Equals(workload, StringComparison.OrdinalIgnoreCase)))
        {
            return 1;
        }

        if (workload.Equals("realtime_overlay", StringComparison.OrdinalIgnoreCase) &&
            metadata.BestFor.Any(item => item.Equals("ocr", StringComparison.OrdinalIgnoreCase) ||
                                         item.Equals("short_text", StringComparison.OrdinalIgnoreCase)))
        {
            return 0.86;
        }

        if ((workload.Equals("document", StringComparison.OrdinalIgnoreCase) ||
             workload.Equals("quality", StringComparison.OrdinalIgnoreCase)) &&
            metadata.BestFor.Any(item => item.Equals("batch", StringComparison.OrdinalIgnoreCase) ||
                                         item.Equals("long_text", StringComparison.OrdinalIgnoreCase)))
        {
            return 0.86;
        }

        return 0.62;
    }

    private static double ContextFit(ModelMetadata metadata, int contextTokens)
        => contextTokens <= metadata.DefaultContextTokens
            ? metadata.ContextScore
            : Math.Clamp(metadata.ContextScore - ((contextTokens - metadata.DefaultContextTokens) / 4096d * 0.18), 0.35, 1);

    private static double TierFit(int candidateTier, int targetTier)
    {
        var distance = Math.Abs(candidateTier - targetTier);
        return distance switch
        {
            0 => 1,
            1 => 0.86,
            2 => 0.68,
            _ => 0.52
        };
    }

    private static string BuildFitReason(
        ModelMetadata metadata,
        double score,
        double memoryFit,
        double languageFit,
        double workloadFit,
        double contextFit,
        double tierFit)
    {
        if (memoryFit <= 0)
        {
            return $"{metadata.DisplayName} is excluded because estimated runtime memory is too high.";
        }

        return $"score {score:0.000}; memory {memoryFit:0.00}, language {languageFit:0.00}, workload {workloadFit:0.00}, context {contextFit:0.00}, tier {tierFit:0.00}.";
    }

    private static IReadOnlyList<string> BuildSignals(
        ComputerHardwareProfile profile,
        int capacityTier,
        int targetTier,
        string workload,
        string preference,
        string source,
        string target,
        int contextTokens)
    {
        return
        [
            $"CPU logical cores: {profile.CpuLogicalCores}",
            profile.MemoryGb is null ? "Memory: unknown" : $"Memory: {profile.MemoryGb:0.#} GB",
            profile.GpuVramGb is null ? "GPU VRAM: unknown" : $"GPU VRAM: {profile.GpuVramGb:0.#} GB",
            $"Capacity tier: {TierName(capacityTier)}",
            $"Target tier: {TierName(targetTier)}",
            $"Workload: {workload}",
            $"Preference: {preference}",
            $"Language pair: {source}->{target}",
            $"Context tokens: {contextTokens}"
        ];
    }

    private static IReadOnlyList<string> BuildWarnings(
        ComputerHardwareProfile profile,
        ComputerModelCandidate selected)
    {
        var warnings = new List<string>();
        if (profile.MemoryGb is null)
        {
            warnings.Add("Memory limit could not be detected; recommendation stays conservative.");
        }

        if (profile.GpuVramGb is null)
        {
            warnings.Add("GPU VRAM was not provided; CPU-safe and low-latency models are preferred.");
        }

        if (!selected.IsInstalled)
        {
            warnings.Add("Recommended model is not installed yet.");
        }

        if (selected.EstimatedMemoryGb is not null && profile.MemoryGb is not null && selected.EstimatedMemoryGb > profile.MemoryGb * 0.55)
        {
            warnings.Add("Recommended model may put noticeable pressure on system memory.");
        }

        if (selected.TierRank <= 1)
        {
            warnings.Add("Keep num_ctx and num_predict low for realtime overlays.");
        }

        return warnings;
    }

    private static IReadOnlyList<ComputerModelCandidate> BuildVisibleCandidates(
        ComputerModelCandidate selected,
        IReadOnlyList<ComputerModelCandidate> candidates)
    {
        var visible = new List<ComputerModelCandidate> { selected };
        visible.AddRange(candidates
            .Where(candidate => !candidate.Name.Equals(selected.Name, StringComparison.OrdinalIgnoreCase) && candidate.IsViable)
            .OrderByDescending(candidate => candidate.IsInstalled)
            .ThenByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.EstimatedMemoryGb ?? double.MaxValue)
            .Take(2));

        if (visible.Count < 3)
        {
            visible.AddRange(candidates
                .Where(candidate => visible.All(existing => !existing.Name.Equals(candidate.Name, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(candidate => candidate.IsViable)
                .ThenBy(candidate => candidate.EstimatedMemoryGb ?? double.MaxValue)
                .ThenBy(candidate => candidate.Rank)
                .Take(3 - visible.Count));
        }

        return visible;
    }

    private static string BuildSelectionReason(
        ComputerModelCandidate selected,
        int capacityTier,
        int targetTier)
        => $"{selected.DisplayName} scored {selected.Score:0.000} and best matches the {TierName(targetTier)} target for this {TierName(capacityTier)} computer profile.";

    private static string TierName(int tier)
        => tier switch
        {
            >= 3 => "quality",
            2 => "balanced",
            1 => "small",
            _ => "minimal"
        };

    private static string Pick(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private ModelCatalogDocument ModelCatalog => _getModelCatalog();

    private static string SelectDisplayName(TranslationModelDescriptor? model, ModelMetadata metadata)
        => model is null || HasGenericDisplayName(model.DisplayName, model.Name)
            ? metadata.DisplayName
            : model.DisplayName;

    private static bool HasGenericDisplayName(string displayName, string name)
        => string.IsNullOrWhiteSpace(displayName) ||
           displayName.Equals(name, StringComparison.OrdinalIgnoreCase);

    private IReadOnlyList<ModelMetadata> BuildModelMetadata(string provider)
    {
        var metadata = ModelCatalog.Models
            .Where(model => IsProviderRuntime(model, provider))
            .Where(IsSelectableCatalogEntry)
            .OrderBy(model => model.Rank)
            .ThenBy(model => model.Name, StringComparer.OrdinalIgnoreCase)
            .Select(model => MapCatalogEntry(provider, model))
            .ToArray();

        if (metadata.Length > 0)
        {
            return metadata;
        }

        return provider.Equals("ollama", StringComparison.OrdinalIgnoreCase)
            ? BuildFallbackOllamaModelMetadata()
            : [];
    }

    private static bool IsSelectableCatalogEntry(ModelCatalogEntry model)
    {
        if (model.Status.Equals("deprecated", StringComparison.OrdinalIgnoreCase) ||
            model.Status.Equals("blocked", StringComparison.OrdinalIgnoreCase) ||
            model.Status.Equals("removed", StringComparison.OrdinalIgnoreCase) ||
            model.Status.Equals("experimental", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !model.Audience.Equals("hidden", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsProviderRuntime(ModelCatalogEntry model, string provider)
        => model.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase) ||
           (provider.Equals("ollama", StringComparison.OrdinalIgnoreCase) && model.Runtimes.Ollama is not null) ||
           (provider.Equals("llama-cpp", StringComparison.OrdinalIgnoreCase) && model.Runtimes.LlamaCpp is not null);

    private static ModelMetadata MapCatalogEntry(string provider, ModelCatalogEntry model)
        => new(
            RuntimeModelName(provider, model),
            model.DisplayName,
            model.Tier,
            model.RecommendedUse,
            model.TierRank,
            model.EstimatedMemoryGb,
            model.DefaultContextTokens,
            model.KvCacheGbPer4096Tokens,
            model.QualityScore,
            model.LatencyScore,
            model.ContextScore,
            model.StabilityScore,
            model.LanguagePairs,
            model.BestFor,
            RuntimeInstallHint(provider, model),
            BuildSourceLinks(model),
            model.Rank);

    private static string RuntimeModelName(string provider, ModelCatalogEntry model)
    {
        if (provider.Equals("llama-cpp", StringComparison.OrdinalIgnoreCase))
        {
            return Pick(model.Runtimes.LlamaCpp?.ModelAlias, model.Id);
        }

        if (provider.Equals("ollama", StringComparison.OrdinalIgnoreCase))
        {
            return Pick(model.Runtimes.Ollama?.ModelName, model.Name);
        }

        return model.Name;
    }

    private static string RuntimeInstallHint(string provider, ModelCatalogEntry model)
    {
        if (provider.Equals("llama-cpp", StringComparison.OrdinalIgnoreCase))
        {
            return model.Artifact is null
                ? "configure llama.cpp runtime"
                : string.IsNullOrWhiteSpace(model.Artifact.DownloadUrl)
                    ? $"place {model.Artifact.Filename} in models/llama-cpp and start managed llama.cpp"
                : $"download {model.Artifact.Filename} and start managed llama.cpp";
        }

        if (provider.Equals("ollama", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(model.Runtimes.Ollama?.InstallCommand))
        {
            return model.Runtimes.Ollama.InstallCommand;
        }

        return string.IsNullOrWhiteSpace(model.Install.Command) ? "manual install" : model.Install.Command;
    }

    private static IReadOnlyDictionary<string, string> BuildSourceLinks(ModelCatalogEntry model)
    {
        var links = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddSourceLink(links, "ollama", model.Source.Ollama);
        AddSourceLink(links, "huggingFace", model.Source.HuggingFace);
        AddSourceLink(links, "download", model.Artifact?.DownloadUrl);
        return links;
    }

    private static void AddSourceLink(Dictionary<string, string> links, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            links[name] = value.Trim();
        }
    }

    private static ModelCatalogDocument CreateFallbackModelCatalog()
        => new()
        {
            CatalogVersion = "fallback-built-in",
            Models = BuildFallbackOllamaModelMetadata()
                .Select(metadata => new ModelCatalogEntry
                {
                    Id = metadata.Name.Replace(":", "-").Replace("/", "-"),
                    Provider = "ollama",
                    Name = metadata.Name,
                    DisplayName = metadata.DisplayName,
                    Status = "candidate",
                    Audience = "beginner",
                    Tier = metadata.Tier,
                    RecommendedUse = metadata.RecommendedUse,
                    TierRank = metadata.TierRank,
                    EstimatedMemoryGb = metadata.EstimatedMemoryGb,
                    DefaultContextTokens = metadata.DefaultContextTokens,
                    KvCacheGbPer4096Tokens = metadata.KvCacheGbPer4096Tokens,
                    QualityScore = metadata.QualityScore,
                    LatencyScore = metadata.LatencyScore,
                    ContextScore = metadata.ContextScore,
                    StabilityScore = metadata.StabilityScore,
                    LanguagePairs = metadata.LanguagePairs,
                    BestFor = metadata.BestFor,
                    Install = new ModelCatalogInstallPlan
                    {
                        Type = "fallback",
                        Command = metadata.InstallHint
                    },
                    Source = new ModelCatalogSource
                    {
                        Ollama = metadata.SourceLinks.TryGetValue("ollama", out var ollama) ? ollama : null,
                        HuggingFace = metadata.SourceLinks.TryGetValue("huggingFace", out var huggingFace) ? huggingFace : null
                    },
                    Rank = metadata.Rank
                })
                .ToArray()
        };

    private static IReadOnlyList<ModelMetadata> BuildFallbackOllamaModelMetadata()
        =>
        [
            new ModelMetadata(
                "qwen2.5:0.5b",
                "Qwen2.5 0.5B",
                "minimal",
                "smoke test",
                TierRank: 0,
                EstimatedMemoryGb: 2.2,
                DefaultContextTokens: 1024,
                KvCacheGbPer4096Tokens: 0.25,
                QualityScore: 0.42,
                LatencyScore: 0.92,
                ContextScore: 0.48,
                StabilityScore: 0.64,
                ["ja->zh-TW", "en->zh-TW", "ja->en", "en->ja", "*"],
                ["smoke_test", "short_text"],
                "ollama pull qwen2.5:0.5b",
                new Dictionary<string, string>
                {
                    ["ollama"] = "https://ollama.com/library/qwen2.5"
                },
                Rank: 20),
            new ModelMetadata(
                "verbeam-mort-qwen2.5-0.5b:latest",
                "Verbeam MORT Qwen2.5 0.5B",
                "small",
                "realtime OCR overlay",
                TierRank: 1,
                EstimatedMemoryGb: 2.5,
                DefaultContextTokens: 1024,
                KvCacheGbPer4096Tokens: 0.25,
                QualityScore: 0.48,
                LatencyScore: 0.94,
                ContextScore: 0.50,
                StabilityScore: 0.82,
                ["ja->zh-TW", "ja->zh-CN", "en->zh-TW", "*->zh-TW", "*"],
                ["realtime_overlay", "ocr", "short_text", "game_dialogue"],
                "run scripts/create-ollama-profiles.ps1",
                new Dictionary<string, string>
                {
                    ["ollama"] = "https://ollama.com/library/qwen2.5"
                },
                Rank: 10),
            new ModelMetadata(
                "qwen2.5:1.5b",
                "Qwen2.5 1.5B",
                "balanced",
                "balanced local translation",
                TierRank: 2,
                EstimatedMemoryGb: 4.2,
                DefaultContextTokens: 2048,
                KvCacheGbPer4096Tokens: 0.45,
                QualityScore: 0.66,
                LatencyScore: 0.72,
                ContextScore: 0.68,
                StabilityScore: 0.74,
                ["ja->zh-TW", "ja->zh-CN", "en->zh-TW", "en->ja", "ko->zh-TW", "*"],
                ["balanced", "subtitle", "ocr", "short_text", "web_article"],
                "ollama pull qwen2.5:1.5b",
                new Dictionary<string, string>
                {
                    ["ollama"] = "https://ollama.com/library/qwen2.5"
                },
                Rank: 30),
            new ModelMetadata(
                "translategemma:latest",
                "TranslateGemma",
                "quality",
                "higher quality translation",
                TierRank: 3,
                EstimatedMemoryGb: 7.5,
                DefaultContextTokens: 4096,
                KvCacheGbPer4096Tokens: 0.75,
                QualityScore: 0.86,
                LatencyScore: 0.48,
                ContextScore: 0.84,
                StabilityScore: 0.80,
                ["ja->zh-TW", "ja->zh-CN", "en->zh-TW", "en->ja", "ko->zh-TW", "*"],
                ["quality", "document", "batch", "long_text", "web_article", "subtitle"],
                "ollama pull translategemma:latest",
                new Dictionary<string, string>
                {
                    ["ollama"] = "https://ollama.com/search?q=translategemma"
                },
                Rank: 40)
        ];

    private IReadOnlyList<ModelRecommendation> BuildProviderRecommendations(string provider)
    {
        var recommendations = new List<ModelRecommendation>();
        var configuredDefault = DefaultModelForProvider(provider);
        if (!string.IsNullOrWhiteSpace(configuredDefault))
        {
            recommendations.Add(new ModelRecommendation(
                configuredDefault,
                configuredDefault,
                "current default",
                "Matches the configured Verbeam default model.",
                Rank: 0));
        }

        recommendations.AddRange(
            BuildModelMetadata(provider)
                .Select(metadata => new ModelRecommendation(
                    metadata.Name,
                    metadata.DisplayName,
                    metadata.RecommendedUse,
                    $"{metadata.DisplayName} is a {metadata.Tier} candidate for {metadata.RecommendedUse}.",
                    metadata.Rank)));

        return recommendations
            .GroupBy(recommendation => recommendation.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(item => item.Rank).First())
            .OrderBy(recommendation => recommendation.Rank)
            .ToArray();
    }

    private bool IsConfiguredDefault(string provider, string model)
        => string.Equals(model, DefaultModelForProvider(provider), StringComparison.OrdinalIgnoreCase);

    private string DefaultModelForProvider(string provider)
        => provider.Equals("ollama", StringComparison.OrdinalIgnoreCase)
            ? _options.Ollama.Model.Trim()
            : provider.Equals("llama-cpp", StringComparison.OrdinalIgnoreCase)
                ? _options.LlamaCpp.Model.Trim()
                : string.Empty;

    private static int RecommendationRank(
        string model,
        IReadOnlyDictionary<string, ModelRecommendation> recommendations)
        => recommendations.TryGetValue(model, out var recommendation) ? recommendation.Rank : int.MaxValue;

    private static bool IsLanguageMatch(string code, string configured)
        => string.Equals(NormalizeCode(code), NormalizeCode(configured), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeCode(string language)
    {
        var value = language.Trim().ToLowerInvariant();
        foreach (var definition in LanguageDefinitions)
        {
            if (definition.Code.Equals(value, StringComparison.OrdinalIgnoreCase) ||
                definition.Aliases.Any(alias => alias.Equals(value, StringComparison.OrdinalIgnoreCase)))
            {
                return definition.Code;
            }
        }

        return language.Trim();
    }

    private sealed record LanguageDefinition(
        string Code,
        string DisplayName,
        string NativeName,
        string PromptName,
        bool IsOcrSupported,
        bool IsSpeechSupported,
        IReadOnlyList<string> Aliases);

    private sealed record ModelRecommendation(
        string Name,
        string DisplayName,
        string UseCase,
        string Reason,
        int Rank);

    private sealed record ModelMetadata(
        string Name,
        string DisplayName,
        string Tier,
        string RecommendedUse,
        int TierRank,
        double EstimatedMemoryGb,
        int DefaultContextTokens,
        double KvCacheGbPer4096Tokens,
        double QualityScore,
        double LatencyScore,
        double ContextScore,
        double StabilityScore,
        IReadOnlyList<string> LanguagePairs,
        IReadOnlyList<string> BestFor,
        string InstallHint,
        IReadOnlyDictionary<string, string> SourceLinks,
        int Rank);

    private sealed record ScoringWeights(
        double Latency,
        double Quality,
        double Context,
        double Language,
        double Stability,
        double Workload);

    private sealed record ComputerModelCandidate(
        string Name,
        string DisplayName,
        string Tier,
        string RecommendedUse,
        int TierRank,
        double? EstimatedMemoryGb,
        bool IsInstalled,
        string InstallHint,
        IReadOnlyDictionary<string, string> SourceLinks,
        int Rank,
        double Score,
        bool IsViable,
        string FitReason);
}
