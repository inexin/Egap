using System.Collections.Frozen;

namespace Egap.Spreadsheets.Printing;

/// <summary>The parts of a slicer a slicer style formats.</summary>
internal enum SlicerStyleElement
{
    WholeTable,
    Header,
    SelectedItemWithData,
    SelectedItemWithNoData,
    UnselectedItemWithData,
    UnselectedItemWithNoData,
}

/// <summary>
/// Excel's 14 built-in slicer styles, which ECMA-376 does not list: <c>tools/excel-slicer-styles-oracle.ps1</c> read them from
/// Excel, and <c>SlicerStyleTests</c> checks these against what it wrote. Colours are written as for
/// <see cref="PivotStyleFormat.Parse(string)"/>; every border is thin.
/// </summary>
internal static class SlicerStyles
{
    /// <summary>The style Excel gives a slicer that names none.</summary>
    public const string Default = "SlicerStyleLight1";

    private static readonly FrozenDictionary<string, IReadOnlyDictionary<SlicerStyleElement, PivotStyleFormat>> Styles = Make();

    /// <summary>The formats of a built-in slicer style by its name; the default style's for null or another name.</summary>
    public static IReadOnlyDictionary<SlicerStyleElement, PivotStyleFormat> Get(string? name) =>
        name is not null && Styles.TryGetValue(name, out IReadOnlyDictionary<SlicerStyleElement, PivotStyleFormat>? style) ? style : Styles[Default];

    /// <summary>The style's formats as text, by element, for the oracle test.</summary>
    internal static IEnumerable<(string Style, SlicerStyleElement Element, string Format)> Texts()
    {
        foreach ((string name, Dictionary<SlicerStyleElement, string> texts) in Definitions())
        {
            foreach ((SlicerStyleElement element, string text) in texts)
            {
                yield return (name, element, text);
            }
        }
    }

    private static FrozenDictionary<string, IReadOnlyDictionary<SlicerStyleElement, PivotStyleFormat>> Make() =>
        Definitions().ToFrozenDictionary(
            d => d.Name,
            d => (IReadOnlyDictionary<SlicerStyleElement, PivotStyleFormat>)d.Texts.ToFrozenDictionary(t => t.Key, t => PivotStyleFormat.Parse(t.Value)),
            StringComparer.OrdinalIgnoreCase);

    private static string Box(string color) => $"Lt{color} Tt{color} Bt{color} Rt{color}";

    private static IEnumerable<(string Name, Dictionary<SlicerStyleElement, string> Texts)> Definitions()
    {
        // Light and dark n frame in accent n (theme colour 3 + n); they differ in their buttons.
        for (int n = 1; n <= 6; n++)
        {
            int accent = 3 + n;
            yield return ($"SlicerStyleLight{n}", new()
            {
                [SlicerStyleElement.WholeTable] = $"c1 {Box(accent.ToString(System.Globalization.CultureInfo.InvariantCulture))}",
                [SlicerStyleElement.Header] = $"b c1 Bt{accent}",
                [SlicerStyleElement.UnselectedItemWithData] = $"f#FFFFFF c#000000 {Box("#CCCCCC")}",
                [SlicerStyleElement.UnselectedItemWithNoData] = $"f#FFFFFF c#828282 {Box("#E0E0E0")}",
                [SlicerStyleElement.SelectedItemWithData] = $"f{accent}~0.599994 c#000000 {Box("#999999")}",
                [SlicerStyleElement.SelectedItemWithNoData] = $"f{accent}~0.799982 c#828282 {Box("#CCCCCC")}",
            });
        }

        yield return ("SlicerStyleOther1", new()
        {
            [SlicerStyleElement.WholeTable] = $"c1 {Box("0~-0.499985")}",
            [SlicerStyleElement.Header] = "b c1 Bt0~-0.349986",
            [SlicerStyleElement.UnselectedItemWithData] = $"f0 c#000000 {Box("#CCCCCC")}",
            [SlicerStyleElement.UnselectedItemWithNoData] = $"f0 c#959595 {Box("#E0E0E0")}",
            [SlicerStyleElement.SelectedItemWithData] = $"f0~-0.249977 c#000000 {Box("#999999")}",
            [SlicerStyleElement.SelectedItemWithNoData] = $"f0~-0.149998 c1 {Box("#CCCCCC")}",
        });

        // Other 2's buttons have gradient fills, which Egap leaves out.
        yield return ("SlicerStyleOther2", new()
        {
            [SlicerStyleElement.WholeTable] = $"c1 {Box("#4F81BD")}",
            [SlicerStyleElement.Header] = "b c1 Bt#4F81BD",
            [SlicerStyleElement.UnselectedItemWithData] = $"c#000000 {Box("#CCCCCC")}",
            [SlicerStyleElement.UnselectedItemWithNoData] = $"c#828282 {Box("#E0E0E0")}",
            [SlicerStyleElement.SelectedItemWithData] = $"c#000000 {Box("#999999")}",
            [SlicerStyleElement.SelectedItemWithNoData] = $"c#828282 {Box("#CCCCCC")}",
        });

        for (int n = 1; n <= 6; n++)
        {
            int accent = 3 + n;
            string a = accent.ToString(System.Globalization.CultureInfo.InvariantCulture);
            yield return ($"SlicerStyleDark{n}", new()
            {
                [SlicerStyleElement.WholeTable] = $"c1 {Box(a)}",
                [SlicerStyleElement.Header] = $"b c1 Bt{a}",
                [SlicerStyleElement.UnselectedItemWithData] = $"f#C0C0C0 c#000000 {Box("#C0C0C0")}",
                [SlicerStyleElement.UnselectedItemWithNoData] = $"f#DFDFDF c#959595 {Box("#DFDFDF")}",
                [SlicerStyleElement.SelectedItemWithData] = $"f{a} c0 {Box(a)}",
                [SlicerStyleElement.SelectedItemWithNoData] = $"f{a}~0.599994 c{a}~-0.249977 {Box(a + "~0.599994")}",
            });
        }
    }
}
