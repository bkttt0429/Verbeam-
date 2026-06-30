namespace Verbeam.Core.Models;

public sealed record ComputerModelRecommendationRequest
{
    public string? Provider { get; init; }
    public string? Workload { get; init; }
    public string? Preference { get; init; }
    public string? Source { get; init; }
    public string? Target { get; init; }
    public int? ContextTokens { get; init; }
    public int? CpuLogicalCores { get; init; }
    public double? MemoryGb { get; init; }
    public bool? HasDedicatedGpu { get; init; }
    public double? GpuVramGb { get; init; }
}

public sealed record ComputerHardwareProfile(
    string OperatingSystem,
    string Architecture,
    int CpuLogicalCores,
    double? MemoryGb,
    bool? HasDedicatedGpu,
    double? GpuVramGb);

public sealed record ComputerModelCandidateDescriptor(
    string Provider,
    string Name,
    string DisplayName,
    string Tier,
    string RecommendedUse,
    bool IsInstalled,
    string InstallHint,
    double Score,
    bool IsViable,
    double? EstimatedMemoryGb,
    string FitReason,
    IReadOnlyDictionary<string, string> SourceLinks);

public sealed record ComputerModelRecommendation(
    string Provider,
    string RecommendedModel,
    string DisplayName,
    string Tier,
    string Workload,
    string Preference,
    bool IsInstalled,
    string InstallHint,
    string Reason,
    ComputerHardwareProfile Profile,
    IReadOnlyList<string> Signals,
    IReadOnlyList<string> Warnings,
    string CatalogVersion,
    IReadOnlyList<ComputerModelCandidateDescriptor> Candidates);
