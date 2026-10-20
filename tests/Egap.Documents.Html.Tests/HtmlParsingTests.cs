using System.Text;

using Egap.Documents.Html;

namespace Egap.Documents.Html.Tests;

/// <summary>Parsing through AngleSharp: encodings, the tree the reader gets, and the guard against hostile nesting.</summary>
public class HtmlParsingTests
{
    [Fact]
    public void EncodingComesFromTheBomTheMetaOrTheFallback()
    {
        byte[] latin2 = [.. "<meta charset=iso-8859-2><p>"u8, 0xE8];
        byte[] late = [.. Encoding.ASCII.GetBytes("<!--" + new string('x', 2000) + "--><meta charset=\"windows-1251\"><p>"), 0xC0];
        byte[] bom = [0xEF, 0xBB, 0xBF, .. "<meta charset=windows-1252><p>"u8, 0xC3, 0xA9];
        byte[] undeclared = [.. "<p>"u8, 0xE9];

        Assert.Equal("\u010D", Text(latin2, null));
        Assert.Equal("\u0410", Text(late, null));
        Assert.Equal("\u00E9", Text(bom, null));
        Assert.Equal("\u00E9", Text(undeclared, null));
        Assert.Equal("\u0439", Text(undeclared, "windows-1251"));
    }

    [Fact]
    public void TheTreeKeepsNamespacesAttributesAndTemplates()
    {
        HtmlDocument document = HtmlParsing.Parse("<svg viewbox='0 0 1 1'><a xlink:href='#x'/></svg><template><td>c</template><p class='a b'>x");
        HtmlElement[] elements = [.. document.Descendants().OfType<HtmlElement>()];

        HtmlElement svg = elements.Single(e => e.LocalName == "svg");
        Assert.Equal(HtmlElement.SvgNamespace, svg.NamespaceUri);
        Assert.Equal("0 0 1 1", svg.GetAttribute("viewBox", null));
        Assert.Equal("#x", elements.Single(e => e.LocalName == "a").GetAttribute("href", HtmlElement.XLinkNamespace));
        Assert.True(((HtmlElement)elements.Single(e => e.IsHtml("template")).Content!.Children[0]).IsHtml("td"));
        Assert.True(elements.Single(e => e.IsHtml("p")).HasClass("b"));
        Assert.Equal(HtmlQuirksMode.Quirks, document.QuirksMode);
        Assert.Equal(HtmlQuirksMode.NoQuirks, HtmlParsing.Parse("<!DOCTYPE html><p>x").QuirksMode);
    }

    [Fact]
    public void OnlyHostileNestingIsCut()
    {
        string list = "<ul>" + string.Concat(Enumerable.Repeat("<li>item", 10_000)) + "</ul>";
        string nested = string.Concat(Enumerable.Repeat("<span>", HtmlNestingGuard.MaxDepth + 100)) + "deep";

        Assert.Same(list, HtmlNestingGuard.Limit(list));
        Assert.Equal(10_000, HtmlParsing.Parse(list).Descendants().OfType<HtmlElement>().Count(e => e.IsHtml("li")));
        string limited = HtmlNestingGuard.Limit(nested);
        Assert.EndsWith("deep", limited, StringComparison.Ordinal);
        Assert.Equal(HtmlNestingGuard.MaxDepth, limited.Split("<span>").Length - 1);
    }

    private static string Text(byte[] html, string? encoding)
    {
        return string.Concat(HtmlParsing.Parse(html, encoding).Body!.Descendants().OfType<HtmlText>().Select(t => t.Data));
    }
}
