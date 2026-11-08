using System.IO.Compression;

using BenchmarkDotNet.Attributes;

using Egap.Spreadsheets;

namespace Egap.Spreadsheets.Benchmarks;

/// <summary>
/// Sorting, filtering and finding on 10,000 rows by 5 columns: numbers, text and a formula per row; a sort by text then number,
/// a custom filter applied, and a find and a replace over the sheet.
/// </summary>
[MemoryDiagnoser]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "BenchmarkDotNet runs instance methods.")]
public class SortFindBenchmarks
{
    private const int Rows = 10_000;

    private Worksheet filtered = null!;
    private Worksheet searched = null!;

    [GlobalSetup]
    public void Setup()
    {
        filtered = WithFilter(Fill());
        searched = Fill();
    }

    [Benchmark(Description = "Sort 10k rows by text then number")]
    public Worksheet Sort()
    {
        Worksheet sheet = Fill();
        sheet.Range($"A1:E{Rows + 1}").Sort(new SortOptions { HasHeader = true }, new SortField(1), new SortField(0, Descending: true));
        return sheet;
    }

    [Benchmark(Description = "AutoFilter.Apply, 10k rows")]
    public int Filter() => filtered.AutoFilter!.Apply();

    [Benchmark(Description = "Find in values, 50k cells")]
    public int Find() => searched.Find("word1*", new FindOptions { LookIn = FindLookIn.Values }).Count;

    [Benchmark(Description = "Replace in formulas, 10k rows")]
    public int Replace()
    {
        Worksheet sheet = Fill();
        return sheet.Replace("A", "C");
    }

    private static Worksheet Fill()
    {
        Workbook book = Workbook.Create();
        Worksheet sheet = book.Worksheets[0];
        sheet["A1"].Value = "N";
        sheet["B1"].Value = "Word";
        sheet["C1"].Value = "M";
        sheet["D1"].Value = "Sum";
        sheet["E1"].Value = "Flag";
        Random random = new(1);
        for (int row = 1; row <= Rows; row++)
        {
            sheet[row, 0].Value = random.Next(1000);
            sheet[row, 1].Value = "word" + random.Next(500);
            sheet[row, 2].Value = random.NextDouble();
            sheet[row, 3].Formula = $"A{row + 1}+C{row + 1}";
            sheet[row, 4].Value = random.Next(2) == 0;
        }

        book.Calculate();
        return sheet;
    }

    /// <summary>The sheet saved, given a filter that keeps numbers over 500 in its first column, and loaded again.</summary>
    private static Worksheet WithFilter(Worksheet sheet)
    {
        MemoryStream saved = new();
        sheet.Workbook.Save(saved);
        using (ZipArchive zip = new(saved, ZipArchiveMode.Update, leaveOpen: true))
        {
            ZipArchiveEntry entry = zip.GetEntry("xl/worksheets/sheet1.xml")!;
            string xml;
            using (StreamReader reader = new(entry.Open()))
            {
                xml = reader.ReadToEnd();
            }

            string filter = $"<autoFilter ref=\"A1:E{Rows + 1}\"><filterColumn colId=\"0\"><customFilters><customFilter operator=\"greaterThan\" val=\"500\"/></customFilters></filterColumn></autoFilter>";
            xml = xml.Replace("</sheetData>", "</sheetData>" + filter, StringComparison.Ordinal);
            entry.Delete();
            using StreamWriter writer = new(zip.CreateEntry("xl/worksheets/sheet1.xml").Open());
            writer.Write(xml);
        }

        saved.Position = 0;
        return Workbook.Load(saved).Worksheets[0];
    }
}
