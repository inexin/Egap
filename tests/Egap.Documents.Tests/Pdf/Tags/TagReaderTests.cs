using System.Numerics;

using Egami.Drawing;
using Egami.Pixels;

using Egap.Documents.Pdf;
using Egap.Documents.Tests.Pdf.Synthetic;
using Egap.Pdf;

namespace Egap.Documents.Tests.Pdf.Tags;

/// <summary>Tagged files first (Build Plan task 3): tags that pass the sanity check give roles, order, tables and figures.</summary>
public sealed class TagReaderTests
{
    private static readonly SolidPaint Black = new(new RgbaF32(0, 0, 0, 1));

    public static TheoryData<string> Scenes => [.. SyntheticScenes.Tagged.Keys];

    [Theory]
    [MemberData(nameof(Scenes))]
    public void TagsGiveTheReadingOrderAndRoles(string scene)
    {
        SyntheticDocument doc = SyntheticScenes.Tagged[scene]();
        using PdfDocument pdf = PdfDocument.Open(doc.Save());
        TagIndex index = TagIndex.Read(pdf)!;

        List<(LayoutRole Role, int Level, string Text, TagNode? Tag)> found = [];
        for (int page = 0; page < doc.PageCount; page++)
        {
            TagResult result = TagReader.Read(index, pdf.Pages[page], PageReader.Read(pdf, page));
            Assert.True(result.Draft is not null, $"{scene} page {page + 1}: {result.Problem}");
            foreach (BlockDraft block in result.Draft.Blocks.Where(b => b.Role != LayoutRole.Footnote).OrderBy(b => b.Order))
            {
                // A paragraph that runs on to the next page is one element: its parts join.
                if (found.Count > 0 && found[^1].Tag == block.Tag)
                {
                    found[^1] = found[^1] with { Text = found[^1].Text + " " + block.Text };
                }
                else
                {
                    // A list item's lines start with its marker, which the truth keeps apart.
                    string text = block.Marker is string marker && block.Text.StartsWith(marker, StringComparison.Ordinal) ? block.Text[marker.Length..] : block.Text;
                    found.Add((block.Role, block.Level, text, block.Tag));
                }
            }
        }

        // The tags keep a footnote's superscript marker in its paragraph, where the truth keeps it apart.
        List<TruthBlock> truth = [.. doc.Blocks.Where(b => b.Order >= 0 && !b.Continues).OrderBy(b => b.Order)];
        List<string> expected = [.. doc.ReadingOrderText().Select((t, i) => truth[i].Role == TruthRole.Paragraph && truth[i].Marker is string m ? t + m : t)];
        Assert.Equal(expected.Select(Squash), found.Select(f => Squash(f.Text)));
        Assert.Equal(truth.Select(b => (Role(b.Role), b.Role is TruthRole.Heading or TruthRole.ListItem ? b.Level : 0)), found.Select(f => (f.Role, f.Level)));
    }

    [Fact]
    public void ListItemsKeepTheirMarkers()
    {
        SyntheticDocument doc = SyntheticScenes.Lists(tagged: true);
        using PdfDocument pdf = PdfDocument.Open(doc.Save());

        PageDraft draft = TagReader.Read(TagIndex.Read(pdf)!, pdf.Pages[0], PageReader.Read(pdf, 0)).Draft!;

        Assert.Equal(doc.Blocks.Where(b => b.Role == TruthRole.ListItem).Select(b => b.Marker),
            draft.Blocks.Where(b => b.Role == LayoutRole.ListItem).OrderBy(b => b.Order).Select(b => b.Marker));
    }

    [Fact]
    public void TablesComeWithTheirCellsAndSpans()
    {
        SyntheticDocument doc = SyntheticScenes.Tables(tagged: true);
        using PdfDocument pdf = PdfDocument.Open(doc.Save());

        PageDraft draft = TagReader.Read(TagIndex.Read(pdf)!, pdf.Pages[0], PageReader.Read(pdf, 0)).Draft!;

        Assert.Equal(3, draft.Tables.Count);
        for (int t = 0; t < 3; t++)
        {
            TruthTable truth = doc.Tables[t];
            TableDraft table = draft.Tables.OrderBy(x => x.Order).ElementAt(t);
            Assert.Equal((truth.Rows, truth.Columns), (table.Rows, table.Columns));
            Assert.Equal(truth.Cells.Select(c => (c.Row, c.Column, c.RowSpan, c.ColumnSpan, c.Text, c.Header)),
                table.Cells.Select(c => (c.Row, c.Column, c.RowSpan, c.ColumnSpan, c.Text, c.Header)));
        }
    }

    [Fact]
    public void ATableAcrossPagesLeavesItsRepeatedHeaderOut()
    {
        SyntheticDocument doc = SyntheticScenes.TableAcrossPages(tagged: true);
        using PdfDocument pdf = PdfDocument.Open(doc.Save());
        TagIndex index = TagIndex.Read(pdf)!;

        List<TableDraft> parts = [.. Enumerable.Range(0, pdf.Pages.Count).SelectMany(p => TagReader.Read(index, pdf.Pages[p], PageReader.Read(pdf, p)).Draft!.Tables)];

        Assert.Equal(doc.Tables.Count, parts.Count);
        Assert.Equal(61, parts.Sum(p => p.Rows));
        Assert.Equal("#", parts[0].Cells[0].Text);
        Assert.Equal("Sample", parts[0].Cells[1].Text);
        Assert.DoesNotContain(parts[1].Cells, c => c.Header);
    }

    [Fact]
    public void FootnotesAndFurnitureAreKeptApart()
    {
        SyntheticDocument doc = SyntheticScenes.HeadersAndFooters(tagged: true);
        using PdfDocument pdf = PdfDocument.Open(doc.Save());
        PageDraft draft = TagReader.Read(TagIndex.Read(pdf)!, pdf.Pages[0], PageReader.Read(pdf, 0)).Draft!;
        SyntheticDocument notes = SyntheticScenes.Footnotes(tagged: true);
        using PdfDocument notesPdf = PdfDocument.Open(notes.Save());
        PageDraft notesDraft = TagReader.Read(TagIndex.Read(notesPdf)!, notesPdf.Pages[0], PageReader.Read(notesPdf, 0)).Draft!;

        Assert.Contains(draft.Artifacts, g => g.Text == "A");
        Assert.DoesNotContain(draft.Blocks, b => b.Text.Contains("Annual Report", StringComparison.Ordinal));
        Assert.Equal(notes.Blocks.Where(b => b.Role == TruthRole.Footnote).Select(b => Squash(b.Marker + b.Text)),
            notesDraft.Blocks.Where(b => b.Role == LayoutRole.Footnote).Select(b => Squash(b.Text)));
    }

    [Fact]
    public void UntaggedFilesHaveNoIndex()
    {
        using PdfDocument pdf = PdfDocument.Open(SyntheticScenes.OneColumn().Save());

        Assert.Null(TagIndex.Read(pdf));
    }

    [Fact]
    public void OneParagraphHoldingAPageFailsTheCheck()
    {
        using PdfDocument pdf = Draw((canvas, p) =>
        {
            canvas.BeginTag(p);
            double y = 60;
            for (int paragraph = 0; paragraph < 5; paragraph++)
            {
                for (int line = 0; line < 5; line++)
                {
                    canvas.DrawText($"Line {line} of paragraph {paragraph} with some words.", new Vector2(56, (float)y), SyntheticDocument.Body, Black);
                    y += 12;
                }

                y += 30;
            }

            canvas.EndTag();
        });

        TagResult result = TagReader.Read(TagIndex.Read(pdf)!, pdf.Pages[0], PageReader.Read(pdf, 0));

        Assert.Null(result.Draft);
        Assert.Contains("several paragraphs", result.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void TagsAcrossColumnsInContentOrderFailTheCheck()
    {
        using PdfDocument pdf = Draw((canvas, p) =>
        {
            canvas.BeginTag(p);
            for (int line = 0; line < 12; line++)
            {
                canvas.DrawText($"Left column line {line}.", new Vector2(56, 60 + (line * 12)), SyntheticDocument.Body, Black);
                canvas.DrawText($"Right column line {line}.", new Vector2(320, 60 + (line * 12)), SyntheticDocument.Body, Black);
            }

            canvas.EndTag();
        });

        TagResult result = TagReader.Read(TagIndex.Read(pdf)!, pdf.Pages[0], PageReader.Read(pdf, 0));

        Assert.Null(result.Draft);
        Assert.Contains("between columns", result.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void MostlyUntaggedTextFailsTheCheck()
    {
        using PdfDocument pdf = Draw((canvas, p) =>
        {
            canvas.BeginTag(p);
            canvas.DrawText("Tagged line.", new Vector2(56, 60), SyntheticDocument.Body, Black);
            canvas.EndTag();
            for (int line = 1; line < 10; line++)
            {
                canvas.DrawText($"Untagged line {line}.", new Vector2(56, 60 + (line * 12)), SyntheticDocument.Body, Black);
            }
        });

        TagResult result = TagReader.Read(TagIndex.Read(pdf)!, pdf.Pages[0], PageReader.Read(pdf, 0));

        Assert.Null(result.Draft);
        Assert.Contains("is tagged", result.Problem, StringComparison.Ordinal);
        Assert.InRange(result.Coverage, 0.05, 0.2);
    }

    [Fact]
    public void FiguresKeepTheirAltTextAndContent()
    {
        using PdfDocument pdf = Draw((canvas, p) =>
        {
            canvas.BeginTag(p);
            canvas.DrawText("A paragraph before the figure.", new Vector2(56, 60), SyntheticDocument.Body, Black);
            canvas.EndTag();
            PdfStructureElement figure = p.Parent!.Add(PdfStructureType.Figure);
            figure.AlternateText = "A red square";
            canvas.BeginTag(figure);
            canvas.FillRect(new RectF(100, 100, 50, 50), new SolidPaint(new RgbaF32(1, 0, 0, 1)));
            canvas.DrawText("Label", new Vector2(110, 170), SyntheticDocument.Body, Black);
            canvas.EndTag();
        });

        PageDraft draft = TagReader.Read(TagIndex.Read(pdf)!, pdf.Pages[0], PageReader.Read(pdf, 0)).Draft!;

        FigureDraft found = Assert.Single(draft.Figures);
        Assert.Equal("A red square", found.AltText);
        Assert.Single(found.Paths);
        Assert.Equal("Label", Assert.Single(found.Lines).Text);
        Assert.Equal([0, 1], new[] { draft.Blocks[0].Order, found.Order });
    }

    private static PdfDocument Draw(Action<PdfCanvas, PdfStructureElement> draw)
    {
        using PdfDocument created = PdfDocument.Create();
        PdfPage page = created.Pages.AddBlank(new PdfRectangle(0, 0, 595, 842));
        PdfStructureElement paragraph = created.Structure.Add(PdfStructureType.Document).Add(PdfStructureType.P);
        using (PdfCanvas canvas = new(page))
        {
            draw(canvas, paragraph);
        }

        using MemoryStream saved = new();
        created.Save(saved);
        return PdfDocument.Open(saved.ToArray());
    }

    private static LayoutRole Role(TruthRole role)
    {
        return role switch
        {
            TruthRole.Title => LayoutRole.Title,
            TruthRole.Heading => LayoutRole.Heading,
            TruthRole.ListItem => LayoutRole.ListItem,
            TruthRole.Quote => LayoutRole.Quote,
            TruthRole.Code => LayoutRole.Code,
            TruthRole.Caption => LayoutRole.Caption,
            TruthRole.Footnote => LayoutRole.Footnote,
            _ => LayoutRole.Text,
        };
    }

    /// <summary>Text without spaces, hyphens at split points and soft hyphens, so line breaks do not matter.</summary>
    private static string Squash(string text)
    {
        return new([.. text.Where(c => !char.IsWhiteSpace(c) && c is not ('-' or '\u00AD'))]);
    }
}
