using Egap.Documents.Layout;

namespace Egap.Documents.Tests;

/// <summary>Margin boxes: the model, layout, HTML's @page margin boxes and DOCX's three-part headers.</summary>
public sealed class MarginBoxTests
{
    private static readonly LayoutOptions Isolated = new() { Fonts = FontSettings.Portable };

    [Fact]
    public void BoxesAreLaidOutInTheMarginsWithPageNumbers()
    {
        Document document = Page(out Section section);
        for (int i = 0; i < 40; i++)
        {
            section.Body.AddParagraph("Body line " + i);
        }

        DocumentBuilder builder = new(document);
        builder.MoveToMarginBox(MarginBoxPosition.BottomRight);
        builder.Write("Page ");
        builder.InsertField("PAGE", "1");
        builder.Write(" of ");
        builder.InsertField("NUMPAGES", "1");
        builder.MoveToMarginBox(MarginBoxPosition.TopLeft);
        builder.Write("Report");
        builder.MoveToMarginBox(MarginBoxPosition.LeftMiddle);
        builder.Write("Draft");

        DocumentLayout layout = document.Layout(Isolated);

        Assert.True(layout.Pages.Count > 1);
        foreach (PageLayout page in layout.Pages)
        {
            AreaBox[] boxes = [.. page.Boxes.OfType<AreaBox>().Where(a => a.Area == PageArea.MarginBox)];
            Assert.Equal(3, boxes.Length);
            AreaBox number = boxes.Single(b => ((MarginBox)b.Node!).Position == MarginBoxPosition.BottomRight);
            Assert.True(number.Bounds.Y >= 400 - 36 - 0.5);
            Assert.Equal(200, number.Bounds.X, 1);
            string text = string.Concat(number.Descendants().OfType<LineBox>().Select(l => l.Text));
            Assert.Equal($"Page {page.Index + 1} of {layout.Pages.Count}", text.Trim());

            AreaBox side = boxes.Single(b => ((MarginBox)b.Node!).Position == MarginBoxPosition.LeftMiddle);
            Assert.True(side.Bounds.Right <= 36.5);
            Assert.InRange(side.Bounds.Y, 100, 200);
        }

        // Boxes leave the body where it was.
        Document plain = Page(out Section plainSection);
        for (int i = 0; i < 40; i++)
        {
            plainSection.Body.AddParagraph("Body line " + i);
        }

        Assert.Equal(plain.Layout(Isolated).Pages.Count, layout.Pages.Count);
    }

    [Fact]
    public void AFirstPageBoxReplacesTheBoxForEveryPage()
    {
        Document document = Page(out Section section);
        section.Body.AddParagraph("One");
        new DocumentBuilder(document).InsertBreak(BreakKind.Page);
        section.Body.AddParagraph("Two");
        section.MarginBoxes.GetOrAdd(MarginBoxPosition.TopCenter).AddParagraph("Running title");
        section.MarginBoxes.GetOrAdd(MarginBoxPosition.TopCenter, firstPage: true);

        DocumentLayout layout = document.Layout(Isolated);

        Assert.Equal(2, layout.Pages.Count);
        Assert.DoesNotContain(layout.Pages[0].Boxes.OfType<AreaBox>(), a => a.Area == PageArea.MarginBox);
        Assert.Single(layout.Pages[1].Boxes.OfType<AreaBox>(), a => a.Area == PageArea.MarginBox);
        Assert.Throws<ArgumentException>(() => section.MarginBoxes.Add(new MarginBox(document, MarginBoxPosition.TopCenter)));
        Assert.Equal(2, section.Clone().MarginBoxes.Count);
    }

    [Fact]
    public void HtmlPageMarginBoxesReadAndWriteBack()
    {
        Document document = HtmlFormat.Parse("""
            <style>
            @page { size: A5; margin: 20mm;
              @bottom-center { content: "Page " counter(page) " of " counter(pages); font-size: 9pt; }
              @top-right { content: "ACME"; color: #c00000; }
              @left-middle { content: "Left"; } }
            @page :first { @top-right { content: none; } }
            </style><p>Hello</p>
            """);

        Section section = document.Sections[0];
        MarginBox bottom = section.MarginBoxes[MarginBoxPosition.BottomCenter]!;
        Assert.StartsWith("Page ", bottom.GetText(), StringComparison.Ordinal);
        Assert.Equal([FieldKind.Page, FieldKind.NumPages], bottom.Descendants<FieldStart>().Select(f => f.GetField()!.Kind));
        Assert.Equal(ParagraphAlignment.Center, bottom.Paragraphs.First().Format.Alignment);
        Assert.Equal(9, bottom.Descendants<Run>().First().Format.Size);
        MarginBox right = section.MarginBoxes[MarginBoxPosition.TopRight]!;
        Assert.Equal(ParagraphAlignment.Right, right.Paragraphs.First().Format.Alignment);
        Assert.Empty(section.MarginBoxes[MarginBoxPosition.TopRight, firstPage: true]!.Blocks);
        Assert.Null(section.MarginBoxes[MarginBoxPosition.BottomCenter, firstPage: true]);

        DocumentLayout layout = document.Layout(Isolated);
        string number = string.Concat(layout.Pages[0].Boxes.OfType<AreaBox>().Single(a => a.Node == bottom).Descendants().OfType<LineBox>().Select(l => l.Text));
        Assert.Equal("Page 1 of 1", number.Trim());

        string html = HtmlFormat.ToHtml(document, out _);
        Assert.Contains("@bottom-center { content: \"Page \" counter(page) \" of \" counter(pages);", html, StringComparison.Ordinal);
        Assert.Contains("@page :first { @top-right { content: none; }", html, StringComparison.Ordinal);
        Document again = HtmlFormat.Parse(html);
        Assert.Equal(section.MarginBoxes.Count, again.Sections[0].MarginBoxes.Count);
        Assert.Equal(right.GetText(), again.Sections[0].MarginBoxes[MarginBoxPosition.TopRight]!.GetText());
    }

    [Fact]
    public void DocxWritesTopAndBottomBoxesAsAThreePartTable()
    {
        Document document = Page(out Section section);
        section.Body.AddParagraph("Body");
        DocumentBuilder builder = new(document);
        builder.MoveToHeaderFooter(HeaderFooterKind.FooterPrimary);
        builder.Write("Confidential");
        builder.MoveToMarginBox(MarginBoxPosition.BottomLeft);
        builder.Write("Left");
        builder.MoveToMarginBox(MarginBoxPosition.BottomRight);
        builder.InsertField("PAGE", "1");
        builder.MoveToMarginBox(MarginBoxPosition.TopCenter, firstPage: true);
        builder.Write("Cover");
        builder.MoveToMarginBox(MarginBoxPosition.RightMiddle);
        builder.Write("Side");

        using MemoryStream stream = new();
        IReadOnlyList<ConversionWarning> warnings = DocxFormat.Save(document, stream);
        stream.Position = 0;
        Document read = DocxFormat.Load(stream);

        Assert.Contains(warnings, w => w.Kind == ConversionWarningKind.PageLayoutDropped);
        Section back = read.Sections[0];
        Assert.True(back.PageSetup.DifferentFirstPage);
        HeaderFooter footer = back.HeadersFooters[HeaderFooterKind.FooterPrimary]!;
        Assert.Equal("Confidential", footer.Paragraphs.First().GetText().Trim());
        Table table = Assert.Single(footer.Tables);
        Assert.Equal(3, table.Rows[0].Cells.Count);
        Assert.Equal("Left", table.Rows[0].Cells[0].GetText().Trim());
        Assert.Equal(ParagraphAlignment.Right, table.Rows[0].Cells[2].Paragraphs.First().Format.Alignment);
        Assert.Single(table.Rows[0].Cells[2].Descendants<FieldStart>());
        HeaderFooter first = back.HeadersFooters[HeaderFooterKind.HeaderFirst]!;
        Assert.Equal("Cover", first.Tables.Single().Rows[0].Cells[1].GetText().Trim());

        // The first page keeps the footer it had, with the boxes for every page.
        Assert.Equal("Confidential", back.HeadersFooters[HeaderFooterKind.FooterFirst]!.Paragraphs.First().GetText().Trim());

        // The model itself is unchanged.
        Assert.Null(section.HeadersFooters[HeaderFooterKind.HeaderFirst]);
        Assert.Null(section.PageSetup.DifferentFirstPage);
    }

    private static Document Page(out Section section)
    {
        Document document = new();
        section = document.AddSection();
        section.PageSetup.PageWidth = 400;
        section.PageSetup.PageHeight = 400;
        section.PageSetup.MarginTop = section.PageSetup.MarginBottom = section.PageSetup.MarginLeft = section.PageSetup.MarginRight = 36;
        return document;
    }
}
