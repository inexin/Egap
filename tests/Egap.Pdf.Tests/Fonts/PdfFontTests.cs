using System.Text;

namespace Egap.Pdf.Tests.Fonts;

/// <summary>Simple fonts, encodings, widths and ToUnicode: Phase 3 task 17.</summary>
public sealed class PdfFontTests
{
    private static readonly PdfDocument Objects = PdfDocument.Create();

    [Fact]
    public void ReadsAStandardFontFromItsMetrics()
    {
        PdfFont font = PdfFont.FromDictionary(Font("Type1", "Helvetica"));

        Assert.Equal((PdfFontType.Type1, true, false), (font.Type, font.IsStandard14, font.IsEmbedded));
        Assert.Equal(PdfFontFlags.Nonsymbolic, font.Flags);
        Assert.Equal(667, font.GetWidth('A'));
        Assert.Equal(("A", "A"), (font.GetGlyphName('A'), font.ToUnicode('A')));

        // StandardEncoding, the font's own: 0x27 is quoteright, not WinAnsi's quotesingle.
        Assert.Equal(("quoteright", "\u2019"), (font.GetGlyphName(0x27), font.ToUnicode(0x27)));
        Assert.Equal(718, font.Ascent);
    }

    [Fact]
    public void AppliesDifferencesOverABaseEncoding()
    {
        PdfDictionary dictionary = Font("Type1", "ABCDEF+Minion");
        dictionary["Encoding"] = new PdfDictionary
        {
            ["BaseEncoding"] = PdfName.Get("WinAnsiEncoding"),
            ["Differences"] = new PdfArray { new PdfInteger(65), PdfName.Get("B"), PdfName.Get("uni00E9"), PdfName.Get("f_f"), PdfName.Get("g42"), PdfName.Get("u1F600") },
        };
        PdfFont font = PdfFont.FromDictionary(dictionary);

        Assert.Equal((true, "Minion"), (font.IsSubset, font.Name));
        Assert.Equal(["B", "\u00E9", "ff", "*", "\U0001F600"], Enumerable.Range(65, 5).Select(font.ToUnicode));
        Assert.Equal("\u20AC", font.ToUnicode(0x80));
        Assert.Equal("Ba", font.ToUnicode("Aa"u8));
    }

    [Fact]
    public void ReadsMacRoman()
    {
        PdfDictionary dictionary = Font("TrueType", "Arial");
        dictionary["Encoding"] = PdfName.Get("MacRomanEncoding");
        PdfFont font = PdfFont.FromDictionary(dictionary);

        Assert.Equal(("eacute", "\u00E9"), (font.GetGlyphName(0x8E), font.ToUnicode(0x8E)));
        Assert.Equal("\uFB01", font.ToUnicode(0xDE));
    }

    [Fact]
    public void PrefersToUnicodeOverGlyphNames()
    {
        PdfDictionary dictionary = Font("Type1", "Helvetica");
        dictionary["ToUnicode"] = Stream("""
            /CIDInit /ProcSet findresource begin 12 dict begin begincmap
            1 begincodespacerange <00> <FF> endcodespacerange
            2 beginbfchar <41> <0058> <42> <00660069> endbfchar
            2 beginbfrange <61> <63> <0430> <70> <71> [<0031> <0032>] endbfrange
            endcmap end end
            """);
        PdfFont font = PdfFont.FromDictionary(dictionary);

        Assert.Equal(["X", "fi", "\u0430", "\u0432", "1", "2", "C"], "ABacpqC".Select(c => font.ToUnicode(c)));
    }

    [Fact]
    public void ScalesType3WidthsByTheFontMatrix()
    {
        PdfDictionary dictionary = Font("Type3", null);
        dictionary["FontMatrix"] = new PdfArray { new PdfReal(0.01), new PdfInteger(0), new PdfInteger(0), new PdfReal(0.01), new PdfInteger(0), new PdfInteger(0) };
        dictionary["FirstChar"] = new PdfInteger(32);
        dictionary["Widths"] = new PdfArray { new PdfInteger(50) };
        dictionary["Encoding"] = new PdfDictionary { ["Differences"] = new PdfArray { new PdfInteger(32), PdfName.Get("space") } };
        PdfFont font = PdfFont.FromDictionary(dictionary);

        Assert.Equal(500, font.GetWidth(32), 6);
        Assert.Equal(" ", font.ToUnicode(32));
        Assert.Null(font.ToUnicode(33));
        Assert.True(font.IsEmbedded);
    }

    [Fact]
    public void ReadsIdentityCompositeFonts()
    {
        PdfDictionary dictionary = Font("Type0", "AAAAAA+Noto");
        dictionary["Encoding"] = PdfName.Get("Identity-V");
        dictionary["DescendantFonts"] = new PdfArray
        {
            new PdfDictionary
            {
                ["Type"] = PdfName.Get("Font"),
                ["Subtype"] = PdfName.Get("CIDFontType2"),
                ["DW"] = new PdfInteger(1000),
                ["W"] = new PdfArray { new PdfInteger(3), new PdfArray { new PdfInteger(250), new PdfInteger(300) }, new PdfInteger(10), new PdfInteger(12), new PdfInteger(500) },
            },
        };
        PdfFont font = PdfFont.FromDictionary(dictionary);

        Assert.Equal((true, true), (font.IsComposite, font.IsVertical));
        Assert.Equal([3, 4, 11, 99], font.GetCodes([0, 3, 0, 4, 0, 11, 0, 99]));
        Assert.Equal([250.0, 300, 500, 1000], font.GetCodes([0, 3, 0, 4, 0, 11, 0, 99]).Select(font.GetWidth));
        Assert.Equal(4, font.GetCid(4));
    }

    [Fact]
    public void ReadsVerticalMetrics()
    {
        PdfDictionary dictionary = Font("Type0", "Vertical");
        dictionary["Encoding"] = PdfName.Get("Identity-V");
        dictionary["DescendantFonts"] = new PdfArray
        {
            new PdfDictionary
            {
                ["Subtype"] = PdfName.Get("CIDFontType0"),
                ["DW"] = new PdfInteger(1000),
                ["DW2"] = new PdfArray { new PdfInteger(900), new PdfInteger(-1100) },
                ["W2"] = new PdfArray { new PdfInteger(5), new PdfArray { new PdfInteger(-500), new PdfInteger(250), new PdfInteger(800) }, new PdfInteger(7), new PdfInteger(8), new PdfInteger(-700), new PdfInteger(300), new PdfInteger(700) },
            },
        };
        PdfFont font = PdfFont.FromDictionary(dictionary);

        Assert.Equal((-500.0, 250.0, 800.0), font.GetVerticalMetrics(5));
        Assert.Equal((-700.0, 300.0, 700.0), font.GetVerticalMetrics(8));
        Assert.Equal((-1100.0, 500.0, 900.0), font.GetVerticalMetrics(9));
        Assert.Null(PdfFont.FromDictionary(Font("Type1", "Helvetica")).GetVerticalMetrics(65));
    }

    [Fact]
    public void SplitsCodesAsAnEmbeddedCMapSays()
    {
        PdfDictionary dictionary = Font("Type0", "Mixed");
        dictionary["Encoding"] = Stream("""
            /CMapName /Mixed-H def
            2 begincodespacerange <00> <80> <8140> <9FFC> endcodespacerange
            1 begincidrange <8140> <817E> 633 endcidrange
            1 begincidchar <41> 34 endcidchar
            """);
        PdfFont font = PdfFont.FromDictionary(dictionary);

        Assert.Equal([0x41, 0x8141, 0x20], font.GetCodes([0x41, 0x81, 0x41, 0x20]));
        Assert.Equal((34, 634), (font.GetCid(0x41), font.GetCid(0x8141)));
        Assert.False(font.IsVertical);
    }

    [Fact]
    public void ListsAPagesFonts()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfPage page = pdf.Pages.AddBlank();
        page.Resources = new PdfDictionary { ["Font"] = new PdfDictionary { ["F1"] = Font("Type1", "Times-Bold"), ["F2"] = Font("TrueType", "Arial") } };

        Assert.Equal(["F1", "F2"], page.Fonts.Keys);
        Assert.Equal("Times-Bold", page.Fonts["F1"].BaseFont);
        Assert.Same(page.Fonts["F2"], page.Fonts["F2"]);
        Assert.False(page.Fonts.ContainsKey("F3"));
        Assert.Throws<KeyNotFoundException>(() => page.Fonts["F3"]);
    }

    private static PdfDictionary Font(string subtype, string? baseFont)
    {
        PdfDictionary font = new() { ["Type"] = PdfName.Get("Font"), ["Subtype"] = PdfName.Get(subtype) };
        if (baseFont is not null)
        {
            font["BaseFont"] = PdfName.Get(baseFont);
        }

        return font;
    }

    /// <summary>A stream as an indirect object, as a dictionary must hold it.</summary>
    private static PdfReference Stream(string text)
    {
        PdfStream stream = new();
        stream.SetDecodedBytes(Encoding.ASCII.GetBytes(text));
        return Objects.Objects.Add(stream);
    }
}
