using System.Collections.Concurrent;
using System.Globalization;

namespace Egap.Spreadsheets.Printing;

/// <summary>
/// The parts of a pivot table a pivot table style formats (ECMA-376 Part 1, 18.18.75), in the order they are put on: a later
/// one wins over an earlier one where both format a cell.
/// </summary>
internal enum PivotStyleElement
{
    WholeTable,
    PageFieldLabels,
    PageFieldValues,
    FirstColumnStripe,
    SecondColumnStripe,
    FirstRowStripe,
    SecondRowStripe,
    BlankRow,
    FirstColumn,
    HeaderRow,
    FirstHeaderCell,
    FirstSubtotalColumn,
    SecondSubtotalColumn,
    ThirdSubtotalColumn,
    FirstSubtotalRow,
    SecondSubtotalRow,
    ThirdSubtotalRow,
    FirstColumnSubheading,
    SecondColumnSubheading,
    ThirdColumnSubheading,
    FirstRowSubheading,
    SecondRowSubheading,
    ThirdRowSubheading,
    LastColumn,
    TotalRow,
}

/// <summary>
/// What a pivot table style puts on a part of the table: bold, a font colour, a solid fill and borders. An edge left null is
/// not stated; one of <see cref="CellBorderStyle.None"/> takes a line away. The inside edges go between the part's cells.
/// </summary>
internal sealed record PivotStyleFormat
{
    public bool Bold { get; init; }

    public CellColor? FontColor { get; init; }

    public CellColor? Fill { get; init; }

    public CellBorder? Left { get; init; }

    public CellBorder? Right { get; init; }

    public CellBorder? Top { get; init; }

    public CellBorder? Bottom { get; init; }

    public CellBorder? Vertical { get; init; }

    public CellBorder? Horizontal { get; init; }

    /// <summary>A format from its compact text (see <c>tools/gen-pivot-styles.py</c>).</summary>
    public static PivotStyleFormat Parse(string text)
    {
        PivotStyleFormat format = new();
        foreach (string part in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part[0])
            {
                case 'b':
                    format = format with { Bold = true };
                    break;
                case 'c':
                    format = format with { FontColor = Color(part[1..]) };
                    break;
                case 'f':
                    format = format with { Fill = Color(part[1..]) };
                    break;
                default:
                    CellBorderStyle style = part[1] switch
                    {
                        't' => CellBorderStyle.Thin,
                        'm' => CellBorderStyle.Medium,
                        'd' => CellBorderStyle.Double,
                        _ => CellBorderStyle.None,
                    };
                    CellBorder border = style == CellBorderStyle.None ? CellBorder.None : new CellBorder(style, Color(part[2..]));
                    format = part[0] switch
                    {
                        'L' => format with { Left = border },
                        'R' => format with { Right = border },
                        'T' => format with { Top = border },
                        'B' => format with { Bottom = border },
                        'V' => format with { Vertical = border },
                        _ => format with { Horizontal = border },
                    };
                    break;
            }
        }

        return format;
    }

    private static CellColor Color(string text)
    {
        if (text.StartsWith('#'))
        {
            uint rgb = uint.Parse(text.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return CellColor.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        }

        int tilde = text.IndexOf('~', StringComparison.Ordinal);
        int theme = int.Parse(tilde < 0 ? text : text[..tilde], CultureInfo.InvariantCulture);
        double tint = tilde < 0 ? 0 : double.Parse(text[(tilde + 1)..], CultureInfo.InvariantCulture);
        return CellColor.FromTheme(theme, tint);
    }
}

/// <summary>Excel's built-in pivot table styles, from ECMA-376's preset table styles.</summary>
internal static partial class PivotStyles
{
    private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<PivotStyleElement, PivotStyleFormat>?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The formats of a built-in pivot style by its name, such as <c>PivotStyleLight16</c>; null for another name.</summary>
    public static IReadOnlyDictionary<PivotStyleElement, PivotStyleFormat>? Get(string name) => Cache.GetOrAdd(name, Load);

    private static Dictionary<PivotStyleElement, PivotStyleFormat>? Load(string name)
    {
        foreach ((string preset, string elements) in Presets)
        {
            if (!preset.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Dictionary<PivotStyleElement, PivotStyleFormat> style = [];
            foreach (string entry in elements.Split(','))
            {
                int space = entry.IndexOf(' ', StringComparison.Ordinal);
                if (Enum.TryParse(entry[..space], ignoreCase: true, out PivotStyleElement element))
                {
                    style[element] = PivotStyleFormat.Parse(Formats[int.Parse(entry[(space + 1)..], CultureInfo.InvariantCulture)]);
                }
            }

            return style;
        }

        return null;
    }
}
