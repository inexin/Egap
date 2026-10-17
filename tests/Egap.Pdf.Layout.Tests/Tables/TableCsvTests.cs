using Egap.Pdf.Layout.Tests.Synthetic;

namespace Egap.Pdf.Layout.Tests.Tables;

/// <summary>Detected tables as CSV (Build Plan task 19).</summary>
public sealed class TableCsvTests
{
    [Fact]
    public void ATableIsCsvWithMergedCellsOnceOrRepeated()
    {
        using PdfDocument pdf = PdfDocument.Open(SyntheticScenes.Tables().Save());
        LayoutTable table = PdfLayoutAnalyzer.Analyze(pdf, 0).Tables[0];

        string once = TableCsv.ToCsv(table);
        string repeated = TableCsv.ToCsv(table, new TableCsvOptions { RepeatMergedCells = true });

        string[] lines = once.Split("\r\n");
        Assert.Equal("Region,2024,2025,Change", lines[0]);
        Assert.Equal("South,98,n/a,", lines[2]);
        Assert.Equal("South,98,n/a,n/a", repeated.Split("\r\n")[2]);
        Assert.Equal(6, lines.Length);
    }

    [Fact]
    public void FieldsAreQuotedAsRfc4180Says()
    {
        using PdfDocument pdf = PdfDocument.Open(SyntheticScenes.Tables().Save());
        LayoutTable table = PdfLayoutAnalyzer.Analyze(pdf, 0).Tables[1];

        string semicolons = TableCsv.ToCsv(table, new TableCsvOptions { Separator = ';' });
        string commas = TableCsv.ToCsv(table);

        Assert.Contains("Small box;20 cm;0.4 kg;12.00", semicolons, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => new TableCsvOptions { Separator = '"' });
        Assert.DoesNotContain("\"", commas, StringComparison.Ordinal);
    }

    [Fact]
    public void ATableAcrossPagesIsWrittenOnceWithoutItsRepeatedHeader()
    {
        using PdfDocument pdf = PdfDocument.Open(SyntheticScenes.TableAcrossPages().Save());

        IReadOnlyList<TableCsvFile> tables = TableCsv.Export(PdfLayoutAnalyzer.Analyze(pdf, cancellationToken: TestContext.Current.CancellationToken));
        IReadOnlyList<TableCsvFile> bodies = TableCsv.Export(PdfLayoutAnalyzer.Analyze(pdf, cancellationToken: TestContext.Current.CancellationToken),
            new TableCsvOptions { IncludeHeaderRows = false });

        TableCsvFile table = Assert.Single(tables);
        Assert.Equal((0, 1), (table.PageIndex, table.HeaderRows));
        string[] lines = table.Text.TrimEnd().Split("\r\n");
        Assert.Equal(61, lines.Length);
        Assert.Equal("#,Sample,Value,Unit", lines[0]);
        Assert.StartsWith("60,Sample 60,", lines[^1], StringComparison.Ordinal);
        Assert.EndsWith(",mg", lines[^1], StringComparison.Ordinal);
        Assert.Equal(60, bodies[0].Text.TrimEnd().Split("\r\n").Length);
    }

    [Fact]
    public void QuotesSeparatorsAndLineBreaksAreQuoted()
    {
        LayoutTable table = new(default, 0, 1, 1, 4, false, false, false,
        [
            new LayoutTableCell(0, 0, 1, 1, false, "a", default, []),
            new LayoutTableCell(0, 1, 1, 1, false, "b,c", default, []),
            new LayoutTableCell(0, 2, 1, 1, false, "say \"hi\"", default, []),
            new LayoutTableCell(0, 3, 1, 1, false, "two\nlines", default, []),
        ]);

        Assert.Equal("a,\"b,c\",\"say \"\"hi\"\"\",\"two\nlines\"\r\n", TableCsv.ToCsv(table));
    }
}
