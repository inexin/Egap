using Egap.Pdf.Optimizing;

namespace Egap.Pdf.Tests.Optimizing;

/// <summary>Font subsetting in the optimizer: Phase 7 task 7.</summary>
public sealed class FontSubsetterTests
{
    private static readonly string Fonts = Path.Combine(AppContext.BaseDirectory, "files", "fonts");

    [Fact]
    public void CutsAFullyEmbeddedTrueTypeFontToTheGlyphsShown()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfDictionary font = SimpleFont(pdf, "TrueType", "FontFile2", null, File.ReadAllBytes(Path.Combine(Fonts, "NotoSans-Latin.ttf")));
        PdfPage page = TextPage(pdf, font, "(Hello, subset!) Tj");
        string text = page.ExtractText();
        PdfStream program = FontFile(font);
        long before = program.EncodedLength;

        (int fonts, long bytes) = FontSubsetter.Run(pdf);

        Assert.Equal(1, fonts);
        Assert.Equal(before - program.EncodedLength, bytes);
        Assert.True(program.EncodedLength < before / 2);
        Assert.Matches("^[A-Z]{6}\\+NotoSans$", font.GetName("BaseFont")!.Value);
        Assert.True(PdfFont.FromDictionary(font).IsSubset);
        Assert.Equal(program.GetDecodedBytes().Length, program.Dictionary.GetInt("Length1"));
        Assert.Equal(text, page.ExtractText());

        // A second run finds the font already cut.
        Assert.Equal(0, FontSubsetter.Run(pdf).Fonts);
    }

    [Fact]
    public void KeepsANameKeyedCffFontWholeWhenTheCutOneLosesItsGlyphNames()
    {
        using PdfDocument pdf = PdfDocument.Create();
        byte[] otf = File.ReadAllBytes(Path.Combine(Fonts, "SourceSans3-Latin.otf"));
        Assert.True(Egami.Fonts.Typeface.FromMemory(otf).TryGetTable(new Egami.Fonts.FontTag("CFF "), out ReadOnlyMemory<byte> cff));
        PdfDictionary font = SimpleFont(pdf, "Type1", "FontFile3", "Type1C", cff.ToArray());
        PdfPage page = TextPage(pdf, font, "(Quick brown fox) Tj");
        byte[] before = FontFile(font).GetEncodedBytes();

        // Egami writes CFF subsets CID-keyed, without the names a simple font's encoding finds glyphs by: the check refuses it.
        Assert.Equal(0, FontSubsetter.Run(pdf).Fonts);
        Assert.Equal(before, FontFile(font).GetEncodedBytes());
        Assert.Equal("Quick brown fox", page.ExtractText().Trim());
    }

    [Fact]
    public void LeavesFormFontsAndSubsetsWhole()
    {
        using PdfDocument pdf = PdfDocument.Create();
        byte[] ttf = File.ReadAllBytes(Path.Combine(Fonts, "NotoSans-Latin.ttf"));
        PdfDictionary form = SimpleFont(pdf, "TrueType", "FontFile2", null, ttf);
        PdfReference formFont = pdf.Objects.Add(form);
        pdf.Catalog["AcroForm"] = new PdfDictionary { ["Fields"] = new PdfArray(), ["DR"] = new PdfDictionary { ["Font"] = new PdfDictionary { ["Helv"] = formFont } } };
        TextPage(pdf, formFont, "(typed) Tj");
        PdfDictionary subset = SimpleFont(pdf, "TrueType", "FontFile2", null, ttf);
        subset["BaseFont"] = PdfName.Get("ABCDEF+NotoSans");
        TextPage(pdf, subset, "(already cut) Tj");

        Assert.Equal(0, FontSubsetter.Run(pdf).Fonts);
        Assert.Equal(ttf.Length, FontFile(form).GetDecodedBytes().Length);
    }

    [Fact]
    public void LeavesFontsOfTilingPatternsWhole()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfDictionary font = SimpleFont(pdf, "TrueType", "FontFile2", null, File.ReadAllBytes(Path.Combine(Fonts, "NotoSans-Latin.ttf")));
        PdfStream pattern = new(new PdfDictionary
        {
            ["PatternType"] = new PdfInteger(1),
            ["PaintType"] = new PdfInteger(1),
            ["TilingType"] = new PdfInteger(1),
            ["BBox"] = new PdfArray { new PdfInteger(0), new PdfInteger(0), new PdfInteger(50), new PdfInteger(50) },
            ["XStep"] = new PdfInteger(50),
            ["YStep"] = new PdfInteger(50),
            ["Resources"] = new PdfDictionary { ["Font"] = new PdfDictionary { ["F1"] = pdf.Objects.Add(font) } },
        });
        pattern.SetDecodedBytes("BT /F1 12 Tf (xyz) Tj ET"u8);
        PdfPage page = pdf.Pages.AddBlank();
        page.Resources = new PdfDictionary { ["Pattern"] = new PdfDictionary { ["P1"] = pdf.Objects.Add(pattern) }, ["Font"] = new PdfDictionary { ["F1"] = font.Clone() } };
        page.SetContent(PdfContent.Parse("/Pattern cs /P1 scn 0 0 100 100 re f BT /F1 12 Tf (abc) Tj ET"u8));

        Assert.Equal(0, FontSubsetter.Run(pdf).Fonts);
    }

    private static PdfStream FontFile(PdfDictionary font) => (PdfStream)font.Get<PdfDictionary>("FontDescriptor")!.Get(font.Get<PdfDictionary>("FontDescriptor")!.ContainsKey("FontFile2") ? "FontFile2" : "FontFile3")!.Resolve();

    private static PdfDictionary SimpleFont(PdfDocument pdf, string subtype, string fileKey, string? fileSubtype, byte[] program)
    {
        PdfStream file = new(new PdfDictionary());
        if (fileSubtype is not null)
        {
            file.Dictionary["Subtype"] = PdfName.Get(fileSubtype);
        }
        else
        {
            file.Dictionary["Length1"] = new PdfInteger(program.Length);
        }

        file.SetDecodedBytes(program);
        PdfArray widths = [.. Enumerable.Range(32, 95).Select(_ => (PdfObject)new PdfInteger(500))];
        return new PdfDictionary
        {
            ["Type"] = PdfName.Get("Font"),
            ["Subtype"] = PdfName.Get(subtype),
            ["BaseFont"] = PdfName.Get(subtype == "TrueType" ? "NotoSans" : "SourceSans3"),
            ["FirstChar"] = new PdfInteger(32),
            ["LastChar"] = new PdfInteger(126),
            ["Widths"] = widths,
            ["Encoding"] = PdfName.Get("WinAnsiEncoding"),
            ["FontDescriptor"] = new PdfDictionary
            {
                ["Type"] = PdfName.Get("FontDescriptor"),
                ["FontName"] = PdfName.Get(subtype == "TrueType" ? "NotoSans" : "SourceSans3"),
                ["Flags"] = new PdfInteger(32),
                [fileKey] = pdf.Objects.Add(file),
            },
        };
    }

    private static PdfPage TextPage(PdfDocument pdf, PdfDictionary font, string show) => TextPage(pdf, pdf.Objects.Add(font), show);

    private static PdfPage TextPage(PdfDocument pdf, PdfReference font, string show)
    {
        PdfPage page = pdf.Pages.AddBlank();
        page.Resources = new PdfDictionary { ["Font"] = new PdfDictionary { ["F1"] = font } };
        page.SetContent(PdfContent.Parse(System.Text.Encoding.ASCII.GetBytes($"BT /F1 24 Tf 72 700 Td {show} ET")));
        return page;
    }
}
