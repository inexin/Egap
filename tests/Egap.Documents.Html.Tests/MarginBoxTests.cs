using Egap.Documents.Layout;

namespace Egap.Documents.Html.Tests;

/// <summary>Margin boxes: the model, layout, HTML's @page margin boxes and DOCX's three-part headers.</summary>
public sealed class MarginBoxTests
{
    private static readonly LayoutOptions Isolated = new() { Fonts = FontSettings.Portable };

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
    public void FuzzFindingsSaveAndLayOut()
    {
        // Control characters in an HTML id (a bookmark name) and title are left out of the DOCX instead of failing the save.
        Document html = HtmlFormat.Parse("<title>T</title><p id='xy'>Text</p>");
        using MemoryStream saved = new();
        DocxFormat.Save(html, saved);
        saved.Position = 0;
        Assert.Equal("Text", DocxFormat.Load(saved).Sections[0].Body.GetText().Trim());

        // Margins wider than the page with margin boxes, and a page far larger than PDF allows.
        Document document = Page(out Section section);
        section.PageSetup.MarginLeft = section.PageSetup.MarginRight = 300;
        section.MarginBoxes.GetOrAdd(MarginBoxPosition.TopCenter).AddParagraph("Box");
        DocxFormat.Save(document, Stream.Null);
        section.PageSetup.PageHeight = 1e12;
        PageLayout page = Assert.Single(document.Layout(Isolated).Pages);
        Assert.Equal(14400, page.Height);
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
