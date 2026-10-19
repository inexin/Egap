using System.Text;
using System.Text.RegularExpressions;

namespace Egap.Pdf.Tests.Text;

/// <summary>Finding text by phrase or regular expression.</summary>
public sealed class FindTextTests
{
    [Fact]
    public void FindsAPhraseWithItsQuad()
    {
        using PdfDocument pdf = Pages(["BT /F1 12 Tf 72 700 Td (The contract is Confidential.) Tj ET"]);

        PdfTextMatch match = Assert.Single(pdf.FindText("Confidential", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(0, match.PageIndex);
        Assert.Equal("Confidential", match.Text);
        Assert.Equal(12, match.Glyphs.Count);
        PdfQuad quad = Assert.Single(match.Quads);
        Assert.InRange(quad.UpperLeft.Y, 708, 712);
        Assert.InRange(quad.LowerLeft.Y, 696, 700);
        Assert.True(quad.UpperLeft.X > 140 && quad.UpperRight.X < 260, quad.ToString());
    }

    [Fact]
    public void CaseAndWholeWordsAreOptions()
    {
        using PdfDocument pdf = Pages(["BT /F1 12 Tf 72 700 Td (Total, total, subtotal, TOTAL) Tj ET"]);

        Assert.Equal(2, pdf.FindText("total", cancellationToken: TestContext.Current.CancellationToken).Count);
        Assert.Equal(4, pdf.FindText("total", new PdfFindOptions { MatchCase = false }, cancellationToken: TestContext.Current.CancellationToken).Count);
        Assert.Equal(3, pdf.FindText("total", new PdfFindOptions { MatchCase = false, WholeWord = true }, cancellationToken: TestContext.Current.CancellationToken).Count);
    }

    [Fact]
    public void FindsAcrossALineBreakInABlock()
    {
        using PdfDocument pdf = Pages(["BT /F1 12 Tf 72 700 Td (signed by the) Tj 0 -14 Td (customer on arrival) Tj ET BT /F1 12 Tf 72 400 Td (the) Tj 0 -200 Td (customer) Tj ET"]);

        PdfTextMatch match = Assert.Single(pdf.FindText("the customer", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("the customer", match.Text);
        Assert.Equal(2, match.Quads.Count);
        Assert.True(match.Quads[0].Bounds.Bottom > match.Quads[1].Bounds.Top - 1);
    }

    [Fact]
    public void FindsRegularExpressionsOnTheChosenPages()
    {
        using PdfDocument pdf = Pages([
            "BT /F1 12 Tf 72 700 Td (ID 12345678901 and 98765432109) Tj ET",
            "BT /F1 12 Tf 72 700 Td (ID 11111111111) Tj ET",
        ]);
        Regex elevenDigits = new(@"\b\d{11}\b");

        Assert.Equal(["12345678901", "98765432109", "11111111111"], pdf.FindText(elevenDigits, cancellationToken: TestContext.Current.CancellationToken).Select(m => m.Text));
        Assert.Equal([1], pdf.FindText(elevenDigits, new PdfFindOptions { Pages = PdfPageRange.Parse("2") }, cancellationToken: TestContext.Current.CancellationToken).Select(m => m.PageIndex));
        Assert.Single(pdf.Pages[1].FindText(elevenDigits));
        Assert.Single(pdf.Pages[1].FindText("ID  1111"));
        Assert.Throws<ArgumentException>(() => pdf.FindText(" ", cancellationToken: TestContext.Current.CancellationToken));
    }

    private static PdfDocument Pages(string[] contents)
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfReference font = pdf.Objects.Add(new PdfDictionary { ["Type"] = PdfName.Get("Font"), ["Subtype"] = PdfName.Get("Type1"), ["BaseFont"] = PdfName.Get("Helvetica") });
        foreach (string content in contents)
        {
            PdfPage page = pdf.Pages.AddBlank();
            page.Resources = new PdfDictionary { ["Font"] = new PdfDictionary { ["F1"] = font } };
            page.SetContent(PdfContent.Parse(Encoding.ASCII.GetBytes(content)));
        }

        using MemoryStream saved = new();
        pdf.Save(saved);
        return PdfDocument.Open(saved.ToArray());
    }
}
