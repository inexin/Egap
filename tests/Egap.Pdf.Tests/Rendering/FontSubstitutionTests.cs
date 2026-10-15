using System.Text;

using Egami;
using Egami.Pixels;

using Egap.Pdf.Fonts;

namespace Egap.Pdf.Tests.Rendering;

/// <summary>Fonts that are not embedded (Phase 4 task 5): bundled standard 14, aliases, flags, and system fonts by name.</summary>
public sealed class FontSubstitutionTests
{
    [Theory]
    [InlineData("Helvetica", "NotoSans-Latin.ttf", false)]
    [InlineData("Helvetica-BoldOblique", "NotoSans-Latin.ttf", true)]
    [InlineData("ArialMT", "NotoSans-Latin.ttf", false)]
    [InlineData("Times-Roman", "FoxitSerif.pfb", false)]
    [InlineData("Times-BoldItalic", "FoxitSerifBoldItalic.pfb", false)]
    [InlineData("TimesNewRomanPS-BoldMT", "FoxitSerifBold.pfb", false)]
    [InlineData("Courier", "FoxitFixed.pfb", false)]
    [InlineData("CourierNew,Italic", "FoxitFixedItalic.pfb", false)]
    [InlineData("Symbol", "FoxitSymbol.pfb", false)]
    [InlineData("ZapfDingbats", "FoxitDingbats.pfb", false)]
    [InlineData("ABCDEF+Garamond", "FoxitSerif.pfb", false)]
    [InlineData("NoSuchFont", "NotoSans-Latin.ttf", false)]
    public void StandardNamesAliasesAndWordsPickABundledFont(string name, string expected, bool synthesized)
    {
        SubstituteFont substitute = SubstituteFonts.For(Font(name), systemFonts: false)!;

        Assert.Equal(expected, substitute.Source);
        Assert.Equal(synthesized, substitute.Synthesis != Egami.Fonts.FontSynthesis.None);
    }

    [Fact]
    public void DescriptorFlagsDecideForUnknownNames()
    {
        PdfDictionary fixedPitch = Font("Mystery").Dictionary.Clone();
        fixedPitch["FontDescriptor"] = new PdfDictionary { ["Type"] = PdfName.Get("FontDescriptor"), ["Flags"] = new PdfInteger(1 | 32), ["ItalicAngle"] = new PdfInteger(-12) };
        PdfDictionary serif = Font("Mystery").Dictionary.Clone();
        serif["FontDescriptor"] = new PdfDictionary { ["Type"] = PdfName.Get("FontDescriptor"), ["Flags"] = new PdfInteger(2 | 32 | (1 << 18)) };

        Assert.Equal("FoxitFixedItalic.pfb", SubstituteFonts.For(PdfFont.FromDictionary(fixedPitch), false)!.Source);
        Assert.Equal("FoxitSerifBold.pfb", SubstituteFonts.For(PdfFont.FromDictionary(serif), false)!.Source);
    }

    [Fact]
    public void OtherNamesFindSystemFontsWhenAllowed()
    {
        Assert.SkipUnless(Egami.Fonts.FontCollection.System.Match("Verdana", Egami.Fonts.FontStyle.Normal) is { } m && m.Typeface.FamilyName == "Verdana", "Verdana is not installed.");

        Assert.Equal("system Verdana", SubstituteFonts.For(Font("Verdana,Bold"), systemFonts: true)!.Source);
        Assert.Equal("NotoSans-Latin.ttf", SubstituteFonts.For(Font("Verdana,Bold"), systemFonts: false)!.Source);
        Assert.Equal("NotoSans-Latin.ttf", SubstituteFonts.For(Font("Helvetica"), systemFonts: true)!.Source);
    }

    [Fact]
    public void BundledFontsHaveTheGlyphsTheirEncodingsName()
    {
        foreach ((string name, int code) in new[] { ("Helvetica", 'A'), ("Times-Roman", 'g'), ("Courier-Bold", '&'), ("Symbol", 0x61), ("ZapfDingbats", 0x34) })
        {
            PdfFont font = Font(name);
            SubstituteFont substitute = SubstituteFonts.For(font, false)!;
            Assert.NotEqual(0, font.GetSubstituteGlyph(substitute.Program, code));
        }
    }

    [Fact]
    public void TextInAFontThatIsNotEmbeddedShows()
    {
        PdfDictionary resources = new() { ["Font"] = new PdfDictionary { ["F1"] = Font("Times-Roman").Dictionary } };
        using Raster<Rgba8> raster = RenderCoreTests.Render("BT /F1 30 Tf 2 4 Td (IIII) Tj ET", width: 60, height: 30, resources: resources);

        Assert.True(RenderCoreTests.Pixels(raster).Count(p => p.R < 128) > 60);
    }

    private static PdfFont Font(string name)
    {
        return PdfFont.FromDictionary(new PdfDictionary
        {
            ["Type"] = PdfName.Get("Font"),
            ["Subtype"] = PdfName.Get("Type1"),
            ["BaseFont"] = PdfName.Get(name),
        });
    }
}
