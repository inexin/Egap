using System.Numerics;

using Egami;
using Egami.Drawing;
using Egami.Pixels;

using Egap.Documents.Pdf;
using Egap.Documents.Tests.Pdf.Synthetic;
using Egap.Pdf;

namespace Egap.Documents.Tests.Pdf.Tagging;

/// <summary>Auto-tagging (Build Plan task 14): a structure tree from the analysis that reads back as the same layout.</summary>
public sealed class PdfAutoTaggerTests
{
    public static TheoryData<string> Scenes => [.. SyntheticScenes.All.Keys];

    [Theory]
    [MemberData(nameof(Scenes))]
    public void TheTagsReadBackAsTheSameLayout(string scene)
    {
        SyntheticDocument doc = SyntheticScenes.All[scene]();
        IReadOnlyList<PdfPageLayout> before;
        byte[] tagged;
        using (PdfDocument pdf = PdfDocument.Open(doc.Save()))
        {
            before = PdfLayoutAnalyzer.Analyze(pdf, cancellationToken: TestContext.Current.CancellationToken);
            PdfAutoTagReport report = PdfAutoTagger.Tag(pdf, cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(report.Tagged);
            Assert.Equal(doc.PageCount, report.Pages);
            using MemoryStream saved = new();
            pdf.Save(saved);
            tagged = saved.ToArray();
        }

        using PdfDocument back = PdfDocument.Open(tagged);
        Assert.True(back.Structure.IsMarked);
        IReadOnlyList<PdfPageLayout> after = PdfLayoutAnalyzer.Analyze(back, cancellationToken: TestContext.Current.CancellationToken);

        foreach ((PdfPageLayout a, PdfPageLayout b) in before.Zip(after))
        {
            Assert.True(b.FromTags, $"{scene} page {a.PageIndex + 1}: the tags did not pass the sanity check");
            Assert.Equal(a.Blocks.Where(x => x.Role is not (LayoutRole.PageHeader or LayoutRole.PageFooter or LayoutRole.PageNumber)).Select(x => x.Text),
                b.Blocks.Where(x => x.Role is not (LayoutRole.PageHeader or LayoutRole.PageFooter or LayoutRole.PageNumber)).Select(x => x.Text));
            Assert.Equal(a.Tables.Select(t => (t.RowCount, t.ColumnCount)), b.Tables.Select(t => (t.RowCount, t.ColumnCount)));
        }

        // Text extraction reads the same after tagging: the content draws what it drew.
        using PdfDocument original = PdfDocument.Open(doc.Save());
        Assert.Equal(Enumerable.Range(0, doc.PageCount).Select(i => PageReader.Read(original, i).Glyphs.Count),
            Enumerable.Range(0, doc.PageCount).Select(i => PageReader.Read(back, i).Glyphs.Count));
    }

    [Fact]
    public void FiguresWithoutAltTextAreReportedAndTaggedFilesLeftAlone()
    {
        using PdfDocument created = PdfDocument.Create();
        PdfPage blank = created.Pages.AddBlank(new PdfRectangle(0, 0, 400, 400));
        using (PdfCanvas canvas = new(blank))
        using (Raster<Rgba8> image = Raster.Create<Rgba8>(6, 6, new Rgba8(250, 10, 10, 255)))
        {
            canvas.DrawText("A paragraph of text above the picture on the page.", new Vector2(40, 40), SyntheticDocument.Body, new SolidPaint(new RgbaF32(0, 0, 0, 1)));
            canvas.DrawImage(image, new RectF(100, 100, 120, 120));
        }

        using MemoryStream saved = new();
        created.Save(saved);
        using PdfDocument pdf = PdfDocument.Open(saved.ToArray());
        using PdfDocument already = PdfDocument.Open(SyntheticScenes.Tagged["one-column"]().Save());

        PdfAutoTagReport report = PdfAutoTagger.Tag(pdf, cancellationToken: TestContext.Current.CancellationToken);
        PdfAutoTagReport skipped = PdfAutoTagger.Tag(already, cancellationToken: TestContext.Current.CancellationToken);

        PdfAutoTagFigure figure = Assert.Single(report.FiguresWithoutAltText);
        Assert.Equal(120, figure.Bounds.Width, 1);
        Assert.Contains(pdf.Structure.Root!.DescendantsAndSelf(), e => e.Type == PdfStructureType.Figure);
        Assert.False(skipped.Tagged);
    }
}
