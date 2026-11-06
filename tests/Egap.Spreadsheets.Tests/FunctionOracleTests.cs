using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Egap.Spreadsheets.Tests;

/// <summary>
/// The built-in functions against Excel: every case in <c>Oracle/functions.txt</c> evaluated here and compared with what
/// Excel calculated for it (<c>Oracle/functions.excel.tsv</c>, written by <c>tools/excel-function-oracle.ps1</c>). A case
/// where Egap knowingly differs is listed with its reason in <c>Oracle/functions.accepted.txt</c>. Each further set of cases,
/// <c>Oracle/functions-NAME.txt</c>, has the same files, and <c>functions-NAME.tolerance.txt</c> may give a function that
/// iterates or approximates a wider tolerance, as name, a tab, relative tolerance, a tab, and why.
/// </summary>
public sealed partial class FunctionOracleTests
{
    private static string Folder => Path.Combine(AppContext.BaseDirectory, "Oracle");

    public static TheoryData<string> CaseSets => new(Directory.GetFiles(Folder, "functions*.excel.tsv")
        .Select(f => Path.GetFileName(f)[..^".excel.tsv".Length]).Order(StringComparer.Ordinal));

    [Theory]
    [MemberData(nameof(CaseSets))]
    public void FunctionsMatchExcel(string set)
    {
        string[] expected = File.ReadAllLines(Path.Combine(Folder, set + ".excel.tsv"));
        CultureInfo culture = Culture(expected[0]);
        Worksheet sheet = Sheet(culture, set);
        Dictionary<string, string> accepted = Accepted(set);
        Dictionary<string, double> tolerances = Tolerances(set);
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
            double tolerance = Tolerance(formula, tolerances);
            bool same = Same(actual, kind, value, tolerance);
            if (accepted.ContainsKey(formula))
            {
                seen.Add(formula);

                // Only well inside the tolerance: an accepted gap near its edge may close on one platform's math library alone.
                if (Same(actual, kind, value, tolerance / 4))
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

        Assert.True(compared > 40, $"only {compared} cases");
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
        Dictionary<string, string> accepted = Accepted("functions");
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

    private static Worksheet Sheet(CultureInfo culture, string set)
    {
        Workbook book = Workbook.Create();
        book.Culture = culture;
        Worksheet sheet = book.Worksheets[0];
        foreach (string raw in File.ReadAllLines(Path.Combine(Folder, set + ".txt")))
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
            else if (text.StartsWith('='))
            {
                cell.Formula = text;
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

        book.CalculateFull();
        return sheet;
    }

    /// <summary>The wider tolerances a set gives its functions, by function name.</summary>
    private static Dictionary<string, double> Tolerances(string set)
    {
        Dictionary<string, double> tolerances = new(StringComparer.Ordinal);
        string path = Path.Combine(Folder, set + ".tolerance.txt");
        if (File.Exists(path))
        {
            foreach (string line in File.ReadAllLines(path).Where(l => l.Length > 0 && !l.StartsWith('#')))
            {
                string[] fields = line.Split('\t');
                Assert.True(fields.Length == 3 && fields[2].Length > 0, $"tolerance line without a reason: {line}");
                tolerances.Add(fields[0], double.Parse(fields[1], CultureInfo.InvariantCulture) + MathLibrarySpread);
            }
        }

        return tolerances;
    }

    /// <summary>The tolerance for a formula: the widest of the functions it calls that have one.</summary>
    private static double Tolerance(string formula, Dictionary<string, double> tolerances)
    {
        double tolerance = Standard;
        foreach ((string name, double wider) in tolerances)
        {
            if (wider > tolerance && Calls(formula, name))
            {
                tolerance = wider;
            }
        }

        return tolerance;
    }

    /// <summary>Whether the formula calls the function by its whole name: MIRR is no call of IRR.</summary>
    private static bool Calls(string formula, string name)
    {
        string call = name + "(";
        for (int at = formula.IndexOf(call, StringComparison.Ordinal); at >= 0; at = formula.IndexOf(call, at + 1, StringComparison.Ordinal))
        {
            if (at == 0 || !(char.IsLetterOrDigit(formula[at - 1]) || formula[at - 1] is '.' or '_'))
            {
                return true;
            }
        }

        return false;
    }

    private static Dictionary<string, string> Accepted(string set)
    {
        Dictionary<string, string> accepted = new(StringComparer.Ordinal);
        string path = Path.Combine(Folder, set + ".accepted.txt");
        if (!File.Exists(path))
        {
            return accepted;
        }

        foreach (string line in File.ReadAllLines(path))
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

    private static bool Same(CellValue actual, string kind, string value, double tolerance) => kind switch
    {
        "number" => actual.Kind == CellValueKind.Number && Close(actual.Number, double.Parse(value, CultureInfo.InvariantCulture), tolerance),
        "text" => actual.Kind == CellValueKind.Text && (actual.Text == value || SameComplex(actual.Text, value, tolerance)),
        "boolean" => actual.Kind == CellValueKind.Boolean && actual.Boolean == (value == "TRUE"),
        "error" => actual.Kind == CellValueKind.Error && CellValue.ErrorText(actual.Error) == value,
        "blank" => actual.Kind == CellValueKind.Empty || (actual.Kind == CellValueKind.Number && actual.Number == 0),
        _ => false,
    };

    /// <summary>
    /// Two complex numbers as text, part by part to the same tolerance as a number. Their text holds 15 digits, so a result that
    /// falls near a rounding point shows another last digit with the last bit of the platform's sine or exponential.
    /// </summary>
    private static bool SameComplex(string actual, string expected, double tolerance)
    {
        return Parts(actual) is { } a && Parts(expected) is { } b && Close(a.Real, b.Real, tolerance) && Close(a.Imaginary, b.Imaginary, tolerance);

        static (double Real, double Imaginary)? Parts(string text)
        {
            Match match = ComplexText().Match(text);
            if (!match.Success)
            {
                return null;
            }

            string imaginary = match.Groups[2].Value;
            return (Read(match.Groups[1].Value), imaginary is "+" or "" ? 1 : imaginary == "-" ? -1 : Read(imaginary));

            static double Read(string part) => part.Length == 0 ? 0 : double.Parse(part.Replace(',', '.'), CultureInfo.InvariantCulture);
        }
    }

    [GeneratedRegex(@"^([-+]?[\d.,]+(?:E[-+]?\d+)?)?([-+]?(?:[\d.,]+(?:E[-+]?\d+)?)?)[ij]$")]
    private static partial Regex ComplexText();

    /// <summary>Equal to 15 significant digits, as Excel shows and compares them.</summary>
    private static bool Close(double actual, double expected, double tolerance = Standard) =>
        actual == expected || Math.Abs(actual - expected) <= tolerance * Math.Max(Math.Abs(actual), Math.Abs(expected));

    private const double Standard = 1e-14;

    /// <summary>
    /// What a listed tolerance gets on top. A list holds the widest gap measured on Windows; .NET's Math.Exp, Log, Pow, Sinh and
    /// the like come from the platform's C library, whose results differ in the last bit or two, so a result from several
    /// such calls moves by some units in the last place (2.2e-16 each) from platform to platform.
    /// </summary>
    private const double MathLibrarySpread = 2e-15;

    private static string Unescape(string text) =>
        text.Replace("\t", "\t", StringComparison.Ordinal).Replace("\r", "\r", StringComparison.Ordinal)
            .Replace("\n", "\n", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);
}
