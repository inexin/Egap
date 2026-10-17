using Egap.Documents;
using Egap.Documents.Layout;

namespace Egap.Pdf.Layout.Tests;

/// <summary>Flow documents saved as PDF (Phase 6 Build Plan task 22).</summary>
public class PdfExportTests
{
    private static readonly LayoutOptions Isolated = new() { Fonts = new FontSettings { UseSystemFonts = false } };

    private static PdfDocument Reopen(Document document, PdfExportOptions? options = null)
    {
        using MemoryStream stream = new();
        document.SaveAsPdf(stream, (options ?? new PdfExportOptions()) with { Layout = Isolated });
        return PdfDocument.Open(stream.ToArray());
    }

    private static byte[] Png()
    {
        using MemoryStream stream = new();
        new Document().Layout(Isolated).Pages[0].Render(4).Save(stream, "png");
        return stream.ToArray();
    }

    [Fact]
    public void PagesTextAndMetadata()
    {
        Document document = new();
        Body body = document.AddSection().Body;
        for (int i = 0; i < 80; i++)
        {
            body.AddParagraph("Paragraph number " + i);
        }

        document.Properties.Title = "Export test";
        document.Properties.Author = "Egap";
        document.Styles.DefaultRunFormat.Language = "nb-NO";

        using PdfDocument pdf = Reopen(document);

        Assert.True(pdf.Pages.Count >= 2);
        Assert.Equal(new PdfRectangle(0, 0, 612, 792), pdf.Pages[0].MediaBox);
        Assert.Contains("Paragraph number 0", pdf.Pages[0].ExtractText());
        Assert.Contains("Paragraph number 79", pdf.Pages[^1].ExtractText());
        Assert.Equal("Export test", pdf.Metadata.Title);
        Assert.Equal("Egap", pdf.Metadata.Author);
        Assert.Equal("nb-NO", pdf.Catalog.GetText("Lang"));
        Assert.False(pdf.Structure.Exists);
    }

    [Fact]
    public void HyperlinksBookmarksAndFieldLinks()
    {
        Document document = new();
        Body body = document.AddSection().Body;
        Paragraph first = body.AddParagraph("See ");
        first.AddHyperlink("the site", "https://example.com/");
        Paragraph inside = body.AddParagraph("Go to ");
        Hyperlink jump = inside.AddHyperlink("the target", "x");
        jump.Address = null;
        jump.Anchor = "target";
        Paragraph target = body.AddParagraph();
        target.Format.PageBreakBefore = true;
        target.Inlines.Add(new BookmarkStart(document, "target"));
        target.AddRun("Target text");

        using PdfDocument pdf = Reopen(document);

        PdfLinkAnnotation[] links = [.. pdf.Pages[0].Annotations.OfType<PdfLinkAnnotation>()];
        Assert.Equal(2, links.Length);
        Assert.Equal("https://example.com/", Assert.IsType<PdfUriAction>(links[0].Action).Uri);
        Assert.True(links[0].Rectangle.Right > links[0].Rectangle.Left + 20);
        Assert.InRange(links[0].Rectangle.Top, 700, 730);
        Assert.Equal("target", Assert.IsType<PdfGoToAction>(links[1].Action).DestinationName);
        Assert.True(pdf.NamedDestinations.TryGet("target", out PdfDestination destination));
        Assert.Equal(1, destination.GetPageIndex(pdf));
        Assert.Equal(720, destination.Top!.Value, 1);
    }

    [Fact]
    public void ALinkOverTwoLinesGetsAnAnnotationForEach()
    {
        Document document = new();
        document.AddSection().Body.AddParagraph().AddHyperlink(string.Join(' ', Enumerable.Repeat("long link text", 20)), "https://example.com/");

        using PdfDocument pdf = Reopen(document);

        PdfLinkAnnotation[] links = [.. pdf.Pages[0].Annotations.OfType<PdfLinkAnnotation>()];
        Assert.True(links.Length >= 2);
        Assert.All(links, l => Assert.Equal("https://example.com/", Assert.IsType<PdfUriAction>(l.Action).Uri));
    }

    [Fact]
    public void HyperlinkFieldCodesParse()
    {
        Assert.Equal(("https://a.b/c d", null), PdfExporter.ParseHyperlink(" HYPERLINK \"https://a.b/c d\" "));
        Assert.Equal((null, "_Toc123"), PdfExporter.ParseHyperlink(" HYPERLINK \\l \"_Toc123\" \\h "));
        Assert.Equal(("file.docx", "part"), PdfExporter.ParseHyperlink("HYPERLINK \"file.docx\" \\l \"part\" \\o \"tip text\""));
    }

    [Fact]
    public void OutlineFromHeadingsAndBookmarks()
    {
        Document document = new();
        Body body = document.AddSection().Body;
        Paragraph chapter = body.AddParagraph("Chapter one");
        chapter.Format.OutlineLevel = 0;
        chapter.Inlines.Add(new BookmarkStart(document, "chapter"));
        Paragraph section = body.AddParagraph("Section  one\tdot one");
        section.Format.OutlineLevel = 1;
        body.AddParagraph("Body text");
        Paragraph second = body.AddParagraph("Chapter two");
        second.Format.OutlineLevel = 0;
        second.Inlines.Add(new BookmarkStart(document, "_Hidden"));

        using PdfDocument pdf = Reopen(document);

        Assert.Equal(["Chapter one", "Chapter two"], pdf.Outline.Children.Select(i => i.Title).ToArray());
        Assert.Equal("Section one dot one", Assert.Single(pdf.Outline.Children[0].Children).Title);

        using PdfDocument bookmarks = Reopen(document, new PdfExportOptions { Outline = OutlineFrom.Bookmarks });
        Assert.Equal("chapter", Assert.Single(bookmarks.Outline.Children).Title);

        using PdfDocument none = Reopen(document, new PdfExportOptions { Outline = OutlineFrom.None, NamedDestinations = false, Links = false });
        Assert.Empty(none.Outline.Children);
        Assert.Empty(none.NamedDestinations.GetNames());
    }

    [Fact]
    public void TaggedExportBuildsTheStructureTree()
    {
        Document document = new();
        Section section = document.AddSection();
        Body body = section.Body;
        body.AddParagraph("Title").Format.OutlineLevel = 0;
        Paragraph text = body.AddParagraph("Some text with ");
        text.AddHyperlink("a link", "https://example.com/");
        List list = document.Lists.AddNumbered();
        body.AddParagraph("first").SetList(list);
        body.AddParagraph("second").SetList(list);
        Table table = body.AddTable(2, 2);
        table.Rows[0].Format.IsHeader = true;
        table.Rows[0].Cells[0].Paragraphs.First().AddRun("Head");
        table.Rows[1].Cells[0].Paragraphs.First().AddRun("Cell");
        Picture picture = body.AddParagraph().AddPicture(new ImageData(Png(), "image/png"), 40, 30);
        picture.Description = "A blank page";
        section.HeadersFooters.GetOrAdd(HeaderFooterKind.HeaderPrimary).AddParagraph("Running header");

        using PdfDocument pdf = Reopen(document, new PdfExportOptions { Tagged = true });

        PdfStructureElement root = Assert.IsType<PdfStructureElement>(pdf.Structure.Root);
        Assert.Equal(PdfStructureType.Document, root.Type);
        Assert.Equal(
            ["H1", "P", "L", "Table", "P"],
            root.Elements.Select(e => e.Type.Name).ToArray());
        PdfStructureElement[] all = [.. root.DescendantsAndSelf()];
        Assert.Equal(2, all.Count(e => e.Type == PdfStructureType.LI));
        Assert.Equal(2, all.Count(e => e.Type == PdfStructureType.TH));
        Assert.Equal(2, all.Count(e => e.Type == PdfStructureType.TD));
        Assert.Equal("A blank page", all.Single(e => e.Type == PdfStructureType.Figure).AlternateText);
        Assert.Single(all, e => e.Type == PdfStructureType.Link);
        Assert.Equal("S", pdf.Pages[0].Dictionary.GetName("Tabs")?.Value);

        // The header is an artifact: its text is on the page but in no element.
        Assert.Contains("Running header", pdf.Pages[0].ExtractText());
    }

    [Fact]
    public void PdfAAndUniversalAccessibility()
    {
        Document document = new();
        Body body = document.AddSection().Body;
        body.AddParagraph("Heading").Format.OutlineLevel = 0;
        body.AddParagraph("Archived text.").AddHyperlink(" Link", "https://example.com/");
        document.Properties.Title = "Archive";
        document.Styles.DefaultRunFormat.Language = "en-US";

        using PdfDocument archive = Reopen(document, new PdfExportOptions { Conformance = PdfAConformance.PdfA2b });
        PdfAReport report = PdfAConverter.Check(archive, PdfAConformance.PdfA2b);
        Assert.True(report.Succeeded, string.Join("\n", report.Unmet.Select(i => i.Clause + " " + i.Description)));

        using PdfDocument accessible = Reopen(document, new PdfExportOptions { Conformance = PdfAConformance.PdfA2a, UniversalAccessibility = true });
        Assert.True(accessible.Structure.Exists);
        PdfAReport ua = PdfAConverter.CheckUniversalAccessibility(accessible);
        Assert.True(ua.Succeeded, string.Join("\n", ua.Unmet.Select(i => i.Clause + " " + i.Description)));
        PdfAReport a = PdfAConverter.Check(accessible, PdfAConformance.PdfA2a);
        Assert.True(a.Succeeded, string.Join("\n", a.Unmet.Select(i => i.Clause + " " + i.Description)));
    }
}
