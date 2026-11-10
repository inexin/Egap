using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Egap.Spreadsheets.Tests;

/// <summary>
/// Pivot table refresh against Excel: <c>tools/excel-pivot-oracle.ps1</c> made eleven pivot tables of one source in
/// <c>Oracle/pivots.xlsx</c> (compact, outline, tabular and classic layouts, every function, page fields, hidden items,
/// custom subtotals, no grand totals, dates and a table of values only), then changed the source and saved Excel's refresh
/// as <c>Oracle/pivots-edited.xlsx</c>. Egap refreshes the first file, as it is and after the same change, and must fill the
/// same cells.
/// </summary>
public sealed class PivotTableTests
{
    private static readonly string Book = Path.Combine(AppContext.BaseDirectory, "Oracle", "pivots.xlsx");
    private static readonly string Edited = Path.Combine(AppContext.BaseDirectory, "Oracle", "pivots-edited.xlsx");

    // The oracle ran with Excel in Norwegian, which names subtotals of dates and numbers in its own formats.
    private static readonly CultureInfo Norwegian = CultureInfo.GetCultureInfo("nb-NO");

    public static TheoryData<string> Sheets() => ["Compact", "CompactColumns", "Outline", "Tabular", "Classic", "Functions", "Filtered", "Subtotals", "NoTotals", "Dates", "ValuesOnly"];

    [Theory]
    [MemberData(nameof(Sheets))]
    public void RefreshKeepsWhatExcelShows(string sheet)
    {
        Workbook book = Load(Book);
        Assert.Equal(11, book.RefreshPivotTables());
        AssertSameCells(Workbook.Load(Book).Worksheets[sheet], RoundTrip(book).Worksheets[sheet]);
    }

    [Theory]
    [MemberData(nameof(Sheets))]
    public void RefreshAfterAnEditMatchesExcel(string sheet)
    {
        Workbook book = Load(Book);
        Edit(book);
        Assert.Equal(11, book.RefreshPivotTables());
        Workbook saved = RoundTrip(book);
        Worksheet excel = Workbook.Load(Edited).Worksheets[sheet];
        AssertSameCells(excel, saved.Worksheets[sheet]);
        Assert.Equal(excel.PivotTables.Single().Range, saved.Worksheets[sheet].PivotTables.Single().Range);
    }

    [Fact]
    public void ListsThePivotTablesAndTheirSource()
    {
        Workbook book = Workbook.Load(Book);
        Assert.Empty(book.Worksheets["Data"].PivotTables);
        PivotTable table = Assert.Single(book.Worksheets["Compact"].PivotTables);
        Assert.Equal("Compact", table.Name);
        Assert.Same(book.Worksheets["Compact"], table.Worksheet);
        Assert.Equal(RangeAddress.Parse("A6:B34"), table.Range);
        Assert.Equal("Data!A1:G61", table.Source);
        Assert.True(table.CanRefresh);
        Assert.Equal("Compact (Compact!A6:B34)", table.ToString());
    }

    [Fact]
    public void TheSourceFollowsRowsPutInAndASheetRenamed()
    {
        Workbook book = Workbook.Load(Book);
        PivotTable table = book.Worksheets["Compact"].PivotTables[0];
        book.Worksheets["Data"].InsertRows(10, 3);
        Assert.Equal("Data!A1:G64", table.Source);
        book.Worksheets["Data"].Name = "Sales data";
        Assert.Equal("'Sales data'!A1:G64", table.Source);
        Assert.Equal(11, book.RefreshPivotTables());
    }

    [Fact]
    public void ABlankHeaderIsRefusedAndNothingChanges()
    {
        Workbook book = Load(Book);
        Worksheet sheet = book.Worksheets["Compact"];
        string before = Cells(sheet);
        book.Worksheets["Data"][0, 2].Value = CellValue.Empty;
        book.Worksheets["Data"][5, 5].Value = 1000;
        Assert.Throws<InvalidOperationException>(sheet.PivotTables[0].Refresh);
        Assert.Equal(before, Cells(sheet));
        Assert.Equal(0, book.RefreshPivotTables());
    }

    [Fact]
    public void ATableThatWouldGrowOverAnotherIsRefused()
    {
        // The Outline table moved onto the Compact sheet below the Compact table, which grows over it once every product
        // has its own name.
        const string Relationship = "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/pivotTable\" Target=\"../pivotTables/pivotTable3.xml\"/>";
        byte[] bytes = Rewrite(
            File.ReadAllBytes(Book),
            ("xl/worksheets/_rels/sheet4.xml.rels", xml => xml.Replace(Relationship, "", StringComparison.Ordinal)),
            ("xl/worksheets/_rels/sheet2.xml.rels", xml => xml.Replace("</Relationships>", Relationship.Replace("rId1", "rId7", StringComparison.Ordinal) + "</Relationships>", StringComparison.Ordinal)),
            ("xl/pivotTables/pivotTable3.xml", xml => xml.Replace("<location ref=\"A6:C25\"", "<location ref=\"A40:C59\"", StringComparison.Ordinal)));
        Workbook book = Workbook.Load(new MemoryStream(bytes));
        book.Culture = Norwegian;
        Worksheet sheet = book.Worksheets["Compact"];
        Assert.Equal(2, sheet.PivotTables.Count);
        Worksheet data = book.Worksheets["Data"];
        for (int r = 1; r <= 60; r++)
        {
            data[r, 2].Value = "Product " + r.ToString(CultureInfo.InvariantCulture);
        }

        string before = Cells(sheet);
        Assert.Throws<InvalidOperationException>(sheet.PivotTables[0].Refresh);
        Assert.Equal(before, Cells(sheet));
        Assert.Equal(RangeAddress.Parse("A6:B34"), sheet.PivotTables[0].Range);
    }

    [Fact]
    public void AnExternalSourceCannotBeRefreshed()
    {
        byte[] bytes = Rewrite(File.ReadAllBytes(Book), "xl/pivotCache/pivotCacheDefinition1.xml",
            xml => xml.Replace("<worksheetSource ", "<worksheetSource r:id=\"rId9\" ", StringComparison.Ordinal));
        Workbook book = Workbook.Load(new MemoryStream(bytes));
        PivotTable table = book.Worksheets["Compact"].PivotTables[0];
        Assert.False(table.CanRefresh);
        Assert.Throws<InvalidOperationException>(table.Refresh);
        Assert.Equal(0, book.RefreshPivotTables());
    }

    [Fact]
    public void AnUnrefreshedBookSavesItsPivotPartsAsTheyWere()
    {
        Workbook book = Workbook.Load(Book);
        _ = book.Worksheets["Compact"].PivotTables;
        Workbook saved = RoundTrip(book);
        Assert.Equal(11, saved.Worksheets.Sum(s => s.PivotTables.Count));
        Assert.Equal(Cells(Workbook.Load(Book).Worksheets["Tabular"]), Cells(saved.Worksheets["Tabular"]));
    }

    private static Workbook Load(string path)
    {
        Workbook book = Workbook.Load(path);
        book.Culture = Norwegian;
        return book;
    }

    private static Workbook RoundTrip(Workbook book)
    {
        using MemoryStream stream = new();
        book.Save(stream);
        stream.Position = 0;
        return Workbook.Load(stream);
    }

    private static void AssertSameCells(Worksheet excel, Worksheet egap)
    {
        Assert.Equal(Cells(excel), Cells(egap));
    }

    private static string Cells(Worksheet sheet)
    {
        StringBuilder text = new();
        for (int row = 0; row < 80; row++)
        {
            for (int column = 0; column < 14; column++)
            {
                CellValue value = sheet[row, column].Value;
                if (!value.IsEmpty)
                {
                    string shown = value.Kind == CellValueKind.Number
                        ? value.Number.ToString("G12", CultureInfo.InvariantCulture)
                        : value.ToString();
                    text.Append(new CellAddress(row, column)).Append('=').Append(shown).Append('\n');
                }
            }
        }

        return text.ToString();
    }

    private static byte[] Rewrite(byte[] package, string name, Func<string, string> change) => Rewrite(package, (name, change));

    private static byte[] Rewrite(byte[] package, params (string Name, Func<string, string> Change)[] changes)
    {
        using MemoryStream stream = new();
        stream.Write(package);
        using (ZipArchive zip = new(stream, ZipArchiveMode.Update, leaveOpen: true))
        {
            foreach ((string name, Func<string, string> change) in changes)
            {
                ZipArchiveEntry entry = zip.GetEntry(name)!;
                string xml;
                using (StreamReader reader = new(entry.Open()))
                {
                    xml = reader.ReadToEnd();
                }

                entry.Delete();
                using StreamWriter writer = new(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                writer.Write(change(xml));
            }
        }

        return stream.ToArray();
    }

    /// <summary>The change <c>tools/excel-pivot-oracle.ps1</c> made before Excel's refresh.</summary>
    private static void Edit(Workbook book)
    {
        string[] reps = ["Ann", "Bob", "Cy", "Di", "Ed", "Flo"];
        string[] products = ["Apples", "Pears", "Plums", "Figs", "Kiwis"];
        Worksheet data = book.Worksheets["Data"];
        data.InsertRows(60, 12);
        for (int i = 0; i < 12; i++)
        {
            int r = 60 + i;
            int k = (i * 11) + 5;
            data[r, 0].Value = new[] { "North", "Central", "West" }[k % 3];
            data[r, 1].Value = reps[k % 6];
            data[r, 2].Value = products[k % 4];
            data[r, 3].Value = 2020 + (k % 3);
            data[r, 4].Value = 44000 + k;
            data[r, 4].NumberFormat = "yyyy-mm-dd";
            data[r, 5].Value = (k % 9) + 2;
            data[r, 6].Value = k * 2.5;
        }

        foreach (int row in Enumerable.Range(2, 59).Append(73))
        {
            int r = row - 1;
            if (data[r, 2].Value is { Kind: CellValueKind.Text } text && text.Text == "Kiwis")
            {
                data[r, 2].Value = "Pears";
            }

            if (row % 4 == 0 && data[r, 5].Value.Kind == CellValueKind.Number)
            {
                data[r, 5].Value = data[r, 5].Value.Number * 2;
            }
        }
    }
}
