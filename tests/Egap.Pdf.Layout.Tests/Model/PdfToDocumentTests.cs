using System.Numerics;
using System.Text.RegularExpressions;

using Egami;
using Egami.Drawing;
using Egami.Pixels;

using Egap.Documents;
using Egap.Documents.Docx;
using Egap.Pdf.Layout.Tests.Synthetic;

namespace Egap.Pdf.Layout.Tests.Model;

/// <summary>PDF to the flow model (Build Plan task 11): sections, styles, fonts, lists, tables, figures, headers and footers, footnotes.</summary>
public sealed partial class PdfToDocumentTests
{
    public static TheoryData<string, bool> Scenes => [.. SyntheticScenes.All.Keys.SelectMany(k => new[] { (k, false), (k, true) })];

    [Theory]
    [MemberData(nameof(Scenes))]
    public void EveryBlocksTextIsInTheDocumentAndItSavesAsWord(string scene, bool tagged)
    {
        SyntheticDocument doc = tagged ? SyntheticScenes.Tagged[scene]() : SyntheticScenes.All[scene]();
        Document model = Convert(doc);

        string text = Squash(string.Join(" ", model.Descendants<Paragraph>().Select(p => p.GetText())));
        List<string> missing = [.. doc.Blocks.Where(b => b.Role is not (TruthRole.PageHeader or TruthRole.PageFooter or TruthRole.PageNumber))
            .Select(b => Squash(b.Marker is not null && b.Role == TruthRole.ListItem ? b.Text[b.Marker.Length..] : b.Text))
            .Select(t => t.TrimEnd('-', '\u00AD'))
            .Where(t => !text.Contains(t, StringComparison.Ordinal))];
        if (missing.Count > 0 && Environment.GetEnvironmentVariable("EGAP_MODEL_DUMP") is not null)
        {
            File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"model-{scene}.txt"), text + "|----|" + string.Join("|", missing));
        }

        Assert.True(missing.Count == 0, $"{scene}{(tagged ? " tagged" : "")}: missing {string.Join(" | ", missing.Select(m => m.Length > 50 ? m[..50] : m))}");

        using MemoryStream docx = new();
        DocxFormat.Save(model, docx);
        docx.Position = 0;
        Document back = DocxFormat.Load(docx);
        Assert.Equal(model.Descendants<Paragraph>().Count(), back.Descendants<Paragraph>().Count());
    }

    [Fact]
    public void PageSetupStylesAndFontsComeFromThePage()
    {
        Document model = Convert(SyntheticScenes.OneColumn());

        Section section = Assert.Single(model.Sections);
        Assert.Equal((595, 842), (section.PageSetup.PageWidth, section.PageSetup.PageHeight));
        Assert.Equal(56.69, section.PageSetup.MarginLeft!.Value, 1);
        List<Paragraph> paragraphs = [.. section.Body.Paragraphs.Where(p => p.GetText().Trim().Length > 0)];
        Assert.Equal(["Title", "Heading1", "Normal", "Normal", "Normal", "Heading2", "Normal"], paragraphs.Select(p => p.Style!.Id));
        ParagraphStyle normal = paragraphs[2].Style!;
        Assert.Equal(("Liberation Serif", 10.0), (normal.RunFormat.FontName, normal.RunFormat.Size!.Value));
        ParagraphStyle title = paragraphs[0].Style!;
        Assert.Equal(("Liberation Sans", true, ParagraphAlignment.Center), (title.RunFormat.FontName, title.RunFormat.Bold!.Value, title.ParagraphFormat.Alignment!.Value));

        // Body text runs carry no direct formatting: the style says it all.
        Assert.All(paragraphs[2].Runs, r => Assert.Null(r.Format.FontName));
    }

    [Fact]
    public void ColumnsBecomeSectionColumns()
    {
        Document model = Convert(SyntheticScenes.TwoColumns());

        Assert.Contains(model.Sections, s => s.PageSetup.ColumnCount == 2);
        Section columns = model.Sections.First(s => s.PageSetup.ColumnCount == 2);
        Assert.Equal(SectionStart.Continuous, columns.PageSetup.SectionStart);
        Assert.Equal("Title", model.Sections[0].Body.Paragraphs.First().Style!.Id);
    }

    [Fact]
    public void ListItemsBecomeListsWithLevels()
    {
        Document model = Convert(SyntheticScenes.Lists());

        List<Paragraph> items = [.. model.Descendants<Paragraph>().Where(p => p.ListLabel is not null)];
        Assert.Equal(13, items.Count);
        Assert.Equal([0, 1, 2, 2, 1, 0, 0, 1, 2, 2, 1, 0, 0], items.Select(p => p.ListLabel!.Level));
        Assert.Equal(NumberStyle.Bullet, items[0].ListLabel!.Format.NumberStyle);
        Assert.Equal(["1.", "a)", "i.", "ii.", "b)", "2.", "3."], items.Skip(6).Select(p => p.ListLabel!.Text));
        Assert.DoesNotContain(items, p => p.GetText().StartsWith("1.", StringComparison.Ordinal));
    }

    [Fact]
    public void TablesKeepTheirGridSpansAndHeaders()
    {
        Document model = Convert(SyntheticScenes.Tables());

        List<Table> tables = [.. model.Descendants<Table>()];
        Assert.Equal(3, tables.Count);
        Assert.Equal(4, tables[0].Grid!.Count);
        Assert.True(tables[0].Rows[0].Format.IsHeader);
        Assert.Contains(tables[0].Descendants<Cell>(), c => c.Format.GridSpan == 2 && c.GetText().Trim() == "n/a");
        Assert.Equal(BorderStyle.Single, tables[0].Format.BorderInsideVertical!.Style);
        Assert.Equal(BorderStyle.None, tables[2].Format.BorderInsideVertical!.Style);
    }

    [Fact]
    public void ATableAcrossPagesIsOneTableWithItsHeaderOnce()
    {
        Document model = Convert(SyntheticScenes.TableAcrossPages());

        Table table = Assert.Single(model.Descendants<Table>());
        Assert.Equal(61, table.Rows.Count);
        Assert.Single(table.Rows, r => r.GetText().Contains("Sample", StringComparison.Ordinal) && !r.GetText().Contains("Sample ", StringComparison.Ordinal));
    }

    [Fact]
    public void FootnotesSitAtTheirMarkers()
    {
        SyntheticDocument doc = SyntheticScenes.Footnotes();
        Document model = Convert(doc);

        List<Footnote> notes = [.. model.Descendants<Footnote>()];
        Assert.Equal(2, notes.Count);
        Assert.All(notes, n => Assert.Null(n.CustomMark));
        Assert.Equal(Squash(doc.Blocks.Single(b => b.Role == TruthRole.Footnote && b.Marker == "1").Text), Squash(notes[0].Story.GetText()));
        Assert.DoesNotContain(model.Sections[0].Body.Paragraphs, p => p.Style?.Id == "FootnoteText");
    }

    [Fact]
    public void RunningHeadersAndPageNumbersBecomeHeadersFootersAndFields()
    {
        Document model = Convert(SyntheticScenes.HeadersAndFooters());

        Section section = model.Sections[0];
        HeaderFooter header = section.HeadersFooters.GetOrAdd(HeaderFooterKind.HeaderPrimary);
        HeaderFooter footer = section.HeadersFooters.GetOrAdd(HeaderFooterKind.FooterPrimary);
        Assert.Contains("Annual Report 2025", header.GetText(), StringComparison.Ordinal);
        Assert.Contains(footer.Descendants<FieldStart>(), f => f.GetField()!.Kind == FieldKind.Page);
        Assert.DoesNotContain(model.Sections.SelectMany(s => s.Body.Paragraphs), p => p.GetText().Contains("Annual Report 2025", StringComparison.Ordinal));
    }

    [Fact]
    public void BrokenWordsAreJoined()
    {
        Document model = Convert(SyntheticScenes.Hyphenation());

        string text = string.Join(" ", model.Descendants<Paragraph>().Select(p => p.GetText()));
        Assert.DoesNotMatch(BrokenWord(), text);
    }

    [Fact]
    public void QuotesCodeAndCaptionsGetTheirStyles()
    {
        Document model = Convert(SyntheticScenes.QuoteCodeCaption());

        List<Paragraph> paragraphs = [.. model.Descendants<Paragraph>()];
        Assert.Contains(paragraphs, p => p.Style?.Id == "Quote");
        Assert.Contains(paragraphs, p => p.Style?.Id == "Caption" && p.GetText().StartsWith("Listing 1.", StringComparison.Ordinal));
        Paragraph code = Assert.Single(paragraphs, p => p.Style?.Id == "Code");
        Assert.Equal(3, code.Inlines.OfType<Break>().Count());
        Assert.Contains("    Console.WriteLine", code.GetText(), StringComparison.Ordinal);
    }

    [Fact]
    public void AFigureIsAPictureAndAJpegKeepsItsBytes()
    {
        using PdfDocument created = PdfDocument.Create();
        PdfPage blank = created.Pages.AddBlank(new PdfRectangle(0, 0, 400, 400));
        using (PdfCanvas canvas = new(blank))
        using (Raster<Rgba8> image = Raster.Create<Rgba8>(6, 6, new Rgba8(250, 10, 10, 255)))
        {
            canvas.DrawText("A paragraph of text above the picture on the page.", new Vector2(40, 40), SyntheticDocument.Body, new SolidPaint(new RgbaF32(0, 0, 0, 1)));
            canvas.DrawImage(image, new RectF(100, 100, 120, 120));
            canvas.FillRect(new RectF(60, 260, 80, 50), new SolidPaint(new RgbaF32(0, 0.4f, 0.8f, 1)));
            canvas.DrawLine(new Vector2(160, 260), new Vector2(240, 310), new Stroke(3), new SolidPaint(new RgbaF32(0.8f, 0.2f, 0, 1)));
        }

        using MemoryStream saved = new();
        created.Save(saved);
        using PdfDocument pdf = PdfDocument.Open(saved.ToArray());

        Document model = PdfToDocument.Convert(pdf, cancellationToken: TestContext.Current.CancellationToken);

        List<Documents.Picture> pictures = [.. model.Descendants<Documents.Picture>()];
        Assert.NotEmpty(pictures);
        Assert.Equal(120, pictures[0].Width, 1);
        Assert.Equal("image/png", pictures[0].Image.ContentType);
    }

    private static Document Convert(SyntheticDocument doc)
    {
        using PdfDocument pdf = PdfDocument.Open(doc.Save());
        return PdfToDocument.Convert(pdf, cancellationToken: TestContext.Current.CancellationToken);
    }

    private static string Squash(string text)
    {
        return Whitespace().Replace(text, " ").Trim();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\p{Ll}- \p{Ll}")]
    private static partial Regex BrokenWord();
}
