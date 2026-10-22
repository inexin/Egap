using System.Globalization;
using System.Text;

namespace Egap.Pdf.Tests.Print;

/// <summary>
/// Hand-made print files with known contents, written byte by byte so what preflight must find does not come from Egap:
/// colour spaces, spot names, output intents, image resolutions, fonts, transparency, overprint, hairlines, boxes, and flat
/// ink scenes whose total area coverage is worked out by hand.
/// </summary>
internal static class PrintSamples
{
    /// <summary>A file and what preflight must find in it.</summary>
    public sealed record PrintFile(string Name, byte[] Bytes, PrintTruth Truth);

    /// <summary>What a file holds, page by page (from 1).</summary>
    public sealed record PrintTruth(string? OutputCondition, string? OutputIntentSpace, IReadOnlyList<PageTruth> Pages);

    /// <summary>
    /// One page: colour space families painted (<c>DeviceCMYK</c>, <c>ICCBased RGB</c> and so on), spot inks by name, images by
    /// resource name with their colour space family and effective dpi, fonts not embedded, transparency and overprint, rules
    /// thinner than 0.25 pt, whether a bleed box reaches 3 mm past the trim box, and the maximum total ink in percent of a
    /// flat-colour page (null where images or blends make it not a hand sum).
    /// </summary>
    public sealed record PageTruth(
        IReadOnlyList<string> Spaces,
        IReadOnlyList<string> Spots,
        IReadOnlyList<ImageTruth> Images,
        IReadOnlyList<string> FontsNotEmbedded,
        bool Transparency,
        bool Overprint,
        int Hairlines,
        bool Bleed,
        double? MaxTac,
        double? ShareOver300);

    /// <summary>An image by resource name, its colour space family and its effective resolution.</summary>
    public sealed record ImageTruth(string Name, string Space, double Dpi);

    /// <summary>Every hand-made file.</summary>
    public static IReadOnlyList<PrintFile> All() => [Affinity(), Spots(), Transparency(), FontsAndRules(), Mixed(), FlatInk()];

    /// <summary>The FOGRA39 profile Egami bundles, as ICC bytes.</summary>
    public static byte[] Fogra39() => Egami.Colors.ColorProfile.Fogra39.ToIcc();

    /// <summary>
    /// The Affinity forum case: a CMYK file with a FOGRA39 output intent (PDF/X and PDF/A), a 3 mm bleed, a photo-like CMYK
    /// fill at 326 % total ink, an ICC-tagged sRGB image passed through untouched (Im1, 300 dpi) and a DeviceRGB image at
    /// 96 dpi (Im2).
    /// </summary>
    public static PrintFile Affinity()
    {
        byte[] fogra = Fogra39();
        byte[] srgb = Egami.Colors.ColorProfile.Srgb.ToIcc();
        const string content = "0.9 0.8 0.76 0.8 k 100 400 200 200 re f\n"
            + "q 72 0 0 72 320 400 cm /Im1 Do Q\n"
            + "q 150 0 0 150 100 100 cm /Im2 Do Q\n";
        Builder pdf = new();
        int catalog = pdf.Reserve(), pages = pdf.Reserve(), page = pdf.Reserve();
        int intentProfile = pdf.Stream($"/N 4", fogra);
        int rgbProfile = pdf.Stream($"/N 3", srgb);
        int im1 = pdf.Stream($"/Type /XObject /Subtype /Image /Width 300 /Height 300 /BitsPerComponent 8 /ColorSpace [/ICCBased {rgbProfile} 0 R]", Gradient(300, 300, 3));
        int im2 = pdf.Stream("/Type /XObject /Subtype /Image /Width 200 /Height 200 /BitsPerComponent 8 /ColorSpace /DeviceRGB", Gradient(200, 200, 3));
        int contents = pdf.Stream("", Encoding.ASCII.GetBytes(content));
        pdf.Set(catalog, $"<< /Type /Catalog /Pages {pages} 0 R /OutputIntents [<< /Type /OutputIntent /S /GTS_PDFX /OutputConditionIdentifier (FOGRA39)"
            + $" /RegistryName (http://www.color.org) /Info (Coated FOGRA39) /DestOutputProfile {intentProfile} 0 R >>] >>");
        pdf.Set(pages, $"<< /Type /Pages /Kids [{page} 0 R] /Count 1 >>");
        pdf.Set(page, $"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 612 792] /TrimBox [36 36 576 756] /BleedBox [27.5 27.5 584.5 764.5]"
            + $" /Contents {contents} 0 R /Resources << /XObject << /Im1 {im1} 0 R /Im2 {im2} 0 R >> >> >>");
        return new PrintFile("affinity.pdf", pdf.ToBytes(catalog), new PrintTruth("FOGRA39", "CMYK",
        [
            new PageTruth(["DeviceCMYK", "ICCBased RGB", "DeviceRGB"], [], [new ImageTruth("Im1", "ICCBased RGB", 300), new ImageTruth("Im2", "DeviceRGB", 96)],
                [], false, false, 0, true, null, null),
        ]));
    }

    /// <summary>
    /// Spot colours: a Separation "PANTONE 186 C" fill at 100 % and 50 %, a DeviceN of Cyan, Magenta and "Gold", the spot
    /// overprinting a CMYK fill with overprint mode 1, and an All separation registration mark.
    /// </summary>
    public static PrintFile Spots()
    {
        const string content = "0.2 0.3 0 0 k 50 500 300 200 re f\n"
            + "/CS0 cs 1 scn 50 600 100 100 re f 0.5 scn 200 600 100 100 re f\n"
            + "q /GS0 gs /CS0 cs 1 scn 100 520 100 60 re f Q\n"
            + "/CS1 cs 0.5 0.2 1 scn 50 300 200 150 re f\n"
            + "/CS2 CS 1 SCN 2 w 400 100 m 500 100 l S\n";
        Builder pdf = new();
        int catalog = pdf.Reserve(), pages = pdf.Reserve(), page = pdf.Reserve();
        int tint = pdf.Stream("/FunctionType 4 /Domain [0 1 0 1 0 1] /Range [0 1 0 1 0 1 0 1]", Encoding.ASCII.GetBytes("{ 0.8 mul 0 }"));
        int contents = pdf.Stream("", Encoding.ASCII.GetBytes(content));
        pdf.Set(catalog, $"<< /Type /Catalog /Pages {pages} 0 R >>");
        pdf.Set(pages, $"<< /Type /Pages /Kids [{page} 0 R] /Count 1 >>");
        pdf.Set(page, $"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 612 792] /Contents {contents} 0 R /Resources << /ColorSpace <<"
            + " /CS0 [/Separation /PANTONE#20186#20C /DeviceCMYK << /FunctionType 2 /Domain [0 1] /C0 [0 0 0 0] /C1 [0 1 0.81 0.04] /N 1 >>]"
            + $" /CS1 [/DeviceN [/Cyan /Magenta /Gold] /DeviceCMYK {tint} 0 R]"
            + " /CS2 [/Separation /All /DeviceCMYK << /FunctionType 2 /Domain [0 1] /C0 [0 0 0 0] /C1 [1 1 1 1] /N 1 >>] >>"
            + " /ExtGState << /GS0 << /Type /ExtGState /op true /OP true /OPM 1 >> >> >> >>");
        return new PrintFile("spots.pdf", pdf.ToBytes(catalog), new PrintTruth(null, null,
        [
            new PageTruth(["DeviceCMYK", "Separation", "DeviceN"], ["Gold", "PANTONE 186 C"], [], [], false, true, 0, false, null, null),
        ]));
    }

    /// <summary>Transparency: a page group, a constant alpha of 0.5, a Multiply blend and a luminosity soft mask.</summary>
    public static PrintFile Transparency()
    {
        const string content = "0 0 0 0.5 k 50 400 300 300 re f\n"
            + "q /GS0 gs 1 0 0 0 k 100 450 200 200 re f Q\n"
            + "q /GS1 gs 0 1 0 0 k 150 500 200 200 re f Q\n"
            + "q /GS2 gs 0 0 1 0 k 100 100 200 200 re f Q\n";
        Builder pdf = new();
        int catalog = pdf.Reserve(), pages = pdf.Reserve(), page = pdf.Reserve();
        int mask = pdf.Stream("/Type /XObject /Subtype /Form /BBox [0 0 612 792] /Group << /S /Transparency /CS /DeviceGray >>",
            Encoding.ASCII.GetBytes("1 g 100 100 100 200 re f"));
        int contents = pdf.Stream("", Encoding.ASCII.GetBytes(content));
        pdf.Set(catalog, $"<< /Type /Catalog /Pages {pages} 0 R >>");
        pdf.Set(pages, $"<< /Type /Pages /Kids [{page} 0 R] /Count 1 >>");
        pdf.Set(page, $"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 612 792] /Group << /S /Transparency /CS /DeviceCMYK /I true >>"
            + $" /Contents {contents} 0 R /Resources << /ExtGState << /GS0 << /ca 0.5 >> /GS1 << /BM /Multiply >>"
            + $" /GS2 << /SMask << /S /Luminosity /G {mask} 0 R >> >> >> >> >>");
        return new PrintFile("transparency.pdf", pdf.ToBytes(catalog), new PrintTruth(null, null,
        [
            new PageTruth(["DeviceCMYK"], [], [], [], true, false, 0, false, null, null),
        ]));
    }

    /// <summary>
    /// Fonts and rules: Helvetica not embedded, a 0.1 pt rule and a zero-width rule (two hairlines), a 1 pt rule that is not
    /// one, a Lab fill, a CalRGB fill, an Indexed image (Im1, 72 dpi) with a DeviceRGB base, no trim or bleed box.
    /// </summary>
    public static PrintFile FontsAndRules()
    {
        const string content = "BT /F1 12 Tf 72 700 Td (Not embedded) Tj ET\n"
            + "0 G 0.1 w 72 650 m 300 650 l S 0 w 72 640 m 300 640 l S 1 w 72 630 m 300 630 l S\n"
            + "/Lab0 cs 50 20 -30 sc 72 500 100 100 re f\n"
            + "/Cal0 cs 0.2 0.4 0.6 sc 200 500 100 100 re f\n"
            + "q 100 0 0 100 72 300 cm /Im1 Do Q\n";
        Builder pdf = new();
        int catalog = pdf.Reserve(), pages = pdf.Reserve(), page = pdf.Reserve();
        byte[] samples = new byte[100 * 100];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = (byte)(i % 4);
        }

        int image = pdf.Stream("/Type /XObject /Subtype /Image /Width 100 /Height 100 /BitsPerComponent 8 /ColorSpace [/Indexed /DeviceRGB 3 <FF000000FF000000FFFFFFFF>]", samples);
        int contents = pdf.Stream("", Encoding.ASCII.GetBytes(content));
        pdf.Set(catalog, $"<< /Type /Catalog /Pages {pages} 0 R >>");
        pdf.Set(pages, $"<< /Type /Pages /Kids [{page} 0 R] /Count 1 >>");
        pdf.Set(page, $"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 612 792] /Contents {contents} 0 R /Resources <<"
            + " /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >> >>"
            + " /ColorSpace << /Lab0 [/Lab << /WhitePoint [0.9505 1 1.089] /Range [-100 100 -100 100] >>]"
            + " /Cal0 [/CalRGB << /WhitePoint [0.9505 1 1.089] /Gamma [2.2 2.2 2.2] >>] >>"
            + $" /XObject << /Im1 {image} 0 R >> >> >>");
        return new PrintFile("fonts-rules.pdf", pdf.ToBytes(catalog), new PrintTruth(null, null,
        [
            new PageTruth(["DeviceGray", "Lab", "CalRGB", "Indexed"], [], [new ImageTruth("Im1", "Indexed", 72)], ["Helvetica"], false, false, 2, false, null, null),
        ]));
    }

    /// <summary>Two pages: the first CMYK and gray only, the second an RGB fill and an RGB stroke.</summary>
    public static PrintFile Mixed()
    {
        Builder pdf = new();
        int catalog = pdf.Reserve(), pages = pdf.Reserve(), page1 = pdf.Reserve(), page2 = pdf.Reserve();
        int c1 = pdf.Stream("", Encoding.ASCII.GetBytes("0 0 0 1 k 72 600 200 100 re f 0.5 g 72 400 200 100 re f\n"));
        int c2 = pdf.Stream("", Encoding.ASCII.GetBytes("1 0 0 rg 72 600 200 100 re f 0 0 1 RG 2 w 72 500 m 300 500 l S\n"));
        pdf.Set(catalog, $"<< /Type /Catalog /Pages {pages} 0 R >>");
        pdf.Set(pages, $"<< /Type /Pages /Kids [{page1} 0 R {page2} 0 R] /Count 2 /MediaBox [0 0 612 792] >>");
        pdf.Set(page1, $"<< /Type /Page /Parent {pages} 0 R /TrimBox [9 9 603 783] /BleedBox [0 0 612 792] /Contents {c1} 0 R /Resources << >> >>");
        pdf.Set(page2, $"<< /Type /Page /Parent {pages} 0 R /TrimBox [9 9 603 783] /BleedBox [0 0 612 792] /Contents {c2} 0 R /Resources << >> >>");
        return new PrintFile("mixed.pdf", pdf.ToBytes(catalog), new PrintTruth(null, null,
        [
            new PageTruth(["DeviceCMYK", "DeviceGray"], [], [], [], false, false, 0, true, 100, 0),
            new PageTruth(["DeviceRGB"], [], [], [], false, false, 0, true, null, null),
        ]));
    }

    /// <summary>
    /// Flat ink scenes for coverage worked out by hand: a 100 x 100 pt page, a 50 x 100 pt rectangle at 90/80/76/80 (326 %) and
    /// beside it 50 x 50 pt at 50/40/40/20 (150 %). Max total ink 326 %, and half the page is over 300 %.
    /// </summary>
    public static PrintFile FlatInk()
    {
        Builder pdf = new();
        int catalog = pdf.Reserve(), pages = pdf.Reserve(), page = pdf.Reserve();
        int contents = pdf.Stream("", Encoding.ASCII.GetBytes("0.9 0.8 0.76 0.8 k 0 0 50 100 re f 0.5 0.4 0.4 0.2 k 50 0 50 50 re f\n"));
        pdf.Set(catalog, $"<< /Type /Catalog /Pages {pages} 0 R >>");
        pdf.Set(pages, $"<< /Type /Pages /Kids [{page} 0 R] /Count 1 >>");
        pdf.Set(page, $"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 100 100] /Contents {contents} 0 R /Resources << >> >>");
        return new PrintFile("flat-ink.pdf", pdf.ToBytes(catalog), new PrintTruth(null, null,
        [
            new PageTruth(["DeviceCMYK"], [], [], [], false, false, 0, false, 326, 0.5),
        ]));
    }

    /// <summary>A smooth test image: <paramref name="components"/> channels of diagonal ramps.</summary>
    private static byte[] Gradient(int width, int height, int components)
    {
        byte[] data = new byte[width * height * components];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                for (int c = 0; c < components; c++)
                {
                    data[(((y * width) + x) * components) + c] = (byte)((((x * (c + 1)) + (y * (3 - c))) * 255 / ((width + height) * 3)) & 0xFF);
                }
            }
        }

        return data;
    }

    /// <summary>Objects written in order with a classic cross-reference table; streams stored as they are.</summary>
    private sealed class Builder
    {
        private readonly List<byte[]?> objects = [];

        public int Reserve()
        {
            objects.Add(null);
            return objects.Count;
        }

        public void Set(int number, string body)
        {
            objects[number - 1] = Encoding.ASCII.GetBytes(body);
        }

        public int Stream(string entries, byte[] data)
        {
            byte[] head = Encoding.ASCII.GetBytes($"<< {entries} /Length {data.Length} >>\nstream\n");
            byte[] tail = "\nendstream"u8.ToArray();
            objects.Add([.. head, .. data, .. tail]);
            return objects.Count;
        }

        public byte[] ToBytes(int root)
        {
            using MemoryStream output = new();
            output.Write("%PDF-1.6\n%\xE2\xE3\xCF\xD3\n"u8);
            List<long> offsets = [];
            for (int i = 0; i < objects.Count; i++)
            {
                offsets.Add(output.Position);
                output.Write(Encoding.ASCII.GetBytes($"{i + 1} 0 obj\n"));
                output.Write(objects[i] ?? throw new InvalidOperationException($"Object {i + 1} was reserved and never set."));
                output.Write("\nendobj\n"u8);
            }

            long xref = output.Position;
            StringBuilder table = new($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
            foreach (long offset in offsets)
            {
                table.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
            }

            table.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Count + 1} /Root {root} 0 R >>\nstartxref\n{xref}\n%%EOF\n");
            output.Write(Encoding.ASCII.GetBytes(table.ToString()));
            return output.ToArray();
        }
    }
}
