using System.Globalization;
using System.Text;

namespace Egap.Spreadsheets.Tests;

/// <summary>Generated format codes over awkward values: parsing and formatting may give odd text but never throw.</summary>
public sealed class NumberFormatRobustnessTests
{
    private static readonly string[] Pieces =
    [
        "0", "0", "#", "#", "?", "?", ".", ",", "%", "E+", "E-", "e+", "/", "@", "\"x\"", "\\-", "_)", "*-", " ", "-", "(",
        "[Red]", "[Color12]", "[>100]", "[<=-5]", "[=0]", "[$-414]", "[$€-2]", "[$-F800]", "[$-F400]", "[$-1]", "[$kr-414]",
        "[h]", "[mm]", "[ss]", "y", "yy", "yyyy", "e", "m", "mm", "mmm", "mmmm", "mmmmm", "d", "dd", "ddd", "dddd", "h", "hh",
        "s", "ss", ".0", ".000", "AM/PM", "A/P", ";", ";", "General", "1", "2", "8", "10", "100", "0/", "B2", "[", "]", "\"",
        "\\", "_", "*",
    ];

    private static readonly double[] Values =
    [
        0, 1, -1, 0.5, -0.5, 1.0 / 3, 2.675, 1e-300, -1e-300, double.Epsilon, 1e15, 1e300, -1e300, double.MaxValue,
        -double.MaxValue, 45366.75, 2958465.99999999, 2958466, 123456789.123, 1e-9, 0.999999999, 59.9999999,
    ];

    private static readonly CultureInfo[] Cultures =
        [CultureInfo.InvariantCulture, CultureInfo.GetCultureInfo("de-DE"), CultureInfo.GetCultureInfo("ar-SA"), CultureInfo.GetCultureInfo("ja-JP")];

    [Fact]
    public void GeneratedCodesNeverThrow()
    {
        Random random = new(20261104);
        List<string> failures = [];
        for (int n = 0; n < 4000; n++)
        {
            StringBuilder code = new();
            int count = random.Next(1, 14);
            for (int i = 0; i < count; i++)
            {
                code.Append(Pieces[random.Next(Pieces.Length)]);
            }

            Check(code.ToString(), Cultures[n % Cultures.Length], n % 2 == 0, failures);
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void RandomTextNeverThrows()
    {
        Random random = new(4);
        List<string> failures = [];
        for (int n = 0; n < 3000; n++)
        {
            char[] code = new char[random.Next(0, 24)];
            for (int i = 0; i < code.Length; i++)
            {
                code[i] = (char)random.Next(32, 127);
            }

            Check(new string(code), Cultures[n % Cultures.Length], n % 3 == 0, failures);
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    private static void Check(string code, CultureInfo culture, bool date1904, List<string> failures)
    {
        try
        {
            NumberFormatCode format = NumberFormatCode.Parse(code);
            foreach (double value in Values)
            {
                Assert.NotNull(format.Format(value, culture, date1904));
                _ = format.GetColor(value);
            }

            Assert.NotNull(format.Format("text", culture));
            Assert.NotNull(format.Format(true, culture));
        }
        catch (Exception exception) when (exception is not Xunit.Sdk.XunitException)
        {
            // One failure for each place that throws, so a run lists every bug once.
            string site = exception.GetType().Name + " " + exception.StackTrace?.Split('\n').FirstOrDefault(line => line.Contains("Egap.Spreadsheets.", StringComparison.Ordinal))?.Trim();
            if (failures.Count < 30 && !failures.Any(f => f.StartsWith(site, StringComparison.Ordinal)))
            {
                failures.Add($"{site} for {code} ({culture.Name}, 1904 {date1904}): {exception.Message}");
            }
        }
    }
}
