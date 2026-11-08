using System.Globalization;

namespace Egap.Spreadsheets.Tests;

/// <summary>
/// Find and replace against Excel: <c>tools/excel-find-oracle.ps1</c> recorded in <c>Oracle/find.excel.tsv</c> the cells Excel
/// found for each search, and each cell's formula after each replace, on the sheet <see cref="Fill"/> makes.
/// </summary>
public sealed class FindTests
{
    private static readonly string[] Lines = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Oracle", "find.excel.tsv"));

    private static readonly CultureInfo Locale = CultureInfo.GetCultureInfo(Lines[0].Split(' ')[^1]);

    public static TheoryData<int> Finds() => Indexes("find ");

    public static TheoryData<int> Replaces() => Indexes("replace ");

    private static TheoryData<int> Indexes(string prefix)
    {
        TheoryData<int> cases = [];
        for (int i = 1; i < Lines.Length; i++)
        {
            if (Lines[i].StartsWith(prefix, StringComparison.Ordinal))
            {
                cases.Add(i);
            }
        }

        return cases;
    }

    [Theory]
    [MemberData(nameof(Finds))]
    public void FindsTheCellsExcelFinds(int line)
    {
        string[] fields = Lines[line].Split('\t');
        string[] search = fields[0]["find ".Length..].Split('|');
        FindOptions options = new()
        {
            LookIn = search[1] switch { "-4163" => FindLookIn.Values, "-4144" => FindLookIn.Notes, _ => FindLookIn.Formulas },
            MatchEntireCell = search[2] == "1",
            Order = search[3] == "2" ? FindOrder.ByColumns : FindOrder.ByRows,
            MatchCase = search[4] == "True",
        };
        Worksheet sheet = Fill();
        Assert.Equal(fields[1], string.Join(',', sheet.Find(search[0], options).Select(c => c.Address.ToString())));
    }

    [Theory]
    [MemberData(nameof(Replaces))]
    public void ReplacesAsExcelDoes(int line)
    {
        string[] fields = Lines[line].Split('\t');
        string[] replace = fields[0]["replace ".Length..].Split('|');
        FindOptions options = new() { MatchEntireCell = replace[2] == "1", MatchCase = replace[3] == "True" };
        Worksheet sheet = Fill();
        try
        {
            sheet.Replace(replace[0], replace[1], options);
        }
        catch (InvalidOperationException)
        {
            // Excel stops at a formula its grammar refuses, as Egap does, and keeps what it replaced before.
        }

        foreach (string cell in fields[2].Split(" ; "))
        {
            string[] parts = cell.Split('=', 2);
            if (parts[0] == "C1note")
            {
                Assert.Equal(parts[1], sheet.Comments[CellAddress.Parse("C1")]!.Text);
                continue;
            }

            Assert.True(parts[1] == Formula(sheet[parts[0]]), $"{parts[0]}: Excel {parts[1]}, Egap {Formula(sheet[parts[0]])}");
        }
    }

    [Fact]
    public void ReplacingLooksOnlyInFormulas()
    {
        Worksheet sheet = Fill();
        Assert.Throws<ArgumentException>(() => sheet.Replace("apple", "pear", new FindOptions { LookIn = FindLookIn.Values }));
        Assert.Throws<ArgumentException>(() => sheet.Find(""));
    }

    [Fact]
    public void ARangeFindsAndReplacesOnlyInItself()
    {
        Worksheet sheet = Fill();
        Assert.Equal(["A1", "A2"], sheet.Range("A1:B2").Find("apple").Select(c => c.Address.ToString()));
        Assert.Equal(1, sheet.Range("C1:C5").Replace("apple", "pear"));
        Assert.Equal("pear", sheet["C2"].Value.Text);
        Assert.Equal("apple", sheet["A2"].Value.Text);
    }

    [Fact]
    public void AReplacedNumberStaysTextInATextCell()
    {
        Workbook book = Workbook.CreateEmpty();
        Worksheet sheet = book.Worksheets.Add("S");
        sheet["A1"].Value = CellValue.FromText("x1");
        sheet["A2"].Value = CellValue.FromText("x2");
        sheet["A2"].NumberFormat = "@";
        Assert.Equal(2, sheet.Replace("x", ""));
        Assert.Equal(CellValue.FromNumber(1), sheet["A1"].Value);
        Assert.Equal(CellValue.FromText("2"), sheet["A2"].Value);
    }

    /// <summary>The sheet the oracle searched, in the oracle's locale, calculated.</summary>
    private static Worksheet Fill()
    {
        Workbook book = Workbook.CreateEmpty();
        book.Culture = Locale;
        Worksheet s = book.Worksheets.Add("S");
        s["A1"].Value = CellValue.FromText("Apple pie");
        s["A2"].Value = CellValue.FromText("apple");
        s["A3"].Value = CellValue.FromNumber(1234.5);
        s["A4"].Formula = "SUM(B1:B2)";
        s["A5"].Formula = "TRUE";
        s["A6"].Formula = "NA()";
        s["A7"].Value = CellValue.FromText("a*b");
        s["A8"].Value = CellValue.FromText("a~b");
        s["A9"].Value = CellValue.FromText("xapplex");
        s["B1"].Value = CellValue.FromNumber(10);
        s["B2"].Value = CellValue.FromNumber(20);
        s["B3"].Formula = "A2&\"x\"";
        s["B4"].Value = CellValue.FromNumber(43831);
        s["B4"].NumberFormat = "yyyy-mm-dd";
        s["B5"].Formula = "1/3";
        s["B6"].Value = CellValue.FromNumber(0.1);
        s["B7"].Formula = "\"apple\"";
        s["B8"].Value = CellValue.FromText("TRUE");
        s["C2"].Value = CellValue.FromText("apple");
        s.Comments.Add("C1", "apple note");
        book.Calculate();
        return s;
    }

    /// <summary>A cell as Excel's Range.Formula gives it: a formula with its =, a number in full, else the value's text.</summary>
    private static string Formula(Cell cell) => cell.Formula is { } formula ? "=" + formula : cell.Value.ToString();
}
