using System.Numerics;

using Egami;
using Egami.Drawing;
using Egami.Pixels;

using Egap.Pdf.Conversion.Tests.Synthetic;

namespace Egap.Pdf.Conversion.Tests.Analysis;

/// <summary>The public page layout (Build Plan task 10): every stage together, tagged and untagged, and the replaceable stages.</summary>
public sealed class PdfLayoutAnalyzerTests
{
    private static readonly SolidPaint Ink = new(new RgbaF32(0, 0, 0, 1));

    public static TheoryData<string, bool> Scenes => [.. SyntheticScenes.All.Keys.SelectMany(k => new[] { (k, false), (k, true) })];

    [Theory]
    [MemberData(nameof(Scenes))]
    public void TheLayoutMatchesTheTruth(string scene, bool tagged)
    {
        SyntheticDocument doc = tagged ? SyntheticScenes.Tagged[scene]() : SyntheticScenes.All[scene]();
        using PdfDocument pdf = PdfDocument.Open(doc.Save());

        IReadOnlyList<PdfPageLayout> pages = PdfLayoutAnalyzer.Analyze(pdf, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(doc.PageCount, pages.Count);
        List<string> wrong = [];
        foreach (PdfPageLayout page in pages)
        {
            Assert.Equal(tagged, page.FromTags);
            List<TruthTable> tables = [.. doc.Tables.Where(t => t.Page == page.PageIndex)];
            Assert.Equal(tables.Count, page.Tables.Count);
            foreach ((TruthTable truth, LayoutTable table) in tables.Zip(page.Tables))
            {
                // A tagged header row drawn again is an artifact, so the tags' table leaves it out.
                bool dropped = tagged && truth.RepeatsHeader;
                Assert.Equal((truth.Rows - (dropped ? 1 : 0), truth.Columns), (table.RowCount, table.ColumnCount));
                Assert.Equal(truth.Cells.Where(c => !dropped || c.Row > 0).Select(c => c.Text), table.Cells.Select(c => c.Text));
            }

            Assert.Empty(page.Figures);
            List<int> orders = [];
            foreach (LayoutBlock block in page.Blocks)
            {
                TruthBlock? truth = doc.Blocks.FirstOrDefault(t => t.Page == page.PageIndex
                    && t.Lines.Any(l => l.Box.Contains(block.Lines[0].Bounds.X + (block.Lines[0].Bounds.Width / 2), block.Lines[0].Bounds.Y + (block.Lines[0].Bounds.Height / 2), 1)));
                if (truth is null)
                {
                    wrong.Add($"page {page.PageIndex + 1}: no truth for \"{block.Text}\"");
                    continue;
                }

                // Tags do not say what furniture is; the page's own layout does, and only for headers that repeat.
                if (Role(truth.Role) != block.Role && !(tagged && truth.Order < 0))
                {
                    wrong.Add($"page {page.PageIndex + 1}: {block.Role} for {truth.Role} \"{Short(block.Text)}\"");
                }

                if (truth.Order >= 0 && (orders.Count == 0 || orders[^1] != truth.Order))
                {
                    orders.Add(truth.Order);
                }
            }

            Assert.True(orders.SequenceEqual(orders.Order()), $"{scene} page {page.PageIndex + 1}: order {string.Join(", ", orders)}");
            Assert.Equal(Enumerable.Range(0, page.Elements.Count), page.Elements.Select(e => e.Order));
        }

        Assert.True(wrong.Count == 0, $"{scene}{(tagged ? " tagged" : "")}: {string.Join("; ", wrong)}");
    }

    [Fact]
    public void ParagraphsAndTablesCarryOnAcrossPages()
    {
        using PdfDocument columns = PdfDocument.Open(SyntheticScenes.TwoColumns().Save());
        using PdfDocument table = PdfDocument.Open(SyntheticScenes.TableAcrossPages().Save());

        IReadOnlyList<PdfPageLayout> text = PdfLayoutAnalyzer.Analyze(columns, cancellationToken: TestContext.Current.CancellationToken);
        IReadOnlyList<PdfPageLayout> tables = PdfLayoutAnalyzer.Analyze(table, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(text[1].Blocks[0].ContinuesPrevious);
        Assert.All(tables.Skip(1), p => Assert.True(p.Tables[0].ContinuesPrevious && p.Tables[0].RepeatsHeader));
    }

    [Fact]
    public void ARegionDetectorNamesItsRegions()
    {
        using PdfDocument pdf = PdfDocument.Open(SyntheticScenes.OneColumn().Save());
        PdfPageLayout plain = PdfLayoutAnalyzer.Analyze(pdf, 0);
        LayoutBlock target = plain.Blocks.First(b => b.Role == LayoutRole.Text);

        PdfPageLayout page = PdfLayoutAnalyzer.Analyze(pdf, 0, new PdfLayoutOptions { RegionDetector = new Fixed(new LayoutRegionHint(LayoutRole.Quote, target.Bounds, 0.42)) });

        LayoutBlock named = page.Blocks.Single(b => b.Text == target.Text);
        Assert.Equal((LayoutRole.Quote, 0.42), (named.Role, named.Confidence));
    }

    [Fact]
    public void TableOptionsTurnDetectionOffOrNameAreas()
    {
        SyntheticDocument doc = SyntheticScenes.Tables();
        using PdfDocument pdf = PdfDocument.Open(doc.Save());
        TruthTable borderless = doc.Tables[2];
        RectF area = new((float)borderless.Box.Left - 2, (float)borderless.Box.Top - 2, (float)borderless.Box.Right - (float)borderless.Box.Left + 4, (float)borderless.Box.Bottom - (float)borderless.Box.Top + 4);

        PdfPageLayout none = PdfLayoutAnalyzer.Analyze(pdf, 0, new PdfLayoutOptions { Tables = TableDetection.None });
        PdfPageLayout ruled = PdfLayoutAnalyzer.Analyze(pdf, 0, new PdfLayoutOptions { Tables = TableDetection.Ruled });
        PdfPageLayout named = PdfLayoutAnalyzer.Analyze(pdf, 0, new PdfLayoutOptions { Tables = TableDetection.None, TableAreas = new Dictionary<int, IReadOnlyList<RectF>> { [0] = [area] } });

        Assert.Empty(none.Tables);
        Assert.True(Assert.Single(ruled.Tables).IsRuled);
        Assert.Equal(borderless.Cells.Select(c => c.Text), Assert.Single(named.Tables).Cells.Select(c => c.Text));
    }

    [Fact]
    public void AReadingOrderDetectorReplacesTheXyCut()
    {
        using PdfDocument pdf = PdfDocument.Open(SyntheticScenes.OneColumn().Save());

        PdfPageLayout page = PdfLayoutAnalyzer.Analyze(pdf, 0, new PdfLayoutOptions { ReadingOrderDetector = new Reverse() });

        Assert.True(page.Blocks[0].Bounds.Y > page.Blocks[^1].Bounds.Y);
    }

    [Fact]
    public void FiguresKeepTheirImagesAndDrawAsPictures()
    {
        using PdfDocument created = PdfDocument.Create();
        PdfPage blank = created.Pages.AddBlank(new PdfRectangle(0, 0, 400, 400));
        using (PdfCanvas canvas = new(blank))
        using (Raster<Rgba8> image = Raster.Create<Rgba8>(6, 6, new Rgba8(250, 10, 10, 255)))
        {
            canvas.DrawText("A paragraph of text above the picture on the page.", new Vector2(40, 40), SyntheticDocument.Body, Ink);
            canvas.DrawImage(image, new RectF(100, 100, 120, 120));
        }

        using MemoryStream saved = new();
        created.Save(saved);
        using PdfDocument pdf = PdfDocument.Open(saved.ToArray());

        LayoutFigure figure = Assert.Single(PdfLayoutAnalyzer.Analyze(pdf, 0).Figures);

        Assert.Equal((6, 6), (Assert.Single(figure.Images).Image!.Width, figure.Images[0].Image!.Height));
        Assert.Equal(120, figure.ToPicture().Bounds.Width, 1);
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
            TruthRole.PageHeader => LayoutRole.PageHeader,
            TruthRole.PageFooter => LayoutRole.PageFooter,
            TruthRole.PageNumber => LayoutRole.PageNumber,
            _ => LayoutRole.Text,
        };
    }

    private static string Short(string text)
    {
        return text.Length <= 40 ? text : text[..40];
    }

    private sealed class Fixed(params LayoutRegionHint[] hints) : ILayoutRegionDetector
    {
        public IReadOnlyList<LayoutRegionHint> Detect(PdfPage page)
        {
            return hints;
        }
    }

    private sealed class Reverse : IReadingOrderDetector
    {
        public IReadOnlyList<int> Order(IReadOnlyList<RectF> bounds, IReadOnlyList<int> contentOrder)
        {
            return [.. Enumerable.Range(0, bounds.Count).OrderByDescending(i => bounds[i].Y)];
        }
    }
}
