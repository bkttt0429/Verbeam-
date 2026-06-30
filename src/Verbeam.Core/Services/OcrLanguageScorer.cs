using Verbeam.Core.Models;

namespace Verbeam.Core.Services;

/// <summary>
/// Scores one OCR run produced with a candidate language so the auto-detection
/// fallback can pick the best of several re-runs. Pure function over the run's
/// blocks and its script detection; higher is better.
/// </summary>
public static class OcrLanguageScorer
{
    public static double Score(
        string candidateLanguage,
        IReadOnlyList<OcrTextBlock> blocks,
        ScriptDetectionResult detection)
    {
        if (blocks.Count == 0 || detection.EffectiveCharCount == 0)
        {
            return 0;
        }

        var averageConfidence = blocks
            .Where(block => !string.IsNullOrWhiteSpace(block.Text))
            .Select(block => Math.Clamp(block.Confidence, 0, 1))
            .DefaultIfEmpty(0)
            .Average();
        var textVolume = Math.Min(1.0, detection.EffectiveCharCount / 40.0);
        var blockCoverage = Math.Min(1.0, blocks.Count / 5.0);
        var scriptConsistency = ScriptConsistency(candidateLanguage, detection);

        return Math.Round(
            0.4 * averageConfidence +
            0.2 * textVolume +
            0.1 * blockCoverage +
            0.3 * scriptConsistency -
            0.5 * detection.MojibakeRatio,
            4);
    }

    private static double ScriptConsistency(string candidateLanguage, ScriptDetectionResult detection)
    {
        var canonical = LanguageRegistry.Normalize(candidateLanguage);
        if (string.Equals(detection.DetectedLanguage, canonical, StringComparison.OrdinalIgnoreCase))
        {
            return 1.0;
        }

        var candidate = detection.Candidates
            .FirstOrDefault(item => string.Equals(item.Language, canonical, StringComparison.OrdinalIgnoreCase));
        return candidate?.Score ?? 0;
    }
}
