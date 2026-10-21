using System.Diagnostics;
using System.Text;

using Egap.Documents.Layout;

namespace Egap.Documents.Html.Tests;

/// <summary>Hostile input for the readers and the layout (Phase 6 Build Plan task 28): deep nesting and repetition stay bounded.</summary>
public class HostileInputTests
{
    private static readonly LayoutOptions Isolated = new() { Fonts = new FontSettings { UseSystemFonts = false } };

    private static Document ReadAndLayOut(string html, TimeSpan limit)
    {
        Stopwatch watch = Stopwatch.StartNew();
        Document document = HtmlFormat.Parse(html);
        _ = document.Layout(Isolated).Pages.Count;
        Assert.True(watch.Elapsed < limit, $"took {watch.Elapsed}");
        return document;
    }

    [Fact]
    public void DeeplyNestedBlocksAreReadAsText()
    {
        string html = string.Concat(Enumerable.Repeat("<div>", 100_000)) + "deep text";

        Document document = ReadAndLayOut(html, TimeSpan.FromSeconds(30));

        Assert.Contains("deep text", document.GetText(), StringComparison.Ordinal);
    }

    [Fact]
    public void DeeplyNestedTablesAreReadAsText()
    {
        string html = string.Concat(Enumerable.Repeat("<table><tr><td>", 20_000)) + "cell text";

        Document document = ReadAndLayOut(html, TimeSpan.FromSeconds(30));

        Assert.Contains("cell text", document.GetText(), StringComparison.Ordinal);
    }

    [Fact]
    public void ManyFormattingTagsStayLinear()
    {
        StringBuilder html = new();
        for (int i = 0; i < 20_000; i++)
        {
            html.Append("<b><i><a href=x>").Append(i).Append("</b>");
        }

        ReadAndLayOut(html.ToString(), TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void HugeAttributesAndEntities()
    {
        string html = "<p title=\"" + new string('a', 1_000_000) + "\">" + string.Concat(Enumerable.Repeat("&amp;&notin;&#x1F600;", 50_000)) + "</p>";

        Document document = ReadAndLayOut(html, TimeSpan.FromSeconds(60));

        Assert.Contains("&∉\U0001F600", document.GetText(), StringComparison.Ordinal);
    }

    [Fact]
    public void PagesOverTheLimitAreRefused()
    {
        HtmlLoadOptions options = new() { Limits = new DocumentLimits { MaxPartSize = 1000 } };

        Assert.Throws<DocumentLimitException>(() => HtmlFormat.Parse(new string('x', 1000), options));
        Assert.Throws<DocumentLimitException>(() => HtmlFormat.Load(new byte[1001], out _, options));
    }
}
