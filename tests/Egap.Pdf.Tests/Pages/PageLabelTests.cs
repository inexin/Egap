using Egap.Pdf.Tests.Parsing;

namespace Egap.Pdf.Tests.Pages;

/// <summary>Page labels (12.4.2): Phase 2 task 5.</summary>
public sealed class PageLabelTests
{
    private static readonly PdfReadOptions Strict = new() { Strict = true };

    /// <summary>A file with <paramref name="pages"/> pages and the catalog entries given.</summary>
    private static byte[] WithPages(int pages, string catalog = "", params (int Number, string Body)[] extra)
    {
        FileBuilder file = new FileBuilder()
            .Object(1, $"<< /Type /Catalog /Pages 2 0 R {catalog} >>")
            .Object(2, $"<< /Type /Pages /Kids [{string.Join(' ', Enumerable.Range(10, pages).Select(n => $"{n} 0 R"))}] /Count {pages} >>");
        for (int i = 0; i < pages; i++)
        {
            file.Object(10 + i, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] >>");
        }

        foreach ((int number, string body) in extra)
        {
            file.Object(number, body);
        }

        return file.StartXref(file.XrefTable($"/Size {10 + pages} /Root 1 0 R")).ToArray();
    }

    private static List<string> Labels(PdfDocument pdf)
    {
        return [.. Enumerable.Range(0, pdf.Pages.Count).Select(pdf.Pages.GetLabel)];
    }

    [Fact]
    public void WithoutLabelsPagesAreNumberedFromOne()
    {
        using PdfDocument pdf = PdfDocument.Open(WithPages(3), Strict);

        Assert.Equal(["1", "2", "3"], Labels(pdf));
        Assert.Empty(pdf.Pages.GetLabelRanges());
    }

    [Fact]
    public void ReadsEveryStyleWithPrefixAndStart()
    {
        // The example of 12.4.2, plus letters past Z and a prefix-only range.
        using PdfDocument pdf = PdfDocument.Open(WithPages(12,
            "/PageLabels << /Nums [0 << /S /r >> 4 << /S /D >> 7 << /S /D /P (A-) /St 8 >> 9 << /S /A /St 26 >> 11 << /P (Cover) >>] >>"), Strict);

        Assert.Equal(["i", "ii", "iii", "iv", "1", "2", "3", "A-8", "A-9", "Z", "AA", "Cover"], Labels(pdf));
        Assert.Equal(
            [
                new PdfPageLabelRange(0, PdfPageLabelStyle.LowerRoman),
                new PdfPageLabelRange(4, PdfPageLabelStyle.Arabic),
                new PdfPageLabelRange(7, PdfPageLabelStyle.Arabic, "A-", 8),
                new PdfPageLabelRange(9, PdfPageLabelStyle.UpperLetters, null, 26),
                new PdfPageLabelRange(11, PdfPageLabelStyle.None, "Cover"),
            ],
            pdf.Pages.GetLabelRanges());
    }

    [Theory]
    [InlineData(PdfPageLabelStyle.UpperRoman, 1, "I")]
    [InlineData(PdfPageLabelStyle.UpperRoman, 1994, "MCMXCIV")]
    [InlineData(PdfPageLabelStyle.LowerRoman, 4999, "mmmmcmxcix")]
    [InlineData(PdfPageLabelStyle.LowerLetters, 1, "a")]
    [InlineData(PdfPageLabelStyle.LowerLetters, 27, "aa")]
    [InlineData(PdfPageLabelStyle.UpperLetters, 79, "AAAA")]
    [InlineData(PdfPageLabelStyle.Arabic, 2147483647, "2147483647")]
    public void FormatsNumbers(PdfPageLabelStyle style, int start, string expected)
    {
        Assert.Equal(expected, new PdfPageLabelRange(0, style, null, start).Format(0));
    }

    [Fact]
    public void PagesBeforeTheFirstRangeAreNumberedFromOne()
    {
        using PdfDocument pdf = PdfDocument.Open(WithPages(3, "/PageLabels << /Nums [1 << /S /R >>] >>"));

        Assert.Equal(["1", "I", "II"], Labels(pdf));
    }

    [Fact]
    public void BadEntriesAreReadLeniently()
    {
        using PdfDocument pdf = PdfDocument.Open(WithPages(3, "/PageLabels << /Nums [0 << /S /Q /P (x) >> 1 << /S /D /St 0 >> 2 (nope)] >>"));

        Assert.Equal(["x", "1", "2"], Labels(pdf));
        Assert.Equal(2, pdf.Pages.GetLabelRanges().Count);
    }

    [Fact]
    public void ReadsLabelsThroughAMultiLevelTree()
    {
        using PdfDocument pdf = PdfDocument.Open(
            WithPages(5, "/PageLabels << /Kids [3 0 R 4 0 R] >>", (3, "<< /Limits [0 0] /Nums [0 << /S /r >>] >>"), (4, "<< /Limits [2 2] /Nums [2 << /S /D /P (B) >>] >>")),
            Strict);

        Assert.Equal(["i", "ii", "B1", "B2", "B3"], Labels(pdf));
    }

    [Fact]
    public void WritesRangesAndReadsThemBack()
    {
        using PdfDocument pdf = PdfDocument.Open(WithPages(6));

        pdf.Pages.SetLabelRange(new PdfPageLabelRange(0, PdfPageLabelStyle.LowerRoman));
        pdf.Pages.SetLabelRange(new PdfPageLabelRange(2, PdfPageLabelStyle.Arabic, "Ch-", 10));
        pdf.Pages.SetLabelRange(new PdfPageLabelRange(5, PdfPageLabelStyle.None, "Back"));
        pdf.Pages.SetLabelRange(new PdfPageLabelRange(2, PdfPageLabelStyle.Arabic, "Ch-", 20));
        Assert.True(pdf.Pages.RemoveLabelRange(5));
        Assert.False(pdf.Pages.RemoveLabelRange(4));

        using MemoryStream output = new();
        pdf.Save(output);
        using PdfDocument reopened = PdfDocument.Open(output.ToArray(), Strict);
        Assert.Equal(["i", "ii", "Ch-20", "Ch-21", "Ch-22", "Ch-23"], Labels(reopened));
        Assert.Equal("<< /S /D /P (Ch-) /St 20 >>", reopened.Catalog.Get<PdfDictionary>("PageLabels")!.Get<PdfArray>("Nums")!.Get(3).ToString());
    }

    [Fact]
    public void ManyRangesSplitTheTree()
    {
        using PdfDocument pdf = PdfDocument.Create();
        for (int i = 0; i < 300; i++)
        {
            pdf.Pages.AddBlank();
            pdf.Pages.SetLabelRange(new PdfPageLabelRange(i, PdfPageLabelStyle.None, $"p{i}"));
        }

        Assert.Equal(300, pdf.Pages.GetLabelRanges().Count);
        Assert.Equal("p299", pdf.Pages.GetLabel(299));
        Assert.NotNull(pdf.Catalog.Get<PdfDictionary>("PageLabels")!["Kids"]);
    }

    [Fact]
    public void RejectsBadRanges()
    {
        using PdfDocument pdf = PdfDocument.Open(WithPages(1));

        Assert.Throws<ArgumentOutOfRangeException>(() => pdf.Pages.SetLabelRange(new PdfPageLabelRange(-1, PdfPageLabelStyle.Arabic)));
        Assert.Throws<ArgumentOutOfRangeException>(() => pdf.Pages.SetLabelRange(new PdfPageLabelRange(0, PdfPageLabelStyle.Arabic, null, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => pdf.Pages.GetLabel(1));
        Assert.False(pdf.IsChanged);
    }
}
