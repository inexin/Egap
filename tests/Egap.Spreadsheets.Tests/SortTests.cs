using System.Globalization;

namespace Egap.Spreadsheets.Tests;

/// <summary>
/// Sorting ranges, against Excel: each case of <c>Oracle/sort.excel.tsv</c>, which <c>tools/excel-sort-oracle.ps1</c> recorded,
/// gives the cells before a sort, the sort, and the cells after it.
/// </summary>
public sealed class SortTests
{
    private static readonly string[][] Excel = [.. File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Oracle", "sort.excel.tsv")).Skip(1).Select(line => line.Split('\t'))];

    /// <summary>Windows' locale when Excel sorted, whose rules the text order follows.</summary>
    private static readonly CultureInfo Locale = CultureInfo.GetCultureInfo(File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Oracle", "sort.excel.tsv")).First().Split(' ')[^1]);

    private static readonly CellFill Filled = CellFill.Solid(CellColor.FromRgb(255, 255, 0));

    public static TheoryData<string> Cases()
    {
        TheoryData<string> cases = [];
        foreach (string[] line in Excel)
        {
            cases.Add(line[0]);
        }

        return cases;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void SortsAsExcelDoes(string name)
    {
        string[] line = Array.Find(Excel, l => l[0] == name)!;
        string[] options = line[1].Split(';');
        Workbook book = Workbook.CreateEmpty();
        book.Culture = Locale;
        Worksheet sheet = book.Worksheets.Add("S");
        Fill(sheet, line[2]);
        switch (options[5])
        {
            case "merge":
                sheet.MergedCells.Add(RangeAddress.Parse("C2:D3"));
                break;
            case "array":
                sheet.Range("E2:E3").SetArrayFormula("A2:A3*3");
                sheet.Range("C2:C3").SetArrayFormula("B2:B3");
                break;
            case "":
                break;
            default:
                foreach (string row in options[5].Split(' '))
                {
                    sheet.Rows[int.Parse(row, CultureInfo.InvariantCulture) - 1].Hidden = true;
                }

                break;
        }

        book.Calculate();
        SortField[] keys = [.. options[1].Split(' ').Select(k => new SortField(int.Parse(k[..^2], CultureInfo.InvariantCulture), k.EndsWith(":d", StringComparison.Ordinal)))];
        SortOptions sort = new() { HasHeader = options[2] == "True", MatchCase = options[3] == "True", Orientation = options[4] == "columns" ? SortOrientation.Columns : SortOrientation.Rows };
        CellRange range = sheet.Range(options[0]);
        if (line[3].StartsWith("ERROR", StringComparison.Ordinal))
        {
            Assert.Throws<InvalidOperationException>(() => range.Sort(sort, keys));
            return;
        }

        range.Sort(sort, keys);
        Assert.Equal(Parts(line[3]), Dump(sheet, line[3]));
    }

    [Fact]
    public void TextWithoutHyphensComesFirstInATie()
    {
        CompareInfo compare = CultureInfo.InvariantCulture.CompareInfo;
        Assert.True(Worksheet.CompareForSort(CellValue.FromText("coop"), CellValue.FromText("co-op"), false, false, compare) < 0);
        Assert.True(Worksheet.CompareForSort(CellValue.FromText("a'c"), CellValue.FromText("a-c"), false, false, compare) < 0);
        Assert.Equal(0, Worksheet.CompareForSort(CellValue.FromText("ab"), CellValue.FromText("AB"), false, false, compare));
    }

    [Fact]
    public void EmptyCellsComeLastBothWays()
    {
        Workbook book = Workbook.CreateEmpty();
        Worksheet sheet = book.Worksheets.Add("S");
        sheet["A1"].Value = CellValue.FromNumber(2);
        sheet["A3"].Value = CellValue.FromNumber(1);
        sheet["A4"].Value = CellValue.FromError(CellError.NotAvailable);
        sheet.Range("A1:A4").Sort(new SortField(0, Descending: true));
        Assert.Equal(CellValue.FromError(CellError.NotAvailable), sheet["A1"].Value);
        Assert.Equal(CellValue.FromNumber(2), sheet["A2"].Value);
        Assert.Equal(CellValue.FromNumber(1), sheet["A3"].Value);
        Assert.True(sheet["A4"].Value.IsEmpty);
    }

    [Fact]
    public void RefusesAKeyOutsideTheRange()
    {
        Workbook book = Workbook.CreateEmpty();
        Worksheet sheet = book.Worksheets.Add("S");
        Assert.Throws<ArgumentOutOfRangeException>(() => sheet.Range("A1:B4").Sort(new SortField(2)));
        Assert.Throws<ArgumentException>(() => sheet.Range("A1:B4").Sort());
    }

    [Fact]
    public void SortsTheGuideSample()
    {
        Workbook book = Workbook.CreateEmpty();
        Worksheet sheet = book.Worksheets.Add("Prices");
        sheet["A1"].Value = CellValue.FromText("Fruit");
        sheet["B1"].Value = CellValue.FromText("Price");
        string[] fruit = ["pear", "apple", "fig"];
        double[] prices = [3, 2, 5];
        for (int i = 0; i < 3; i++)
        {
            sheet[i + 1, 0].Value = CellValue.FromText(fruit[i]);
            sheet[i + 1, 1].Value = CellValue.FromNumber(prices[i]);
        }

        sheet.Range("A1:B4").Sort(new SortOptions { HasHeader = true }, new SortField(1, Descending: true));
        Assert.Equal(["Fruit", "fig", "pear", "apple"], [.. Enumerable.Range(0, 4).Select(r => sheet[r, 0].Value.Text)]);
    }

    /// <summary>Writes cells, notes, links and fills from the oracle's form.</summary>
    private static void Fill(Worksheet sheet, string cells)
    {
        foreach (string part in cells.Split('|'))
        {
            if (part.StartsWith('!'))
            {
                string[] note = part[1..].Split('=', 2);
                sheet.Comments.Add(note[0], note[1]);
            }
            else if (part.StartsWith('@'))
            {
                string[] link = part[1..].Split('=', 2);
                sheet.Hyperlinks.Add(RangeAddress.Parse(link[0]), link[1]);
            }
            else if (part.StartsWith('#'))
            {
                sheet[part[1..]].UpdateStyle(s => s with { Fill = Filled });
            }
            else
            {
                string[] cell = part.Split('=', 2);
                string body = cell[1][2..];
                Cell target = sheet[cell[0]];
                switch (cell[1][0])
                {
                    case 'n':
                        target.Value = CellValue.FromNumber(double.Parse(body, CultureInfo.InvariantCulture));
                        break;
                    case 's':
                        target.Value = CellValue.FromText(body);
                        break;
                    case 'b':
                        target.Value = CellValue.FromBoolean(body == "TRUE");
                        break;
                    default:
                        target.Formula = body;
                        break;
                }
            }
        }
    }

    private static List<string> Parts(string cells) => [.. cells.Split('|').Order(StringComparer.Ordinal)];

    /// <summary>The cells, notes, links and fills of the area the oracle recorded, in its form.</summary>
    private static List<string> Dump(Worksheet sheet, string expected)
    {
        int lastRow = 0, lastColumn = 0;
        foreach (string part in expected.Split('|'))
        {
            CellAddress address = CellAddress.Parse(part.TrimStart('!', '@', '#').Split('=')[0]);
            lastRow = Math.Max(lastRow, address.Row);
            lastColumn = Math.Max(lastColumn, address.Column);
        }

        List<string> parts = [];
        for (int row = 0; row <= lastRow + 2; row++)
        {
            for (int column = 0; column <= lastColumn + 1; column++)
            {
                Cell cell = sheet[row, column];
                string a = cell.Address.ToString();
                if (cell.Formula is { } formula)
                {
                    parts.Add($"{a}=f:{formula}");
                }
                else if (!cell.Value.IsEmpty)
                {
                    CellValue value = cell.Value;
                    parts.Add(a + "=" + value.Kind switch
                    {
                        CellValueKind.Number => "n:" + value.Number.ToString("R", CultureInfo.InvariantCulture),
                        CellValueKind.Boolean => value.Boolean ? "b:TRUE" : "b:FALSE",
                        _ => "s:" + value.Text,
                    });
                }

                if (sheet.Comments[cell.Address] is { } note)
                {
                    parts.Add($"!{a}={note.Text}");
                }

                if (sheet.Hyperlinks.FirstOrDefault(l => l.Range.Contains(cell.Address)) is { } link)
                {
                    parts.Add($"@{a}={link.Address}");
                }

                if (cell.Style.Fill == Filled)
                {
                    parts.Add("#" + a);
                }
            }
        }

        return [.. parts.Order(StringComparer.Ordinal)];
    }
}
