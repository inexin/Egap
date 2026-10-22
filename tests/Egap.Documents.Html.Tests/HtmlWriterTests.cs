using Egap.Documents.Html;

namespace Egap.Documents.Html.Tests;

/// <summary>The model written as HTML5 (Phase 6 Build Plan task 26), checked by reading it back.</summary>
public class HtmlWriterTests
{
    private static Document Sample()
    {
        Document document = new();
        document.Properties.Title = "Report & co";
        Section section = document.AddSection();
        section.PageSetup.PageWidth = 595.3;
        section.PageSetup.PageHeight = 841.9;
        section.PageSetup.MarginLeft = 50;
        Body body = section.Body;
        Paragraph heading = body.AddParagraph("Heading <one>");
        heading.Style = (ParagraphStyle)document.Styles.AddBuiltIn(BuiltInStyle.Heading1);
        heading.Inlines.Insert(0, new BookmarkStart(document, "start"));
        Paragraph text = body.AddParagraph("Plain ");
        text.AddRun("bold").Format.Bold = true;
        text.AddRun(" red").Format.Color = DocumentColor.FromHex("FF0000");
        text.AddHyperlink(" link", "https://example.com/?a=1&b=2");
        text.Format.Alignment = ParagraphAlignment.Center;
        List list = document.Lists.AddNumbered();
        body.AddParagraph("first").SetList(list);
        body.AddParagraph("nested").SetList(list, 1);
        body.AddParagraph("second").SetList(list);
        Table table = body.AddTable(2, 3);
        table.Rows[0].Cells[0].Format.GridSpan = 2;
        table.Rows[0].Cells.RemoveAt(1);
        table.Rows[0].Cells[1].Format.VerticalMerge = VerticalMerge.Restart;
        table.Rows[1].Cells[2].Format.VerticalMerge = VerticalMerge.Continue;
        table.Rows[0].Cells[0].Paragraphs.First().AddRun("wide");
        table.Rows[0].Cells[1].Paragraphs.First().AddRun("tall");
        body.AddParagraph("Note").AddFootnote("The note text.");
        return document;
    }

    [Fact]
    public void WritesSemanticHtmlThatReadsBack()
    {
        string html = HtmlFormat.ToHtml(Sample(), out IReadOnlyList<ConversionWarning> warnings);

        Assert.StartsWith("<!DOCTYPE html>", html, StringComparison.Ordinal);
        Assert.Contains("<title>Report &amp; co</title>", html, StringComparison.Ordinal);
        Assert.Contains("<h1 class=\"p-Heading1\" id=\"start\">Heading &lt;one&gt;</h1>", html, StringComparison.Ordinal);
        Assert.Contains("<strong>bold</strong>", html, StringComparison.Ordinal);
        Assert.Contains("href=\"https://example.com/?a=1&amp;b=2\"", html, StringComparison.Ordinal);
        Assert.Contains("colspan=\"2\"", html, StringComparison.Ordinal);
        Assert.Contains("rowspan=\"2\"", html, StringComparison.Ordinal);
        Assert.Contains("@page { size: 595.3pt 841.9pt;", html, StringComparison.Ordinal);
        Assert.DoesNotContain(warnings, w => w.Kind != ConversionWarningKind.FieldsFlattened);

        Document read = HtmlFormat.Parse(html);
        Paragraph[] paragraphs = [.. read.Sections[0].Body.Paragraphs];
        Assert.Equal(595.3, read.Sections[0].PageSetup.PageWidth!.Value, 1);
        Assert.Equal(0, paragraphs[0].GetEffectiveFormat().OutlineLevel);
        Assert.Contains(paragraphs[0].Inlines, i => i is BookmarkStart { Name: "start" });
        Assert.True(paragraphs[1].Runs.Single(r => r.Text == "bold").GetEffectiveFormat().Bold);
        Assert.Equal(DocumentColor.FromHex("FF0000"), paragraphs[1].Runs.Single(r => r.Text.Trim() == "red").Format.Color);
        Assert.Equal(ParagraphAlignment.Center, paragraphs[1].Format.Alignment);
        Assert.Equal(["first", "nested", "second"], paragraphs.Where(p => p.ListLabel is not null).Take(3).Select(p => p.GetText().Trim('\r')).ToArray());
        Assert.Equal(1, paragraphs.Single(p => p.GetText().StartsWith("nested", StringComparison.Ordinal)).Format.ListLevel);
        Table table = read.Sections[0].Body.Tables.Single();
        Assert.Equal(2, table.Rows[0].Cells[0].Format.GridSpan);
        Assert.Equal(VerticalMerge.Continue, table.Rows[1].Cells[^1].Format.VerticalMerge);
        Assert.Contains("The note text.", read.GetText(), StringComparison.Ordinal);
    }

    [Fact]
    public void PicturesEmbeddedOrThroughTheCallback()
    {
        Document document = new();
        byte[] png = [0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10, 1, 2, 3];
        document.AddSection().Body.AddParagraph().AddPicture(new ImageData(png, "image/png"), 72, 36).Description = "logo";

        string embedded = HtmlFormat.ToHtml(document);
        string linked = HtmlFormat.ToHtml(document, new HtmlSaveOptions { ImageWriter = _ => "images/logo.png" });
        string dropped = HtmlFormat.ToHtml(document, out IReadOnlyList<ConversionWarning> warnings, new HtmlSaveOptions { EmbedImages = false });

        Assert.Contains("src=\"data:image/png;base64,iVBORw0KGgoBAgM=\" alt=\"logo\"", embedded, StringComparison.Ordinal);
        Assert.Contains("width: 72pt; height: 36pt", embedded, StringComparison.Ordinal);
        Assert.Contains("src=\"images/logo.png\"", linked, StringComparison.Ordinal);
        Assert.DoesNotContain("<img", dropped, StringComparison.Ordinal);
        Assert.Contains(warnings, w => w.Kind == ConversionWarningKind.PictureDropped);
    }

    [Fact]
    public void WrittenHtmlParsesWithoutErrors()
    {
        string html = HtmlFormat.ToHtml(Sample());
        List<string> errors = [];
        AngleSharp.Html.Parser.HtmlParser parser = new();
        parser.Error += (_, e) => errors.Add(((AngleSharp.Html.Dom.Events.HtmlErrorEvent)e).Message);

        parser.ParseDocument(html);

        Assert.Empty(errors);
    }

    [Fact]
    public void ABookmarkOrNoteInsideALinkDoesNotNestLinks()
    {
        Document document = new();
        Paragraph paragraph = document.AddSection().Body.AddParagraph("See ");
        Hyperlink link = paragraph.AddHyperlink("the site", "https://example.com/");
        link.Inlines.Insert(0, new BookmarkStart(document, "site"));
        Footnote note = new(document);
        note.AddParagraph("A note.");
        link.Inlines.Add(note);

        string html = HtmlFormat.ToHtml(document);

        string anchor = html[html.IndexOf("<a href=\"https://example.com/\"", StringComparison.Ordinal)..];
        anchor = anchor[..(anchor.IndexOf("</a>", StringComparison.Ordinal) + 4)];
        Assert.Equal(1, anchor.Split("<a ").Length - 1);
        Assert.Contains("<span id=\"site\"></span>", anchor, StringComparison.Ordinal);
        Assert.Contains("id=\"fnref1\"", anchor, StringComparison.Ordinal);
        Paragraph read = HtmlFormat.Parse(html).Sections[0].Body.Paragraphs.First();
        Assert.Contains(read.Descendants().OfType<BookmarkStart>(), b => b.Name == "site");
        Assert.StartsWith("the site", read.Inlines.OfType<Hyperlink>().Single().GetText(), StringComparison.Ordinal);
    }
}
