using System.Text;
using Verbeam.Core.Models;

namespace Verbeam.Core.Services;

public sealed record ScriptDetectionResult(
    string DetectedLanguage,
    string Script,
    double Confidence,
    IReadOnlyList<OcrLanguageCandidate> Candidates,
    double MojibakeRatio,
    int EffectiveCharCount)
{
    public static readonly ScriptDetectionResult Empty =
        new(string.Empty, string.Empty, 0, Array.Empty<OcrLanguageCandidate>(), 0, 0);

    /// <summary>
    /// True when the text contains characters that exist in only one of the two
    /// Chinese standards. Without such evidence a Hant/Hans verdict is just the
    /// tie-break default and must not justify skipping a zh-CN/zh-TW conversion.
    /// </summary>
    public bool HasChineseVariantEvidence { get; init; }
}

/// <summary>
/// Pure codepoint-statistics language detector. Counts letters per Unicode script,
/// then infers the canonical language: Hangul → ko-KR, any meaningful Kana → ja-JP,
/// Han without Kana → Chinese (Hant vs Hans decided by characters that exist in only
/// one of the two standards), otherwise the dominant remaining script. Latin is
/// treated as auxiliary annotation (e.g. "編譯 (Compile)") unless it dominates the text.
/// Kanji-only Japanese is indistinguishable from Chinese here, so ja-JP stays a
/// candidate whenever Han is present without Kana.
/// </summary>
public static class UnicodeScriptDetector
{
    // Characters whose codepoint exists only in the simplified standard.
    private const string SimplifiedOnly =
        "们这来对时会国学发说没还见关门问长东车马鸟语习书买卖电气体广边讨论译预连执档汉简网软义务应导属张层岁号" +
        "区医难备党仅众优传伤价华伟划么经济贸样产种动业农让认识题写读飞机场转变压历团园远运过达违围为伪构购货质" +
        "贵费资赖临举乐录钱银错镜阅队阳阴际陈随隐雾颜顺须顾频风饭馆驱验骑编组键显设计处数据图标点选择确开闭储载缩复贴";

    // Characters whose codepoint exists only in the traditional standard.
    private const string TraditionalOnly =
        "們這來對時會國學發說沒還見關門問長東車馬鳥語習書買賣電氣體廣邊討論譯預連執檔漢簡網軟義務應導屬張層歲號" +
        "區醫難備黨僅眾優傳傷價華偉劃麼經濟貿樣產種動業農讓認識題寫讀飛機場轉變壓歷團園遠運過達違圍為偽構購貨質" +
        "貴費資賴臨舉樂錄錢銀錯鏡閱隊陽陰際陳隨隱霧顏順須顧頻風飯館驅驗騎編組鍵顯設計處數據圖標點選擇確開閉儲載縮複貼";

    private static readonly HashSet<int> SimplifiedOnlySet = ToCodepointSet(SimplifiedOnly);
    private static readonly HashSet<int> TraditionalOnlySet = ToCodepointSet(TraditionalOnly);

    public static ScriptDetectionResult Detect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return ScriptDetectionResult.Empty;
        }

        var counts = new ScriptCounts();
        foreach (var rune in text.EnumerateRunes())
        {
            counts.Add(rune.Value);
        }

        return Infer(counts);
    }

    /// <summary>
    /// Combines per-block detections into a document-level result. Each block's
    /// candidate scores are weighted (typically by effective characters × OCR
    /// confidence) so a large confident block outweighs a stray short one.
    /// </summary>
    public static ScriptDetectionResult Aggregate(
        IReadOnlyList<(ScriptDetectionResult Detection, double Weight)> blocks)
    {
        var totals = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var totalWeight = 0.0;
        var confidenceWeighted = 0.0;
        var mojibakeWeighted = 0.0;
        var effectiveChars = 0;
        var scriptVotes = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (var (detection, weight) in blocks)
        {
            if (detection.EffectiveCharCount == 0 || weight <= 0)
            {
                continue;
            }

            totalWeight += weight;
            confidenceWeighted += detection.Confidence * weight;
            mojibakeWeighted += detection.MojibakeRatio * weight;
            effectiveChars += detection.EffectiveCharCount;
            foreach (var candidate in detection.Candidates)
            {
                totals[candidate.Language] = totals.GetValueOrDefault(candidate.Language) + candidate.Score * weight;
            }

            if (!string.IsNullOrEmpty(detection.Script))
            {
                scriptVotes[detection.Script] = scriptVotes.GetValueOrDefault(detection.Script) + weight;
            }
        }

        if (totalWeight <= 0 || totals.Count == 0)
        {
            return ScriptDetectionResult.Empty;
        }

        var candidates = totals
            .Select(pair => new OcrLanguageCandidate(pair.Key, Math.Round(pair.Value / totalWeight, 4)))
            .OrderByDescending(candidate => candidate.Score)
            .ToArray();
        var script = scriptVotes.Count == 0
            ? string.Empty
            : scriptVotes.OrderByDescending(pair => pair.Value).First().Key;

        return new ScriptDetectionResult(
            candidates[0].Language,
            script,
            Math.Round(confidenceWeighted / totalWeight, 4),
            candidates,
            Math.Round(mojibakeWeighted / totalWeight, 4),
            effectiveChars);
    }

    private static ScriptDetectionResult Infer(ScriptCounts counts)
    {
        var total = counts.EffectiveTotal;
        if (total == 0)
        {
            return ScriptDetectionResult.Empty;
        }

        double han = counts.Han, kana = counts.Kana, hangul = counts.Hangul, latin = counts.Latin;
        var cjk = han + kana + hangul;
        var mojibakeRatio = counts.Mojibake == 0
            ? 0.0
            : (double)counts.Mojibake / (total + counts.Mojibake);

        var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        if (hangul > 0)
        {
            scores[LanguageRegistry.Korean] = 0.5 + 0.5 * (hangul / total);
        }

        if (kana > 0)
        {
            // Any meaningful kana means Japanese regardless of how many kanji surround it.
            var kanaShare = kana / Math.Max(1, han + kana);
            scores[LanguageRegistry.Japanese] = 0.55 + 0.45 * Math.Min(1.0, kanaShare * 4);
        }

        if (han > 0)
        {
            var simplifiedHits = counts.SimplifiedHits;
            var traditionalHits = counts.TraditionalHits;
            var hanShare = han / total;
            var variantEvidence = Math.Min(1.0, Math.Abs(traditionalHits - simplifiedHits) / Math.Max(2.0, han * 0.2));
            var baseScore = 0.45 + 0.3 * hanShare;

            if (kana == 0)
            {
                if (traditionalHits >= simplifiedHits)
                {
                    scores[LanguageRegistry.TraditionalChinese] = baseScore + 0.25 * variantEvidence;
                    scores[LanguageRegistry.SimplifiedChinese] = baseScore - 0.15 - 0.2 * variantEvidence;
                }
                else
                {
                    scores[LanguageRegistry.SimplifiedChinese] = baseScore + 0.25 * variantEvidence;
                    scores[LanguageRegistry.TraditionalChinese] = baseScore - 0.15 - 0.2 * variantEvidence;
                }

                // Kanji-only text can still be Japanese; keep it as a fallback candidate.
                scores[LanguageRegistry.Japanese] = Math.Max(scores.GetValueOrDefault(LanguageRegistry.Japanese), 0.2);
            }
            else
            {
                // Kana present: Han characters are kanji, not Chinese evidence.
                scores[LanguageRegistry.TraditionalChinese] = 0.1;
                scores[LanguageRegistry.SimplifiedChinese] = 0.1;
            }
        }

        if (latin > 0)
        {
            var latinShare = latin / total;
            if (cjk == 0)
            {
                scores[LanguageRegistry.English] = 0.5 + 0.5 * latinShare;
            }
            else if (latinShare > 0.75)
            {
                // Latin overwhelmingly dominates: treat CJK as the annotation instead.
                scores[LanguageRegistry.English] = 0.45 + 0.4 * latinShare;
            }
            else
            {
                // Auxiliary terms like "編譯 (Compile)" must not flip the document to English.
                scores[LanguageRegistry.English] = 0.15 * latinShare;
            }
        }

        if (scores.Count == 0)
        {
            return ScriptDetectionResult.Empty;
        }

        var shortTextPenalty = total < 4 ? 0.6 : 1.0;
        var candidates = scores
            .Select(pair => new OcrLanguageCandidate(
                pair.Key,
                Math.Round(Math.Clamp(pair.Value * shortTextPenalty - 0.5 * mojibakeRatio, 0, 1), 4)))
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ToArray();
        if (candidates.Length == 0)
        {
            return ScriptDetectionResult.Empty;
        }

        return new ScriptDetectionResult(
            candidates[0].Language,
            DescribeScripts(counts),
            candidates[0].Score,
            candidates,
            Math.Round(mojibakeRatio, 4),
            total)
        {
            HasChineseVariantEvidence = counts.SimplifiedHits + counts.TraditionalHits > 0
        };
    }

    private static string DescribeScripts(ScriptCounts counts)
    {
        var total = Math.Max(1, counts.EffectiveTotal);
        var entries = new List<(string Name, double Share)>();
        if (counts.Han > 0)
        {
            var name = counts.TraditionalHits > counts.SimplifiedHits
                ? "Hant"
                : counts.SimplifiedHits > counts.TraditionalHits ? "Hans" : "Hani";
            entries.Add((name, (double)counts.Han / total));
        }

        if (counts.Kana > 0)
        {
            entries.Add(("Kana", (double)counts.Kana / total));
        }

        if (counts.Hangul > 0)
        {
            entries.Add(("Hang", (double)counts.Hangul / total));
        }

        if (counts.Latin > 0)
        {
            entries.Add(("Latn", (double)counts.Latin / total));
        }

        if (counts.Cyrillic > 0)
        {
            entries.Add(("Cyrl", (double)counts.Cyrillic / total));
        }

        if (counts.Arabic > 0)
        {
            entries.Add(("Arab", (double)counts.Arabic / total));
        }

        if (counts.Thai > 0)
        {
            entries.Add(("Thai", (double)counts.Thai / total));
        }

        return string.Join("+", entries
            .Where(entry => entry.Share >= 0.08)
            .OrderByDescending(entry => entry.Share)
            .Select(entry => entry.Name));
    }

    private static HashSet<int> ToCodepointSet(string characters)
    {
        var set = new HashSet<int>();
        foreach (var rune in characters.EnumerateRunes())
        {
            set.Add(rune.Value);
        }

        return set;
    }

    private struct ScriptCounts
    {
        public int Han;
        public int Kana;
        public int Hangul;
        public int Latin;
        public int Cyrillic;
        public int Arabic;
        public int Thai;
        public int Mojibake;
        public int SimplifiedHits;
        public int TraditionalHits;

        public readonly int EffectiveTotal
            => Han + Kana + Hangul + Latin + Cyrillic + Arabic + Thai;

        public void Add(int codepoint)
        {
            if (codepoint == 0xFFFD)
            {
                Mojibake++;
                return;
            }

            if (IsHan(codepoint))
            {
                Han++;
                if (SimplifiedOnlySet.Contains(codepoint))
                {
                    SimplifiedHits++;
                }
                else if (TraditionalOnlySet.Contains(codepoint))
                {
                    TraditionalHits++;
                }

                return;
            }

            if ((codepoint >= 0x3040 && codepoint <= 0x30FF) ||
                (codepoint >= 0x31F0 && codepoint <= 0x31FF) ||
                (codepoint >= 0xFF66 && codepoint <= 0xFF9D))
            {
                Kana++;
                return;
            }

            if ((codepoint >= 0xAC00 && codepoint <= 0xD7AF) ||
                (codepoint >= 0x1100 && codepoint <= 0x11FF) ||
                (codepoint >= 0x3130 && codepoint <= 0x318F))
            {
                Hangul++;
                return;
            }

            if ((codepoint >= 'A' && codepoint <= 'Z') ||
                (codepoint >= 'a' && codepoint <= 'z') ||
                (codepoint >= 0x00C0 && codepoint <= 0x024F))
            {
                Latin++;
                return;
            }

            if (codepoint >= 0x0400 && codepoint <= 0x04FF)
            {
                Cyrillic++;
                return;
            }

            if (codepoint >= 0x0600 && codepoint <= 0x06FF)
            {
                Arabic++;
                return;
            }

            if (codepoint >= 0x0E00 && codepoint <= 0x0E7F)
            {
                Thai++;
                return;
            }

            // Digits, punctuation, whitespace, symbols: not letters, not counted.
        }

        private static bool IsHan(int codepoint)
            => (codepoint >= 0x4E00 && codepoint <= 0x9FFF) ||
               (codepoint >= 0x3400 && codepoint <= 0x4DBF) ||
               (codepoint >= 0xF900 && codepoint <= 0xFAFF) ||
               (codepoint >= 0x20000 && codepoint <= 0x2EBEF);
    }
}
