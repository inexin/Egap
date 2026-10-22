using Egami.Barcodes;

using Egap.Documents.Layout;
using Egap.Documents.Pdf;
using Egap.Documents.Reports;
using Egap.Pdf;

namespace Egap.Documents.Tests;

/// <summary>The fluent report API: what it builds in the model, and that it lays out.</summary>
public sealed class ReportTests
{
    private sealed record Line(string Name, int Qty, decimal Price);

    private static readonly Line[] Lines = [.. Enumerable.Range(1, 40).Select(i => new Line("Item " + i, i % 4 + 1, 10m * i))];

    /// <summary>An address block, as a report would reuse it.</summary>
    private sealed class Address(string name, string street) : IComponent
    {
        public void Compose(Container container)
        {
            container.Padding(4).Border(0.5).Column(col =>
            {
                col.Item().Text(name).Bold();
                col.Item().Text(street);
            });
        }
    }

    internal static Document Invoice()
    {
        return Report.Create(r => r
            .Title("Invoice 2026-118")
            .Style("Total", s => s.FontSize(14).Bold())
            .DefaultTextStyle(s => s.FontSize(10))
            .Page(p => p
                .Size(PdfPageSize.A4).Margins(Length.Mm(15))
                .Header(h => h.BorderBottom(1).Row(row =>
                {
                    row.Spacing(10);
                    row.Fixed(Length.Mm(30)).Barcode(Barcode.Qr("https://egap.dev/i/118"), Length.Mm(20), Length.Mm(20));
                    row.Grow().Text("Invoice 2026-118").Style(s => s.FontSize(20).Bold());
                    row.Auto().AlignRight().Text("21 October 2026");
                }))
                .Content(c => c.Column(col =>
                {
                    col.Spacing(8);
                    col.Item().Grid(g => g.Columns(GridTrack.Fraction(1), GridTrack.Fraction(1)).Spacing(0, 12)
                        .Item().Component(new Address("Acme Ltd", "Storgata 1, Oslo")));
                    col.Item().Table(t => t
                        .Columns(c => c.Relative(3).Fixed(40).Fixed(70))
                        .Lines(0.5)
                        .HeaderBackground(DocumentColor.FromHex("EEEEEE"))
                        .Header("Item", "Qty", "Price")
                        .Rows(Lines, l => [l.Name, l.Qty, l.Price])
                        .Footer(f => f.Cell(3).AlignRight().Text("continued")));
                    col.Item().KeepTogether().Background("F4F8FC").CornerRadius(6).Padding(8).Column(totals =>
                    {
                        totals.Item().AlignRight().Text("Total: " + Lines.Sum(l => l.Qty * l.Price)).Style("Total");
                        totals.Item().AlignRight().Text("Due within 14 days");
                    });
                    col.Item().Row(row =>
                    {
                        row.Auto().CheckBox("paid");
                        row.Grow().Text(" Paid");
                    });
                }))
                .Footer(f => f.AlignCenter().Text($"Page {Fields.Page} of {Fields.Pages}"))));
    }

    [Fact]
    public void TheReportBuildsTheModel()
    {
        Document report = Invoice();

        Section section = Assert.Single(report.Sections);
        Assert.Equal(595.28, section.PageSetup.PageWidth!.Value, 0.01);
        Assert.Equal("Invoice 2026-118", report.Properties.Title);
        Assert.True(report.DefaultLayoutOptions!.Strict);

        FlexContainer header = section.HeadersFooters[HeaderFooterKind.HeaderPrimary]!.Descendants<FlexContainer>().Single();
        Assert.Equal(3, header.Items.Count);
        Assert.Equal(10, header.Layout.ColumnGap);
        Assert.IsType<Picture>(header.Items[0].Descendants<Picture>().Single());
        Assert.Equal(20, header.Items[1].Descendants<Run>().Single().Format.Size);

        Table table = section.Body.Descendants<Table>().Single();
        Assert.Equal(42, table.Rows.Count);
        Assert.True(table.Rows[0].Format.IsHeader);
        Assert.True(table.Rows[^1].Format.IsFooter);
        Assert.Equal(3, table.Grid!.Count);
        Assert.Equal(40, table.Grid[1]);
        Assert.Equal("Item 1", table.Rows[1].Cells[0].GetText().Trim());

        ContentControl totals = section.Body.Descendants<ContentControl>().Single(c => c.Paging?.KeepTogether == true);
        Assert.Equal(6, totals.Box!.CornerRadius.TopLeft);
        Assert.Equal(8, totals.Box.MarginTop);
        Assert.Equal("Total", ((Paragraph)totals.Blocks[0]).Runs.Single().Style!.Id);

        Field[] fields = [.. report.GetFields()];
        Assert.Contains(fields, f => f.Kind == FieldKind.Page);
        Assert.Contains(fields, f => f.Kind == FieldKind.NumPages);
        Assert.Single(report.Descendants<InlineContentControl>());
    }

    [Fact]
    public void TheReportLaysOutStrictlyOnSeveralPages()
    {
        Document report = Invoice();

        DocumentLayout layout = report.Layout();

        Assert.True(layout.Pages.Count >= 2);
        Table table = report.Descendants<Table>().Single();
        Assert.Contains(layout.Pages[1].Boxes.SelectMany(b => b.Descendants()).OfType<RowBox>(), r => r.IsRepeatedHeader);
        Assert.Equal($"Page 2 of {layout.Pages.Count}", layout.Pages[1].Boxes.OfType<AreaBox>().Single(a => a.Area == PageArea.Footer).Descendants().OfType<LineBox>().Single().Text);

        // The body starts below the header, which sits at the top margin.
        LineBox first = layout.Pages[0].BodyLines.First();
        LayoutRect headerArea = layout.Pages[0].Boxes.OfType<AreaBox>().Single(a => a.Area == PageArea.Header).Bounds;
        Assert.True(first.Bounds.Y >= headerArea.Bottom - 0.01);
        Assert.Equal(Length.Mm(15).Points, headerArea.Y, 0.5);

        // The totals are kept together, on one page.
        ContentControl totals = report.Descendants<ContentControl>().Single(c => c.Paging?.KeepTogether == true);
        Assert.Single(layout.FindBoxes(totals));
        _ = table;
    }

    [Fact]
    public void TheSameReportSavesAsPdfAndDocx()
    {
        Document report = Invoice();

        using MemoryStream saved = new();
        report.SaveAsPdf(saved);
        using PdfDocument pdf = PdfDocument.Open(saved.ToArray());
        using MemoryStream docx = new();
        DocxFormat.Save(report, docx);
        Document back = DocxFormat.Load(new MemoryStream(docx.ToArray()));

        Assert.Contains("Invoice 2026-118", pdf.Pages[0].ExtractText(), StringComparison.Ordinal);
        Assert.Contains("Item 40", string.Concat(pdf.Pages.Select(p => p.ExtractText())), StringComparison.Ordinal);
        Assert.Contains("Item 40", back.GetText(), StringComparison.Ordinal);
        Assert.Contains("Due within 14 days", back.GetText(), StringComparison.Ordinal);
    }

    [Fact]
    public void ContainersTakeOneElementAndStrictLayoutFailsLoudly()
    {
        Assert.Throws<InvalidOperationException>(() => Report.Create(r => r.Page(p => p.Content(c =>
        {
            c.Text("one");
            c.Text("two");
        }))));

        Document tooWide = Report.Create(r => r.Page(p => p.Size(Length.Mm(80), Length.Mm(80)).Margins(Length.Mm(5)).Content(c => c.Text(new string('W', 200)))));
        Assert.Throws<LayoutException>(() => tooWide.Layout());
        Assert.NotEmpty(tooWide.Layout(new LayoutOptions()).Pages);
    }

    [Fact]
    public void TextSpansStylesAndPagingSettings()
    {
        TextStyle note = TextStyle.Default.FontSize(8).Italic().FontColor("666666");
        Document report = Report.Create(r => r.Page(p => p.Content(c => c.Column(col =>
        {
            col.Item().Text(t =>
            {
                t.Span("Page ");
                t.CurrentPageNumber().Bold();
                t.Span(" of ");
                t.TotalPages();
                t.LineBreak();
                t.Hyperlink("Egap", "https://github.com/inexin/Egap");
            });
            col.Item().Text("A note").Style(note);
            col.Item().EnsureSpace(Length.Mm(40)).ScaleToFit().Text("Scaled when it must be").ClampLines(2);
            col.Item().PageBreak();
            col.Item().Line(1);
        }))));

        Paragraph[] paragraphs = [.. report.Sections[0].Body.Descendants<Paragraph>()];
        Assert.Equal(2, report.GetFields().Count());
        Assert.Single(paragraphs[0].Inlines.OfType<Hyperlink>());
        Run noted = paragraphs[1].Runs.Single();
        Assert.Equal((8.0, true), (noted.Format.Size!.Value, noted.Format.Italic!.Value));
        ContentControl paged = report.Descendants<ContentControl>().Single();
        Assert.Equal(Length.Mm(40).Points, paged.Paging!.EnsureSpace!.Value, 0.01);
        Assert.True(paged.Paging.ScaleToFit);
        Assert.Equal(2, ((Paragraph)paged.Blocks[0]).Format.MaxLines);
        Assert.Equal(2, report.Layout().Pages.Count);
    }
}
