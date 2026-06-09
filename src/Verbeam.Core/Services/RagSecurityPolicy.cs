using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Verbeam.Core.Services;

public static partial class RagSecurityPolicy
{
    public const string UserVerified = "user_verified";
    public const string TrustedImport = "trusted_import";
    public const string LocalGenerated = "local_generated";
    public const string UntrustedImport = "untrusted_import";
    public const string Quarantined = "quarantined";

    private static readonly HashSet<string> AllowedTrustLevels = new(StringComparer.Ordinal)
    {
        UserVerified,
        TrustedImport,
        LocalGenerated,
        UntrustedImport,
        Quarantined
    };

    private static readonly HashSet<string> ExactMemoryTrustLevels = new(StringComparer.Ordinal)
    {
        UserVerified,
        TrustedImport
    };

    private static readonly HashSet<string> AllowedVisibilities = new(StringComparer.Ordinal)
    {
        "private",
        "session",
        "profile",
        "shared"
    };

    private static readonly char[] HiddenInstructionCharacters =
    [
        '\u200B',
        '\u200C',
        '\u200D',
        '\u2060',
        '\uFEFF'
    ];

    public static string NormalizeTrustLevel(string? trustLevel, string? origin)
    {
        var normalized = !string.IsNullOrWhiteSpace(trustLevel)
            ? trustLevel.Trim().Replace('-', '_').ToLowerInvariant()
            : TrustLevelFromOrigin(origin);

        if (!AllowedTrustLevels.Contains(normalized))
        {
            throw new ArgumentException($"trustLevel must be one of: {string.Join(", ", AllowedTrustLevels)}.");
        }

        return normalized;
    }

    public static string NormalizeVisibility(string? visibility)
    {
        var normalized = string.IsNullOrWhiteSpace(visibility)
            ? "profile"
            : visibility.Trim().Replace('-', '_').ToLowerInvariant();

        if (!AllowedVisibilities.Contains(normalized))
        {
            throw new ArgumentException($"visibility must be one of: {string.Join(", ", AllowedVisibilities)}.");
        }

        return normalized;
    }

    public static string NormalizeClassification(string? classification)
        => string.IsNullOrWhiteSpace(classification)
            ? "normal"
            : classification.Trim().ReplaceLineEndings(" ");

    public static bool CanUseForExactMemory(string trustLevel)
        => ExactMemoryTrustLevels.Contains(trustLevel);

    public static string ComputeSourceHash(params string?[] values)
    {
        var joined = string.Join("\n", values.Select(value => value?.ReplaceLineEndings("\n").Trim() ?? string.Empty));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined))).ToLowerInvariant();
    }

    public static string BuildSecurityFlagsJson(params string?[] values)
    {
        var flags = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            AddFlags(value, flags);
        }

        return JsonSerializer.Serialize(flags);
    }

    public static string QuarantineIfNeeded(string trustLevel, string securityFlagsJson)
    {
        if (trustLevel is UserVerified or TrustedImport or Quarantined)
        {
            return trustLevel;
        }

        return securityFlagsJson == "[]" ? trustLevel : Quarantined;
    }

    public static string SanitizePromptData(string value)
    {
        var normalized = value.ReplaceLineEndings("\n").Trim();
        foreach (var hidden in HiddenInstructionCharacters)
        {
            normalized = normalized.Replace(hidden.ToString(), string.Empty, StringComparison.Ordinal);
        }

        return RoleMarkerRegex().Replace(normalized, "${indent}[${role} data]:");
    }

    private static string TrustLevelFromOrigin(string? origin)
    {
        var normalized = string.IsNullOrWhiteSpace(origin)
            ? UserVerified
            : origin.Trim().Replace('-', '_').ToLowerInvariant();

        return normalized switch
        {
            "user_verified" or "user" or "manual" => UserVerified,
            "trusted_import" => TrustedImport,
            "local_generated" or "generated" or "auto_extracted" => LocalGenerated,
            "untrusted_import" or "imported" or "external" => UntrustedImport,
            "quarantined" => Quarantined,
            _ => UserVerified
        };
    }

    private static void AddFlags(string? value, ISet<string> flags)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (value.IndexOfAny(HiddenInstructionCharacters) >= 0)
        {
            flags.Add("hidden_unicode");
        }

        var normalized = value.ReplaceLineEndings("\n").ToLowerInvariant();
        if (RoleMarkerRegex().IsMatch(value))
        {
            flags.Add("role_marker");
        }

        if (normalized.Contains("ignore previous", StringComparison.Ordinal) ||
            normalized.Contains("ignore all previous", StringComparison.Ordinal) ||
            normalized.Contains("disregard previous", StringComparison.Ordinal) ||
            normalized.Contains("system prompt", StringComparison.Ordinal) ||
            normalized.Contains("developer message", StringComparison.Ordinal) ||
            normalized.Contains("reveal your instructions", StringComparison.Ordinal) ||
            normalized.Contains("follow these instructions", StringComparison.Ordinal))
        {
            flags.Add("prompt_injection_phrase");
        }
    }

    [GeneratedRegex(@"^(?<indent>\s*)(?<role>system|developer|assistant|tool|user)\s*:", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex RoleMarkerRegex();
}
