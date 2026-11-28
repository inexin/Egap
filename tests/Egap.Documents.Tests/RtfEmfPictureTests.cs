using System.Text;

using Egami;
using Egami.Barcodes;
using Egami.Pixels;

using Egap.Documents.Charts;
using Egap.Documents.Layout;

namespace Egap.Documents.Tests;

/// <summary>Pictures RTF has no form of (SVG, charts, shape groups) written as EMF vectors with a PNG beside them.</summary>
public sealed class RtfEmfPictureTests
{
    private const string Svg = """
        <svg xmlns="http://www.w3.org/2000/svg" width="120" height="80" viewBox="0 0 120 80">
          <rect x="0" y="0" width="120" height="80" fill="#ffffff"/>
          <rect x="10" y="10" width="60" height="40" fill="#d02020"/>
          <circle cx="85" cy="50" r="25" fill="#2040c0"/>
        </svg>
        """;

    private static (string Rtf, Document Back, IReadOnlyList<ConversionWarning> Warnings) RoundTrip(Document document)
    {
        using MemoryStream stream = new();
        IReadOnlyList<ConversionWarning> warnings = RtfFormat.Save(document, stream);
        string rtf = Encoding.ASCII.GetString(stream.ToArray());
        stream.Position = 0;
        return (rtf, RtfFormat.Load(stream), warnings);
    }

    /// <summary>A document with one empty paragraph whose page margins are set, as RTF's defaults are not Egap's.</summary>
    private static (Document Document, Paragraph Paragraph) Empty(string text = "")
    {
        Document document = new();
        Section section = document.AddSection();
        section.PageSetup.MarginLeft = section.PageSetup.MarginRight = section.PageSetup.MarginTop = section.PageSetup.MarginBottom = 72;
        return (document, section.Body.AddParagraph(text));
    }

    private static Raster<Rgba8> Render(Document document) =>
        document.Layout(new LayoutOptions { Fonts = FontSettings.Portable }).Pages[0].Render(dpi: 96);

    /// <summary>The mean difference over every channel of two renders the same size, 0 to 255.</summary>
    private static double MeanDifference(Raster<Rgba8> a, Raster<Rgba8> b)
    {
        Assert.Equal(a.Width, b.Width);
        Assert.Equal(a.Height, b.Height);
        long sum = 0;
        for (int y = 0; y < a.Height; y++)
        {
            ReadOnlySpan<Rgba8> rowA = a.Row(y), rowB = b.Row(y);
            for (int x = 0; x < a.Width; x++)
            {
                sum += Math.Abs(rowA[x].R - rowB[x].R) + Math.Abs(rowA[x].G - rowB[x].G) + Math.Abs(rowA[x].B - rowB[x].B);
            }
        }

        return sum / (3.0 * a.Width * a.Height);
    }

    [Fact]
    public void AnSvgIsWrittenAsAnEmfWithAPngBesideItAndRendersAsTheSvgDoes()
    {
        (Document document, Paragraph paragraph) = Empty();
        paragraph.Inlines.Add(new Picture(document, new ImageData(Encoding.UTF8.GetBytes(Svg), "image/svg+xml"), 120, 80));

        (string rtf, Document back, IReadOnlyList<ConversionWarning> warnings) = RoundTrip(document);

        Assert.Contains(@"{\*\shppict{\pict", rtf, StringComparison.Ordinal);
        Assert.Contains(@"\emfblip", rtf, StringComparison.Ordinal);
        Assert.Contains(@"{\nonshppict{\pict", rtf, StringComparison.Ordinal);
        Assert.Contains(@"\pngblip", rtf, StringComparison.Ordinal);
        Assert.DoesNotContain(warnings, w => w.Message.Contains("SVG", StringComparison.Ordinal));
        Picture picture = back.Descendants<Picture>().Single();
        Assert.Equal("image/x-emf", picture.Image.ContentType);
        Assert.Equal(120, picture.Width, 0.5);
        Assert.Equal(80, picture.Height, 0.5);

        using Raster<Rgba8> expected = Render(document);
        using Raster<Rgba8> actual = Render(back);
        Assert.True(MeanDifference(expected, actual) < 1.5, $"mean difference {MeanDifference(expected, actual)}");
    }

    [Fact]
    public void ABarcodeKeepsItsVectorsAndStillReads()
    {
        Document document = new();
        new DocumentBuilder(document).InsertBarcode(Barcode.Code128("ORDER-118"), 200, 60);

        (string rtf, Document back, _) = RoundTrip(document);

        Assert.Contains(@"\emfblip", rtf, StringComparison.Ordinal);
        Assert.Equal("image/x-emf", back.Descendants<Picture>().Single().Image.ContentType);
        using Raster<Rgba8> page = back.Layout(new LayoutOptions { Fonts = FontSettings.Portable }).Pages[0].Render(dpi: 200);
        Assert.Equal("ORDER-118", page.ReadBarcode()?.Text);
    }

    [Fact]
    public void AFloatingSvgHoldsTheEmfInItsShape()
    {
        (Document document, Paragraph paragraph) = Empty("Text");
        paragraph.Inlines.Add(new Picture(document, new ImageData(Encoding.UTF8.GetBytes(Svg), "image/svg+xml"), 120, 80)
        {
            Floating = new FloatingPlacement(),
        });

        (string rtf, Document back, _) = RoundTrip(document);

        Assert.Contains(@"\emfblip", rtf, StringComparison.Ordinal);
        Assert.DoesNotContain(@"\nonshppict{\pict", rtf[..rtf.IndexOf(@"\emfblip", StringComparison.Ordinal)], StringComparison.Ordinal);
        Picture picture = back.Descendants<Picture>().Single();
        Assert.Equal("image/x-emf", picture.Image.ContentType);
        Assert.NotNull(picture.Floating);
    }

    [Fact]
    public void AChartIsWrittenAsAnEmfPictureOfIt()
    {
        Chart chart = Chart.Create(ChartType.Column);
        chart.Plots[0].Series.Add([1, 2], ["Jan", "Feb"], "North");
        chart.Plots[0].Series.Add([3, 1], ["Jan", "Feb"], "South");
        (Document document, Paragraph paragraph) = Empty();
        paragraph.Inlines.Add(new Island(document, chart, 360, 216));

        (string rtf, Document back, IReadOnlyList<ConversionWarning> warnings) = RoundTrip(document);

        Assert.Contains(@"\emfblip", rtf, StringComparison.Ordinal);
        Assert.Contains(warnings, w => w.Kind == ConversionWarningKind.DrawingDropped);
        Picture picture = back.Descendants<Picture>().Single();
        Assert.Equal("image/x-emf", picture.Image.ContentType);
        using Raster<Rgba8> expected = Render(document);
        using Raster<Rgba8> actual = Render(back);
        Assert.True(MeanDifference(expected, actual) < 1.5, $"mean difference {MeanDifference(expected, actual)}");
    }
}
