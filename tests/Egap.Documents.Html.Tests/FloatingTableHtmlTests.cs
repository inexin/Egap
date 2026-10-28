namespace Egap.Documents.Html.Tests;

/// <summary>CSS float on a table: read as a floating table, written back as a float.</summary>
public sealed class FloatingTableHtmlTests
{
    [Fact]
    public void FloatedTablesFloatAndGoBack()
    {
        const string html = "<table style=\"float: right; margin-left: 12pt\"><tr><td>a</td></tr></table><p>text beside</p>";

        Document document = HtmlFormat.Parse(html);
        Table table = document.Descendants<Table>().Single();
        string written = document.ToHtml();

        Assert.Equal(FloatingAlignment.End, table.Format.Position!.HorizontalAlignment);
        Assert.Equal(HorizontalAnchor.Column, table.Format.Position.HorizontalAnchor);
        Assert.Equal(12, table.Format.Position.DistanceLeft, 1);
        Assert.Contains("float: right", written, StringComparison.Ordinal);
        Assert.Null(HtmlFormat.Parse("<table><tr><td>a</td></tr></table>").Descendants<Table>().Single().Format.Position);
    }
}
