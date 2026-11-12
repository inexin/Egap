using System.Globalization;

using Egap.Spreadsheets.Printing;

namespace Egap.Spreadsheets.Tests;

/// <summary>
/// The built-in slicer styles Egap draws with against what Excel said of them: <c>Oracle/slicer-styles.excel.txt</c>, written
/// by <c>tools/excel-slicer-styles-oracle.ps1</c>.
/// </summary>
public sealed class SlicerStyleTests
{
    private static readonly string Oracle = Path.Combine(AppContext.BaseDirectory, "Oracle", "slicer-styles.excel.txt");

    [Fact]
    public void EveryStyleFormatsItsPartsAsExcelSays()
    {
        Dictionary<(string, SlicerStyleElement), PivotStyleFormat> excel = [];
        foreach (string line in File.ReadLines(Oracle).Skip(1))
        {
            string[] parts = line.Split('\t');
            SlicerStyleElement? element = parts[1] switch
            {
                "WholeTable" => SlicerStyleElement.WholeTable,
                "HeaderRow" => SlicerStyleElement.Header,
                "SelectedItemWithData" => SlicerStyleElement.SelectedItemWithData,
                "SelectedItemWithNoData" => SlicerStyleElement.SelectedItemWithNoData,
                "UnselectedItemWithData" => SlicerStyleElement.UnselectedItemWithData,
                "UnselectedItemWithNoData" => SlicerStyleElement.UnselectedItemWithNoData,
                _ => null,
            };
            if (element is { } known)
            {
                excel[(parts[0], known)] = PivotStyleFormat.Parse(Convert(parts[2]));
            }
        }

        List<(string Style, SlicerStyleElement Element, string Format)> egap = [.. SlicerStyles.Texts()];
        Assert.Equal(14 * 6, egap.Count);
        Assert.Equal(excel.Count, egap.Count);
        foreach ((string style, SlicerStyleElement element, string format) in egap)
        {
            PivotStyleFormat expected = excel[(style, element)];
            Assert.True(expected == PivotStyleFormat.Parse(format), $"{style} {element}: {format}");
            Assert.Same(SlicerStyles.Get(style)[element], SlicerStyles.Get(style.ToUpperInvariant())[element]);
        }

        Assert.Same(SlicerStyles.Get("SlicerStyleLight1"), SlicerStyles.Get(null));
        Assert.Same(SlicerStyles.Get("SlicerStyleLight1"), SlicerStyles.Get("MyStyle"));
    }

    /// <summary>
    /// The oracle's parts in the compact text: Excel's theme colour n is theme index n - 1, its RGB colours are BGR numbers,
    /// tints round to six places, and every border is thin. A fill with a pattern other than solid (Other 2's gradients) is
    /// not in the oracle.
    /// </summary>
    private static string Convert(string parts)
    {
        List<string> result = [];
        foreach (string part in parts.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == "bold")
            {
                result.Add("b");
                continue;
            }

            int equals = part.IndexOf('=', StringComparison.Ordinal);
            string key = part[..equals];
            string value = part[(equals + 1)..];
            string color = Color(key == "fill" ? value[..value.IndexOf('/', StringComparison.Ordinal)] : value[(value.LastIndexOf('/') + 1)..]);
            result.Add(key switch
            {
                "fill" => "f" + color,
                "font" => "c" + color,
                _ => key + "t" + color,
            });
        }

        return string.Join(' ', result);
    }

    private static string Color(string text)
    {
        if (text.StartsWith("rgb", StringComparison.Ordinal))
        {
            int bgr = int.Parse(text[3..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return string.Create(CultureInfo.InvariantCulture, $"#{bgr & 0xFF:X2}{(bgr >> 8) & 0xFF:X2}{(bgr >> 16) & 0xFF:X2}");
        }

        int tilde = text.IndexOf('~', StringComparison.Ordinal);
        int theme = int.Parse(text[5..(tilde < 0 ? text.Length : tilde)], CultureInfo.InvariantCulture) - 1;
        if (tilde < 0)
        {
            return theme.ToString(CultureInfo.InvariantCulture);
        }

        double tint = Math.Round(double.Parse(text[(tilde + 1)..], CultureInfo.InvariantCulture), 6);
        return string.Create(CultureInfo.InvariantCulture, $"{theme}~{tint}");
    }
}
