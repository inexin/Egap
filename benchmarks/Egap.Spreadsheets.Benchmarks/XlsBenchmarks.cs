using BenchmarkDotNet.Attributes;

using Egap.Spreadsheets.Tests;

using static Egap.Spreadsheets.Tests.XlsBuilder;

namespace Egap.Spreadsheets.Benchmarks;

/// <summary>
/// Loading an Excel 97-2003 (BIFF8) workbook of 10,000 rows by 10 columns (100,000 cells), the same data as
/// <see cref="XlsxBenchmarks"/>: numbers, text from 1,000 distinct strings in the shared string table, a formula per row
/// and two cell styles.
/// </summary>
[MemoryDiagnoser]
public class XlsBenchmarks
{
    private const int Rows = 10_000;
    private const int Columns = 10;

    private byte[] file = [];

    [GlobalSetup]
    public void Setup()
    {
        XlsBuilder builder = new();

        // The shared strings, in an SST record and as many CONTINUE records as they need, each string whole in one record.
        List<byte[]> strings = [.. Enumerable.Range(0, 1000).Select(i => String("word" + i))];
        List<byte> record = [.. U32(Rows * (Columns - 2) / 2), .. U32(1000)];
        int type = 0x00FC;
        foreach (byte[] text in strings)
        {
            if (record.Count + text.Length > 8224)
            {
                builder.Global(type, [.. record]);
                type = 0x003C;
                record.Clear();
            }

            record.AddRange(text);
        }

        builder.Global(type, [.. record]);
        builder.Global(0x041E, U16(164), String("#,##0.00"));
        builder.Styles([Font("Arial", 10, bold: true)], Xf(0, 164), Xf(5, 0));
        builder.Sheet("Sheet1");
        for (int row = 0; row < Rows; row++)
        {
            for (int column = 0; column < Columns - 2; column++)
            {
                int xf = row == 0 ? 17 : column == 0 ? 16 : 15;
                if (column % 2 == 0)
                {
                    builder.Cell(0x0203, At(row, column, xf), Double((row * 1.5) + column));
                }
                else
                {
                    builder.Cell(0x00FD, At(row, column, xf), U32((uint)((row + column) % 1000)));
                }
            }

            builder.Cell(0x0006, Formula(row, Columns - 2, row * 1.5 * 2, [.. Ref(0x24, row, 0), 0x1E, .. U16(2), 0x05]));
            builder.Cell(0x0203, At(row, Columns - 1), Double(row));
        }

        file = builder.Build();
    }

    [Benchmark(Description = "Load XLS, 100k cells")]
    public Workbook Load() => Workbook.Load(new MemoryStream(file, writable: false));
}
