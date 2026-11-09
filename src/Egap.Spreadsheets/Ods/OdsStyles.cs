using System.Globalization;
using System.Text;
using System.Xml.Linq;

using Egap.Documents.Odf;

using static Egap.Documents.Odf.OdfNames;

namespace Egap.Spreadsheets.Ods;

/// <summary>
/// An ODS file's styles (ODF 1.3 Part 3, 16): cell styles as <see cref="CellStyle"/>s with their parents' properties, data
/// styles as Excel number format codes, and the widths, heights and visibility of column, row and table styles.
/// </summary>
internal sealed class OdsStyles
{
    private const char Quote = '"';

    private readonly Dictionary<string, XElement> cellStyles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, XElement> dataStyles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, XElement> columnStyles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, XElement> rowStyles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, XElement> tableStyles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> indexes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> formats = new(StringComparer.Ordinal);
    private readonly Workbook book;

    public OdsStyles(Workbook book, params XElement?[] roots)
    {
        this.book = book;

        // Named styles first, so that automatic styles of the same name, which content.xml uses, win.
        foreach (XElement? root in roots)
        {
            if (root is null)
            {
                continue;
            }

            foreach (XElement group in root.Elements().Where(e => e.Name.Namespace == OfficeNs && e.Name.LocalName is "styles" or "automatic-styles"))
            {
                foreach (XElement style in group.Elements())
                {
                    string? name = style.Attr(StyleNs, "name");
                    if (name is null)
                    {
                        continue;
                    }

                    if (style.Name == StyleNs + "style")
                    {
                        Dictionary<string, XElement>? family = style.Attr(StyleNs, "family") switch
                        {
                            "table-cell" => cellStyles,
                            "table-column" => columnStyles,
                            "table-row" => rowStyles,
                            "table" => tableStyles,
                            _ => null,
                        };
                        if (family is not null)
                        {
                            family[name] = style;
                        }
                    }
                    else if (style.Name.Namespace == NumberNs)
                    {
                        dataStyles[name] = style;
                    }
                }
            }
        }

        // The default cell style is the workbook's style 0, which cells and columns without a style of their own take.
        if (book.Styles.Count == 0)
        {
            if (book.Styles.Fills.Count == 0)
            {
                book.Styles.Fills.Add(CellFill.None);
                book.Styles.Fills.Add(new CellFill { Pattern = CellFillPattern.Gray125 });
            }

            book.Styles.Add(cellStyles.ContainsKey(DefaultName) ? Style(DefaultName) : CellStyle.Default);
        }
    }

    /// <summary>The name of the default cell style, which cells without a style take.</summary>
    public const string DefaultName = "Default";

    /// <summary>The workbook style index of a cell style, made on first use; 0 for the default style or an unknown name.</summary>
    public int Index(string? name)
    {
        name ??= DefaultName;
        if (indexes.TryGetValue(name, out int index))
        {
            return index;
        }

        index = name == DefaultName || !cellStyles.ContainsKey(name) ? 0 : book.Styles.Add(Style(name));
        indexes[name] = index;
        return index;
    }

    /// <summary>A column style's width in points, and whether it breaks the page before.</summary>
    public double? ColumnWidth(string? name) =>
        name is not null && columnStyles.TryGetValue(name, out XElement? style)
            ? OdfUnits.Length(style.Element(StyleNs + "table-column-properties")?.Attr(StyleNs, "column-width"))
            : null;

    /// <summary>A row style's height in points, and whether the height is the row's own rather than fitted to its text.</summary>
    public (double? Height, bool Custom) RowHeight(string? name)
    {
        if (name is null || !rowStyles.TryGetValue(name, out XElement? style) || style.Element(StyleNs + "table-row-properties") is not { } properties)
        {
            return (null, false);
        }

        return (OdfUnits.Length(properties.Attr(StyleNs, "row-height")), properties.Attr(StyleNs, "use-optimal-row-height") != "true");
    }

    /// <summary>Whether a table style hides its sheet, and its tab colour.</summary>
    public (bool Hidden, Egami.Pixels.Rgba8? Tab) Table(string? name)
    {
        if (name is null || !tableStyles.TryGetValue(name, out XElement? style) || style.Element(StyleNs + "table-properties") is not { } properties)
        {
            return (false, null);
        }

        string? tab = properties.Attributes().FirstOrDefault(a => a.Name.LocalName == "tab-color")?.Value;
        return (properties.Attr(TableNs, "display") == "false", OdfUnits.Color(tab));
    }

    /// <summary>Whether a cell style, or a parent of it, shows values with a boolean data style.</summary>
    public bool IsBoolean(string? name)
    {
        HashSet<string> seen = [];
        for (string? at = name; at is not null && seen.Add(at) && cellStyles.TryGetValue(at, out XElement? style); at = style.Attr(StyleNs, "parent-style-name"))
        {
            if (style.Attr(StyleNs, "data-style-name") is { } data)
            {
                return dataStyles.TryGetValue(data, out XElement? format) && format.Name.LocalName == "boolean-style";
            }
        }

        return false;
    }

    /// <summary>A cell style with what its parents give it.</summary>
    private CellStyle Style(string name)
    {
        List<XElement> chain = [];
        HashSet<string> seen = [];
        for (string? at = name; at is not null && seen.Add(at) && cellStyles.TryGetValue(at, out XElement? style); at = style.Attr(StyleNs, "parent-style-name"))
        {
            chain.Add(style);
        }

        // A style without a parent takes the default cell style's properties, as the file's default style element gives them.
        chain.Reverse();
        Dictionary<XName, string> text = [], cell = [], paragraph = [];
        string? dataStyle = null;
        foreach (XElement style in chain)
        {
            Merge(text, style.Element(StyleNs + "text-properties"));
            Merge(cell, style.Element(StyleNs + "table-cell-properties"));
            Merge(paragraph, style.Element(StyleNs + "paragraph-properties"));
            dataStyle = style.Attr(StyleNs, "data-style-name") ?? dataStyle;
        }

        CellStyle result = CellStyle.Default;
        result = result with { Font = Font(text, result.Font), Fill = Fill(cell), Borders = Borders(cell), Alignment = Alignment(cell, paragraph) };
        if (dataStyle is not null && NumberFormat(dataStyle) is { } code)
        {
            result = result with { NumberFormat = code };
        }

        if (cell.GetValueOrDefault(StyleNs + "cell-protect") is { } protect)
        {
            result = result with
            {
                Protection = new CellProtection
                {
                    Locked = protect.Contains("protected", StringComparison.Ordinal),
                    Hidden = protect.Contains("hidden", StringComparison.Ordinal),
                },
            };
        }

        return result;
    }

    private static void Merge(Dictionary<XName, string> into, XElement? properties)
    {
        foreach (XAttribute attribute in properties?.Attributes() ?? [])
        {
            into[attribute.Name] = attribute.Value;
        }
    }

    private static CellFont Font(Dictionary<XName, string> text, CellFont font)
    {
        // Excel's font names are 1 to 31 characters; a longer one is left for the default.
        if ((text.GetValueOrDefault(StyleNs + "font-name") ?? text.GetValueOrDefault(FoNs + "font-family"))?.Trim().Trim('\'', '"') is { Length: > 0 and <= 31 } family)
        {
            font = font with { Name = family };
        }

        if (OdfUnits.Length(text.GetValueOrDefault(FoNs + "font-size")) is double size and >= 1 and <= 409)
        {
            font = font with { Size = Math.Round(size * 2) / 2 };
        }

        string? weight = text.GetValueOrDefault(FoNs + "font-weight");
        string? underline = text.GetValueOrDefault(StyleNs + "text-underline-style");
        string? through = text.GetValueOrDefault(StyleNs + "text-line-through-style");
        string? position = text.GetValueOrDefault(StyleNs + "text-position");
        font = font with
        {
            Bold = weight is "bold" or "600" or "700" or "800" or "900",
            Italic = text.GetValueOrDefault(FoNs + "font-style") is "italic" or "oblique",
            Underline = underline is null or "none" ? CellUnderline.None
                : text.GetValueOrDefault(StyleNs + "text-underline-type") == "double" ? CellUnderline.Double : CellUnderline.Single,
            Strikethrough = through is not (null or "none"),
            VerticalPosition = position is null ? CellVerticalTextPosition.Baseline
                : position.StartsWith("super", StringComparison.Ordinal) || (position.Length > 0 && char.IsAsciiDigit(position[0])) ? CellVerticalTextPosition.Superscript
                : position.StartsWith("sub", StringComparison.Ordinal) || position.StartsWith('-') ? CellVerticalTextPosition.Subscript
                : CellVerticalTextPosition.Baseline,
        };
        if (OdfUnits.Color(text.GetValueOrDefault(FoNs + "color")) is { } color)
        {
            font = font with { Color = CellColor.FromRgb(color.R, color.G, color.B) };
        }

        return font;
    }

    private static CellFill Fill(Dictionary<XName, string> cell) =>
        OdfUnits.Color(cell.GetValueOrDefault(FoNs + "background-color")) is { } color ? CellFill.Solid(CellColor.FromRgb(color.R, color.G, color.B)) : CellFill.None;

    private static CellBorders Borders(Dictionary<XName, string> cell)
    {
        string? all = cell.GetValueOrDefault(FoNs + "border");
        CellBorder Side(string name) => Border(cell.GetValueOrDefault(FoNs + "border-" + name) ?? all);
        CellBorder up = Border(cell.GetValueOrDefault(StyleNs + "diagonal-bl-tr"));
        CellBorder down = Border(cell.GetValueOrDefault(StyleNs + "diagonal-tl-br"));
        return new CellBorders
        {
            Left = Side("left"),
            Right = Side("right"),
            Top = Side("top"),
            Bottom = Side("bottom"),
            Diagonal = up.Style != CellBorderStyle.None ? up : down,
            DiagonalUp = up.Style != CellBorderStyle.None,
            DiagonalDown = down.Style != CellBorderStyle.None,
        };
    }

    /// <summary>A border such as <c>0.74pt solid #000000</c>, by its line style and width.</summary>
    private static CellBorder Border(string? value)
    {
        if (value is null || value == "none")
        {
            return CellBorder.None;
        }

        double width = 0.75;
        string line = "solid";
        CellColor color = CellColor.Auto;
        foreach (string part in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (OdfUnits.Color(part) is { } rgb)
            {
                color = CellColor.FromRgb(rgb.R, rgb.G, rgb.B);
            }
            else if (OdfUnits.Length(part) is double length)
            {
                width = length;
            }
            else if (part.Length > 0 && char.IsAsciiLetter(part[0]))
            {
                line = part;
            }
        }

        CellBorderStyle style = line switch
        {
            "none" or "hidden" => CellBorderStyle.None,
            "double" or "double-thin" => CellBorderStyle.Double,
            "dotted" => CellBorderStyle.Dotted,
            "dashed" or "long-dash" => width > 1.5 ? CellBorderStyle.MediumDashed : CellBorderStyle.Dashed,
            "dot-dash" => width > 1.5 ? CellBorderStyle.MediumDashDot : CellBorderStyle.DashDot,
            "dot-dot-dash" => width > 1.5 ? CellBorderStyle.MediumDashDotDot : CellBorderStyle.DashDotDot,
            _ => width >= 2.25 ? CellBorderStyle.Thick : width > 1.25 ? CellBorderStyle.Medium : width < 0.5 ? CellBorderStyle.Hair : CellBorderStyle.Thin,
        };
        return style == CellBorderStyle.None ? CellBorder.None : new CellBorder(style, color);
    }

    private static CellAlignment Alignment(Dictionary<XName, string> cell, Dictionary<XName, string> paragraph)
    {
        CellAlignment alignment = CellAlignment.Default;

        // A horizontal alignment counts only when the cell does not align by its value's type.
        if (cell.GetValueOrDefault(StyleNs + "text-align-source") != "value-type")
        {
            alignment = alignment with
            {
                Horizontal = paragraph.GetValueOrDefault(FoNs + "text-align") switch
                {
                    "start" or "left" => CellHorizontalAlignment.Left,
                    "center" => CellHorizontalAlignment.Center,
                    "end" or "right" => CellHorizontalAlignment.Right,
                    "justify" => CellHorizontalAlignment.Justify,
                    _ => CellHorizontalAlignment.General,
                },
            };
        }

        alignment = alignment with
        {
            Vertical = cell.GetValueOrDefault(StyleNs + "vertical-align") switch
            {
                "top" => CellVerticalAlignment.Top,
                "middle" => CellVerticalAlignment.Center,
                _ => CellVerticalAlignment.Bottom,
            },
            WrapText = cell.GetValueOrDefault(FoNs + "wrap-option") == "wrap",
            ShrinkToFit = cell.GetValueOrDefault(StyleNs + "shrink-to-fit") == "true",
        };
        if (double.TryParse(cell.GetValueOrDefault(StyleNs + "rotation-angle")?.TrimEnd('d', 'e', 'g'), NumberStyles.Float, CultureInfo.InvariantCulture, out double angle))
        {
            int degrees = (int)Math.Round(angle) % 360;
            degrees = degrees < 0 ? degrees + 360 : degrees;
            int rotation = degrees <= 90 ? degrees : degrees >= 270 ? 90 + (360 - degrees) : 0;
            if (rotation is > 0 and <= 180)
            {
                alignment = alignment with { TextRotation = rotation };
            }
        }

        return alignment;
    }

    /// <summary>A data style as an Excel number format code; null for one that shows values as General does.</summary>
    public string? NumberFormat(string name)
    {
        if (formats.TryGetValue(name, out string? code))
        {
            return code.Length == 0 ? null : code;
        }

        code = dataStyles.TryGetValue(name, out XElement? style) ? Code(style, 0) : "";
        if (code is "General")
        {
            code = "";
        }

        formats[name] = code;
        return code.Length == 0 ? null : code;
    }

    /// <summary>A data style's format code, with the sections its maps (ODF 1.3 Part 3, 16.3) apply before its own.</summary>
    private string Code(XElement style, int depth)
    {
        string own = Section(style);
        List<(string Condition, string Code)> maps = [];
        if (depth < 4)
        {
            foreach (XElement map in style.Elements(StyleNs + "map"))
            {
                if (map.Attr(StyleNs, "condition") is { } condition && map.Attr(StyleNs, "apply-style-name") is { } applied
                    && dataStyles.TryGetValue(applied, out XElement? other))
                {
                    maps.Add((Condition(condition), Code(other, depth + 1)));
                }
            }
        }

        if (maps.Count == 0)
        {
            return own;
        }

        // A text style that maps every number to a section is a code whose last section is the text one.
        if (style.Name.LocalName == "text-style")
        {
            if (maps is [(">=0", string whole), ("<0", string negative)])
            {
                return whole == negative ? whole + ";" + own : whole + ";" + negative + ";" + whole + ";" + own;
            }

            if (maps is [(">0", string plus), ("<0", string minus), ("=0", string zero)])
            {
                return plus + ";" + minus + ";" + zero + ";" + own;
            }
        }

        // Excel's own sections: positive or zero, negative, then zero, written without conditions.
        if (maps is [(">=0", string positive)])
        {
            return positive + ";" + own;
        }

        if (maps is [(">0", string above), ("<0", string below)])
        {
            return above + ";" + below + ";" + own;
        }

        return string.Join(";", maps.Take(2).Select(m => "[" + m.Condition + "]" + m.Code)) + ";" + own;
    }

    /// <summary>A map's condition, such as <c>value()&gt;=0</c>, as an Excel section condition such as <c>&gt;=0</c>.</summary>
    private static string Condition(string condition)
    {
        string text = condition.Replace(" ", "", StringComparison.Ordinal);
        return text.StartsWith("value()", StringComparison.Ordinal) ? text["value()".Length..] : text;
    }

    /// <summary>One data style's parts in order, as an Excel section.</summary>
    private static string Section(XElement style)
    {
        StringBuilder code = new();
        if (style.Element(StyleNs + "text-properties")?.Attr(FoNs, "color") is { } colour && ColorName(colour) is { } named)
        {
            code.Append('[').Append(named).Append(']');
        }

        bool truncate = style.Attr(NumberNs, "truncate-on-overflow") != "false";
        bool percent = style.Name.LocalName == "percentage-style";
        foreach (XElement part in style.Elements())
        {
            if (part.Name.Namespace != NumberNs)
            {
                continue;
            }

            bool longForm = part.Attr(NumberNs, "style") == "long";
            switch (part.Name.LocalName)
            {
                case "number":
                    Number(code, part);
                    break;
                case "scientific-number":
                    Number(code, part);
                    code.Append("E+").Append('0', Math.Max(1, OdfUnits.Int(part.Attr(NumberNs, "min-exponent-digits")) ?? 2));
                    break;
                case "fraction":
                    int whole = OdfUnits.Int(part.Attr(NumberNs, "min-integer-digits")) ?? 0;
                    if (whole > 0 || part.Attr(NumberNs, "min-integer-digits") is not null)
                    {
                        code.Append('#').Append(whole > 0 ? new string('0', whole) : "").Append(' ');
                    }

                    code.Append('?', Math.Max(1, OdfUnits.Int(part.Attr(NumberNs, "min-numerator-digits")) ?? 1)).Append('/');
                    if (OdfUnits.Int(part.Attr(NumberNs, "denominator-value")) is int denominator and > 0)
                    {
                        code.Append(denominator.ToString(CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        code.Append('?', Math.Max(1, OdfUnits.Int(part.Attr(NumberNs, "min-denominator-digits")) ?? 1));
                    }

                    break;
                case "text":
                    Literal(code, part.Value, percent);
                    break;
                case "currency-symbol":
                    string symbol = part.Value;
                    if (symbol.Length > 0)
                    {
                        code.Append("[$").Append(symbol);
                        if (part.Attr(NumberNs, "language") is { } language)
                        {
                            try
                            {
                                string tag = part.Attr(NumberNs, "country") is { } country ? language + "-" + country : language;
                                code.Append('-').Append(CultureInfo.GetCultureInfo(tag).LCID.ToString("X", CultureInfo.InvariantCulture));
                            }
                            catch (CultureNotFoundException)
                            {
                            }
                        }

                        code.Append(']');
                    }

                    break;
                case "text-content":
                    code.Append('@');
                    break;
                case "boolean":
                    return "General";
                case "day":
                    code.Append(longForm ? "dd" : "d");
                    break;
                case "month":
                    bool textual = part.Attr(NumberNs, "textual") == "true";
                    code.Append(textual ? (longForm ? "mmmm" : "mmm") : (longForm ? "mm" : "m"));
                    break;
                case "year":
                    code.Append(longForm ? "yyyy" : "yy");
                    break;
                case "day-of-week":
                    code.Append(longForm ? "dddd" : "ddd");
                    break;
                case "era":
                    code.Append(longForm ? "ggg" : "g");
                    break;
                case "quarter":
                    Literal(code, longForm ? "Q" : "Q", false);
                    break;
                case "hours":
                    code.Append(truncate ? (longForm ? "hh" : "h") : (longForm ? "[hh]" : "[h]"));
                    break;
                case "minutes":
                    code.Append(longForm ? "mm" : "m");
                    break;
                case "seconds":
                    code.Append(longForm ? "ss" : "s");
                    if (OdfUnits.Int(part.Attr(NumberNs, "decimal-places")) is int places and > 0)
                    {
                        code.Append('.').Append('0', Math.Min(places, 3));
                    }

                    break;
                case "am-pm":
                    code.Append("AM/PM");
                    break;
            }
        }

        if (percent && !code.ToString().Contains('%', StringComparison.Ordinal))
        {
            code.Append('%');
        }

        return code.Length == 0 ? "General" : code.ToString();
    }

    private static void Number(StringBuilder code, XElement part)
    {
        int integers = OdfUnits.Int(part.Attr(NumberNs, "min-integer-digits")) ?? 1;
        int decimals = OdfUnits.Int(part.Attr(NumberNs, "decimal-places")) ?? 0;
        int minDecimals = OdfUnits.Int(part.Attr(NumberNs, "min-decimal-places")) ?? decimals;
        bool grouping = part.Attr(NumberNs, "grouping") == "true";
        string whole = integers > 0 ? new string('0', integers) : "#";
        if (grouping)
        {
            whole = whole.Length >= 4 ? whole : "#,##" + whole.PadLeft(1, '0')[..Math.Min(whole.Length, 1)];
            if (integers > 1 && integers < 4)
            {
                whole = "#," + new string('#', 3 - integers) + new string('0', integers);
            }
        }

        code.Append(whole);
        if (decimals > 0)
        {
            code.Append('.').Append('0', Math.Clamp(minDecimals, 0, decimals)).Append('#', decimals - Math.Clamp(minDecimals, 0, decimals));
        }

        if (double.TryParse(part.Attr(NumberNs, "display-factor"), NumberStyles.Float, CultureInfo.InvariantCulture, out double factor) && factor > 1)
        {
            for (double f = factor; f >= 1000; f /= 1000)
            {
                code.Append(',');
            }
        }
    }

    /// <summary>Literal text in a section: characters Excel shows as they are stay bare, the rest are quoted.</summary>
    private static void Literal(StringBuilder code, string text, bool percent)
    {
        if (text.Length == 0)
        {
            return;
        }

        if (text.All(c => c is ' ' or '-' or '(' or ')' or ':' or '/' or ',' or '$' or '+' || (percent && c == '%')))
        {
            code.Append(text);
            return;
        }

        code.Append(Quote).Append(text.Replace("\"", "\\\"", StringComparison.Ordinal)).Append(Quote);
    }

    private static string? ColorName(string colour) => colour.ToUpperInvariant() switch
    {
        "#FF0000" => "Red",
        "#0000FF" => "Blue",
        "#00FF00" => "Green",
        "#000000" => "Black",
        "#FFFFFF" => "White",
        "#FFFF00" => "Yellow",
        "#FF00FF" => "Magenta",
        "#00FFFF" => "Cyan",
        _ => null,
    };
}
