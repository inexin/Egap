using System.Globalization;
using System.Text;

namespace Egap.Spreadsheets.Tests;

/// <summary>
/// The built-in functions against Excel: every case in <c>Oracle/functions.txt</c> evaluated here and compared with what
/// Excel calculated for it (<c>Oracle/functions.excel.tsv</c>, written by <c>tools/excel-function-oracle.ps1</c>). A case
/// where Egap knowingly differs is listed with its reason in <c>Oracle/functions.accepted.txt</c>.
/// </summary>
public sealed class FunctionOracleTests
{
    private static string Folder => Path.Combine(AppContext.BaseDirectory, "Oracle");

    [Fact]
    public void FunctionsMatchExcel()
    {
        string[] expected = File.ReadAllLines(Path.Combine(Folder, "functions.excel.tsv"));
        CultureInfo culture = Culture(expected[0]);
        Worksheet sheet = Sheet(culture);
        Dictionary<string, string> accepted = Accepted();
        StringBuilder failures = new();
        HashSet<string> seen = [];
        int compared = 0;
        foreach (string line in expected.Skip(1))
        {
            string[] fields = line.Split('\t');
            int index = int.Parse(fields[0], CultureInfo.InvariantCulture);
            string kind = fields[1];
            string value = Unescape(fields[2]);
            string formula = string.Join('\t', fields.Skip(3));
            if (kind == "invalid")
            {
                continue;
            }

            CellValue actual = sheet.Evaluate(formula, new CellAddress(20 * index, 11));
            compared++;
            bool same = Same(actual, kind, value);
            if (accepted.ContainsKey(formula))
            {
                seen.Add(formula);
                if (same)
                {
                    failures.AppendLine(CultureInfo.InvariantCulture, $"{formula}: now matches Excel; take it off the accepted list");
                }
            }
            else if (!same)
            {
                failures.AppendLine(CultureInfo.InvariantCulture, $"{formula}: Excel {kind} {value}, Egap {actual.Kind} {actual}");
            }
        }

        foreach (string formula in accepted.Keys.Where(f => !seen.Contains(f)))
        {
            failures.AppendLine(CultureInfo.InvariantCulture, $"{formula}: accepted but not a case");
        }

        Assert.True(compared > 500, $"only {compared} cases");
        Assert.True(failures.Length == 0, failures.ToString());
    }

    /// <summary>
    /// Whole workbooks Excel built, calculated and saved (<c>tools/excel-workbook-oracle.ps1</c>): loaded, calculated again
    /// in full by <see cref="Workbook.CalculateFull"/>, and every formula cell compared with the result Excel saved.
    /// </summary>
    [Theory]
    [InlineData("functions.xlsx")]
    [InlineData("calculation.xlsx")]
    public void WorkbooksCalculateAsExcel(string file)
    {
        Workbook book = Workbook.Load(Path.Combine(Folder, file));
        book.Culture = Culture(File.ReadAllLines(Path.Combine(Folder, "functions.excel.tsv"))[0]);
        List<(Cell Cell, CellValue Saved)> formulas = [];
        foreach (Worksheet sheet in book.Worksheets)
        {
            if (sheet.UsedRange is { } used)
            {
                formulas.AddRange(used.UsedCells.Where(c => c.Formula is not null || c.ArrayRange is not null).Select(c => (c, c.Value)));
            }
        }

        CalculationResult result = book.CalculateFull();
        Assert.Empty(result.CircularReferences);
        Dictionary<string, string> accepted = Accepted();
        StringBuilder failures = new();
        foreach ((Cell cell, CellValue saved) in formulas)
        {
            string formula = (cell.Formula ?? cell.Worksheet[cell.ArrayRange!.Value.Address.First.ToString()].Formula ?? "").Replace("_xlfn.", "", StringComparison.Ordinal);
            if (!Same(cell.Value, saved) && !accepted.ContainsKey(formula))
            {
                failures.AppendLine(CultureInfo.InvariantCulture, $"{cell.Worksheet.Name}!{cell.Address} {formula}: Excel {saved.Kind} {saved}, Egap {cell.Value.Kind} {cell.Value}");
            }
        }

        Assert.True(formulas.Count > 50, $"only {formulas.Count} formulas");
        Assert.True(failures.Length == 0, failures.ToString());
    }

    /// <summary>
    /// DOLLAR writes what the culture says, whichever platform's data it came from: the krone before the amount, as Windows
    /// has nb-NO, or after it, as ICU on Linux has it.
    /// </summary>
    [Theory]
    [InlineData(2, 9, "kr 1 234,57", "-kr 5,00")]
    [InlineData(3, 8, "1 234,57 kr", "-5,00 kr")]
    public void DollarFollowsTheCulturesCurrencyPatterns(int positive, int negative, string expected, string expectedNegative)
    {
        CultureInfo culture = (CultureInfo)CultureInfo.GetCultureInfo("nb-NO").Clone();
        NumberFormatInfo numbers = culture.NumberFormat;
        numbers.CurrencySymbol = "kr";
        numbers.CurrencyDecimalSeparator = ",";
        numbers.CurrencyGroupSeparator = " ";
        numbers.CurrencyPositivePattern = positive;
        numbers.CurrencyNegativePattern = negative;
        Workbook book = Workbook.Create();
        book.Culture = culture;
        Worksheet sheet = book.Worksheets[0];

        Assert.Equal(expected, sheet.Evaluate("DOLLAR(1234.567)").Text);
        Assert.Equal(expectedNegative, sheet.Evaluate("DOLLAR(-5)").Text);
    }

    private static bool Same(CellValue actual, CellValue saved) => saved.Kind switch
    {
        CellValueKind.Number => actual.Kind == CellValueKind.Number && Close(actual.Number, saved.Number),
        CellValueKind.Empty => actual.IsEmpty || (actual.Kind == CellValueKind.Number && actual.Number == 0)
            || (actual.Kind == CellValueKind.Text && actual.Text.Length == 0),
        _ => actual == saved,
    };

    /// <summary>
    /// The culture Excel ran in, with the separators and currency format it used. Excel takes them from the PC's regional
    /// settings, and .NET's data for the same culture name differs between platforms (ICU on Linux puts the krone after
    /// the amount), so they are set from what Excel recorded rather than taken from .NET.
    /// </summary>
    private static CultureInfo Culture(string header)
    {
        CultureInfo culture = (CultureInfo)CultureInfo.GetCultureInfo(Field(header, "locale ", ",")).Clone();
        string decimalSeparator = Field(header, "decimal '", "'");
        string thousands = Field(header, "thousands '", "'");
        NumberFormatInfo numbers = culture.NumberFormat;
        numbers.NumberDecimalSeparator = numbers.CurrencyDecimalSeparator = decimalSeparator;
        numbers.NumberGroupSeparator = numbers.CurrencyGroupSeparator = thousands;
        numbers.CurrencySymbol = Field(header, "currency '", "'");
        bool before = bool.Parse(Field(header, "currency-before '", "'"));
        bool space = bool.Parse(Field(header, "currency-space '", "'"));
        int negative = int.Parse(Field(header, "currency-negative '", "'"), CultureInfo.InvariantCulture);
        numbers.CurrencyPositivePattern = (before, space) switch
        {
            (true, false) => 0,     // $n
            (false, false) => 1,    // n$
            (true, true) => 2,      // $ n
            (false, true) => 3,     // n $
        };

        // Excel's negative forms, 0 to 3: (sx), -sx, s-x and sx-, with the symbol where the positive form has it.
        int[] negatives = (before, space) switch
        {
            (true, false) => [0, 1, 2, 3],
            (false, false) => [4, 5, 6, 7],
            (true, true) => [14, 9, 12, 11],
            (false, true) => [15, 8, 13, 10],
        };
        numbers.CurrencyNegativePattern = negatives[negative];
        return culture;
    }

    private static string Field(string header, string marker, string end)
    {
        int start = header.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        return header[start..header.IndexOf(end, start, StringComparison.Ordinal)];
    }

    private static Worksheet Sheet(CultureInfo culture)
    {
        Workbook book = Workbook.Create();
        book.Culture = culture;
        Worksheet sheet = book.Worksheets[0];
        foreach (string raw in File.ReadAllLines(Path.Combine(Folder, "functions.txt")))
        {
            string line = raw.Trim();
            if (!line.StartsWith('@'))
            {
                continue;
            }

            int space = line.IndexOf(' ', StringComparison.Ordinal);
            string text = line[(space + 1)..];
            Cell cell = sheet[line[1..space]];
            if (text.StartsWith('"'))
            {
                cell.Value = text[1..^1];
            }
            else if (text is "TRUE" or "FALSE")
            {
                cell.Value = text == "TRUE";
            }
            else if (CellValue.TryParseError(text, out CellError error))
            {
                cell.Value = error;
            }
            else
            {
                cell.Value = double.Parse(text, CultureInfo.InvariantCulture);
            }
        }

        return sheet;
    }

    private static Dictionary<string, string> Accepted()
    {
        Dictionary<string, string> accepted = new(StringComparer.Ordinal);
        foreach (string line in File.ReadAllLines(Path.Combine(Folder, "functions.accepted.txt")))
        {
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            string[] fields = line.Split('\t');
            Assert.True(fields.Length == 2 && fields[1].Length > 0, $"accepted line without a reason: {line}");
            accepted.Add(fields[0], fields[1]);
        }

        return accepted;
    }

    private static bool Same(CellValue actual, string kind, string value) => kind switch
    {
        "number" => actual.Kind == CellValueKind.Number && Close(actual.Number, double.Parse(value, CultureInfo.InvariantCulture)),
        "text" => actual.Kind == CellValueKind.Text && actual.Text == value,
        "boolean" => actual.Kind == CellValueKind.Boolean && actual.Boolean == (value == "TRUE"),
        "error" => actual.Kind == CellValueKind.Error && CellValue.ErrorText(actual.Error) == value,
        "blank" => actual.Kind == CellValueKind.Empty || (actual.Kind == CellValueKind.Number && actual.Number == 0),
        _ => false,
    };

    /// <summary>Equal to 15 significant digits, as Excel shows and compares them.</summary>
    private static bool Close(double actual, double expected) =>
        actual == expected || Math.Abs(actual - expected) <= 1e-14 * Math.Max(Math.Abs(actual), Math.Abs(expected));

    private static string Unescape(string text) =>
        text.Replace("\t", "\t", StringComparison.Ordinal).Replace("\r", "\r", StringComparison.Ordinal)
            .Replace("\n", "\n", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);
}
