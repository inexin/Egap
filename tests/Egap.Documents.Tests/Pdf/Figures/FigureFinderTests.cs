using System.Numerics;

using Egami;
using Egami.Drawing;
using Egami.Pixels;

using Egap.Documents.Pdf;
using Egap.Documents.Tests.Pdf.Synthetic;
using Egap.Documents.Tests.Pdf.Tables;
using Egap.Pdf;

namespace Egap.Documents.Tests.Pdf.Figures;

/// <summary>Figures and images (Build Plan task 9).</summary>
public sealed class FigureFinderTests
{
    private static readonly SolidPaint Ink = new(new RgbaF32(0, 0, 0, 1));

    public static TheoryData<string> Scenes => [.. SyntheticScenes.All.Keys];

    [Theory]
    [MemberData(nameof(Scenes))]
    public void TextPagesHaveNoFigures(string scene)
    {
        SyntheticDocument doc = SyntheticScenes.All[scene]();
        using PdfDocument pdf = PdfDocument.Open(doc.Save());

        for (int page = 0; page < doc.PageCount; page++)
        {
            PageContent content = PageReader.Read(pdf, page);
            Assert.Empty(FigureFinder.Find(content, content.Glyphs, BorderlessTableTests.Detect(content)));
        }
    }

    [Fact]
    public void AnImageIsAFigureWithItsBytesAndPlacement()
    {
        (PdfDocument pdf, PageContent content) = Draw((c, _) =>
        {
            using Raster<Rgba8> image = Raster.Create<Rgba8>(8, 6, new Rgba8(10, 120, 200, 255));
            c.DrawImage(image, new RectF(100, 120, 160, 120));
        });
        using (pdf)
        {
            FigureDraft figure = Assert.Single(FigureFinder.Find(content, content.Glyphs, []));

            PageImage image = Assert.Single(figure.Images);
            Assert.NotNull(image.Stream);
            Assert.True(image.Stream.EncodedLength > 0);
            Assert.Equal(new PageBox(100, 120, 260, 240), Round(figure.Box));
            Assert.False(figure.Decorative);
        }
    }

    [Fact]
    public void AChartWithLabelsIsOneFigure()
    {
        (PdfDocument pdf, PageContent content) = Draw((c, _) =>
        {
            c.DrawText("Body text above the chart runs across the page.", new Vector2(50, 40), SyntheticDocument.Body, Ink);
            c.DrawLine(new Vector2(80, 300), new Vector2(380, 300), new Stroke(1), Ink);
            c.DrawLine(new Vector2(80, 100), new Vector2(80, 300), new Stroke(1), Ink);
            for (int i = 0; i < 5; i++)
            {
                c.FillRect(new RectF(100 + (i * 55), 300 - (30 * (i + 1)), 35, 30 * (i + 1)), new SolidPaint(new RgbaF32(0.2f, 0.4f, 0.8f, 1)));
                c.DrawText($"Q{i + 1}", new Vector2(108 + (i * 55), 314), SyntheticDocument.FontOf(SyntheticDocument.Sans, 8), Ink);
            }
        });
        using (pdf)
        {
            FigureDraft figure = Assert.Single(FigureFinder.Find(content, content.Glyphs, []));

            Assert.Equal(7, figure.Paths.Count);
            Assert.Equal("Q1 Q2 Q3 Q4 Q5", Assert.Single(figure.Lines).Text);
            Assert.DoesNotContain(figure.Lines, l => l.Text.StartsWith("Body", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void UnderlinesAndSeparatorsAreNotFigures()
    {
        (PdfDocument pdf, PageContent content) = Draw((c, _) =>
        {
            c.DrawText("Underlined words", new Vector2(50, 60), SyntheticDocument.Body, Ink);
            c.DrawLine(new Vector2(50, 62), new Vector2(120, 62), new Stroke(0.5f), Ink);
            c.DrawLine(new Vector2(50, 100), new Vector2(350, 100), new Stroke(0.5f), Ink);
            c.DrawText("Text below the separator.", new Vector2(50, 120), SyntheticDocument.Body, Ink);
        });
        using (pdf)
        {
            Assert.Empty(FigureFinder.Find(content, content.Glyphs, []));
        }
    }

    [Fact]
    public void AnArtifactImageIsDecorative()
    {
        (PdfDocument pdf, PageContent content) = Draw((c, _) =>
        {
            c.BeginArtifact();
            using Raster<Rgba8> image = Raster.Create<Rgba8>(4, 4, new Rgba8(200, 200, 200, 255));
            c.DrawImage(image, new RectF(20, 20, 40, 40));
            c.EndTag();
        });
        using (pdf)
        {
            Assert.True(Assert.Single(FigureFinder.Find(content, content.Glyphs, [])).Decorative);
        }
    }

    [Fact]
    public void AFigureIsRecordedAsAPicture()
    {
        (PdfDocument pdf, PageContent content) = Draw((c, _) => c.FillCircle(new Vector2(200, 200), 50, Ink));
        using (pdf)
        {
            FigureDraft figure = Assert.Single(FigureFinder.Find(content, content.Glyphs, []));

            Egami.Drawing.Picture picture = FigureFinder.Record(pdf.Pages[0], figure);

            Assert.Equal(figure.Box.Width, picture.Bounds.Width, 1);
            Assert.Equal(figure.Box.Height, picture.Bounds.Height, 1);
        }
    }

    [Fact]
    public void AnIconInALineIsInline()
    {
        (PdfDocument pdf, PageContent content) = Draw((c, _) =>
        {
            c.DrawText("Press the", new Vector2(50, 60), SyntheticDocument.Body, Ink);
            using Raster<Rgba8> image = Raster.Create<Rgba8>(4, 4, new Rgba8(0, 0, 0, 255));
            c.DrawImage(image, new RectF(95, 51, 10, 10));
            c.DrawText("button to go on.", new Vector2(108, 60), SyntheticDocument.Body, Ink);
        });
        using (pdf)
        {
            FigureDraft figure = Assert.Single(FigureFinder.Find(content, content.Glyphs, []));

            Assert.True(FigureFinder.InLine(figure, content.Lines));
        }
    }

    private static (PdfDocument Pdf, PageContent Content) Draw(Action<PdfCanvas, PdfDocument> draw)
    {
        using PdfDocument created = PdfDocument.Create();
        PdfPage page = created.Pages.AddBlank(new PdfRectangle(0, 0, 420, 420));
        using (PdfCanvas canvas = new(page))
        {
            draw(canvas, created);
        }

        using MemoryStream saved = new();
        created.Save(saved);
        PdfDocument pdf = PdfDocument.Open(saved.ToArray());
        return (pdf, PageReader.Read(pdf, 0));
    }

    private static PageBox Round(PageBox box)
    {
        return new(Math.Round(box.Left, 2), Math.Round(box.Top, 2), Math.Round(box.Right, 2), Math.Round(box.Bottom, 2));
    }
}
