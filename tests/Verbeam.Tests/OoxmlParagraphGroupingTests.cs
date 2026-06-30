using System.Xml.Linq;
using Verbeam.Core.Services;

namespace Verbeam.Tests;

public sealed class OoxmlParagraphGroupingTests
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Xml = "http://www.w3.org/XML/1998/namespace";

    private static XElement Run(XNamespace ns, string text) => new(ns + "r", new XElement(ns + "t", text));

    [Fact]
    public void GroupTextNodes_Docx_MergesRunsInSameParagraph()
    {
        var document = new XDocument(new XElement(W + "document",
            new XElement(W + "body",
                new XElement(W + "p", Run(W, "Hel"), Run(W, "lo")),
                new XElement(W + "p", Run(W, "World")))));

        var groups = OoxmlParagraphGrouping.GroupTextNodes(document, "docx");

        Assert.Equal(2, groups.Count);
        Assert.Equal("Hello", OoxmlParagraphGrouping.JoinGroupText(groups[0]));
        Assert.Equal("World", OoxmlParagraphGrouping.JoinGroupText(groups[1]));
    }

    [Fact]
    public void GroupTextNodes_SkipsWhitespaceOnlyRuns()
    {
        var document = new XDocument(new XElement(W + "document",
            new XElement(W + "body",
                new XElement(W + "p", Run(W, "Keep"), Run(W, "   ")))));

        var group = Assert.Single(OoxmlParagraphGrouping.GroupTextNodes(document, "docx"));
        Assert.Equal("Keep", OoxmlParagraphGrouping.JoinGroupText(group));
    }

    [Fact]
    public void GroupTextNodes_Pptx_GroupsByDrawingParagraph()
    {
        var document = new XDocument(new XElement(A + "txBody",
            new XElement(A + "p", Run(A, "Foo"), Run(A, "Bar"))));

        var group = Assert.Single(OoxmlParagraphGrouping.GroupTextNodes(document, "pptx"));
        Assert.Equal("FooBar", OoxmlParagraphGrouping.JoinGroupText(group));
    }

    [Fact]
    public void GroupTextNodes_Xlsx_GroupsBySharedStringItem()
    {
        var document = new XDocument(new XElement(S + "sst",
            new XElement(S + "si", new XElement(S + "t", "Cell A")),
            new XElement(S + "si", new XElement(S + "t", "Cell B"))));

        var groups = OoxmlParagraphGrouping.GroupTextNodes(document, "xlsx");
        Assert.Equal(new[] { "Cell A", "Cell B" }, groups.Select(OoxmlParagraphGrouping.JoinGroupText));
    }

    [Fact]
    public void ApplyTranslation_WritesFirstRunAndBlanksRest()
    {
        var document = new XDocument(new XElement(W + "document",
            new XElement(W + "body",
                new XElement(W + "p", Run(W, "Hel"), Run(W, "lo")))));
        var group = Assert.Single(OoxmlParagraphGrouping.GroupTextNodes(document, "docx"));

        OoxmlParagraphGrouping.ApplyTranslation(group, "你好");

        Assert.Equal("你好", group[0].Value);
        Assert.Equal(string.Empty, group[1].Value);
    }

    [Fact]
    public void ApplyTranslation_PreservesEdgeWhitespace()
    {
        var document = new XDocument(new XElement(W + "document",
            new XElement(W + "body", new XElement(W + "p", Run(W, "x")))));
        var group = Assert.Single(OoxmlParagraphGrouping.GroupTextNodes(document, "docx"));

        OoxmlParagraphGrouping.ApplyTranslation(group, " 你好 ");

        Assert.Equal(" 你好 ", group[0].Value);
        Assert.Equal("preserve", group[0].Attribute(Xml + "space")?.Value);
    }
}
