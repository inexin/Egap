using System.Data;

using BenchmarkDotNet.Attributes;

using Egap.Spreadsheets;

namespace Egap.Spreadsheets.Benchmarks;

/// <summary>
/// Importing and exporting 100,000 rows of 6 columns (whole number, text, decimal, date, bool, nullable double): from a list
/// of objects, a <see cref="DataTable"/> and a data reader, and back to a table and a list; with a hand-written loop over
/// <see cref="Cell.Value"/> as the baseline.
/// </summary>
[MemoryDiagnoser]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "BenchmarkDotNet runs instance methods.")]
public class DataTransferBenchmarks : IDisposable
{
    private const int Rows = 100_000;

    private Item[] items = null!;
    private DataTable table = null!;
    private Worksheet filled = null!;

    [GlobalSetup]
    public void Setup()
    {
        items = [.. Enumerable.Range(0, Rows).Select(i => new Item
        {
            Id = i,
            Name = "name" + (i % 1000),
            Price = i * 0.25m,
            Date = new DateTime(2020, 1, 1).AddDays(i % 2000),
            Active = i % 3 == 0,
            Score = i % 7 == 0 ? null : i * 0.5,
        })];
        table = new DataTable();
        table.Columns.Add("Id", typeof(int));
        table.Columns.Add("Name", typeof(string));
        table.Columns.Add("Price", typeof(decimal));
        table.Columns.Add("Date", typeof(DateTime));
        table.Columns.Add("Active", typeof(bool));
        table.Columns.Add("Score", typeof(double));
        foreach (Item item in items)
        {
            table.Rows.Add(item.Id, item.Name, item.Price, item.Date, item.Active, item.Score is { } s ? s : DBNull.Value);
        }

        filled = Workbook.Create().Worksheets[0];
        filled["A1"].ImportData(items);
    }

    [Benchmark(Baseline = true, Description = "Hand-written loop, 100k rows")]
    public Worksheet Loop()
    {
        Worksheet sheet = Workbook.Create().Worksheets[0];
        int row = 1;
        foreach (Item item in items)
        {
            sheet.Cells[row, 0].Value = CellValue.FromNumber(item.Id);
            sheet.Cells[row, 1].Value = CellValue.FromText(item.Name);
            sheet.Cells[row, 2].Value = CellValue.FromNumber((double)item.Price);
            sheet.Cells[row, 3].Value = CellValue.FromNumber(item.Date.ToOADate());
            sheet.Cells[row, 3].NumberFormat = "yyyy-mm-dd";
            sheet.Cells[row, 4].Value = CellValue.FromBoolean(item.Active);
            if (item.Score is { } score)
            {
                sheet.Cells[row, 5].Value = CellValue.FromNumber(score);
            }

            row++;
        }

        return sheet;
    }

    [Benchmark(Description = "ImportData list, 100k rows")]
    public CellRange ImportList() => Workbook.Create().Worksheets[0]["A1"].ImportData(items);

    [Benchmark(Description = "ImportData DataTable, 100k rows")]
    public CellRange ImportTable() => Workbook.Create().Worksheets[0]["A1"].ImportData(table);

    [Benchmark(Description = "ImportData IDataReader, 100k rows")]
    public CellRange ImportReader()
    {
        using DataTableReader reader = table.CreateDataReader();
        return Workbook.Create().Worksheets[0]["A1"].ImportData(reader);
    }

    [Benchmark(Description = "ToDataTable, 100k rows")]
    public DataTable ExportTable() => filled.Range("A:F").ToDataTable();

    [Benchmark(Description = "ToList, 100k rows")]
    public List<Item> ExportList() => filled.Range("A:F").ToList<Item>();

    [GlobalCleanup]
    public void Dispose()
    {
        table?.Dispose();
        GC.SuppressFinalize(this);
    }

    public sealed class Item
    {
        public int Id { get; set; }

        public string Name { get; set; } = "";

        public decimal Price { get; set; }

        public DateTime Date { get; set; }

        public bool Active { get; set; }

        public double? Score { get; set; }
    }
}
