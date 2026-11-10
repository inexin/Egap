using System.Globalization;

using Egap.Spreadsheets.Formulas;

namespace Egap.Spreadsheets.Tests;

/// <summary>
/// Conditional formatting and data validation against Excel: <c>tools/excel-conditional-format-oracle.ps1</c> made the rules of
/// <c>Oracle/conditional-formats.excel.xlsx</c> in Excel and recorded in <c>Oracle/conditional-formats.excel.tsv</c> the fill,
/// font and text Excel shows in each cell, the icon each icon set gives it, and whether each value passes its validation.
/// </summary>
public sealed class ConditionalFormatEvaluationTests
{
    private static readonly string[] Lines = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Oracle", "conditional-formats.excel.tsv"));

    private static readonly string Book = Path.Combine(AppContext.BaseDirectory, "Oracle", "conditional-formats.excel.xlsx");

    private static readonly string[][] Excel = [.. Lines.Where(line => !line.StartsWith('#')).Select(line => line.Split('\t'))];

    private static readonly string[] Periods = ["today", "yesterday", "last 7 days", "this week", "last week", "last month", "tomorrow", "next week", "next month", "this month"];

    /// <summary>The day Excel made the oracle on: the time period rules are checked with the clock pinned to it.</summary>
    private static readonly DateTime ExcelToday = ExcelValues.ToDateTime(int.Parse(Lines[0].Split("today ")[1].Split(',')[0], CultureInfo.InvariantCulture), false)!.Value;

    /// <summary>Days around which weeks, months and years turn: a Monday, a Sunday, the 1st, the 31st, a leap day, a new year.</summary>
    public static TheoryData<string> Days() => ["2026-11-09", "2026-11-15", "2026-11-01", "2026-12-01", "2026-10-31", "2026-12-31", "2027-01-01", "2024-02-29", "2026-03-01"];

    public static TheoryData<string> Formats() => [.. Excel.Where(line => line[0] is "F" or "I").Select(line => line[1]).Distinct()];

    public static TheoryData<string> Validations() => [.. Excel.Where(line => line[0] == "V").Select(line => line[1]).Distinct()];

    [Theory]
    [MemberData(nameof(Formats))]
    public void FormatsAsExcelDoes(string label)
    {
        using IDisposable clock = Clock.Pin(ExcelToday);
        Workbook book = Load();
        Worksheet sheet = book.Worksheets["Rules"];
        int column = Column(sheet, label);
        ConditionalFormatEvaluation evaluation = sheet.ConditionalFormats.Evaluate();
        List<string> misses = [];
        foreach (string[] line in Excel.Where(line => line[1] == label))
        {
            int row = int.Parse(line[2], CultureInfo.InvariantCulture) - 1;
            ConditionalCellFormat format = evaluation[row, column];
            string cell = new CellAddress(row, column).ToString();
            if (line[0] == "I")
            {
                int egap = format.Icon is { } icon ? icon.Index + 1 : 0;
                if (egap.ToString(CultureInfo.InvariantCulture) != line[3])
                {
                    misses.Add($"{cell}: Excel icon {line[3]}, Egap {egap}");
                }

                continue;
            }

            string fill = Bgr(format.Style.Fill.Pattern == CellFillPattern.Solid ? format.Style.Fill.Foreground : CellColor.White);
            string font = Bgr(format.Style.Font.Color);
            string bold = format.Style.Font.Bold ? "1" : "0", italic = format.Style.Font.Italic ? "1" : "0";
            // A colour scale's colour may be a step off in a channel: Excel mixed one of 249 channels a step short of Egap.
            bool sameFill = label.StartsWith("scale", StringComparison.Ordinal) ? Near(fill, line[3]) : fill == line[3];
            if (!sameFill || font != line[4] || bold != line[5] || italic != line[6])
            {
                misses.Add($"{cell}: Excel {line[3]} {line[4]} {line[5]} {line[6]}, Egap {fill} {font} {bold} {italic}");
            }

            if (label == "number format")
            {
                string text = NumberFormatCode.Parse(format.Style.NumberFormat).Format(sheet.Cells[row, column].Value, book.Culture, book.Date1904);
                // Egap writes nb-NO's own minus sign, U+2212, where Excel writes a hyphen.
                if (text.Replace('\u2212', '-') != line[7])
                {
                    misses.Add($"{cell}: Excel shows {line[7]}, Egap {text}");
                }
            }
        }

        Assert.True(misses.Count == 0, string.Join(Environment.NewLine, misses));
    }

    [Theory]
    [MemberData(nameof(Validations))]
    public void ValidatesAsExcelDoes(string label)
    {
        using IDisposable clock = Clock.Pin(ExcelToday);
        Worksheet sheet = Load().Worksheets["Valid"];
        int column = Column(sheet, label);
        List<string> misses = [];
        foreach (string[] line in Excel.Where(line => line[0] == "V" && line[1] == label))
        {
            CellAddress cell = new(int.Parse(line[2], CultureInfo.InvariantCulture) - 1, column);
            string egap = sheet.DataValidations.IsValid(cell) ? "1" : "0";
            if (egap != line[3])
            {
                misses.Add($"{cell} ({sheet.Cells[cell.Row, cell.Column].Value}): Excel {line[3]}, Egap {egap}");
            }
        }

        Assert.True(misses.Count == 0, string.Join(Environment.NewLine, misses));
    }

    [Fact]
    public void RulesMadeInCodeApply()
    {
        Worksheet sheet = Workbook.CreateEmpty().Worksheets.Add("S");
        for (int row = 0; row < 5; row++)
        {
            sheet.Cells[row, 0].Value = CellValue.FromNumber(row * 2);
        }

        CellStyle red = CellStyle.Default with { Fill = CellFill.Solid(CellColor.FromRgb(255, 0, 0)) };
        ConditionalFormatRule rule = ConditionalFormatRule.CellIs(ComparisonOperator.GreaterThan, "5", red);
        sheet.ConditionalFormats.Add("A1:A5", rule);
        ConditionalFormatEvaluation evaluation = sheet.ConditionalFormats.Evaluate();
        Assert.Equal(CellFill.None, evaluation["A3"].Style.Fill);
        Assert.Empty(evaluation["A3"].Rules);
        Assert.Equal(red.Fill, evaluation["A4"].Style.Fill);
        Assert.Same(rule, Assert.Single(evaluation["A4"].Rules));
        Assert.Equal(red.Fill, sheet.ConditionalFormats.Evaluate(CellAddress.Parse("A5")).Style.Fill);
        Assert.Empty(evaluation["B4"].Rules);
    }

    [Fact]
    public void StopIfTrueKeepsLaterRulesOff()
    {
        Worksheet sheet = Workbook.CreateEmpty().Worksheets.Add("S");
        sheet.Cells[0, 0].Value = CellValue.FromNumber(3);
        CellStyle bold = CellStyle.Default with { Font = CellStyle.Default.Font with { Bold = true } };
        CellStyle italic = CellStyle.Default with { Font = CellStyle.Default.Font with { Italic = true } };
        ConditionalFormatRule first = ConditionalFormatRule.Expression("A1>0", bold);
        ConditionalFormatRule second = ConditionalFormatRule.Expression("A1>1", italic);
        first.Priority = 1;
        second.Priority = 2;
        sheet.ConditionalFormats.Add("A1", first, second);
        CellStyle both = sheet.ConditionalFormats.Evaluate(CellAddress.Parse("A1")).Style;
        Assert.True(both.Font.Bold && both.Font.Italic);
        first.StopIfTrue = true;
        CellStyle stopped = sheet.ConditionalFormats.Evaluate(CellAddress.Parse("A1")).Style;
        Assert.True(stopped.Font.Bold);
        Assert.False(stopped.Font.Italic);
    }

    [Fact]
    public void DataBarsAndColorScalesFollowTheValues()
    {
        Worksheet sheet = Workbook.CreateEmpty().Worksheets.Add("S");
        for (int row = 0; row < 3; row++)
        {
            sheet.Cells[row, 0].Value = CellValue.FromNumber(row * 5);
            sheet.Cells[row, 1].Value = CellValue.FromNumber(row * 5);
        }

        sheet.ConditionalFormats.Add("A1:A3", ConditionalFormatRule.DataBar(CellColor.FromRgb(0, 0, 255)));
        sheet.ConditionalFormats.Add("B1:B3", ConditionalFormatRule.ColorScale(CellColor.FromRgb(0, 0, 0), CellColor.FromRgb(200, 100, 0)));
        ConditionalFormatEvaluation evaluation = sheet.ConditionalFormats.Evaluate();
        Assert.Equal([0, 0.5, 1], [.. Enumerable.Range(0, 3).Select(row => evaluation[row, 0].DataBar!.Value.Fraction)]);
        Assert.Equal(CellColor.FromRgb(100, 50, 0), evaluation["B2"].Style.Fill.Foreground);
        Assert.True(evaluation["B2"].ShowsValue);
    }

    [Fact]
    public void ValidationFindsTheValuesThatFail()
    {
        Worksheet sheet = Workbook.CreateEmpty().Worksheets.Add("S");
        double[] values = [1, 11, 5.5, 7];
        for (int row = 0; row < values.Length; row++)
        {
            sheet.Cells[row, 0].Value = CellValue.FromNumber(values[row]);
        }

        sheet.Cells[4, 0].Value = CellValue.FromText("x");
        sheet.DataValidations.Add(RangeAddress.Parse("A1:A10"), DataValidationType.WholeNumber, "1", "10").Operator = ComparisonOperator.Between;
        Assert.Equal(["A2", "A3", "A5"], sheet.DataValidations.FindInvalid().Select(cell => cell.ToString()));
        Assert.True(sheet.DataValidations.IsValid(CellAddress.Parse("A6")));
        Assert.False(sheet.DataValidations.IsValid(CellAddress.Parse("A6"), CellValue.FromNumber(0)));
        Assert.True(sheet.DataValidations.IsValid(CellAddress.Parse("B1"), CellValue.FromText("anything")));
    }

    [Fact]
    public void CustomRulesCheckTheValueToBeEntered()
    {
        Worksheet sheet = Workbook.CreateEmpty().Worksheets.Add("S");
        sheet.Cells[0, 0].Value = CellValue.FromNumber(1);
        sheet.Cells[0, 1].Value = CellValue.FromNumber(10);
        sheet.Cells[1, 0].Formula = "A1*2";
        sheet.DataValidations.Add(RangeAddress.Parse("A1:B1"), DataValidationType.Custom, "A1>5");
        CellAddress a1 = CellAddress.Parse("A1"), b1 = CellAddress.Parse("B1");

        Assert.False(sheet.DataValidations.IsValid(a1));
        Assert.True(sheet.DataValidations.IsValid(a1, CellValue.FromNumber(10)));
        Assert.True(sheet.DataValidations.IsValid(b1));
        Assert.False(sheet.DataValidations.IsValid(b1, CellValue.FromNumber(2)));
        Assert.True(sheet.DataValidations.IsValid(b1, CellValue.FromText("x")));   // text compares above numbers, as in Excel

        // The cells hold what they held, and nothing that depends on them changed.
        Assert.Equal(CellValue.FromNumber(1), sheet.Cells[0, 0].Value);
        Assert.Equal(CellValue.FromNumber(10), sheet.Cells[0, 1].Value);
        Assert.Equal("A1*2", sheet.Cells[1, 0].Formula);
    }

    [Fact]
    public void ListsOfferTheirItems()
    {
        Workbook book = Workbook.CreateEmpty();
        Worksheet sheet = book.Worksheets.Add("S");
        sheet.Cells[0, 3].Value = CellValue.FromText("red");
        sheet.Cells[1, 3].Value = CellValue.FromText("green");
        sheet.DataValidations.AddList(RangeAddress.Parse("A1:A5"), ["one", "two"]);
        sheet.DataValidations.Add(RangeAddress.Parse("B1:B5"), DataValidationType.List, "$D$1:$D$2");
        Assert.Equal(["one", "two"], sheet.DataValidations.ListItems(CellAddress.Parse("A3")).Select(item => item.Text));
        Assert.Equal(["red", "green"], sheet.DataValidations.ListItems(CellAddress.Parse("B3")).Select(item => item.Text));
        Assert.Empty(sheet.DataValidations.ListItems(CellAddress.Parse("C3")));
        Assert.True(sheet.DataValidations.IsValid(CellAddress.Parse("B1"), CellValue.FromText("GREEN")));
        Assert.False(sheet.DataValidations.IsValid(CellAddress.Parse("A1"), CellValue.FromText("ONE")));

        // A name whose cells include a blank one lets anything through while blanks are ignored, as in Excel.
        book.Names.Add("Colours", "S!$D$1:$D$3");
        sheet.DataValidations.Add(RangeAddress.Parse("C1:C5"), DataValidationType.List, "Colours");
        Assert.True(sheet.DataValidations.IsValid(CellAddress.Parse("C1"), CellValue.FromText("blue")));
    }

    [Theory]
    [MemberData(nameof(Days))]
    public void TimePeriodsFollowTheCalendarOnAnyDay(string day)
    {
        DateTime today = DateTime.Parse(day, CultureInfo.InvariantCulture);
        using IDisposable clock = Clock.Pin(today.AddHours(15));
        // Weeks start on Sunday, as WEEKDAY counts them.
        DateTime week = today.AddDays(-(int)today.DayOfWeek);
        Dictionary<string, Func<DateTime, bool>> expected = new()
        {
            ["today"] = date => date == today,
            ["yesterday"] = date => date == today.AddDays(-1),
            ["tomorrow"] = date => date == today.AddDays(1),
            ["last 7 days"] = date => date > today.AddDays(-7) && date <= today,
            ["this week"] = date => date >= week && date < week.AddDays(7),
            ["last week"] = date => date >= week.AddDays(-7) && date < week,
            ["next week"] = date => date >= week.AddDays(7) && date < week.AddDays(14),
            ["this month"] = date => SameMonth(date, today),
            ["last month"] = date => SameMonth(date, today.AddMonths(-1)),
            ["next month"] = date => SameMonth(date, today.AddMonths(1)),
        };
        int[] offsets = [-62, -40, -32, -31, -28, -14, -13, -8, -7, -6, -1, 0, 1, 6, 7, 8, 13, 14, 31, 40];

        Workbook book = Load();
        Worksheet sheet = book.Worksheets["Rules"];
        foreach (string period in Periods)
        {
            int column = Column(sheet, period);
            for (int i = 0; i < offsets.Length; i++)
            {
                sheet.Cells[i + 1, column].Value = CellValue.FromNumber(ExcelValues.Serial(today.AddDays(offsets[i]), false)!.Value);
            }
        }

        ConditionalFormatEvaluation evaluation = sheet.ConditionalFormats.Evaluate();
        List<string> misses = [];
        foreach (string period in Periods)
        {
            int column = Column(sheet, period);
            for (int i = 0; i < offsets.Length; i++)
            {
                ConditionalCellFormat format = evaluation[i + 1, column];
                bool shown = format.Style.Fill.Pattern == CellFillPattern.Solid && Bgr(format.Style.Fill.Foreground) == "255";
                DateTime date = today.AddDays(offsets[i]);
                if (shown != expected[period](date))
                {
                    misses.Add($"{period}, {date:yyyy-MM-dd}: Egap {(shown ? "shows" : "does not show")} it");
                }
            }
        }

        Assert.True(misses.Count == 0, string.Join(Environment.NewLine, misses));
    }

    private static bool SameMonth(DateTime a, DateTime b) => a.Year == b.Year && a.Month == b.Month;

    /// <summary>The oracle's workbook, in Excel's culture.</summary>
    private static Workbook Load()
    {
        Workbook book = Workbook.Load(Book);
        book.Culture = CultureInfo.GetCultureInfo("nb-NO");
        return book;
    }

    /// <summary>The column of a label in the first row; a label with " 2" added is the column after its first.</summary>
    private static int Column(Worksheet sheet, string label)
    {
        bool second = label.EndsWith(" 2", StringComparison.Ordinal) && !label.StartsWith("scale 2", StringComparison.Ordinal);
        string find = second ? label[..^2] : label;
        for (int column = 0; column < 200; column++)
        {
            if (sheet.Cells[0, column].Value is { Kind: CellValueKind.Text } text && text.Text == find)
            {
                return second ? column + 1 : column;
            }
        }

        throw new InvalidOperationException("No column for " + label);
    }

    private static bool Near(string egap, string excel)
    {
        uint a = uint.Parse(egap, CultureInfo.InvariantCulture), b = uint.Parse(excel, CultureInfo.InvariantCulture);
        for (int shift = 0; shift < 24; shift += 8)
        {
            if (Math.Abs((int)((a >> shift) & 0xFF) - (int)((b >> shift) & 0xFF)) > 1)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A colour as Excel's BGR number: RGB colours as they are, the font's automatic and theme text colour black.</summary>
    private static string Bgr(CellColor color)
    {
        uint rgb = color.Kind == CellColorKind.Rgb ? color.Value & 0xFFFFFF : 0;
        if (color.Kind == CellColorKind.Theme && color.Value == 0)
        {
            rgb = 0xFFFFFF;
        }

        uint bgr = ((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF);
        return bgr.ToString(CultureInfo.InvariantCulture);
    }
}
