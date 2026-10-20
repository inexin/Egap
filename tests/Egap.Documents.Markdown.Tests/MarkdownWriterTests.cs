namespace Egap.Documents.Markdown.Tests;

/// <summary>
/// The Markdown writer (Phase 6 Build Plan task 8): Markdown written back from the model, documents made in code or read
/// from other formats written as GitHub Flavored Markdown, and the warnings for what Markdown cannot hold.
/// </summary>
public class MarkdownWriterTests
{
    [Theory]
    [InlineData("# Title\n\nSome *italic*, **bold**, ~~gone~~ and `code`.")]
    [InlineData("- one\n- two\n  - nested\n- three")]
    [InlineData("1. first\n\n   more of first\n\n2. second")]
    [InlineData("> quoted\n>\n> > deeper\n\n- [ ] todo\n- [x] done")]
    [InlineData("```csharp\nvar x = 1;\n\nvar y = 2;\n```\n\n    indented")]
    [InlineData("| A | B |\n|:--|--:|\n| 1 | 2 |")]
    [InlineData("[Egap](https://github.com/inexin/Egap \"repo\") <https://example.com> ![alt](a.png)")]
    [InlineData("Text[^1] here.\n\n[^1]: The note.")]
    [InlineData("line one\\\nline two\n\n---\n\n\\# not a heading, 1\\. not a list, \\*not\\* emphasis")]
    public void MarkdownSurvivesTheModel(string markdown)
    {
        Document first = MarkdownFormat.Parse(markdown);
        string written = MarkdownFormat.ToMarkdown(first);
        Document second = MarkdownFormat.Parse(written);

        Assert.Equal(Describe(first), Describe(second));
        Assert.Equal(written, MarkdownFormat.ToMarkdown(second));
    }

    [Fact]
    public void DocumentBuiltInCodeIsWrittenAsMarkdown()
    {
        Document doc = new();
        DocumentBuilder builder = new(doc);
        builder.ParagraphStyle = (ParagraphStyle)doc.Styles.AddBuiltIn(BuiltInStyle.Heading2);
        builder.Writeln("Results");
        builder.ParagraphStyle = doc.Styles.DefaultParagraphStyle;
        builder.Write("Revenue grew ");
        builder.Font.Bold = true;
        builder.Write("12 %");
        builder.Font.Bold = null;
        builder.InsertParagraph();
        List numbered = doc.Lists.AddNumbered();
        builder.CurrentParagraph.SetList(numbered);
        builder.Write("first point");
        builder.InsertParagraph();
        builder.Write("second point");

        string markdown = MarkdownFormat.ToMarkdown(doc, out IReadOnlyList<ConversionWarning> warnings);

        Assert.Equal("## Results\n\nRevenue grew **12 %**\n\n1. first point\n2. second point\n", markdown);
        Assert.Empty(warnings);
    }

    [Fact]
    public void WarningsSayWhatMarkdownDrops()
    {
        Document doc = new();
        Section section = doc.AddSection();
        section.HeadersFooters.GetOrAdd(HeaderFooterKind.HeaderPrimary).AddParagraph("Header");
        Paragraph p = section.Body.AddParagraph();
        p.AddRun("red", new RunFormat { Color = DocumentColor.FromHex("#FF0000") });
        p.AddPicture(ImageData.FromBytes([0x89, (byte)'P', (byte)'N', (byte)'G']), 10, 10);
        p.Inlines.Add(new Comment(doc, "Lars"));
        Table table = section.Body.AddTable(1, 2);
        table.Rows[0].Cells[0].Format.GridSpan = 2;

        string markdown = MarkdownFormat.ToMarkdown(doc, out IReadOnlyList<ConversionWarning> warnings);

        Assert.Equal(
            [ConversionWarningKind.FormattingDropped, ConversionWarningKind.MergedCellsSplit, ConversionWarningKind.PictureDropped, ConversionWarningKind.PageLayoutDropped, ConversionWarningKind.CommentsDropped],
            warnings.Select(w => w.Kind));
        Assert.StartsWith("red\n", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void PicturesGoThroughTheImageWriterOrAsDataUris()
    {
        Document doc = new();
        Paragraph p = doc.AddSection().Body.AddParagraph();
        Picture picture = p.AddPicture(new ImageData(new byte[] { 1, 2, 3 }, "image/png"), 10, 10);
        picture.Description = "logo";

        string stored = MarkdownFormat.ToMarkdown(doc, new MarkdownSaveOptions { ImageWriter = _ => "images/logo.png" });
        string embedded = MarkdownFormat.ToMarkdown(doc, new MarkdownSaveOptions { EmbedImages = true });

        Assert.Equal("![logo](images/logo.png)\n", stored);
        Assert.Equal("![logo](data:image/png;base64,AQID)\n", embedded);
    }

    [Fact]
    public void NeighbouringListsStayApart()
    {
        Document doc = new();
        Body body = doc.AddSection().Body;
        List a = doc.Lists.AddBulleted();
        List b = doc.Lists.AddBulleted();
        body.AddParagraph("a1").SetList(a);
        body.AddParagraph("b1").SetList(b);

        string markdown = MarkdownFormat.ToMarkdown(doc);
        Document back = MarkdownFormat.Parse(markdown);

        Assert.NotSame(back.Sections[0].Body.Paragraphs.First().ListLabel!.List, back.Sections[0].Body.Paragraphs.Last().ListLabel!.List);
    }

    [Fact]
    public void FieldsChangesAndMonospaceRuns()
    {
        Document doc = new();
        Paragraph p = doc.AddSection().Body.AddParagraph("Page ");
        p.AddField("PAGE", "3");
        p.AddRun(" old").Revision = new Revision(RevisionKind.Deletion, "Ada");
        p.AddRun(" new").Revision = new Revision(RevisionKind.Insertion, "Ada");
        p.AddRun(" x = 1", new RunFormat { FontName = "Courier New" });

        string markdown = MarkdownFormat.ToMarkdown(doc, out IReadOnlyList<ConversionWarning> warnings);

        Assert.Equal("Page 3 new `x = 1`\n", markdown);
        Assert.Contains(warnings, w => w.Kind == ConversionWarningKind.FieldsFlattened);
        Assert.Contains(warnings, w => w.Kind == ConversionWarningKind.ChangesAccepted);
    }

    [Fact]
    public void SaveWritesUtf8WithoutBom()
    {
        Document doc = MarkdownFormat.Parse("# Ærø\n");
        using MemoryStream stream = new();

        MarkdownFormat.Save(doc, stream);

        Assert.Equal("# Ærø\n"u8.ToArray(), stream.ToArray());
    }

    [Fact]
    public void FootnoteHoldingOnlyATableReads()
    {
        Document doc = MarkdownFormat.Parse("x[^n]\n\n[^n]: | a |\n    |---|\n");

        Footnote note = doc.Descendants<Footnote>().Single();
        Assert.Single(note.Story.Tables);
        Assert.Contains("| a |", MarkdownFormat.ToMarkdown(doc), StringComparison.Ordinal);
    }

    /// <summary>What a document holds, as text: each block's kind, style, list label and text.</summary>
    private static string Describe(Document document)
    {
        return string.Join("\n", document.Sections[0].Body.Descendants().Select(n => n switch
        {
            Paragraph p => $"P {p.Style?.Id} {p.ListLabel?.Text} {p.GetText()}",
            Table t => $"T {t.Rows.Count}",
            Run r => $"R {r.Text} {r.Format.Bold} {r.Format.Italic} {r.Style?.Id}",
            Hyperlink h => $"H {h.Address} {h.Anchor}",
            Picture pic => $"I {pic.SourceUrl} {pic.Description}",
            _ => n.GetType().Name,
        }));
    }
}
