using System.Globalization;
using System.Text;

using Egami.Fonts;

namespace Egap.Pdf.Tests.PdfA;

/// <summary>PDF/A conversion and its report (Phase 5 task 15): every fix reported, unmet rules leave the file unchanged.</summary>
public sealed class PdfAConvertTests
{
    /// <summary>
    /// A file that breaks many rules conversion can fix: Helvetica not embedded, a script run on opening, page actions,
    /// an LZW content stream, Info without XMP, a hidden note without an appearance, a form asking viewers to draw it, an
    /// interpolated image and a transfer function.
    /// </summary>
    internal static byte[] Breaking(string content = "BT /F1 12 Tf 72 700 Td (Archive me) Tj ET q 10 0 0 10 300 300 cm /Im Do Q /G gs", string version = "1.6", string fontName = "Helvetica")
    {
        string lzw = Encoding.Latin1.GetString(Lzw(Encoding.Latin1.GetBytes(content)));
        string[] objects =
        [
            /* 1 */ "<< /Type /Catalog /Pages 2 0 R /OpenAction << /S /JavaScript /JS (app.alert(1)) >> /AcroForm << /Fields [] /NeedAppearances true >> >>",
            /* 2 */ "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            /* 3 */ "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R /AA << /O << /S /JavaScript /JS (z) >> >>"
                + " /Resources << /Font << /F1 5 0 R >> /XObject << /Im 7 0 R >> /ExtGState << /G << /TR /Identity >> >> >> /Annots [6 0 R] >>",
            /* 4 */ $"<< /Length {lzw.Length} /Filter /LZWDecode >>\nstream\n{lzw}\nendstream",
            /* 5 */ $"<< /Type /Font /Subtype /Type1 /BaseFont /{fontName} /Encoding /WinAnsiEncoding >>",
            /* 6 */ "<< /Type /Annot /Subtype /Square /Rect [100 100 200 200] /F 2 /C [1 0 0] >>",
            /* 7 */ "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 /Interpolate true /Length 1 >>\nstream\n\x80\nendstream",
        ];
        StringBuilder file = new($"%PDF-{version}\n");
        List<int> offsets = [];
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(Encoding.Latin1.GetByteCount(file.ToString()));
            file.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        int xref = Encoding.Latin1.GetByteCount(file.ToString());
        file.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Length + 1}\n0000000000 65535 f\r\n");
        foreach (int offset in offsets)
        {
            file.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n\r\n");
        }

        file.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R /Info << /Title (Breaking) /Author (Someone) /CreationDate (D:20260101120000Z) >> >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(file.ToString());
    }

    /// <summary>LZW without compression worth the name: a clear-table code, then each byte as a 9-bit code, kept below the first growth.</summary>
    private static byte[] Lzw(byte[] data)
    {
        List<byte> output = [];
        int buffer = 0, bits = 0;
        void Code(int code)
        {
            buffer = (buffer << 9) | code;
            bits += 9;
            while (bits >= 8)
            {
                output.Add((byte)(buffer >> (bits - 8)));
                bits -= 8;
            }
        }

        int written = 0;
        Code(256);
        foreach (byte b in data)
        {
            // Each code adds a table entry; clear before the table needs 10-bit codes.
            if (written++ == 250)
            {
                Code(256);
                written = 1;
            }

            Code(b);
        }

        Code(257);
        if (bits > 0)
        {
            output.Add((byte)(buffer << (8 - bits)));
        }

        return [.. output];
    }

    private static byte[] Save(PdfDocument pdf)
    {
        using MemoryStream output = new();
        pdf.Save(output);
        return output.ToArray();
    }

    [Fact]
    public void EveryFixIsReportedAndTheResultChecksClean()
    {
        using PdfDocument pdf = PdfDocument.Open(Breaking());
        Assert.Contains("Archive me", pdf.Pages[0].ExtractText(), StringComparison.Ordinal);
        PdfAReport report = PdfAConverter.Convert(pdf, PdfAConformance.PdfA2b);
        Assert.True(report.Succeeded, report.ToString());
        string text = report.ToString();
        foreach (string expected in (string[])["output intent", "JavaScript", "additional actions", "LZW", "Helvetica", "Title", "hidden", "no appearance", "draw field appearances", "interpolation", "transfer function", "identify"])
        {
            Assert.Contains(expected, text, StringComparison.Ordinal);
        }

        Assert.All(report.Issues, i => Assert.True(i.Fixed));
        byte[] saved = Save(pdf);
        Assert.StartsWith("%PDF-1.7", Encoding.Latin1.GetString(saved, 0, 8), StringComparison.Ordinal);
        using PdfDocument result = PdfDocument.Open(saved);
        Assert.Equal(PdfAConformance.PdfA2b, result.Conformance);
        PdfAReport again = PdfAConverter.Check(result, PdfAConformance.PdfA2b);
        Assert.True(again.Succeeded, again.ToString());
        Assert.Contains("Archive me", result.Pages[0].ExtractText(), StringComparison.Ordinal);
        Assert.Equal("Breaking", result.Metadata.Title);
    }

    [Fact]
    public void AnUnmetRuleChangesNothing()
    {
        using PdfDocument pdf = PdfDocument.Open(Breaking("BT /F1 12 Tf 72 700 Td (x) Tj ET /S gs 0 0 1 1 k 0 0 10 10 re f", "1.4"));
        pdf.Pages[0].Resources!.Get<PdfDictionary>("ExtGState")!["S"] = new PdfDictionary { ["ca"] = new PdfReal(0.5) };
        byte[] before = Save(pdf);
        using PdfDocument fresh = PdfDocument.Open(before);
        PdfAReport report = PdfAConverter.Convert(fresh, PdfAConformance.PdfA1b);
        Assert.False(report.Succeeded);
        Assert.Contains(report.Unmet, i => !i.Fixable && i.Description.Contains("transparency", StringComparison.Ordinal));
        Assert.Contains(report.Unmet, i => !i.Fixable && i.Description.Contains("DeviceCMYK", StringComparison.Ordinal));
        Assert.Contains(report.Unmet, i => i.Fixable && i.Description.Contains("JavaScript", StringComparison.Ordinal));
        Assert.Empty(report.Fixes);
        Assert.False(fresh.IsChanged);
        Assert.Equal(before, Save(fresh));
    }

    [Fact]
    public void CheckingChangesNothing()
    {
        using PdfDocument pdf = PdfDocument.Open(Breaking());
        PdfAReport report = PdfAConverter.Check(pdf, PdfAConformance.PdfA3u);
        Assert.False(report.Succeeded);
        Assert.All(report.Issues, i => Assert.False(i.Fixed));
        Assert.All(report.Issues, i => Assert.True(i.Fixable, i.Description));
        Assert.False(pdf.IsChanged);
    }

    [Fact]
    public void AnUnknownFontNeedsSubstitutionAndKeepsItsWidths()
    {
        // A font no machine has; with substitution, a bundled sans font takes the PDF's widths.
        byte[] input = Breaking(fontName: "EgapTestUnknownSans");
        using (PdfDocument pdf = PdfDocument.Open(input))
        {
            PdfDictionary font = pdf.Pages[0].Resources!.Get<PdfDictionary>("Font")!.Get<PdfDictionary>("F1")!;
            font["FirstChar"] = new PdfInteger(65);
            font["LastChar"] = new PdfInteger(66);
            font["Widths"] = new PdfArray { new PdfInteger(700), new PdfInteger(650) };
            PdfAReport report = PdfAConverter.Convert(pdf, PdfAConformance.PdfA2b);
            Assert.False(report.Succeeded);
            Assert.Contains(report.Unmet, i => i.Description.Contains("EgapTestUnknownSans", StringComparison.Ordinal) && i.Description.Contains("AllowFontSubstitution", StringComparison.Ordinal));

            report = PdfAConverter.Convert(pdf, PdfAConformance.PdfA2b, new PdfAConvertOptions { AllowFontSubstitution = true });
            Assert.True(report.Succeeded, report.ToString());
            Assert.Contains(report.Fixes, i => i.Description.Contains("substituted", StringComparison.Ordinal));
            using PdfDocument saved = PdfDocument.Open(Save(pdf));
            PdfDictionary embedded = saved.Pages[0].Resources!.Get<PdfDictionary>("Font")!.Get<PdfDictionary>("F1")!;
            Assert.Equal(700, embedded.Get<PdfArray>("Widths")!.GetNumber((int)(65 - embedded.GetInt("FirstChar")!.Value)));

            // The subset's own advance for "A" is the PDF's width.
            Typeface face = Typeface.FromMemory(embedded.Get<PdfDictionary>("FontDescriptor")!.Get<PdfStream>("FontFile2")!.GetDecodedBytes());
            Assert.Equal(700, new Font(face, 1000).GetAdvance(face.GetGlyph('A')), 0.5);
        }
    }

    [Fact]
    public void ThePdf20DefaultIsPdfA4()
    {
        using PdfDocument pdf = PdfDocument.Open(Breaking(version: "2.0"));
        Assert.Equal(PdfAConformance.PdfA4, PdfAConverter.DefaultFor(pdf));
        PdfAReport report = PdfAConverter.Convert(pdf);
        Assert.True(report.Succeeded, report.ToString());
        using PdfDocument saved = PdfDocument.Open(Save(pdf));
        Assert.Equal(PdfAConformance.PdfA4, saved.Conformance);
        Assert.Null(saved.Trailer["Info"]);
        Assert.Equal("Breaking", saved.Metadata.Title);
    }

    [Fact]
    public void AnEncryptedFileIsWrittenWithoutEncryption()
    {
        byte[] encrypted;
        using (PdfDocument pdf = PdfDocument.Open(Breaking()))
        using (MemoryStream output = new())
        {
            pdf.Save(output, new PdfSaveOptions { Encryption = PdfEncryption.Aes256("", "owner") });
            encrypted = output.ToArray();
        }

        using PdfDocument opened = PdfDocument.Open(encrypted);
        Assert.True(opened.IsEncrypted);
        PdfAReport report = PdfAConverter.Convert(opened, PdfAConformance.PdfA2b);
        Assert.True(report.Succeeded, report.ToString());
        Assert.Contains(report.Fixes, i => i.Description.Contains("encrypted", StringComparison.Ordinal));
        using PdfDocument saved = PdfDocument.Open(Save(opened));
        Assert.False(saved.IsEncrypted);
        Assert.Contains("Archive me", saved.Pages[0].ExtractText(), StringComparison.Ordinal);
    }

    [Fact]
    public void PdfA1IsWrittenWithoutObjectStreams()
    {
        using PdfDocument source = PdfDocument.Open(Breaking());
        byte[] compact;
        using (MemoryStream output = new())
        {
            source.Save(output, new PdfSaveOptions { Mode = PdfSaveMode.Rewrite, Compression = PdfCompression.Compact });
            compact = output.ToArray();
        }

        using PdfDocument pdf = PdfDocument.Open(compact);
        PdfAReport report = PdfAConverter.Convert(pdf, PdfAConformance.PdfA1b);
        Assert.True(report.Succeeded, report.ToString());
        string saved = Encoding.Latin1.GetString(Save(pdf));
        Assert.StartsWith("%PDF-1.4", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("/ObjStm", saved, StringComparison.Ordinal);
    }

    [Fact]
    public void OldXmpIsPutInForm()
    {
        // Acrobat 5 era XMP: properties in schemas PDF/A does not know, a creator as plain text, a header with bytes.
        string xmp = "<?xpacket begin='' id='W5M0MpCehiHzreSzNTczkc9d' bytes='1024'?><x:xmpmeta xmlns:x='adobe:ns:meta/'><rdf:RDF xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns#'>"
            + "<rdf:Description rdf:about='' xmlns:pdf='http://ns.adobe.com/pdf/1.3/' xmlns:dc='http://purl.org/dc/elements/1.1/' xmlns:xap='http://ns.adobe.com/xap/1.0/' pdf:Title='Old' xap:Author='Someone'>"
            + "<dc:creator>Someone</dc:creator><pdf:Producer>Distiller</pdf:Producer></rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end='w'?>";
        using PdfDocument pdf = PdfDocument.Open(Breaking());
        PdfStream metadata = new(new PdfDictionary { ["Type"] = PdfName.Get("Metadata"), ["Subtype"] = PdfName.Get("XML") });
        metadata.SetDecodedBytes(Encoding.UTF8.GetBytes(xmp), compress: false);
        pdf.Catalog["Metadata"] = pdf.Objects.Add(metadata);
        PdfAReport report = PdfAConverter.Convert(pdf, PdfAConformance.PdfA2b);
        Assert.True(report.Succeeded, report.ToString());
        Assert.Contains(report.Fixes, i => i.Clause == "6.6.2.3");
        using PdfDocument saved = PdfDocument.Open(Save(pdf));
        string written = saved.Metadata.GetXmp()!.ToString();
        Assert.DoesNotContain("pdf:Title", written, StringComparison.Ordinal);
        Assert.DoesNotContain("Author", written, StringComparison.Ordinal);
        Assert.DoesNotContain("bytes=", written, StringComparison.Ordinal);
        Assert.Contains("rdf:Seq", written, StringComparison.Ordinal);
        Assert.Contains("Distiller", written, StringComparison.Ordinal);
        Assert.True(PdfAConverter.Check(saved, PdfAConformance.PdfA2b).Succeeded);
    }

    [Fact]
    public void ControlCharactersInInfoDoNotStopConversion()
    {
        using PdfDocument pdf = PdfDocument.Open(Breaking());
        pdf.Trailer.Get<PdfDictionary>("Info")!["Title"] = PdfString.FromText("Bad" + "\0" + "title");
        PdfAReport report = PdfAConverter.Convert(pdf, PdfAConformance.PdfA2b);
        Assert.True(report.Succeeded, report.ToString());
        using PdfDocument saved = PdfDocument.Open(Save(pdf));
        Assert.Equal("Badtitle", saved.Metadata.Title);
        Assert.True(PdfAConverter.Check(saved, PdfAConformance.PdfA2b).Succeeded);
    }

    [Fact]
    public void UnknownOperatorsAndMissingResourcesAreRemoved()
    {
        using PdfDocument pdf = PdfDocument.Open(Breaking("BT /F1 12 Tf 72 700 Td (Kept) Tj ET 1 0 0 1 0 0 cm foo /Gone Do /Im Do"));
        PdfAReport report = PdfAConverter.Convert(pdf, PdfAConformance.PdfA2b);
        Assert.True(report.Succeeded, report.ToString());
        Assert.Contains(report.Fixes, i => i.Clause == "6.2.2" && i.Description.Contains("foo", StringComparison.Ordinal) && i.Description.Contains("Gone", StringComparison.Ordinal));
        string content = Encoding.Latin1.GetString(pdf.Pages[0].GetContent().ToBytes());
        Assert.DoesNotContain("foo", content, StringComparison.Ordinal);
        Assert.DoesNotContain("/Gone", content, StringComparison.Ordinal);
        Assert.Contains("/Im Do", content, StringComparison.Ordinal);
    }

    [Fact]
    public void TextInAMissingFontIsUnmet()
    {
        using PdfDocument pdf = PdfDocument.Open(Breaking("BT /Nowhere 12 Tf 72 700 Td (x) Tj ET"));
        PdfAReport report = PdfAConverter.Convert(pdf, PdfAConformance.PdfA2b);
        Assert.False(report.Succeeded);
        Assert.Contains(report.Unmet, i => !i.Fixable && i.Description.Contains("Nowhere", StringComparison.Ordinal));
    }

    [Fact]
    public void ObjectRulesAreFixed()
    {
        using PdfDocument pdf = PdfDocument.Open(Breaking());
        PdfDictionary font = pdf.Pages[0].Resources!.Get<PdfDictionary>("Font")!.Get<PdfDictionary>("F1")!;
        font.Remove("Type");
        PdfStream abbreviated = new(new PdfDictionary());
        abbreviated.SetDecodedBytes("q Q"u8);
        abbreviated.Dictionary["Filter"] = PdfName.Get("Fl");
        PdfReference data = pdf.Objects.Add(abbreviated);
        pdf.Catalog["Extra"] = data;
        PdfDictionary field = new() { ["FT"] = PdfName.Get("Tx"), ["T"] = PdfString.FromText("f"), ["AA"] = new PdfDictionary { ["K"] = new PdfDictionary { ["S"] = PdfName.Get("JavaScript"), ["JS"] = PdfString.FromText("x") } } };
        pdf.Catalog.Get<PdfDictionary>("AcroForm")!.Get<PdfArray>("Fields")!.Add(pdf.Objects.Add(field));
        PdfAReport report = PdfAConverter.Convert(pdf, PdfAConformance.PdfA2b);
        Assert.True(report.Succeeded, report.ToString());
        Assert.Equal("Font", font.GetName("Type")?.Value);
        Assert.Equal("FlateDecode", abbreviated.Dictionary.GetName("Filter")?.Value);
        Assert.False(field.ContainsKey("AA"));
    }

    [Fact]
    public void AFontNamedAaIsNotTakenForAdditionalActions()
    {
        using PdfDocument pdf = PdfDocument.Open(Breaking("BT /AA 12 Tf 72 700 Td (Kept) Tj ET"));
        PdfDictionary fonts = pdf.Pages[0].Resources!.Get<PdfDictionary>("Font")!;
        fonts["AA"] = fonts["F1"]!;
        fonts.Remove("F1");
        PdfAReport report = PdfAConverter.Convert(pdf, PdfAConformance.PdfA2b);
        Assert.True(report.Succeeded, report.ToString());
        Assert.NotNull(fonts.Get("AA"));
    }

    [Fact]
    public void AButtonsAppearanceBecomesStates()
    {
        using PdfDocument pdf = PdfDocument.Open(Breaking());
        PdfStream look = new(new PdfDictionary { ["Type"] = PdfName.Get("XObject"), ["Subtype"] = PdfName.Get("Form"), ["BBox"] = new PdfArray { new PdfInteger(0), new PdfInteger(0), new PdfInteger(10), new PdfInteger(10) } });
        look.SetDecodedBytes([]);
        PdfDictionary button = new()
        {
            ["Type"] = PdfName.Get("Annot"),
            ["Subtype"] = PdfName.Get("Widget"),
            ["FT"] = PdfName.Get("Btn"),
            ["T"] = PdfString.FromText("b"),
            ["F"] = new PdfInteger(4),
            ["Rect"] = new PdfArray { new PdfInteger(10), new PdfInteger(10), new PdfInteger(20), new PdfInteger(20) },
            ["AP"] = new PdfDictionary { ["N"] = pdf.Objects.Add(look) },
        };
        PdfReference reference = pdf.Objects.Add(button);
        pdf.Pages[0].Dictionary.Get<PdfArray>("Annots")!.Add(reference);
        pdf.Catalog.Get<PdfDictionary>("AcroForm")!.Get<PdfArray>("Fields")!.Add(reference);
        PdfAReport report = PdfAConverter.Convert(pdf, PdfAConformance.PdfA2b);
        Assert.True(report.Succeeded, report.ToString());
        PdfDictionary states = Assert.IsType<PdfDictionary>(button.Get<PdfDictionary>("AP")!.Get("N")!.Resolve());
        Assert.True(states.ContainsKey(button.GetName("AS")!.Value), string.Join(",", states.Entries.Select(e => e.Key.Value)) + " AS=" + button.GetName("AS")?.Value);
    }

    [Fact]
    public void ACmykFileConvertsWithAGivenProfile()
    {
        // A CMYK output profile as far as the converter reads one: the header's data colour space.
        byte[] profile = new byte[128];
        "CMYK"u8.CopyTo(profile.AsSpan(16));
        "prtr"u8.CopyTo(profile.AsSpan(12));
        using PdfDocument pdf = PdfDocument.Open(Breaking("0 0 0 1 k 0 0 10 10 re f BT /F1 12 Tf 72 700 Td (x) Tj ET"));

        // The red square's appearance would be drawn in RGB, which a CMYK intent does not cover.
        pdf.Pages[0].Dictionary.Remove("Annots");
        PdfAReport check = PdfAConverter.Check(pdf, PdfAConformance.PdfA2b);
        Assert.Contains(check.Unmet, i => !i.Fixable && i.Description.Contains("DeviceCMYK", StringComparison.Ordinal));
        PdfAReport report = PdfAConverter.Convert(pdf, PdfAConformance.PdfA2b, new PdfAConvertOptions { OutputIntentProfile = profile, OutputCondition = "Test CMYK" });
        Assert.True(report.Succeeded, report.ToString());
        PdfDictionary intent = (PdfDictionary)pdf.Catalog.Get<PdfArray>("OutputIntents")![0].Resolve()!;
        Assert.Equal("Test CMYK", intent.GetText("OutputConditionIdentifier"));
        Assert.Equal(4, intent.Get<PdfStream>("DestOutputProfile")!.Dictionary.GetInt("N"));
    }

    [Fact]
    public void APageTooSmallIsUnmet()
    {
        using PdfDocument pdf = PdfDocument.Open(Breaking());
        pdf.Pages[0].Dictionary["MediaBox"] = new PdfArray { new PdfInteger(0), new PdfInteger(0), new PdfInteger(2), new PdfInteger(2) };
        PdfAReport report = PdfAConverter.Convert(pdf, PdfAConformance.PdfA2b);
        Assert.False(report.Succeeded);
        Assert.Contains(report.Unmet, i => i.Clause == "6.1.13" && i.Page == 1);
    }

    [Fact]
    public void LevelUWritesUnicodeMaps()
    {
        using PdfDocument pdf = PdfDocument.Open(Breaking());
        PdfAReport report = PdfAConverter.Convert(pdf, PdfAConformance.PdfA3u);
        Assert.True(report.Succeeded, report.ToString());
        using PdfDocument saved = PdfDocument.Open(Save(pdf));
        Assert.Equal(PdfAConformance.PdfA3u, saved.Conformance);
        Assert.NotNull(saved.Pages[0].Resources!.Get<PdfDictionary>("Font")!.Get<PdfDictionary>("F1")!.Get<PdfStream>("ToUnicode"));
        Assert.True(PdfAConverter.Check(saved, PdfAConformance.PdfA3u).Succeeded);
    }

    [Fact]
    public void LevelUNeedsTextForEveryCode()
    {
        // A Type 3 font whose glyph has a private name: nothing says what text it is, which only level u minds.
        static PdfDocument WithType3()
        {
            PdfDocument pdf = PdfDocument.Open(Breaking("BT /T3 12 Tf 72 700 Td (a) Tj ET"));
            PdfStream glyph = new(new PdfDictionary());
            glyph.SetDecodedBytes("10 0 d0 0 0 10 10 re f"u8);
            pdf.Pages[0].Resources!.Get<PdfDictionary>("Font")!["T3"] = pdf.Objects.Add(new PdfDictionary
            {
                ["Type"] = PdfName.Get("Font"),
                ["Subtype"] = PdfName.Get("Type3"),
                ["FontBBox"] = new PdfArray { new PdfInteger(0), new PdfInteger(0), new PdfInteger(10), new PdfInteger(10) },
                ["FontMatrix"] = new PdfArray { new PdfReal(0.1), new PdfInteger(0), new PdfInteger(0), new PdfReal(0.1), new PdfInteger(0), new PdfInteger(0) },
                ["CharProcs"] = new PdfDictionary { ["box7"] = pdf.Objects.Add(glyph) },
                ["Encoding"] = new PdfDictionary { ["Differences"] = new PdfArray { new PdfInteger(97), PdfName.Get("box7") } },
                ["FirstChar"] = new PdfInteger(97),
                ["LastChar"] = new PdfInteger(97),
                ["Widths"] = new PdfArray { new PdfInteger(10) },
            });
            return pdf;
        }

        using (PdfDocument pdf = WithType3())
        {
            PdfAReport report = PdfAConverter.Convert(pdf, PdfAConformance.PdfA2b);
            Assert.True(report.Succeeded, report.ToString());
        }

        using (PdfDocument pdf = WithType3())
        {
            PdfAReport report = PdfAConverter.Convert(pdf, PdfAConformance.PdfA2u);
            Assert.False(report.Succeeded);
            Assert.Contains(report.Unmet, i => i.Clause == "6.2.11.7" && !i.Fixable);
        }
    }

    [Fact]
    public void TheLzwTestStreamDecodes()
    {
        // Guards the test file builder: the LZW stream decodes to the content it was made from.
        using PdfDocument pdf = PdfDocument.Open(Breaking("BT ET"));
        Assert.Equal("BT ET", Encoding.Latin1.GetString(((PdfStream)pdf.Pages[0].Dictionary["Contents"]!.Resolve()!).GetDecodedBytes()));
    }
}
