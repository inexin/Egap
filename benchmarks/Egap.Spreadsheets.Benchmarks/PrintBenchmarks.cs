using BenchmarkDotNet.Attributes;

using Egap.Documents.Layout;

namespace Egap.Spreadsheets.Benchmarks;

/// <summary>
/// Printing a sheet of 1,000 rows by 20 columns (20,000 numbers and texts, with a header and footer and print titles):
/// laying out its pages, and printing them all to PDF.
/// </summary>
[MemoryDiagnoser]
public class PrintBenchmarks
{
    private static readonly SheetPrintOptions Print = new() { Fonts = FontSettings.Portable, PrintTime = new DateTime(2026, 11, 5) };

    private Worksheet sheet = null!;

    [GlobalSetup]
    public void Setup()
    {
        sheet = Workbook.Create().Worksheets[0];
        for (int row = 0; row < 1000; row++)
        {
            for (int column = 0; column < 20; column++)
            {
                if (column % 5 == 0)
                {
                    sheet.Cells.SetValue(row, column, $"Item {row}-{column}");
                }
                else
                {
                    sheet.Cells.SetValue(row, column, (row * 31.7) + column);
                }
            }
        }

        sheet.PageSetup.PrintTitleRows = RangeAddress.Parse("1:1");
        sheet.PageSetup.Header = "&L&A&RPage &P of &N";
        sheet.PageSetup.Footer = "&C&D";
    }

    [Benchmark(Description = "Paginate 20k cells")]
    public int Paginate() => sheet.Paginate(Print).Pages.Count;

    [Benchmark(Description = "Print 20k cells to PDF")]
    public long SaveAsPdf()
    {
        using MemoryStream stream = new();
        sheet.SaveAsPdf(stream, new SheetPdfOptions { Print = Print });
        return stream.Length;
    }
}
