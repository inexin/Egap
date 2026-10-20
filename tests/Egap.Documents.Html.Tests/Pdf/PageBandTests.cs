using Egap.Documents.Layout;
using Egap.Documents.Pdf;
using Egap.Pdf;

namespace Egap.Documents.Html.Tests.Pdf;

/// <summary>Header and footer options on conversion, and running elements.</summary>
public sealed class PageBandTests
{
    private static readonly PdfConvertOptions Isolated = new()
    {
        Export = new PdfExportOptions { Layout = new LayoutOptions { Fonts = FontSettings.Portable } },
    };

    private static readonly string TwoPages = "<title>Report</title><p>One</p><p style='break-before: page'>Two</p>";

    [Fact]
    public void ATextFooterFillsThePlaceholdersOnEachPage()
    {
        using PdfDocument pdf = Reopen(PdfConvert.FromHtml(TwoPages, Isolated with
        {
            Footer = PageBand.Text(left: "{title}", center: "Page {page} of {total-pages}", right: "{page:000}"),
        }));

        Assert.Equal(2, pdf.Pages.Count);
        for (int i = 0; i < 2; i++)
        {
            string text = pdf.Pages[i].ExtractText();
            Assert.Contains($"Page {i + 1} of 2", text, StringComparison.Ordinal);
            Assert.Contains($"00{i + 1}", text, StringComparison.Ordinal);
            Assert.Contains("Report", text, StringComparison.Ordinal);
            PdfTextLine line = pdf.Pages[i].GetText().Lines.First(l => l.Text.Contains("Page", StringComparison.Ordinal));
            Assert.True(line.Box.Bottom < 30, "the footer sits in the bottom margin");
        }
    }

    [Fact]
    public void AnHtmlHeaderUsesThePagesStyleSheetsAndReplacesItsOwnBoxes()
    {
        string html = "<style>.hdr { font-weight: bold; text-align: right } @page { @top-center { content: 'Old' } }</style>" + TwoPages;

        using PdfDocument pdf = Reopen(PdfConvert.FromHtml(html, Isolated with { Header = PageBand.Html("<div class='hdr'>ACME {page}/{pages}</div>") }));

        string second = pdf.Pages[1].ExtractText();
        Assert.Contains("ACME 2/2", second, StringComparison.Ordinal);
        Assert.DoesNotContain("Old", second, StringComparison.Ordinal);
        PdfTextLine line = pdf.Pages[1].GetText().Lines.First(l => l.Text.Contains("ACME", StringComparison.Ordinal));
        Assert.True(line.Box.Right > pdf.Pages[1].MediaBox.Right / 2, "right-aligned by the page's own CSS");
        Assert.True(line.Box.Bottom > pdf.Pages[1].MediaBox.Top - 60, "the header sits in the top margin");
    }

    [Fact]
    public void RunningElementsMoveIntoTheMarginBoxesThatNameThem()
    {
        Document document = HtmlFormat.Parse("""
            <style>
            .running { position: running(header); }
            @page { @top-center { content: element(header); } }
            </style>
            <div class='running'><b>ACME</b> Ltd</div><p>Body</p>
            """);

        MarginBox box = document.Sections[0].MarginBoxes[MarginBoxPosition.TopCenter]!;
        Assert.Equal("ACME Ltd", box.GetText().Trim());
        Assert.DoesNotContain("ACME", document.Sections[0].Body.GetText(), StringComparison.Ordinal);
        Assert.True(box.Descendants<Run>().First().Format.Bold);
        Assert.Equal("ACME Ltd", HtmlFormat.Parse("<div><b>ACME</b> Ltd</div>").Sections[0].Body.GetText().Trim());

        using PdfDocument pdf = Reopen(PdfConvert.FromHtml("<style>.running { position: running(header); } @page { @top-center { content: element(header); } }</style><div class='running'>ACME</div><p>Body</p>", Isolated));
        Assert.Contains("ACME", pdf.Pages[0].ExtractText(), StringComparison.Ordinal);
    }

    private static PdfDocument Reopen(PdfDocument pdf)
    {
        using (pdf)
        {
            using MemoryStream saved = new();
            pdf.Save(saved);
            return PdfDocument.Open(saved.ToArray());
        }
    }
}
