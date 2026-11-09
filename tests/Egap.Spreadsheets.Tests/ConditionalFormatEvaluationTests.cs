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

    public static TheoryData<string> Formats() => [.. Excel.Where(line => line[0] is "F" or "I").Select(line => line[1]).Distinct()];

    public static TheoryData<string> Validations() => [.. Excel.Where(line => line[0] == "V").Select(line => line[1]).Distinct()];

    [Theory]
    [MemberData(nameof(Formats))]
    public void FormatsAsExcelDoes(string label)
    {
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

    /// <summary>The oracle's workbook, with the dates of the time period rules moved by the days since Excel's today.</summary>
    private static Workbook Load()
    {
        Workbook book = Workbook.Load(Book);
        book.Culture = CultureInfo.GetCultureInfo("nb-NO");
        int today = int.Parse(Lines[0].Split("today ")[1].Split(',')[0], CultureInfo.InvariantCulture);
        double shift = ExcelValues.Serial(DateTime.Today, false)!.Value - today;
        Worksheet sheet = book.Worksheets["Rules"];
        foreach (string period in Periods)
        {
            int column = Column(sheet, period);
            for (int row = 1; row <= 20; row++)
            {
                CellValue value = sheet.Cells[row, column].Value;
                if (value.Kind == CellValueKind.Number)
                {
                    sheet.Cells[row, column].Value = CellValue.FromNumber(value.Number + shift);
                }
            }
        }

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
