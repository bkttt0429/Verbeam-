using System.Xml.Linq;

namespace Verbeam.Core.Services;

/// <summary>
/// Groups OOXML run text (<c>&lt;w:t&gt;</c> / <c>&lt;a:t&gt;</c>) by its nearest
/// paragraph ancestor so a sentence split across formatting runs is translated as
/// one unit instead of run-by-run. The merged translation is written back into the
/// first run of the group and the remaining runs are blanked, keeping each run's
/// formatting while consolidating its text.
/// <para>
/// Known v1 limitation: structural breaks expressed as sibling elements
/// (<c>&lt;w:br/&gt;</c>, <c>&lt;w:tab/&gt;</c>) are not part of the merged text, so a
/// hard line break inside a paragraph collapses to a space-less join. This favours
/// coherent sentence translation over exact intra-paragraph layout.
/// </para>
/// </summary>
public static class OoxmlParagraphGrouping
{
    private const string WordprocessingNs = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string DrawingNs = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private const string SpreadsheetNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace XmlNs = "http://www.w3.org/XML/1998/namespace";

    public static bool IsTextElement(XElement element)
        => element.Name.LocalName == "t" &&
           element.Name.NamespaceName is WordprocessingNs or DrawingNs or SpreadsheetNs;

    /// <summary>
    /// Returns the non-empty run text elements grouped by paragraph, in document
    /// order. Whitespace-only runs are skipped (left untouched by the caller).
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<XElement>> GroupTextNodes(XDocument document, string? sourceKind)
    {
        var groups = new List<IReadOnlyList<XElement>>();
        if (document.Root is null)
        {
            return groups;
        }

        var paragraphNames = ParagraphLocalNames(sourceKind);
        List<XElement>? current = null;
        XElement? currentKey = null;

        foreach (var node in document.Descendants().Where(IsTextElement))
        {
            if (string.IsNullOrWhiteSpace(node.Value))
            {
                continue;
            }

            var key = FindParagraph(node, paragraphNames) ?? node;
            if (!ReferenceEquals(key, currentKey))
            {
                if (current is { Count: > 0 })
                {
                    groups.Add(current);
                }

                current = new List<XElement>();
                currentKey = key;
            }

            current!.Add(node);
        }

        if (current is { Count: > 0 })
        {
            groups.Add(current);
        }

        return groups;
    }

    /// <summary>Concatenates a group's run text into a single translation source.</summary>
    public static string JoinGroupText(IReadOnlyList<XElement> group)
        => string.Concat(group.Select(element => element.Value));

    /// <summary>
    /// Writes <paramref name="translation"/> into the group's first run and blanks the
    /// rest. Preserves leading/trailing whitespace by marking the first run
    /// <c>xml:space="preserve"</c> when needed.
    /// </summary>
    public static void ApplyTranslation(IReadOnlyList<XElement> group, string translation)
    {
        if (group.Count == 0)
        {
            return;
        }

        var first = group[0];
        if (translation.Length != translation.Trim().Length)
        {
            first.SetAttributeValue(XmlNs + "space", "preserve");
        }

        first.Value = translation;
        for (var index = 1; index < group.Count; index++)
        {
            group[index].Value = string.Empty;
        }
    }

    private static XElement? FindParagraph(XElement node, IReadOnlyCollection<string> paragraphNames)
        => node.Ancestors().FirstOrDefault(ancestor => paragraphNames.Contains(ancestor.Name.LocalName));

    private static IReadOnlyCollection<string> ParagraphLocalNames(string? sourceKind)
        => sourceKind?.Trim().ToLowerInvariant() switch
        {
            "xlsx" => new[] { "si", "is" },
            "docx" or "pptx" => new[] { "p" },
            _ => new[] { "p", "si", "is" }
        };
}
