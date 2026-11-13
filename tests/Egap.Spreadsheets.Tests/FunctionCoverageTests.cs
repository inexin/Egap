using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Egap.Spreadsheets.Tests;

/// <summary>
/// Which of Excel's functions are built in, against <c>Oracle/excel-functions.txt</c>: Microsoft's list of Excel's functions,
/// each name tried in Excel, with why a function that is not built in is not. The report in
/// <c>docs/articles/guides/spreadsheet-functions.md</c> is made from them, by <c>tools/update-function-coverage.py</c>.
/// </summary>
public sealed partial class FunctionCoverageTests
{
    private static readonly (string Key, string Text)[] Reasons =
    [
        ("service", "They ask a service on the web, which a library that calculates offline has no answer from."),
        ("data", "They ask a data model, which Egap does not load."),
        ("code", "They run code from outside the workbook."),
        ("bytes", "They work in the bytes of double-byte text or on East Asian text conversions, which need the tables of those code pages."),
        ("files", "They read a file or a web page as they calculate, which a library should not do for a workbook it opens."),
        ("model", "Excel does not document how it fits their model's parameters, so Egap leaves them out rather than give other numbers."),
    ];

    private static string Folder => Path.Combine(AppContext.BaseDirectory, "Oracle");

    private static Dictionary<string, string> ExcelFunctions()
    {
        Dictionary<string, string> functions = new(StringComparer.Ordinal);
        foreach (string line in File.ReadAllLines(Path.Combine(Folder, "excel-functions.txt")))
        {
            if (line.Length > 0 && line[0] != '#')
            {
                string[] parts = line.Split('\t');
                functions.Add(parts[0], parts.Length > 1 ? parts[1] : "");
            }
        }

        return functions;
    }

    private static string Report()
    {
        Dictionary<string, string> excel = ExcelFunctions();
        HashSet<string> builtIn = new(FunctionRegistry.BuiltInFunctions, StringComparer.Ordinal);
        List<string> done = [.. excel.Keys.Where(builtIn.Contains).Order(StringComparer.Ordinal)];
        StringBuilder text = new();
        text.Append("# Spreadsheet functions\n\n");
        text.Append(CultureInfo.InvariantCulture, $"Egap.Spreadsheets has {done.Count} of the {excel.Count} functions on Microsoft's list of Excel's functions. Each is checked\n");
        text.Append("against what Excel calculates, with the cases in `tests/Egap.Spreadsheets.Tests/Oracle/functions*.txt`. The tests make\n");
        text.Append("this page, and fail when it is out of date: `python tools/update-function-coverage.py` writes it again.\n\n");
        text.Append("A file that calls a function not built in keeps the formula and the value saved with it until the cell is calculated,\n");
        text.Append("when it gives `#NAME?`. `FunctionRegistry.Add` adds one of your own.\n\n");
        text.Append(CultureInfo.InvariantCulture, $"## Built in ({done.Count})\n\n");
        Names(text, done);
        text.Append(CultureInfo.InvariantCulture, $"\n## Not planned yet ({excel.Count - done.Count})\n");
        foreach ((string key, string reason) in Reasons)
        {
            text.Append('\n').Append(reason).Append("\n\n");
            Names(text, [.. excel.Where(p => p.Value == key).Select(p => p.Key).Order(StringComparer.Ordinal)]);
        }

        return text.ToString();
    }

    private static void Names(StringBuilder text, List<string> names)
    {
        int width = 0;
        for (int i = 0; i < names.Count; i++)
        {
            string name = "`" + names[i] + "`" + (i + 1 < names.Count ? "," : "");
            if (width > 0 && width + name.Length + 1 > 110)
            {
                text.Append('\n');
                width = 0;
            }
            else if (width > 0)
            {
                text.Append(' ');
                width++;
            }

            text.Append(name);
            width += name.Length;
        }

        text.Append('\n');
    }

    [Fact]
    public void TheListSaysWhichFunctionsAreBuiltIn()
    {
        HashSet<string> builtIn = new(FunctionRegistry.BuiltInFunctions, StringComparer.Ordinal);
        Dictionary<string, string> excel = ExcelFunctions();
        Assert.DoesNotContain(builtIn, name => !excel.ContainsKey(name));
        Assert.Empty(excel.Where(p => builtIn.Contains(p.Key) && p.Value.Length > 0).Select(p => p.Key));
        Assert.Empty(excel.Where(p => !builtIn.Contains(p.Key) && !Reasons.Any(r => r.Key == p.Value)).Select(p => p.Key));
        Assert.True(builtIn.Count >= 400, $"only {builtIn.Count}");
    }

    [Fact]
    public void EveryFunctionHasACaseCheckedAgainstExcel()
    {
        StringBuilder formulas = new();
        foreach (string file in Directory.GetFiles(Folder, "functions*.excel.tsv"))
        {
            foreach (string line in File.ReadAllLines(file).Skip(1))
            {
                string[] fields = line.Split('\t');

                // A LAMBDA given to each function says only that it takes none.
                if (fields[1] != "invalid" && !fields[3].EndsWith("(LAMBDA(x,x))", StringComparison.Ordinal))
                {
                    formulas.Append(fields[3]).Append('\n');
                }
            }
        }

        string all = formulas.ToString().ToUpperInvariant();
        HashSet<string> called = new(CallRegex().Matches(all).Select(m => m.Groups[1].Value), StringComparer.Ordinal);
        Assert.DoesNotContain(FunctionRegistry.BuiltInFunctions, name => !called.Contains(name));
    }

    [Fact]
    public void TheReportIsUpToDate()
    {
        string folder = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(folder, "Egap.slnx")))
        {
            folder = Path.GetDirectoryName(folder) ?? throw new DirectoryNotFoundException("No Egap.slnx above the tests.");
        }

        string path = Path.Combine(folder, "docs", "articles", "guides", "spreadsheet-functions.md");
        string report = Report();
        if (Environment.GetEnvironmentVariable("EGAP_WRITE_FUNCTION_COVERAGE") == "1")
        {
            File.WriteAllText(path, report, new UTF8Encoding(false));
        }

        Assert.Equal(report, File.ReadAllText(path).ReplaceLineEndings("\n"));
    }

    [GeneratedRegex(@"([A-Z][A-Z0-9.]*)\(")]
    private static partial Regex CallRegex();
}
