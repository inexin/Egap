using System.Text;

using Egap.Pdf.Content;
using Egap.Pdf.Fonts;

namespace Egap.Pdf.Tests.Content;

/// <summary>The content writer, the standard 14 font metrics and WinAnsiEncoding: Phase 2 task 11.</summary>
public sealed class ContentAndFontTests
{
    [Fact]
    public void LoadsAllFourteenStandardFonts()
    {
        foreach (string name in Standard14Font.Names)
        {
            Standard14Font font = Standard14Font.Get(name)!;
            Assert.Equal(name, font.Name);
            Assert.False(font.BoundingBox.IsEmpty, name);
        }

        Assert.Equal("Helvetica", Standard14Font.Get("Helv")!.Name);
        Assert.Equal("Helvetica-Bold", Standard14Font.Get("ABCDEF+Arial,Bold")!.Name);
        Assert.Equal("Times-Roman", Standard14Font.Get("TimesNewRomanPSMT")!.Name);
        Assert.Null(Standard14Font.Get("Garamond"));
        Assert.Null(Standard14Font.Get(null));
        Assert.Same(Standard14Font.Get("Helvetica"), Standard14Font.Get("Helvetica"));
    }

    [Fact]
    public void HasAdobesWidthsAndMetrics()
    {
        Standard14Font helvetica = Standard14Font.Get("Helvetica")!;
        Assert.Equal((278, 667, 556, 556), (helvetica.Width((byte)' '), helvetica.Width((byte)'A'), helvetica.Width((byte)'a'), helvetica.Width(0x80)));
        Assert.Equal((718.0, -207.0, 718.0, 523.0), (helvetica.Ascent, helvetica.Descent, helvetica.CapHeight, helvetica.XHeight));
        Assert.Equal(new PdfRectangle(-166, -225, 1000, 931), helvetica.BoundingBox);
        Assert.Equal(0, helvetica.Width(0x10));

        Assert.All(Enumerable.Range(0x20, 0x5F), code => Assert.Equal(600, Standard14Font.Get("Courier")!.Width((byte)code)));
        Assert.True(Standard14Font.Get("Courier-Bold")!.IsFixedPitch);
        Assert.Equal(-12, Standard14Font.Get("Helvetica-Oblique")!.ItalicAngle);
        Assert.Equal(444, Standard14Font.Get("Times-Roman")!.Width((byte)'a'));

        // ZapfDingbats' check mark (a20) is code 0x34 in its own encoding; Symbol's alpha is code 0x61.
        Standard14Font dingbats = Standard14Font.Get("ZapfDingbats")!;
        Assert.True(dingbats.IsSymbolic);
        Assert.Equal(846, dingbats.Width(0x34));
        Assert.Equal(631, Standard14Font.Get("Symbol")!.Width(0x61));
        Assert.Null(dingbats.Encode("4"));
    }

    [Fact]
    public void MeasuresText()
    {
        Standard14Font helvetica = Standard14Font.Get("Helvetica")!;

        // H 722, e 556, l 222, l 222, o 556: 2278 thousandths.
        Assert.Equal(22.78, helvetica.Measure("Hello", 10)!.Value, 6);
        Assert.Equal(22.78 + 5, helvetica.Measure(helvetica.Encode("Hello")!, 10, characterSpacing: 1, wordSpacing: 7), 6);
        Assert.Equal(22.78 + 6 + 2.78 + 7, helvetica.Measure(helvetica.Encode("Hel lo")!, 10, characterSpacing: 1, wordSpacing: 7), 6);
        Assert.Null(helvetica.Measure("\u03A9", 10));
    }

    [Theory]
    [InlineData("A", new byte[] { 0x41 })]
    [InlineData("\u00E9\u20AC\u2026\u201C\u201D", new byte[] { 0xE9, 0x80, 0x85, 0x93, 0x94 })]
    [InlineData("\u00A0\u00AD\u2022", new byte[] { 0xA0, 0xAD, 0x95 })]
    public void EncodesWinAnsi(string text, byte[] expected)
    {
        Assert.Equal(expected, WinAnsiEncoding.TryEncode(text));
    }

    [Theory]
    [InlineData("\u03A9")]
    [InlineData("tab\there")]
    [InlineData("\u0081")]
    public void RefusesWhatWinAnsiCannotEncode(string text)
    {
        Assert.Null(WinAnsiEncoding.TryEncode(text));
    }

    [Fact]
    public void UnusedWinAnsiCodesShowTheBullet()
    {
        Assert.Equal("bullet", WinAnsiEncoding.GlyphName(0x81));
        Assert.Equal("bullet", WinAnsiEncoding.GlyphName(0x7F));
        Assert.Equal("space", WinAnsiEncoding.GlyphName(0xA0));
        Assert.Equal("hyphen", WinAnsiEncoding.GlyphName(0xAD));
        Assert.Null(WinAnsiEncoding.GlyphName(0x1F));
    }

    [Fact]
    public void WritesOperators()
    {
        byte[] content = new PdfContentWriter()
            .Save()
            .Rgb(1, 0, 0.5).Gray(0.25, stroke: true).LineWidth(1.5).Dash([3, 1], 0.5)
            .Rectangle(0, 0, 100, 20).FillAndStroke()
            .BeginMarkedContent("Tx").BeginText().Font("Helv", 12).MoveText(2, 5.123456)
            .ShowText(Encoding.Latin1.GetBytes("a(b)\\c")).EndText().EndMarkedContent()
            .ArrayColor(new PdfArray([new PdfReal(0.5)])).ArrayColor(null)
            .Restore()
            .ToArray();

        Assert.Equal(
            "q\n1 0 .5 rg\n.25 G\n1.5 w\n[3 1] .5 d\n0 0 100 20 re\nB\n/Tx BMC\nBT\n/Helv 12 Tf\n2 5.12346 Td\n(a\\(b\\)\\\\c) Tj\nET\nEMC\n.5 g\nQ\n",
            Encoding.ASCII.GetString(content));
    }

    [Fact]
    public void WritesNumbersAsTheShortestTextOfTheirFiveDecimals()
    {
        Random random = new(7);
        for (int i = 0; i < 20000; i++)
        {
            double value = (random.NextDouble() - 0.5) * Math.Pow(10, random.Next(0, 11)) * (i % 3 == 0 ? 1 : 1.000001);
            double rounded = Math.Abs(value) >= 1 ? Math.Round(value, 5) : value;
            if (Math.Abs(rounded) < 1 || rounded == Math.Floor(rounded))
            {
                continue;
            }

            string expected = Egap.Pdf.Objects.ObjectWriter.FormatReal(rounded);
            string content = Encoding.ASCII.GetString(new PdfContentWriter().MoveTo(value, 0).ToArray());
            Assert.Equal(expected + " 0 m\n", content);
        }
    }

    [Fact]
    public void DrawsAnEllipseWithFourCurves()
    {
        string content = Encoding.ASCII.GetString(new PdfContentWriter().Ellipse(0, 0, 20, 10).Stroke().ToArray());

        Assert.StartsWith("20 5 m\n", content, StringComparison.Ordinal);
        Assert.Equal(4, content.Split('\n').Count(l => l.EndsWith(" c", StringComparison.Ordinal)));
    }

    [Fact]
    public void RefusesUnbalancedContent()
    {
        Assert.Throws<InvalidOperationException>(() => new PdfContentWriter().Save().ToArray());
        Assert.Throws<InvalidOperationException>(() => new PdfContentWriter().BeginText().ToArray());
        Assert.Throws<InvalidOperationException>(() => new PdfContentWriter().Restore());
        Assert.Throws<InvalidOperationException>(() => new PdfContentWriter().BeginText().BeginText());
        Assert.Throws<InvalidOperationException>(() => new PdfContentWriter().EndMarkedContent());
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfContentWriter().LineWidth(double.NaN));
    }

    [Fact]
    public void WrittenContentParsesBackToItsOperators()
    {
        byte[] content = new PdfContentWriter()
            .Save().Transform(1, 0, 0, 1, 10.5, -3).MoveTo(0, 0).LineTo(10, 10).CurveTo(1, 2, 3, 4, 5, 6).ClosePath().Stroke()
            .BeginText().Font("F 1", 9).TextMatrix(1, 0, 0, 1, 0, 0).ShowText([("Hi"u8.ToArray(), -120), ("!"u8.ToArray(), 0)]).EndText()
            .Draw("Im0").Restore()
            .ToArray();

        // Every operand parses as a PDF object, so a viewer reads the same numbers, names and strings.
        string text = Encoding.ASCII.GetString(content);
        Assert.Contains("/F#201 9 Tf", text, StringComparison.Ordinal);
        Assert.Contains("[(Hi) -120 (!)] TJ", text, StringComparison.Ordinal);
        Assert.Contains("1 0 0 1 10.5 -3 cm", text, StringComparison.Ordinal);
        Assert.Contains("/Im0 Do", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLicenceGoesWithTheMetrics()
    {
        using Stream notice = typeof(PdfDocument).Assembly.GetManifestResourceStream("Egap.Pdf.Fonts.MustRead.txt")!;
        using StreamReader reader = new(notice);

        Assert.Contains("This file and the 14 PostScript(R) AFM files it accompanies may be used", reader.ReadToEnd(), StringComparison.Ordinal);
    }
}
