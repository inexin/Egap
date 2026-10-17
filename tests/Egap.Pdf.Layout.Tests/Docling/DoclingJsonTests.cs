using System.Text.Json;

using Egap.Pdf.Layout.Tests.Synthetic;

namespace Egap.Pdf.Layout.Tests.Docling;

/// <summary>DoclingDocument JSON (Build Plan task 18): written from page layouts and read back to the same layouts.</summary>
public sealed class DoclingJsonTests
{
    public static TheoryData<string> Scenes => [.. SyntheticScenes.All.Keys];

    [Theory]
    [MemberData(nameof(Scenes))]
    public void LayoutsGoThroughDoclingJsonAndBack(string scene)
    {
        using PdfDocument pdf = PdfDocument.Open(SyntheticScenes.All[scene]().Save());
        IReadOnlyList<PdfPageLayout> pages = PdfLayoutAnalyzer.Analyze(pdf, cancellationToken: TestContext.Current.CancellationToken);

        string json = DoclingJson.Write(pages, new DoclingJsonOptions { Name = scene });
        IReadOnlyList<PdfPageLayout> back = DoclingJson.Read(json);

        Assert.Equal(pages.Count, back.Count);
        foreach ((PdfPageLayout a, PdfPageLayout b) in pages.Zip(back))
        {
            Assert.Equal((a.Width, a.Height), (b.Width, b.Height));
            Assert.Equal(a.Blocks.Select(x => (x.Role, x.Text, x.Level)), b.Blocks.Select(x => (x.Role, x.Text, x.Level)));
            Assert.Equal(a.Tables.Select(t => (t.RowCount, t.ColumnCount, t.IsRuled)), b.Tables.Select(t => (t.RowCount, t.ColumnCount, t.IsRuled)));
            Assert.Equal(a.Tables.SelectMany(t => t.Cells).Select(c => (c.Row, c.Column, c.RowSpan, c.ColumnSpan, c.Text)),
                b.Tables.SelectMany(t => t.Cells).Select(c => (c.Row, c.Column, c.RowSpan, c.ColumnSpan, c.Text)));
            foreach ((LayoutBlock x, LayoutBlock y) in a.Blocks.Zip(b.Blocks))
            {
                Assert.Equal(x.Bounds.X, y.Bounds.X, 1);
                Assert.Equal(x.Bounds.Y, y.Bounds.Y, 1);
                Assert.Equal(x.Bounds.Height, y.Bounds.Height, 1);
            }
        }
    }

    [Fact]
    public void TheJsonIsDoclingsShape()
    {
        using PdfDocument pdf = PdfDocument.Open(SyntheticScenes.HeadersAndFooters().Save());

        using JsonDocument json = JsonDocument.Parse(DoclingJson.Write(PdfLayoutAnalyzer.Analyze(pdf, cancellationToken: TestContext.Current.CancellationToken)));

        JsonElement root = json.RootElement;
        Assert.Equal(("DoclingDocument", "1.10.0"), (root.GetProperty("schema_name").GetString(), root.GetProperty("version").GetString()));
        JsonElement first = root.GetProperty("texts")[0];
        Assert.Equal("#/texts/0", first.GetProperty("self_ref").GetString());
        Assert.Equal("BOTTOMLEFT", first.GetProperty("prov")[0].GetProperty("bbox").GetProperty("coord_origin").GetString());
        Assert.Contains(root.GetProperty("texts").EnumerateArray(), t => t.GetProperty("content_layer").GetString() == "furniture" && t.GetProperty("label").GetString() == "page_header");
        Assert.Contains(root.GetProperty("body").GetProperty("children").EnumerateArray(), c => c.GetProperty("$ref").GetString() == "#/texts/0" || c.GetProperty("$ref").GetString()!.StartsWith("#/texts/", StringComparison.Ordinal));
        Assert.True(root.GetProperty("pages").GetProperty("1").GetProperty("size").GetProperty("width").GetDouble() > 0);
    }

    [Fact]
    public void ListItemsSitInListGroups()
    {
        using PdfDocument pdf = PdfDocument.Open(SyntheticScenes.Lists().Save());

        using JsonDocument json = JsonDocument.Parse(DoclingJson.Write(PdfLayoutAnalyzer.Analyze(pdf, cancellationToken: TestContext.Current.CancellationToken)));

        JsonElement groups = json.RootElement.GetProperty("groups");
        Assert.Equal(2, groups.GetArrayLength());
        Assert.All(groups.EnumerateArray(), g => Assert.Equal("list", g.GetProperty("label").GetString()));
        Assert.Equal(7, groups[1].GetProperty("children").GetArrayLength());
    }

    [Fact]
    public void NotDoclingIsAnError()
    {
        Assert.Throws<JsonException>(() => DoclingJson.Read("{\"texts\": []}"));
        Assert.ThrowsAny<JsonException>(() => DoclingJson.Read("not json"));
    }
}
