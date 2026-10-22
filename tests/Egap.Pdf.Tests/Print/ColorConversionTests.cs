using Egami;
using Egami.Colors;
using Egami.Pixels;

namespace Egap.Pdf.Tests.Print;

/// <summary>Colour conversion into a print profile: what converts, what stays, spots, black and images.</summary>
public sealed class ColorConversionTests
{
    private static double[] Fogra(float r, float g, float b)
    {
        ColorTransform transform = ColorTransform.Create(ColorProfile.Srgb, ColorProfile.Fogra39, new ColorTransformOptions { Intent = RenderingIntent.RelativeColorimetric, BlackPointCompensation = true });
        float[] cmyk = new float[4];
        transform.Apply([r, g, b], cmyk);
        return [.. cmyk.Select(c => (double)c)];
    }

    private static string Content(PdfPage page) => System.Text.Encoding.Latin1.GetString(page.GetContent().ToBytes());

    private static double[] Operands(PdfPage page, string op) => [.. page.GetContent().First(o => o.Operator == op).Operands.Select(o => o is PdfReal r ? r.Value : o is PdfInteger i ? (double)i.Value : double.NaN)];

    [Fact]
    public void RgbBecomesCmykThroughTheProfileAndCmykAndGrayStay()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfPage page = pdf.Pages.AddBlank();
        page.SetContent(PdfContent.Parse("0.2 0.4 0.6 rg 0 0 100 100 re f 0.1 0.2 0.3 0.4 k 0 0 50 50 re f 0.5 g 0 0 20 20 re f"u8));

        PdfColorConversionResult result = PdfColorConverter.Convert(pdf, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Colors);
        string content = Content(page);
        Assert.DoesNotContain(" rg", content, StringComparison.Ordinal);
        PdfOperation[] k = [.. page.GetContent().Where(o => o.Operator == "k")];
        Assert.Equal(2, k.Length);
        double[] expected = Fogra(0.2f, 0.4f, 0.6f);
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(expected[i], ((PdfReal)k[0].Operands[i]).Value, 4);
        }

        Assert.Contains("0.1 0.2 0.3 0.4 k", content, StringComparison.Ordinal);
        Assert.Contains("0.5 g", content, StringComparison.Ordinal);
    }

    [Fact]
    public void BlackTextAndThinRulesGoOnBlackInkAloneAndLargeFillsStayRich()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfPage page = pdf.Pages.AddBlank();
        page.Resources = new PdfDictionary { ["Font"] = new PdfDictionary { ["F1"] = new PdfDictionary { ["Type"] = PdfName.Get("Font"), ["Subtype"] = PdfName.Get("Type1"), ["BaseFont"] = PdfName.Get("Helvetica") } } };
        page.SetContent(PdfContent.Parse("0 0 0 rg 0 0 300 300 re f BT /F1 12 Tf 72 700 Td (Black) Tj ET 0 0 300 300 re f 0 0 0 RG 0.3 w 0 0 m 100 0 l S 2 w 0 10 m 100 10 l S"u8));

        PdfColorConversionResult result = PdfColorConverter.Convert(pdf, cancellationToken: TestContext.Current.CancellationToken);

        PdfOperation[] ops = [.. page.GetContent()];
        int bt = Array.FindIndex(ops, o => o.Operator == "BT"), tj = Array.FindIndex(ops, o => o.Operator == "Tj"), et = Array.FindIndex(ops, o => o.Operator == "ET");
        PdfOperation inText = ops[bt..tj].Last(o => o.Operator == "k");
        Assert.Equal([0.0, 0, 0, 1], inText.Operands.Select(o => ((PdfReal)o).Value));

        // After the text, fills are the rich black of the profile again.
        PdfOperation after = ops[et..].First(o => o.Operator == "k");
        Assert.True(((PdfReal)after.Operands[0]).Value > 0.3, "rich black has cyan");

        // The 0.3 pt rule strokes in pure black, the 2 pt one in rich black.
        int thin = Array.FindIndex(ops, o => o.Operator == "S");
        Assert.Equal([0.0, 0, 0, 1], ops[..thin].Last(o => o.Operator == "K").Operands.Select(o => ((PdfReal)o).Value));
        int thick = Array.FindLastIndex(ops, o => o.Operator == "S");
        Assert.True(((PdfReal)ops[..thick].Last(o => o.Operator == "K").Operands[0]).Value > 0.3);
        Assert.Equal(2, result.PureBlack);

        using PdfDocument colorimetric = PdfDocument.Create();
        PdfPage other = colorimetric.Pages.AddBlank();
        other.Resources = (PdfDictionary)page.Resources!.Clone();
        other.SetContent(PdfContent.Parse("0 0 0 rg BT /F1 12 Tf 72 700 Td (Black) Tj ET"u8));
        PdfColorConverter.Convert(colorimetric, new PdfColorConversionOptions { BlackText = PdfBlackText.Colorimetric }, TestContext.Current.CancellationToken);
        Assert.True(((PdfReal)other.GetContent().First(o => o.Operator == "k").Operands[0]).Value > 0.3);
    }

    [Fact]
    public void SpotsKeepTheirPlatesWithTheAlternateConvertedOrConvertWhenAsked()
    {
        static PdfDocument Spot(out PdfPage page)
        {
            PdfDocument pdf = PdfDocument.Create();
            page = pdf.Pages.AddBlank();
            PdfDictionary tint = new()
            {
                ["FunctionType"] = new PdfInteger(2),
                ["Domain"] = new PdfArray { new PdfInteger(0), new PdfInteger(1) },
                ["C0"] = new PdfArray { new PdfInteger(1), new PdfInteger(1), new PdfInteger(1) },
                ["C1"] = new PdfArray { new PdfReal(0.9), new PdfInteger(0), new PdfReal(0.1) },
                ["N"] = new PdfInteger(1),
            };
            page.Resources = new PdfDictionary { ["ColorSpace"] = new PdfDictionary { ["CS0"] = new PdfArray { PdfName.Get("Separation"), PdfName.Get("Rubine"), PdfName.Get("DeviceRGB"), tint } } };
            page.SetContent(PdfContent.Parse("/CS0 cs 1 sc 0 0 100 100 re f"u8));
            return pdf;
        }

        using (PdfDocument kept = Spot(out PdfPage page))
        {
            PdfColorConversionResult result = PdfColorConverter.Convert(kept, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(1, result.Alternates);
            Assert.Contains("/CS0 cs", Content(page), StringComparison.Ordinal);
            PdfArray space = page.Resources!.Get<PdfDictionary>("ColorSpace")!.Get<PdfArray>("CS0")!;
            Assert.Equal("DeviceCMYK", space.GetName(2)!.Value);
            using PdfSeparations plates = page.RenderSeparations(new PdfRenderOptions { Dpi = 18 }, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(255, plates.Plates.Single(p => p.Name == "Rubine").Raster.GetPixel(10, 190).L);
            Assert.DoesNotContain(PdfPreflight.Run(kept, PdfPrintProfile.PdfX4, TestContext.Current.CancellationToken).Issues, i => i.Clause == "ColorSpace");
        }

        using PdfDocument converted = Spot(out PdfPage converts);
        PdfColorConverter.Convert(converted, new PdfColorConversionOptions { Spots = PdfSpotConversion.Convert }, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("/CS0", Content(converts), StringComparison.Ordinal);
        double[] cmyk = Fogra(0.9f, 0, 0.1f);
        Assert.Equal(cmyk[1], Operands(converts, "sc")[1], 3);
    }

    [Fact]
    public void ImagesConvertAndJpegsStayJpegs()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfPage page = pdf.Pages.AddBlank();
        using Raster<Rgb8> photo = Raster.Create(64, 64, new Rgb8(200, 40, 40));
        using MemoryStream jpeg = new();
        photo.Save(jpeg, Egami.Formats.Jpeg.JpegFormat.Info, new Egami.Formats.Jpeg.JpegWriterOptions { Quality = 80 });
        PdfStream photoStream = new(new PdfDictionary
        {
            ["Type"] = PdfName.Get("XObject"),
            ["Subtype"] = PdfName.Get("Image"),
            ["Width"] = new PdfInteger(64),
            ["Height"] = new PdfInteger(64),
            ["BitsPerComponent"] = new PdfInteger(8),
            ["ColorSpace"] = PdfName.Get("DeviceRGB"),
            ["Filter"] = PdfName.Get("DCTDecode"),
        });
        photoStream.SetEncodedBytes(jpeg.ToArray());
        page.Resources = new PdfDictionary { ["XObject"] = new PdfDictionary { ["Im"] = pdf.Objects.Add(photoStream) } };
        page.SetContent(PdfContent.Parse("q 100 0 0 100 0 0 cm /Im Do Q q 100 0 0 50 0 200 cm /Ix Do Q"u8));
        PdfStream flat = new(new PdfDictionary
        {
            ["Type"] = PdfName.Get("XObject"),
            ["Subtype"] = PdfName.Get("Image"),
            ["Width"] = new PdfInteger(2),
            ["Height"] = new PdfInteger(1),
            ["BitsPerComponent"] = new PdfInteger(8),
            ["ColorSpace"] = new PdfArray { PdfName.Get("Indexed"), PdfName.Get("DeviceRGB"), new PdfInteger(1), PdfString.FromBytes([255, 0, 0, 0, 0, 255], hex: true) },
        });
        flat.SetDecodedBytes([0, 1]);
        page.Resources.Get<PdfDictionary>("XObject")!["Ix"] = pdf.Objects.Add(flat);

        PdfColorConversionResult result = PdfColorConverter.Convert(pdf, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Images);
        PdfStream converted = photoStream;
        Assert.Equal("DCTDecode", converted.Dictionary.GetName("Filter")!.Value);
        Assert.Equal("DeviceCMYK", converted.Dictionary.GetName("ColorSpace")!.Value);
        PdfArray palette = Assert.IsType<PdfArray>(flat.Dictionary.Get("ColorSpace"));
        Assert.Equal("DeviceCMYK", palette.GetName(1)!.Value);
        Assert.Equal(8, ((PdfString)palette[3]).Bytes.Length);
        Assert.Equal([0, 1], flat.GetDecodedBytes());
        Assert.Empty(PdfPreflight.Run(pdf, PdfPrintProfile.SheetfedCoated with { RequiredBleed = null, MinImageDpi = null }, TestContext.Current.CancellationToken).Issues);
    }

    [Fact]
    public void ShadingsGetAFunctionInTheTarget()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfPage page = pdf.Pages.AddBlank();
        PdfDictionary shading = new()
        {
            ["ShadingType"] = new PdfInteger(2),
            ["ColorSpace"] = PdfName.Get("DeviceRGB"),
            ["Coords"] = new PdfArray { new PdfInteger(0), new PdfInteger(0), new PdfInteger(100), new PdfInteger(0) },
            ["Function"] = new PdfDictionary
            {
                ["FunctionType"] = new PdfInteger(2),
                ["Domain"] = new PdfArray { new PdfInteger(0), new PdfInteger(1) },
                ["C0"] = new PdfArray { new PdfInteger(1), new PdfInteger(0), new PdfInteger(0) },
                ["C1"] = new PdfArray { new PdfInteger(0), new PdfInteger(0), new PdfInteger(1) },
                ["N"] = new PdfInteger(1),
            },
        };
        page.Resources = new PdfDictionary { ["Shading"] = new PdfDictionary { ["Sh"] = pdf.Objects.Add(shading) } };
        page.SetContent(PdfContent.Parse("0 0 100 100 re W n /Sh sh"u8));

        PdfColorConverter.Convert(pdf, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("DeviceCMYK", shading.GetName("ColorSpace")!.Value);
        PdfStream function = Assert.IsType<PdfStream>(shading.Get("Function")!.Resolve());
        Assert.Equal(8, function.Dictionary.Get<PdfArray>("Range")!.Count);
        Assert.Equal(256 * 4, function.GetDecodedBytes().Length);
    }

    [Fact]
    public void RetargetCmykMovesCmykBetweenProfiles()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfPage page = pdf.Pages.AddBlank();
        page.SetContent(PdfContent.Parse("0.9 0.8 0.76 0.8 k 0 0 100 100 re f"u8));
        ColorProfile coated = ColorProfile.Fogra39;

        PdfColorConverter.Convert(pdf, new PdfColorConversionOptions { RetargetCmyk = true, SourceCmyk = coated, Target = ColorProfile.Srgb }, TestContext.Current.CancellationToken);

        double[] rgb = Operands(page, "rg");
        Assert.All(rgb, v => Assert.InRange(v, 0, 0.25));
    }

    [Fact]
    public void ConvertedPagesShowAsBefore()
    {
        using PdfDocument pdf = PdfDocument.Open(new MemoryStream(PrintSamples.Affinity().Bytes));
        using Raster<Rgba8> before = pdf.Pages[0].Render(new PdfRenderOptions { Dpi = 18 }, TestContext.Current.CancellationToken);

        PdfColorConverter.Convert(pdf, cancellationToken: TestContext.Current.CancellationToken);

        using Raster<Rgba8> after = pdf.Pages[0].Render(new PdfRenderOptions { Dpi = 18 }, TestContext.Current.CancellationToken);
        double worst = 0;
        for (int y = 0; y < after.Height; y++)
        {
            for (int x = 0; x < after.Width; x++)
            {
                Rgba8 p = after.GetPixel(x, y), q = before.GetPixel(x, y);
                worst = Math.Max(worst, Math.Max(Math.Abs(p.R - q.R), Math.Max(Math.Abs(p.G - q.G), Math.Abs(p.B - q.B))));
            }
        }

        // RGB that fits the press gamut comes back within a few levels through the FOGRA39 output intent.
        Assert.True(worst <= 40, $"a pixel moved {worst} levels");
        PdfPreflightReport report = PdfPreflight.Run(pdf, PdfPrintProfile.SheetfedCoated with { MinImageDpi = null }, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(report.Issues, i => i.Clause == "ColorSpace");
    }
}
