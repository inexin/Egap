using System.Text.Json;

using Egap.Pdf.Conversion.Tests.Synthetic;

namespace Egap.Pdf.Conversion.Tests.Docling;

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

    [Fact]
    public void PicturesCarryTheirImageAsDataUrisFilesOrNothing()
    {
        using PdfDocument created = PdfDocument.Create();
        PdfPage blank = created.Pages.AddBlank(new PdfRectangle(0, 0, 400, 400));
        using (PdfCanvas canvas = new(blank))
        using (Egami.Raster<Egami.Pixels.Rgba8> image = Egami.Raster.Create<Egami.Pixels.Rgba8>(6, 6, new Egami.Pixels.Rgba8(250, 10, 10, 255)))
        {
            canvas.DrawText("A paragraph of text above the picture on the page.", new System.Numerics.Vector2(40, 40), SyntheticDocument.Body,
                new Egami.Drawing.SolidPaint(new Egami.Pixels.RgbaF32(0, 0, 0, 1)));
            canvas.DrawImage(image, new Egami.Drawing.RectF(100, 100, 120, 120));
        }

        using MemoryStream saved = new();
        created.Save(saved);
        using PdfDocument pdf = PdfDocument.Open(saved.ToArray());
        IReadOnlyList<PdfPageLayout> pages = PdfLayoutAnalyzer.Analyze(pdf, cancellationToken: TestContext.Current.CancellationToken);
        string folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "egap-docling-" + Guid.NewGuid().ToString("N"));

        string inline = DoclingJson.Write(pages);
        string omitted = DoclingJson.Write(pages, new DoclingJsonOptions { Images = DoclingImageMode.Omit });
        string files = DoclingJson.Write(pages, new DoclingJsonOptions { Images = DoclingImageMode.Files, ImagesFolder = folder });

        using JsonDocument a = JsonDocument.Parse(inline), b = JsonDocument.Parse(omitted), c = JsonDocument.Parse(files);
        JsonElement picture = a.RootElement.GetProperty("pictures")[0].GetProperty("image");
        Assert.StartsWith("data:image/png;base64,", picture.GetProperty("uri").GetString(), StringComparison.Ordinal);
        Assert.False(b.RootElement.GetProperty("pictures")[0].TryGetProperty("image", out _));
        Assert.Equal(System.IO.Path.GetFileName(folder) + "/image-001.png", c.RootElement.GetProperty("pictures")[0].GetProperty("image").GetProperty("uri").GetString());
        Assert.True(File.Exists(System.IO.Path.Combine(folder, "image-001.png")));
        Assert.Throws<InvalidOperationException>(() => DoclingJson.Write(pages, new DoclingJsonOptions { Images = DoclingImageMode.Files }));

        // The data URI comes back as the picture of the document read from the JSON.
        Egap.Documents.Document model = DoclingJson.ToDocument(inline);
        Assert.Equal("image/png", Assert.Single(model.Descendants<Egap.Documents.Picture>()).Image.ContentType);
        Directory.Delete(folder, true);
    }

    [Theory]
    [MemberData(nameof(Scenes))]
    public void DoclingJsonBecomesADocument(string scene)
    {
        using PdfDocument pdf = PdfDocument.Open(SyntheticScenes.All[scene]().Save());
        IReadOnlyList<PdfPageLayout> pages = PdfLayoutAnalyzer.Analyze(pdf, cancellationToken: TestContext.Current.CancellationToken);

        Egap.Documents.Document model = DoclingJson.ToDocument(DoclingJson.Write(pages));

        string text = string.Join(" ", model.Descendants<Egap.Documents.Paragraph>().Select(p => p.GetText()));
        foreach (LayoutBlock block in pages.SelectMany(p => p.Blocks).Where(b => b.Role is not (LayoutRole.ListItem or LayoutRole.PageHeader or LayoutRole.PageFooter or LayoutRole.PageNumber)))
        {
            Assert.Contains(block.Text, text, StringComparison.Ordinal);
        }

        Assert.Equal(pages.SelectMany(p => p.Tables).Count(t => !t.ContinuesPrevious) + pages.SelectMany(p => p.Tables).Count(t => t.ContinuesPrevious), model.Descendants<Egap.Documents.Table>().Count());
        Egap.Documents.DocxFormat.Save(model, Stream.Null);
    }
}
