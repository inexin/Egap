using System.Text;

using Egami.Css;

using Egap.Documents.Html;

namespace Egap.Documents.Html.Tests;

/// <summary>The HTML parser and its DOM (Phase 6 Build Plan task 24); html5lib's tests run in the compare repo.</summary>
public class HtmlParserTests
{
    [Fact]
    public void MisnestedMarkupBuildsTheBrowsersTree()
    {
        HtmlDocument document = HtmlTreeBuilder.Parse("<!DOCTYPE html><p>One<b>two<i>three</b>four</i><table>x<tr><td>cell</table>");

        HtmlElement body = document.Body!;
        HtmlElement p = (HtmlElement)body.Children[0];
        Assert.Equal("p", p.LocalName);
        // "four" goes into a second i, as browsers rebuild it after the misnested b closes.
        Assert.Equal(["#text", "b", "i"], p.Children.Select(Name).ToArray());

        // The text in the table is fostered before it; the cell gets an implied tbody and tr.
        HtmlElement table = body.Descendants().OfType<HtmlElement>().Single(e => e.LocalName == "table");
        Assert.Equal("x", ((HtmlText)table.Parent!.Children[table.Index - 1]).Data);
        Assert.Equal(["tbody", "tr", "td"], table.Descendants().OfType<HtmlElement>().Select(e => e.LocalName).ToArray());

        static string Name(HtmlNode node) => node is HtmlElement element ? element.LocalName : "#text";
    }

    [Fact]
    public void ForeignContentAndTemplates()
    {
        HtmlDocument document = HtmlTreeBuilder.Parse("<svg viewbox='0 0 1 1'><foreignobject><p>x</p></foreignobject></svg><template><td>c</template>");

        HtmlElement svg = document.Descendants().OfType<HtmlElement>().Single(e => e.LocalName == "svg");
        Assert.Equal(HtmlElement.SvgNamespace, svg.NamespaceUri);
        Assert.Equal("0 0 1 1", svg.GetAttribute("viewBox", null));
        HtmlElement foreignObject = (HtmlElement)svg.Children[0];
        Assert.Equal("foreignObject", foreignObject.LocalName);
        Assert.True(((HtmlElement)foreignObject.Children[0]).IsHtml("p"));

        HtmlElement template = document.Descendants().OfType<HtmlElement>().Single(e => e.IsHtml("template"));
        Assert.Empty(template.Children);
        Assert.True(((HtmlElement)template.Content!.Children[0]).IsHtml("td"));
    }

    [Fact]
    public void FragmentsParseInTheirContext()
    {
        HtmlFragment cells = HtmlTreeBuilder.ParseFragment("<td>a<td>b", "tr");
        HtmlFragment text = HtmlTreeBuilder.ParseFragment("<b>not bold</b>", "title");

        Assert.Equal(2, cells.Children.OfType<HtmlElement>().Count(e => e.IsHtml("td")));
        Assert.Equal("<b>not bold</b>", ((HtmlText)text.Children.Single()).Data);
    }

    [Fact]
    public void EncodingFromTheBomOrTheMeta()
    {
        byte[] latin2 = [.. "<meta charset=iso-8859-2><p>"u8, 0xE8];
        HtmlDocument czech = HtmlTreeBuilder.Parse(latin2, out string used);
        Assert.Equal("ISO-8859-2", used);
        Assert.Equal("č", ((HtmlText)czech.Body!.Descendants().OfType<HtmlText>().Single()).Data);

        // A meta beyond the prescan's 1024 bytes still changes the encoding, by parsing again.
        byte[] late = Encoding.ASCII.GetBytes("<!--" + new string('x', 2000) + "--><meta charset=\"windows-1251\"><p>").Concat(new byte[] { 0xC0 }).ToArray();
        HtmlDocument russian = HtmlTreeBuilder.Parse(late, out used);
        Assert.Equal("windows-1251", used);
        Assert.Equal("А", russian.Body!.Descendants().OfType<HtmlText>().Single().Data);

        HtmlTreeBuilder.Parse([0xEF, 0xBB, 0xBF, .. "<meta charset=windows-1252>é"u8], out used);
        Assert.Equal("UTF-8", used);
    }

    [Fact]
    public void ElementsAnswerCssSelectors()
    {
        HtmlDocument document = HtmlTreeBuilder.Parse("<div id=main class='note wide'><p>first</p><p lang=nb>second</p><span></span></div>");
        HtmlElement[] elements = [.. document.Descendants().OfType<HtmlElement>()];

        string[] Matching(string selector) =>
            [.. elements.Where(e => CssSelectorList.Parse(selector).Matches(e)).Select(e => e.LocalName + (e.Id is { } id ? "#" + id : ""))];

        Assert.Equal(["div#main"], Matching("DIV.note.wide"));
        Assert.Equal(["p"], Matching("#main > p:first-child"));
        Assert.Equal(["p"], Matching("p:lang(nb)"));
        Assert.Equal(["span"], Matching("p + p ~ :empty"));
    }
}
