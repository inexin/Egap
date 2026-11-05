using System.Text;

using BenchmarkDotNet.Attributes;

using Egap.Spreadsheets;

namespace Egap.Spreadsheets.Benchmarks;

/// <summary>
/// Cell text and CSV on 1,000 rows by 100 columns (100,000 cells) of numbers in four formats: showing every cell's text,
/// writing the sheet as CSV and reading that CSV back with type detection.
/// </summary>
[MemoryDiagnoser]
public class TextBenchmarks
{
    private const int Rows = 1000;
    private const int Columns = 100;
    private static readonly string[] Formats = ["General", "#,##0.00", "0%", "yyyy-mm-dd"];

    private Worksheet sheet = null!;
    private byte[] csv = null!;

    [GlobalSetup]
    public void Setup()
    {
        sheet = Workbook.Create().Worksheets[0];
        for (int column = 0; column < Columns; column++)
        {
            sheet.Range(0, column, Rows - 1, column).UpdateStyle(s => s with { NumberFormat = Formats[column % Formats.Length] });
        }

        for (int row = 0; row < Rows; row++)
        {
            for (int column = 0; column < Columns; column++)
            {
                sheet.Cells.SetValue(row, column, column % 4 == 3 ? 40000 + row : (row * 31.7) + column);
            }
        }

        MemoryStream stream = new();
        sheet.SaveCsv(stream);
        csv = stream.ToArray();
    }

    [Benchmark(Description = "Text of 100k cells")]
    public int CellText()
    {
        int length = 0;
        for (int row = 0; row < Rows; row++)
        {
            for (int column = 0; column < Columns; column++)
            {
                length += sheet[row, column].Text.Length;
            }
        }

        return length;
    }

    [Benchmark(Description = "SaveCsv, 100k cells")]
    public long SaveCsv()
    {
        MemoryStream stream = new();
        sheet.SaveCsv(stream);
        return stream.Length;
    }

    [Benchmark(Description = "LoadCsv, 100k cells")]
    public Workbook LoadCsv() => Workbook.LoadCsv(new MemoryStream(csv));

    [Benchmark(Description = "LoadCsv, 100k cells, no detection")]
    public Workbook LoadCsvText() => Workbook.LoadCsv(new MemoryStream(csv), new CsvOptions { DetectTypes = false, Encoding = Encoding.UTF8 });
}
