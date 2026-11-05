using BenchmarkDotNet.Attributes;

using Egap.Spreadsheets;

namespace Egap.Spreadsheets.Benchmarks;

/// <summary>
/// Loading and saving an XLSX workbook of 10,000 rows by 10 columns (100,000 cells): numbers, text from 1,000 distinct
/// strings, a formula per row and a few styles, as a typical data export holds.
/// </summary>
[MemoryDiagnoser]
public class XlsxBenchmarks
{
    private const int Rows = 10_000;
    private const int Columns = 10;

    private Workbook book = null!;
    private byte[] file = [];

    [GlobalSetup]
    public void Setup()
    {
        book = Workbook.Create();
        Worksheet sheet = book.Worksheets[0];
        CellStyle money = CellStyle.Default with { NumberFormat = "#,##0.00" };
        CellStyle bold = CellStyle.Default with { Font = CellFont.Default with { Bold = true } };
        for (int row = 0; row < Rows; row++)
        {
            for (int column = 0; column < Columns - 2; column++)
            {
                sheet.Cells.SetValue(row, column, column % 2 == 0 ? row * 1.5 + column : "word" + ((row + column) % 1000));
            }

            sheet[row, Columns - 2].Formula = $"A{row + 1}*2";
            sheet[row, Columns - 1].Value = row;
            sheet[row, 0].Style = money;
        }

        sheet.Range(0, 0, 0, Columns - 1).SetStyle(bold);
        using MemoryStream stream = new();
        book.Save(stream);
        file = stream.ToArray();
    }

    [Benchmark(Description = "Load 100k cells")]
    public Workbook Load() => Workbook.Load(new MemoryStream(file, writable: false));

    [Benchmark(Description = "Save 100k cells")]
    public long Save()
    {
        using MemoryStream stream = new(file.Length);
        book.Save(stream);
        return stream.Length;
    }
}
