using System.IO.Compression;
using System.Xml.Linq;

using Egap.Documents.Docx;
using Egap.Documents.Layout;
using Egap.Documents.Pdf;
using Egap.Documents.Reports;

namespace Egap.Documents.Tests;

/// <summary>Floating tables and paragraph frames: the model, DOCX read and write, and their layout.</summary>
public class FloatingTableTests
{
    private static readonly LayoutOptions Isolated = new() { Fonts = new FontSettings { UseSystemFonts = false } };
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const double Margin = 72;

    [Fact]
    public void DocxReadsAndWritesTablePositions()
    {
        Document document = new();
        Table table = Body(document).AddTable(1, 1);
        table.Format.Position = new TablePosition
        {
            HorizontalAnchor = HorizontalAnchor.Margin,
            HorizontalAlignment = FloatingAlignment.End,
            VerticalAnchor = VerticalAnchor.Paragraph,
            VerticalOffset = 12,
            DistanceLeft = 9,
            DistanceBottom = 4,
            AllowOverlap = false,
        };

        byte[] docx = Save(document);
        XElement tblPr = Part(docx).Descendants(W + "tblPr").Single();
        TablePosition read = DocxFormat.Load(new MemoryStream(docx)).Sections[0].Body.Tables.Single().Format.Position!;

        Assert.Equal(table.Format.Position, read);
        Assert.Equal(["tblpPr", "tblOverlap"], tblPr.Elements().Take(2).Select(e => e.Name.LocalName));
        Assert.Equal("right", (string?)tblPr.Element(W + "tblpPr")!.Attribute(W + "tblpXSpec"));
        Assert.Equal("never", (string?)tblPr.Element(W + "tblOverlap")!.Attribute(W + "val"));
    }

    [Fact]
    public void DocxReadsAndWritesFrames()
    {
        Document document = new();
        Paragraph paragraph = Body(document).AddParagraph("Framed");
        paragraph.Format.Frame = new FramePosition
        {
            Width = 100,
            Height = 30,
            HeightRule = RowHeightRule.AtLeast,
            HorizontalAnchor = HorizontalAnchor.Page,
            HorizontalOffset = 50,
            VerticalAnchor = VerticalAnchor.Page,
            VerticalOffset = 60,
            DistanceHorizontal = 6,
            Wrap = FrameWrap.NotBeside,
        };
        Paragraph drop = Body(document).AddParagraph("D");
        drop.Format.Frame = new FramePosition { DropCap = DropCap.Drop, DropCapLines = 3, HorizontalAnchor = HorizontalAnchor.Column, VerticalAnchor = VerticalAnchor.Paragraph };

        Document read = DocxFormat.Load(new MemoryStream(Save(document)));

        Assert.Equal(paragraph.Format.Frame, read.Sections[0].Body.Paragraphs.First().Format.Frame);
        Assert.Equal(drop.Format.Frame, read.Sections[1].Body.Paragraphs.First().Format.Frame);
    }

    [Fact]
    public void MissingAnchorsAreTheTextsAcrossAndThePagesDown()
    {
        Document document = Load("""<w:tbl><w:tblPr><w:tblpPr w:tblpX="1440" w:tblpY="720"/></w:tblPr><w:tblGrid><w:gridCol w:w="2000"/></w:tblGrid><w:tr><w:tc><w:p/></w:tc></w:tr></w:tbl><w:p/>""");

        TablePosition position = document.Sections[0].Body.Tables.Single().Format.Position!;

        Assert.Equal(HorizontalAnchor.Column, position.HorizontalAnchor);
        Assert.Equal(VerticalAnchor.Page, position.VerticalAnchor);
        Assert.Equal(72, position.HorizontalOffset);
        Assert.True(position.AllowOverlap);
    }

    [Fact]
    public void TextWrapsBesideAFloatingTable()
    {
        Document document = new();
        Body body = Body(document);
        Table table = body.AddTable(2, 1);
        table.Grid = [150];
        table.Format.Position = new TablePosition { HorizontalAnchor = HorizontalAnchor.Column, VerticalAnchor = VerticalAnchor.Paragraph, DistanceRight = 10 };
        Paragraph text = body.AddParagraph("Text beside the floating table that wraps around it on the side with more room.");

        DocumentLayout layout = document.Layout(Isolated);

        LayoutRect tableBounds = layout.FindBoxes(table).Single().Box.Bounds;
        ParagraphBox paragraph = (ParagraphBox)layout.FindBoxes(text).First(b => b.Box is ParagraphBox).Box;
        Assert.Equal(Margin, tableBounds.X, 1);
        Assert.Equal(Margin, tableBounds.Y, 1);
        Assert.True(paragraph.Lines[0].Bounds.X >= tableBounds.Right + 10 - 0.5, $"line at {paragraph.Lines[0].Bounds.X}, table to {tableBounds.Right}");
    }

    [Fact]
    public void PageAnchorsAndAlignment()
    {
        Document document = new();
        Body body = Body(document);
        Table table = body.AddTable(1, 1);
        table.Grid = [100];
        table.Format.Position = new TablePosition
        {
            HorizontalAnchor = HorizontalAnchor.Margin,
            HorizontalAlignment = FloatingAlignment.End,
            VerticalAnchor = VerticalAnchor.Page,
            VerticalOffset = 200,
        };
        body.AddParagraph("x");

        LayoutRect bounds = document.Layout(Isolated).FindBoxes(table).Single().Box.Bounds;

        Assert.Equal(612 - Margin, bounds.Right, 1);
        Assert.Equal(200, bounds.Y, 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ATallFloatingTableGoesOnOnTheNextPage(bool textAfterBreak)
    {
        Document document = new();
        Body body = Body(document);
        Table table = body.AddTable(60, 1);
        table.Grid = [200];
        table.Rows[0].Format.IsHeader = true;
        for (int r = 0; r < table.Rows.Count; r++)
        {
            table.Rows[r].Cells[0].Paragraphs.Single().AddRun("row " + r);
            table.Rows[r].Format.Height = 20;
            table.Rows[r].Format.HeightRule = RowHeightRule.Exactly;
        }

        table.Format.Position = new TablePosition();
        Paragraph text = body.AddParagraph("After");
        document.Settings.WordCompatibility.AllowTextAfterFloatingTableBreak = textAfterBreak;

        DocumentLayout layout = document.Layout(Isolated);

        List<(PageLayout Page, LayoutBox Box)> parts = [.. layout.FindBoxes(table)];
        Assert.Equal(2, parts.Count);
        Assert.Equal(Margin, parts[1].Box.Bounds.Y, 1);
        Assert.True(((RowBox)parts[1].Box.Children[0]).IsRepeatedHeader);
        Assert.Equal(60 * 20, parts.Sum(p => p.Box.Bounds.Height) - 20, 1);
        Assert.Same(textAfterBreak ? layout.Pages[0] : layout.Pages[1], layout.FindBoxes(text)[0].Page);
    }

    [Fact]
    public void DoNotBreakWrappedTablesKeepsItWhole()
    {
        Document document = new();
        Body body = Body(document);
        Table table = body.AddTable(60, 1);
        foreach (Row row in table.Rows)
        {
            row.Format.Height = 20;
            row.Format.HeightRule = RowHeightRule.Exactly;
        }

        table.Format.Position = new TablePosition();
        body.AddParagraph("After");
        document.Settings.WordCompatibility.DoNotBreakWrappedTables = true;

        Assert.Single(document.Layout(Isolated).FindBoxes(table));
    }

    [Fact]
    public void TablesThatMayNotOverlapMoveDown()
    {
        Document document = new();
        Body body = Body(document);
        Table first = body.AddTable(2, 1);
        first.Format.Position = new TablePosition { VerticalAnchor = VerticalAnchor.Page, VerticalOffset = 100 };
        Table second = body.AddTable(1, 1);
        second.Format.Position = new TablePosition { VerticalAnchor = VerticalAnchor.Page, VerticalOffset = 110, AllowOverlap = false, DistanceTop = 5 };
        body.AddParagraph("x");

        DocumentLayout layout = document.Layout(Isolated);

        LayoutRect a = layout.FindBoxes(first).Single().Box.Bounds;
        LayoutRect b = layout.FindBoxes(second).Single().Box.Bounds;
        Assert.Equal(a.Bottom + 5, b.Y, 1);
    }

    [Fact]
    public void AFloatingTableInACellIsLaidOutInTheText()
    {
        Document document = new();
        Table outer = Body(document).AddTable(1, 1);
        Cell cell = outer.Rows[0].Cells[0];
        cell.Paragraphs.Single().AddRun("before");
        Table inner = new(document);
        cell.Blocks.Add(inner);
        inner.Rows.Add(new Row(document));
        inner.Rows[0].Cells.Add(new Cell(document));
        inner.Rows[0].Cells[0].AddParagraph("inner");
        inner.Format.Position = new TablePosition { VerticalAnchor = VerticalAnchor.Page, VerticalOffset = 500 };

        DocumentLayout layout = document.Layout(Isolated);

        Assert.True(layout.FindBoxes(inner).Single().Box.Bounds.Y < 200);
        LayoutWarning warning = Assert.Single(layout.Warnings);
        Assert.Equal(LayoutWarningKind.FloatLaidOutInline, warning.Kind);
        Assert.Same(inner, warning.Node);
        Assert.Equal(0, warning.PageIndex);
        Assert.Contains("table cell", warning.Message, StringComparison.Ordinal);

        // A second layout reuses the paragraphs it laid out before and still warns.
        Assert.Single(document.Layout(Isolated).Warnings);
    }

    [Fact]
    public void AFrameInAHeaderWarnsOnceThroughOnWarning()
    {
        Document document = new();
        Body(document).AddParagraph("Body text");
        Paragraph framed = document.Sections[0].HeadersFooters.GetOrAdd(HeaderFooterKind.HeaderPrimary).AddParagraph("Letterhead");
        framed.Format.Frame = new FramePosition { Width = 120, HorizontalAnchor = HorizontalAnchor.Page, HorizontalOffset = 400, VerticalAnchor = VerticalAnchor.Page, VerticalOffset = 30 };
        List<LayoutWarning> heard = [];

        DocumentLayout layout = document.Layout(new LayoutOptions { Fonts = Isolated.Fonts, OnWarning = heard.Add });

        LayoutWarning warning = Assert.Single(layout.Warnings);
        Assert.Same(framed, warning.Node);
        Assert.Contains("header", warning.Message, StringComparison.Ordinal);
        Assert.Equal([warning], heard);
    }

    [Fact]
    public void FloatsInTheBodyDoNotWarn()
    {
        Document document = new();
        Body body = Body(document);
        body.AddParagraph("Letterhead").Format.Frame = new FramePosition { Width = 120, HorizontalAnchor = HorizontalAnchor.Page, HorizontalOffset = 400, VerticalAnchor = VerticalAnchor.Page, VerticalOffset = 30 };
        body.AddTable(1, 1).Format.Position = new TablePosition { HorizontalAlignment = FloatingAlignment.End };
        body.AddParagraph("Body text");

        Assert.Empty(document.Layout(Isolated).Warnings);
    }

    [Fact]
    public void PdfExportTellsTheLayoutWarnings()
    {
        Document document = new();
        Table outer = Body(document).AddTable(1, 1);
        Paragraph framed = outer.Rows[0].Cells[0].Paragraphs.Single();
        framed.AddRun("framed in a cell");
        framed.Format.Frame = new FramePosition { Width = 120, HorizontalAnchor = HorizontalAnchor.Page, VerticalAnchor = VerticalAnchor.Page };
        List<LayoutWarning> heard = [];

        using MemoryStream pdf = new();
        document.SaveAsPdf(pdf, new PdfExportOptions { Layout = new LayoutOptions { Fonts = Isolated.Fonts, OnWarning = heard.Add } });

        Assert.Same(framed, Assert.Single(heard).Node);
        Assert.True(pdf.Length > 0);
    }

    [Fact]
    public void FramesArePlacedByTheirAnchors()
    {
        Document document = new();
        Body body = Body(document);
        Paragraph framed = body.AddParagraph("Letterhead");
        framed.Format.Frame = new FramePosition { Width = 120, HorizontalAnchor = HorizontalAnchor.Page, HorizontalOffset = 400, VerticalAnchor = VerticalAnchor.Page, VerticalOffset = 30 };
        Paragraph text = body.AddParagraph("Body text");

        DocumentLayout layout = document.Layout(Isolated);

        LayoutRect frame = layout.FindBoxes(framed).First(b => b.Box is ParagraphBox).Box.Bounds;
        Assert.Equal(400, frame.X, 1);
        Assert.Equal(30, frame.Y, 1);
        Assert.Equal(120, frame.Width, 1);
        Assert.Equal(Margin, layout.FindBoxes(text).First(b => b.Box is ParagraphBox).Box.Bounds.Y, 1);
    }

    [Fact]
    public void ADropCapSitsAtTheStartOfTheNextParagraph()
    {
        Document document = new();
        Body body = Body(document);
        Paragraph cap = body.AddParagraph();
        cap.AddRun("W").Format.Size = 40;
        cap.Format.Frame = new FramePosition { DropCap = DropCap.Drop, DropCapLines = 3, HorizontalAnchor = HorizontalAnchor.Column, VerticalAnchor = VerticalAnchor.Paragraph, DistanceHorizontal = 2 };
        Paragraph text = body.AddParagraph("ord by word the paragraph wraps beside the large letter for its first lines and then goes on under it.");

        DocumentLayout layout = document.Layout(Isolated);

        LayoutRect letter = layout.FindBoxes(cap).First(b => b.Box is ParagraphBox).Box.Bounds;
        ParagraphBox paragraph = (ParagraphBox)layout.FindBoxes(text).First(b => b.Box is ParagraphBox).Box;
        Assert.Equal(Margin, letter.X, 1);
        Assert.Equal(Margin, letter.Y, 1);
        Assert.True(paragraph.Lines[0].Bounds.X >= letter.Right, $"first line at {paragraph.Lines[0].Bounds.X}, letter to {letter.Right}");
    }

    [Fact]
    public void ReportsFloatTables()
    {
        Document document = Report.Create(r => r.Page(p => p.Content().Column(c =>
        {
            c.Item().Table(t => t.Columns(1).Floating(new TablePosition { HorizontalAlignment = FloatingAlignment.End }).Rows([1], i => [i]));
            c.Item().Text("beside");
        })));

        Table table = document.Sections[0].Body.Descendants().OfType<Table>().Single();

        Assert.Equal(FloatingAlignment.End, table.Format.Position!.HorizontalAlignment);
    }

    [Fact]
    public void TaggedExportKeepsAFloatingTableATable()
    {
        Document document = new();
        Body body = Body(document);
        Table table = body.AddTable(1, 1);
        table.Rows[0].Cells[0].Paragraphs.Single().AddRun("Floating cell");
        table.Format.Position = new TablePosition { HorizontalAlignment = FloatingAlignment.End };
        body.AddParagraph("Beside");

        using MemoryStream stream = new();
        document.SaveAsPdf(stream, new PdfExportOptions { Tagged = true, Layout = Isolated });
        using Egap.Pdf.PdfDocument pdf = Egap.Pdf.PdfDocument.Open(stream.ToArray());

        Egap.Pdf.PdfStructureElement[] all = [.. pdf.Structure.Root!.DescendantsAndSelf()];
        Assert.Contains(all, e => e.Type == Egap.Pdf.PdfStructureType.Table);
        Assert.Contains("Floating cell", pdf.Pages[0].ExtractText(), StringComparison.Ordinal);
    }

    private static Body Body(Document document)
    {
        Section section = document.AddSection();
        section.PageSetup.PageWidth = 612;
        section.PageSetup.PageHeight = 792;
        section.PageSetup.MarginLeft = Margin;
        section.PageSetup.MarginRight = Margin;
        section.PageSetup.MarginTop = Margin;
        section.PageSetup.MarginBottom = Margin;
        return section.Body;
    }

    private static byte[] Save(Document document)
    {
        using MemoryStream stream = new();
        DocxFormat.Save(document, stream);
        return stream.ToArray();
    }

    private static XElement Part(byte[] docx)
    {
        using ZipArchive zip = new(new MemoryStream(docx));
        using Stream part = zip.GetEntry("word/document.xml")!.Open();
        return XElement.Load(part);
    }

    /// <summary>A DOCX whose body is <paramref name="body"/>, loaded.</summary>
    private static Document Load(string body)
    {
        using MemoryStream source = new(Save(new Document()));
        using MemoryStream patched = new();
        source.CopyTo(patched);
        using (ZipArchive zip = new(patched, ZipArchiveMode.Update, leaveOpen: true))
        {
            zip.GetEntry("word/document.xml")!.Delete();
            using StreamWriter writer = new(zip.CreateEntry("word/document.xml").Open());
            writer.Write("<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>" + body + "</w:body></w:document>");
        }

        patched.Position = 0;
        return DocxFormat.Load(patched);
    }
}
