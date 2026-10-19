using System.Text;

namespace Egap.Pdf.Tests.Pages;

/// <summary>Page range strings and lengths in units.</summary>
public sealed class PageRangeTests
{
    [Theory]
    [InlineData("1,3,5-7,z", 10, new[] { 0, 2, 4, 5, 6, 9 })]
    [InlineData("", 3, new[] { 0, 1, 2 })]
    [InlineData("all", 3, new[] { 0, 1, 2 })]
    [InlineData("z-1", 4, new[] { 3, 2, 1, 0 })]
    [InlineData("r2-z", 5, new[] { 3, 4 })]
    [InlineData("1-z:odd", 6, new[] { 0, 2, 4 })]
    [InlineData("1-z:even", 6, new[] { 1, 3, 5 })]
    [InlineData("1-z,x3-4", 6, new[] { 0, 1, 4, 5 })]
    [InlineData("x2", 3, new[] { 0, 2 })]
    [InlineData("1,1", 2, new[] { 0, 0 })]
    [InlineData(" 2 - 3 , R1 ", 5, new[] { 1, 2, 4 })]
    public void ResolvesToPageIndices(string text, int count, int[] expected)
    {
        Assert.Equal(expected, PdfPageRange.Parse(text).Resolve(count));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1-")]
    [InlineData("a")]
    [InlineData("1:third")]
    [InlineData("1,,2")]
    [InlineData("-3")]
    public void RefusesWhatIsNotARange(string text)
    {
        Assert.False(PdfPageRange.TryParse(text, out _));
        Assert.Throws<FormatException>(() => PdfPageRange.Parse(text));
    }

    [Fact]
    public void RefusesPagesPastTheEnd()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfPageRange.Parse("2-5").Resolve(4));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfPageRange.Parse("r5").Resolve(4));
    }

    [Fact]
    public void ExtractsAndStampsTheChosenPages()
    {
        using PdfDocument pdf = Pages(5);

        using PdfDocument extracted = pdf.ExtractPages(PdfPageRange.Parse("z,2"));
        Assert.Equal(["P5", "P2"], extracted.Pages.Select(p => p.ExtractText().Trim()));

        PdfStampResult stamped = pdf.Stamp(PdfStamp.Text("S") with { PageRange = PdfPageRange.Parse("2-z:even") });
        Assert.Equal(2, stamped.Pages);
        Assert.Contains("S", pdf.Pages[2].ExtractText(), StringComparison.Ordinal);
        Assert.DoesNotContain("S", pdf.Pages[1].ExtractText(), StringComparison.Ordinal);
    }

    [Fact]
    public void LengthsConvertBetweenUnits()
    {
        Assert.Equal(72, Length.In(1).Points, 9);
        Assert.Equal(72, Length.Mm(25.4).Points, 9);
        Assert.Equal(72, Length.Cm(2.54).Points, 9);
        Assert.Equal(10, Length.Mm(10).Millimetres, 9);
        Assert.Equal(Length.Mm(20), Length.Parse("2 cm"));
        Assert.Equal(Length.In(8.5), Length.Parse("8.5IN"));
        Assert.Equal(Length.Pt(12), Length.Parse("12"));
        Assert.False(Length.TryParse("12px", out _));
        Assert.False(Length.TryParse("mm", out _));
        Assert.Equal("72pt", Length.In(1).ToString());
    }

    [Fact]
    public void LengthsGoWherePointsGo()
    {
        NUpOptions options = new() { Margin = Length.Mm(10) };
        Assert.Equal(Length.Mm(10).Points, options.Margin, 9);

        Length fromPoints = 36;
        Assert.Equal(Length.In(0.5), fromPoints);
        Assert.Equal(Length.Pt(46), fromPoints + 10);
        Assert.True(Length.Mm(10) > Length.Pt(28));
        Assert.Throws<ArgumentOutOfRangeException>(() => Length.Mm(double.NaN));
    }

    private static PdfDocument Pages(int count)
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfReference font = pdf.Objects.Add(new PdfDictionary { ["Type"] = PdfName.Get("Font"), ["Subtype"] = PdfName.Get("Type1"), ["BaseFont"] = PdfName.Get("Helvetica") });
        for (int i = 1; i <= count; i++)
        {
            PdfPage page = pdf.Pages.AddBlank();
            page.Resources = new PdfDictionary { ["Font"] = new PdfDictionary { ["F1"] = font } };
            page.SetContent(PdfContent.Parse(Encoding.ASCII.GetBytes($"BT /F1 24 Tf 280 400 Td (P{i}) Tj ET")));
        }

        using MemoryStream saved = new();
        pdf.Save(saved);
        return PdfDocument.Open(saved.ToArray());
    }
}
