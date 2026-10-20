using System.Numerics;

using Egami;
using Egami.Drawing;
using Egami.Fonts;
using Egami.Pixels;

using Egap.Documents.Pdf;
using Egap.Documents.Tests.Pdf.Synthetic;
using Egap.Pdf;

namespace Egap.Documents.Tests.Pdf.Input;

/// <summary>Page input for layout analysis (Build Plan task 2): glyphs, words and lines, paths, images, annotations and outline.</summary>
public sealed class PageReaderTests
{
    private static readonly SolidPaint Black = new(new RgbaF32(0, 0, 0, 1));

    public static TheoryData<string> Scenes => [.. SyntheticScenes.All.Keys];

    [Theory]
    [MemberData(nameof(Scenes))]
    public void LinesMatchPhase3TextExtraction(string scene)
    {
        using PdfDocument pdf = PdfDocument.Open(SyntheticScenes.All[scene]().Save());

        for (int i = 0; i < pdf.Pages.Count; i++)
        {
            PageContent content = PageReader.Read(pdf, i);
            Assert.Equal(pdf.Pages[i].GetText().Lines.Select(l => l.Text), content.Lines.Select(l => l.Text));
        }
    }

    [Theory]
    [MemberData(nameof(Scenes))]
    public void LinesSitWhereTheGeneratorDrewThem(string scene)
    {
        SyntheticDocument doc = SyntheticScenes.All[scene]();
        using PdfDocument pdf = PdfDocument.Open(doc.Save());

        for (int page = 0; page < doc.PageCount; page++)
        {
            PageContent content = PageReader.Read(pdf, page);
            Assert.Equal((doc.Width, doc.Height), (content.Width, content.Height));
            foreach (TruthLine truth in doc.Blocks.Where(b => b.Page == page).SelectMany(b => b.Lines))
            {
                PageLine? line = content.Lines.FirstOrDefault(l => l.Text.Replace(" ", "", StringComparison.Ordinal)
                    .Contains(truth.Text.Replace(" ", "", StringComparison.Ordinal), StringComparison.Ordinal) && l.Box.Overlap(Box(truth.Box)) > 0);
                Assert.True(line is not null, $"{scene} page {page + 1}: no line for \"{truth.Text}\"");

                // The words of the truth line start at its left edge (unless it starts with spaces) and share its baseline area.
                if (!char.IsWhiteSpace(truth.Text[0]))
                {
                    PageWord first = line.Words.First(w => truth.Text.StartsWith(w.Text, StringComparison.Ordinal) || w.Box.Left >= truth.Box.Left - 0.5);
                    Assert.InRange(first.Box.Left, truth.Box.Left - 1, truth.Box.Left + 1);
                }

                Assert.InRange(line.Box.CenterY, truth.Box.Top, truth.Box.Bottom);
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void RotatedPagesReadUpright(int rotate)
    {
        using PdfDocument created = PdfDocument.Create();
        PdfPage page = created.Pages.AddBlank(new PdfRectangle(0, 0, 400, 600));
        page.Rotate = rotate;
        PdfMatrix canvasMatrix;
        using (PdfCanvas canvas = new(page))
        {
            canvasMatrix = canvas.PageMatrix;
            canvas.DrawText("Upright text", new Vector2(60, 100), SyntheticDocument.FontOf(SyntheticDocument.Sans, 12), Black);
        }

        using PdfDocument pdf = Reopen(created);
        PageContent content = PageReader.Read(pdf, 0);

        bool turned = rotate is 90 or 270;
        Assert.Equal(turned ? (600.0, 400.0) : (400.0, 600.0), (content.Width, content.Height));
        Assert.True(Identity(canvasMatrix * content.ToDisplayed), $"{canvasMatrix * content.ToDisplayed}");
        PageLine line = Assert.Single(content.Lines);
        Assert.Equal("Upright text", line.Text);
        Assert.True(line.IsHorizontal);
        Assert.InRange(line.Box.Left, 59.5, 60.5);
        Assert.InRange(line.Box.Bottom, 100, 104);
        Assert.InRange(line.Box.Top, 88, 92);
    }

    [Fact]
    public void RuledTablesArriveAsStrokedPaths()
    {
        SyntheticDocument doc = SyntheticScenes.Tables();
        using PdfDocument pdf = PdfDocument.Open(doc.Save());

        PageContent content = PageReader.Read(pdf, 0);

        TruthTable ruled = doc.Tables[0];
        List<PagePath> inside = [.. content.Paths.Where(p => p.Stroked && p.Bounds.Left >= ruled.Box.Left - 1 && p.Bounds.Right <= ruled.Box.Right + 1
            && p.Bounds.Top >= ruled.Box.Top - 1 && p.Bounds.Bottom <= ruled.Box.Bottom + 1)];
        Assert.NotEmpty(inside);
        Assert.All(inside, p => Assert.InRange(p.LineWidth, 0.49, 0.51));
        Assert.Contains(inside, p => Math.Abs(p.Bounds.Top - ruled.Box.Top) < 0.5 && p.Bounds.Height < 0.01 && p.Bounds.Width > 50);
        Assert.Contains(inside, p => Math.Abs(p.Bounds.Left - ruled.Box.Left) < 0.5 && p.Bounds.Width < 0.01);
    }

    [Fact]
    public void ImagesKeepTheirPlacement()
    {
        using PdfDocument created = PdfDocument.Create();
        PdfPage page = created.Pages.AddBlank(new PdfRectangle(0, 0, 300, 300));
        using (PdfCanvas canvas = new(page))
        using (Raster<Rgba8> image = Raster.Create<Rgba8>(4, 2, new Rgba8(200, 30, 30, 255)))
        {
            canvas.DrawImage(image, new RectF(100, 50, 80, 40));
        }

        using PdfDocument pdf = Reopen(created);
        PageImage placed = Assert.Single(PageReader.Read(pdf, 0).Images);

        Assert.Equal((100.0, 50.0, 180.0, 90.0), (Math.Round(placed.Bounds.Left, 3), Math.Round(placed.Bounds.Top, 3), Math.Round(placed.Bounds.Right, 3), Math.Round(placed.Bounds.Bottom, 3)));
        Assert.Equal(new PdfPoint(100, 50), Round(placed.Corners[0]));
        Assert.NotNull(placed.Stream);
    }

    [Fact]
    public void LinksAndNotesCarryTheirTargets()
    {
        using PdfDocument created = PdfDocument.Create();
        PdfPage first = created.Pages.AddBlank(new PdfRectangle(0, 0, 300, 400));
        PdfPage second = created.Pages.AddBlank(new PdfRectangle(0, 0, 300, 400));
        first.Annotations.Add(new PdfLinkAnnotation(new PdfRectangle(10, 360, 110, 380), new PdfUriAction("https://example.com/")));
        first.Annotations.Add(new PdfLinkAnnotation(new PdfRectangle(10, 300, 110, 320), PdfDestination.ToPage(second, PdfDestinationFit.Xyz) with { Left = 0, Top = 250 }));
        first.Annotations.Add(new PdfTextAnnotation(new PdfRectangle(200, 200, 220, 220), "Check this"));

        using PdfDocument pdf = Reopen(created);
        List<PageAnnotation> annotations = [.. PageReader.Read(pdf, 0).Annotations];

        Assert.Equal(3, annotations.Count);
        Assert.Equal("https://example.com/", annotations[0].Uri);
        Assert.Equal((10.0, 20.0, 110.0, 40.0), (annotations[0].Box.Left, annotations[0].Box.Top, annotations[0].Box.Right, annotations[0].Box.Bottom));
        Assert.Equal((1, 150.0), (annotations[1].TargetPage!.Value, annotations[1].TargetTop!.Value));
        Assert.Equal(("Text", "Check this"), (annotations[2].Subtype, annotations[2].Contents));
    }

    [Fact]
    public void OutlineEntriesGoToTheirPages()
    {
        using PdfDocument created = PdfDocument.Create();
        PdfPage first = created.Pages.AddBlank(new PdfRectangle(0, 0, 300, 400));
        PdfPage second = created.Pages.AddBlank(new PdfRectangle(0, 0, 300, 400));
        PdfOutlineItem chapter = created.Outline.Add("Chapter", PdfDestination.ToPage(first));
        chapter.Add("Section", PdfDestination.ToPage(second, PdfDestinationFit.Xyz) with { Top = 300 });

        using PdfDocument pdf = Reopen(created);
        List<OutlineEntry> outline = PageReader.ReadOutline(pdf);

        Assert.Equal([new("Chapter", 0, 0, null), new("Section", 1, 1, 100)], outline);
        Assert.Equal("Section", Assert.Single(PageReader.Read(pdf, 1, outline).Outline).Title);
    }

    [Fact]
    public void MarkedContentIsRecordedPerGlyph()
    {
        using PdfDocument created = PdfDocument.Create();
        PdfPage page = created.Pages.AddBlank(new PdfRectangle(0, 0, 300, 300));
        PdfStructureElement paragraph = created.Structure.Add(PdfStructureType.Document).Add(PdfStructureType.P);
        Font font = SyntheticDocument.FontOf(SyntheticDocument.Serif, 10);
        using (PdfCanvas canvas = new(page))
        {
            canvas.BeginTag(paragraph);
            canvas.DrawText("Tagged", new Vector2(20, 40), font, Black);
            canvas.EndTag();
            canvas.BeginArtifact();
            canvas.DrawText("Furniture", new Vector2(20, 280), font, Black);
            canvas.EndTag();
        }

        using PdfDocument pdf = Reopen(created);
        PageContent content = PageReader.Read(pdf, 0);

        Assert.All(content.Lines[0].Words.SelectMany(w => w.Glyphs), g => Assert.True(g.Mcid >= 0 && !g.IsArtifact));
        Assert.All(content.Lines[1].Words.SelectMany(w => w.Glyphs), g => Assert.True(g.Mcid < 0 && g.IsArtifact));
    }

    [Fact]
    public void ActualTextSpansBecomeOneGlyph()
    {
        Typeface face = SyntheticDocument.FontOf(SyntheticDocument.Serif, 10).Typeface;
        ushort f = face.GetGlyph('f'), i = face.GetGlyph('i');
        GlyphRun run = GlyphRun.Create(new Font(face, 10), [f, i], [new(0, 0), new(3, 0)], "fi", [0, 0]);
        using PdfDocument created = PdfDocument.Create();
        PdfPage page = created.Pages.AddBlank(new PdfRectangle(0, 0, 100, 100));
        using (PdfCanvas canvas = new(page))
        {
            canvas.DrawGlyphRun(run, new Vector2(5, 50), Black);
        }

        using PdfDocument pdf = Reopen(created);
        PageGlyph glyph = Assert.Single(PageReader.Read(pdf, 0).Glyphs);

        Assert.Equal("fi", glyph.Text);
    }

    private static PdfDocument Reopen(PdfDocument document)
    {
        using MemoryStream saved = new();
        document.Save(saved);
        return PdfDocument.Open(saved.ToArray());
    }

    private static PageBox Box(TruthBox box)
    {
        return new(box.Left, box.Top, box.Right, box.Bottom);
    }

    private static bool Identity(PdfMatrix m)
    {
        return Math.Abs(m.A - 1) < 1e-9 && Math.Abs(m.B) < 1e-9 && Math.Abs(m.C) < 1e-9 && Math.Abs(m.D - 1) < 1e-9 && Math.Abs(m.E) < 1e-9 && Math.Abs(m.F) < 1e-9;
    }

    private static PdfPoint Round(PdfPoint p)
    {
        return new(Math.Round(p.X, 3), Math.Round(p.Y, 3));
    }
}
