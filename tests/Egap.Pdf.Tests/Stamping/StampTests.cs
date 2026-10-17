using System.Globalization;

using Egami;
using Egami.Pixels;

namespace Egap.Pdf.Tests.Stamping;

/// <summary>Stamping: Phase 7 tasks 12 and 13.</summary>
public sealed class StampTests
{
    private static readonly string Fonts = Path.Combine(AppContext.BaseDirectory, "files", "fonts");

    [Fact]
    public void PageNumbersFillTheTemplateAtTheAnchor()
    {
        using PdfDocument pdf = Pages(3, 0);

        PdfStampResult result = pdf.Stamp(PdfStamp.Text("Page {page} of {pages}") with { Anchor = StampAnchor.BottomCenter, Offset = new(0, 24) });

        Assert.Equal(3, result.Pages);
        PdfTextLine line = Assert.Single(pdf.Pages[1].GetText().Lines, l => l.Text.Contains("Page", StringComparison.Ordinal));
        Assert.Equal("Page 2 of 3", line.Text.Trim());
        PdfRectangle box = new(line.Words.Min(w => w.Box.Left), line.Words.Min(w => w.Box.Bottom), line.Words.Max(w => w.Box.Right), line.Words.Max(w => w.Box.Top));
        Assert.InRange((box.Left + box.Right) / 2, 300, 312);
        Assert.InRange(box.Bottom, 20, 30);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void StampsStandUprightOnTurnedPages(int rotate)
    {
        using PdfDocument pdf = Pages(1, rotate);
        pdf.Stamp(PdfStamp.Text("XXXXXXXXXXXXXXXX") with { Anchor = StampAnchor.TopLeft, FontSize = 20, Color = new Rgba8(255, 0, 0, 255) });

        // Shown, the page's top left corner holds the red text, whatever the rotation.
        using Raster<Rgba8> shown = pdf.Pages[0].Render(new PdfRenderOptions { Dpi = 72 }, TestContext.Current.CancellationToken);
        int red = 0;
        for (int y = 0; y < 20; y++)
        {
            for (int x = 0; x < 150; x++)
            {
                Rgba8 p = shown.GetPixel(x, y);
                red += p.R > 200 && p.G < 100 ? 1 : 0;
            }
        }

        Assert.True(red > 200, $"{red} red pixels in the shown top left");
    }

    [Fact]
    public void StampsAreMarkedAsPaginationArtifacts()
    {
        using PdfDocument pdf = Pages(1, 0);
        pdf.Stamp(PdfStamp.Text("Head") with { Anchor = StampAnchor.TopCenter });
        pdf.Stamp(PdfStamp.Text("Foot"));
        pdf.Stamp(PdfStamp.Watermark("DRAFT"));

        PdfOperation[] marks = [.. pdf.Pages[0].GetContent().Where(o => o.Operator == "BDC")];
        Assert.Equal(["Header", "Footer", "Watermark"], marks.Select(m => ((PdfDictionary)m.Operands[1]).GetName("Subtype")!.Value));
        Assert.All(marks, m => Assert.Equal("Pagination", ((PdfDictionary)m.Operands[1]).GetName("Type")!.Value));
    }

    [Fact]
    public void RemoveStampsGivesBackWhatThePageShowed()
    {
        using PdfDocument pdf = Pages(2, 0);
        string text = pdf.Pages[0].ExtractText();
        using Raster<Rgba8> before = pdf.Pages[0].Render(new PdfRenderOptions { Dpi = 36 }, TestContext.Current.CancellationToken);
        pdf.Stamp(PdfStamp.Watermark("CONFIDENTIAL") with { Layer = "Watermark" });
        pdf.Stamp(PdfStamp.Text("{page}") with { Under = true });
        Assert.NotEqual(text, pdf.Pages[0].ExtractText());

        Assert.Equal(4, pdf.RemoveStamps());

        Assert.Equal(text, pdf.Pages[0].ExtractText());
        using Raster<Rgba8> after = pdf.Pages[0].Render(new PdfRenderOptions { Dpi = 36 }, TestContext.Current.CancellationToken);
        for (int y = 0; y < before.Height; y++)
        {
            Assert.True(before.Row(y).SequenceEqual(after.Row(y)), $"row {y}");
        }

        Assert.Equal(0, pdf.RemoveStamps());
    }

    [Fact]
    public void LayersAreListedForViewersToHide()
    {
        using PdfDocument pdf = Pages(2, 0);
        using Raster<Rgba8> plain = pdf.Pages[0].Render(new PdfRenderOptions { Dpi = 36 }, TestContext.Current.CancellationToken);
        pdf.Stamp(PdfStamp.Watermark("DRAFT") with { Layer = "Watermark" });
        pdf.Stamp(PdfStamp.Text("{page}") with { Layer = "Watermark" });

        PdfLayer layer = Assert.Single(pdf.Layers);
        Assert.Equal("Watermark", layer.Name);
        layer.IsVisible = false;
        using Raster<Rgba8> hidden = pdf.Pages[0].Render(new PdfRenderOptions { Dpi = 36 }, TestContext.Current.CancellationToken);
        for (int y = 0; y < plain.Height; y++)
        {
            Assert.True(plain.Row(y).SequenceEqual(hidden.Row(y)), $"row {y}");
        }
    }

    [Fact]
    public void BatesNumbersContinueAcrossDocuments()
    {
        PdfBatesSequence bates = new("ACME-", digits: 6, start: 41);
        using PdfDocument first = Pages(2, 0), second = Pages(3, 0);

        PdfStampResult a = first.Stamp(PdfStamp.Bates(bates));
        PdfStampResult b = second.Stamp(PdfStamp.Bates(bates) with { Template = "Exhibit {bates}" });

        Assert.Equal(("ACME-000041", "ACME-000042"), (a.FirstBates, a.LastBates));
        Assert.Equal(("ACME-000043", "ACME-000045"), (b.FirstBates, b.LastBates));
        Assert.Contains("Exhibit ACME-000044", second.Pages[1].ExtractText(), StringComparison.Ordinal);
        Assert.Equal(46, bates.Next);
    }

    [Fact]
    public void TemplatesTakeLabelsDatesTitleAndCulture()
    {
        using PdfDocument pdf = Pages(2, 0);
        pdf.Metadata.Title = "Annual report";
        pdf.Pages.SetLabelRange(new PdfPageLabelRange(0, PdfPageLabelStyle.LowerRoman, null, 1));
        pdf.Clock = new FixedClock(new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero));

        pdf.Stamp(PdfStamp.Text("{title} {label} {date:dd. MMMM yyyy} {pages:00}") with { Culture = CultureInfo.GetCultureInfo("nb-NO") });

        Assert.Contains("Annual report ii 17. oktober 2026 02", pdf.Pages[1].ExtractText(), StringComparison.Ordinal);
    }

    [Fact]
    public void ACallersFontIsEmbeddedAndPdfANeedsOne()
    {
        using PdfDocument pdf = Pages(1, 0);
        Egami.Fonts.Typeface noto = Egami.Fonts.Typeface.FromFile(Path.Combine(Fonts, "NotoSans-Latin.ttf"));
        pdf.Stamp(PdfStamp.Text("Résumé – {page}") with { Font = noto, Anchor = StampAnchor.TopRight });

        // The subset and its ToUnicode map are written when the document is saved.
        using MemoryStream saved = new();
        pdf.Save(saved);
        using PdfDocument reopened = PdfDocument.Open(saved.ToArray());
        Assert.Contains("Résumé – 1", reopened.Pages[0].ExtractText(), StringComparison.Ordinal);

        using PdfDocument archive = PdfDocument.Create(new PdfCreateOptions { Conformance = PdfAConformance.PdfA2b });
        archive.Pages.AddBlank();
        Assert.Throws<InvalidOperationException>(() => archive.Stamp(PdfStamp.Text("{page}")));
        Assert.Equal(1, archive.Stamp(PdfStamp.Text("{page}") with { Font = noto }).Pages);
    }

    [Fact]
    public void ImageAndPageStampsAndChosenPages()
    {
        using PdfDocument pdf = Pages(3, 0);
        using Raster<Rgba8> logo = Raster.Create<Rgba8>(20, 10, new Rgba8(0, 128, 0, 255));
        pdf.Stamp(PdfStamp.Image(logo, 40, 20) with { Pages = [2] });
        using PdfDocument letterhead = Pages(1, 0);
        pdf.Stamp(PdfStamp.Page(letterhead.Pages[0]) with { Scale = 0.5, Anchor = StampAnchor.TopLeft });

        using Raster<Rgba8> second = pdf.Pages[1].Render(new PdfRenderOptions { Dpi = 72 }, TestContext.Current.CancellationToken);
        using Raster<Rgba8> first = pdf.Pages[0].Render(new PdfRenderOptions { Dpi = 72 }, TestContext.Current.CancellationToken);
        Assert.Equal(new Rgba8(0, 128, 0, 255), second.GetPixel(600, 10));
        Assert.NotEqual(new Rgba8(0, 128, 0, 255), first.GetPixel(600, 10));
        Assert.Equal(2, pdf.Pages[2].ExtractText().Split("P1", StringSplitOptions.None).Length - 1 + pdf.Pages[2].ExtractText().Split("P3", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void RefusesBadOptions()
    {
        using PdfDocument pdf = Pages(1, 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => pdf.Stamp(PdfStamp.Text("x") with { Opacity = 2 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => pdf.Stamp(PdfStamp.Text("x") with { FontSize = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfBatesSequence(step: 0));
    }

    /// <summary>US Letter pages with "P1", "P2" ... in the middle, saved and reopened.</summary>
    private static PdfDocument Pages(int count, int rotate)
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfReference font = pdf.Objects.Add(new PdfDictionary { ["Type"] = PdfName.Get("Font"), ["Subtype"] = PdfName.Get("Type1"), ["BaseFont"] = PdfName.Get("Helvetica") });
        for (int i = 1; i <= count; i++)
        {
            PdfPage page = pdf.Pages.AddBlank();
            page.Resources = new PdfDictionary { ["Font"] = new PdfDictionary { ["F1"] = font } };
            page.SetContent(PdfContent.Parse(System.Text.Encoding.ASCII.GetBytes($"BT /F1 24 Tf 280 400 Td (P{i}) Tj ET")));
            page.Rotate = rotate;
        }

        using MemoryStream saved = new();
        pdf.Save(saved);
        return PdfDocument.Open(saved.ToArray());
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
