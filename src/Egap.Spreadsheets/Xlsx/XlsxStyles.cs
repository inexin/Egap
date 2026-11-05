using System.Globalization;
using System.Xml.Linq;

using Egap.Spreadsheets.Storage;

namespace Egap.Spreadsheets.Xlsx;

/// <summary>
/// Reads and writes the style part (ECMA-376 Part 1, 18.8). A loaded workbook keeps the file's indexes for fonts, fills,
/// borders and cell formats, and writes the file's own elements back for them; styles added since are written from the model.
/// The tables the model does not hold (cell style formats, named cell styles, differential formats, table styles, colours,
/// extensions) are written back as they were.
/// </summary>
internal static class XlsxStyles
{
    private static readonly XNamespace Ns = XlsxNames.Main;

    private static readonly string[] StyleSheetOrder =
        ["numFmts", "fonts", "fills", "borders", "cellStyleXfs", "cellXfs", "cellStyles", "dxfs", "tableStyles", "colors", "extLst"];

    private static readonly HashSet<string> FontChildren = ["b", "i", "strike", "u", "vertAlign", "sz", "color", "name", "family", "charset", "scheme"];
    private static readonly HashSet<string> AlignmentAttributes = ["horizontal", "vertical", "wrapText", "shrinkToFit", "indent", "textRotation", "readingOrder"];
    private static readonly HashSet<string> CellFormatAttributes =
        ["numFmtId", "fontId", "fillId", "borderId", "xfId", "applyNumberFormat", "applyFont", "applyFill", "applyBorder", "applyAlignment", "applyProtection"];

    public static void Read(StyleTable table, XDocument? document)
    {
        if (document?.Root is not { } root)
        {
            table.AddDefaults();
            return;
        }

        StyleSource source = new(document);
        table.Source = source;
        foreach (XElement numFmt in Children(root, "numFmts", "numFmt"))
        {
            int id = XlsxNames.Int((string?)numFmt.Attribute("numFmtId"), -1);
            if (id >= 0 && (string?)numFmt.Attribute("formatCode") is { Length: > 0 and <= 255 } code)
            {
                table.NumberFormats.AddRead(id, code);
            }
        }

        foreach (XElement font in Children(root, "fonts", "font"))
        {
            (CellFont value, bool exact) = ReadFont(font);
            table.Fonts.AddRead(value, exact);
            source.Fonts.Add(font);
        }

        foreach (XElement fill in Children(root, "fills", "fill"))
        {
            (CellFill value, bool exact) = ReadFill(fill);
            table.Fills.AddRead(value, exact);
            source.Fills.Add(fill);
        }

        foreach (XElement border in Children(root, "borders", "border"))
        {
            (CellBorders value, bool exact) = ReadBorders(border);
            table.Borders.AddRead(value, exact);
            source.Borders.Add(border);
        }

        foreach (XElement dxf in Children(root, "dxfs", "dxf"))
        {
            table.DifferentialStyles.Add(ReadDifferential(dxf));
        }

        if (table.Fonts.Count == 0 || table.Fills.Count == 0 || table.Borders.Count == 0)
        {
            // A style part without the tables every cell format points into cannot be kept; the model writes its own.
            table.Source = null;
            table.AddDefaults();
            return;
        }

        foreach (XElement xf in Children(root, "cellXfs", "xf"))
        {
            int font = Index(xf, "fontId", table.Fonts.Count);
            int fill = Index(xf, "fillId", table.Fills.Count);
            int border = Index(xf, "borderId", table.Borders.Count);
            int numberFormat = XlsxNames.Int((string?)xf.Attribute("numFmtId"), 0);
            string? code = table.NumberFormats.Code(numberFormat);
            bool exact = code is not null && xf.Attributes().All(a => a.IsNamespaceDeclaration || (a.Name.Namespace == XNamespace.None && CellFormatAttributes.Contains(a.Name.LocalName)))
                && XlsxNames.Int((string?)xf.Attribute("xfId"), 0) == 0;
            (CellAlignment alignment, bool alignmentExact) = ReadAlignment(xf.Element(Ns + "alignment"));
            CellProtection protection = ReadProtection(xf.Element(Ns + "protection"));
            exact &= alignmentExact && xf.Elements().All(e => e.Name == Ns + "alignment" || e.Name == Ns + "protection");
            CellStyle style = new()
            {
                Font = table.Fonts[font],
                Fill = table.Fills[fill],
                Borders = table.Borders[border],
                NumberFormat = code ?? "General",
                Alignment = alignment,
                Protection = protection,
            };
            table.AddRead(style, new CellFormatRecord(font, fill, border, numberFormat, alignment, protection), exact);
            source.CellFormats.Add(xf);
        }

        table.AddDefaults();
    }

    public static XDocument Write(StyleTable table, IReadOnlyList<XElement>? newDifferentials = null)
    {
        StyleSource? source = table.Source;
        XElement root = source?.Document.Root is { } kept ? new XElement(kept) : NewStyleSheet();

        XElement numFmts = Container(root, "numFmts");
        numFmts.RemoveNodes();
        foreach ((int id, string code) in table.NumberFormats.Custom.OrderBy(p => p.Key))
        {
            numFmts.Add(new XElement(Ns + "numFmt", new XAttribute("numFmtId", XlsxNames.Integer(id)), new XAttribute("formatCode", code)));
        }

        SetCount(numFmts);
        if (table.NumberFormats.Custom.Count == 0)
        {
            numFmts.Remove();
        }

        WriteTable(Container(root, "fonts"), table.Fonts.Count, source?.Fonts, i => WriteFont(table.Fonts[i]));
        WriteTable(Container(root, "fills"), table.Fills.Count, source?.Fills, i => WriteFill(table.Fills[i]));
        WriteTable(Container(root, "borders"), table.Borders.Count, source?.Borders, i => WriteBorders(table.Borders[i]));
        if (source is null)
        {
            Container(root, "cellStyleXfs").Add(new XElement(Ns + "xf", Attr("numFmtId", 0), Attr("fontId", 0), Attr("fillId", 0), Attr("borderId", 0)));
            SetCount(root.Element(Ns + "cellStyleXfs")!);
        }

        WriteTable(Container(root, "cellXfs"), table.Count, source?.CellFormats, i => WriteCellFormat(table.Record(i)));
        if (source is null)
        {
            XElement styles = Container(root, "cellStyles");
            styles.Add(new XElement(Ns + "cellStyle", new XAttribute("name", "Normal"), Attr("xfId", 0), Attr("builtinId", 0)));
            SetCount(styles);
            XElement tableStyles = Container(root, "tableStyles");
            tableStyles.Add(Attr("count", 0), new XAttribute("defaultTableStyle", "TableStyleMedium2"), new XAttribute("defaultPivotStyle", "PivotStyleLight16"));
        }

        if (source is null || newDifferentials is { Count: > 0 })
        {
            XElement dxfs = Container(root, "dxfs");
            dxfs.Add(newDifferentials ?? []);
            SetCount(dxfs);
        }

        return new XDocument(root);
    }

    /// <summary>
    /// A differential format (18.8.14) as a style: the font's bold, italic, underline, strikethrough and colour, the
    /// number format, the fill and the borders; what else it sets is left to the file's element.
    /// </summary>
    internal static CellStyle ReadDifferential(XElement dxf)
    {
        CellStyle style = CellStyle.Default;
        if (dxf.Element(Ns + "font") is { } font)
        {
            bool Flag(string name) => font.Element(Ns + name) is { } flag && XlsxNames.Bool((string?)flag.Attribute("val"), true);
            CellUnderline underline = CellUnderline.None;
            if (font.Element(Ns + "u") is { } u && !TryEnum((string?)u.Attribute("val") ?? "single", out underline))
            {
                underline = CellUnderline.Single;
            }

            style = style with
            {
                Font = CellFont.Default with
                {
                    Bold = Flag("b"),
                    Italic = Flag("i"),
                    Strikethrough = Flag("strike"),
                    Underline = underline,
                    Color = font.Element(Ns + "color") is { } color ? ReadColor(color).Color : CellFont.Default.Color,
                },
            };
        }

        if ((string?)dxf.Element(Ns + "numFmt")?.Attribute("formatCode") is { Length: > 0 and <= 255 } code)
        {
            style = style with { NumberFormat = code };
        }

        if (dxf.Element(Ns + "fill")?.Element(Ns + "patternFill") is { } pattern)
        {
            // A differential fill's solid colour is its background colour.
            string? type = (string?)pattern.Attribute("patternType");
            CellColor background = ReadColor(pattern.Element(Ns + "bgColor")).Color;
            CellColor foreground = ReadColor(pattern.Element(Ns + "fgColor")).Color;
            style = style with
            {
                Fill = type is null or "solid"
                    ? CellFill.Solid(pattern.Element(Ns + "bgColor") is null ? foreground : background)
                    : TryEnum(type, out CellFillPattern kind) ? new CellFill { Pattern = kind, Foreground = foreground, Background = background } : CellFill.None,
            };
        }

        if (dxf.Element(Ns + "border") is { } border)
        {
            style = style with { Borders = ReadBorders(border).Borders };
        }

        return style;
    }

    /// <summary>A style as a differential format: what differs from <see cref="CellStyle.Default"/> in the parts a dxf holds.</summary>
    internal static XElement WriteDifferential(CellStyle style, StyleTable table)
    {
        XElement dxf = new(Ns + "dxf");
        CellFont font = style.Font;
        CellFont normal = CellFont.Default;
        XElement fontElement = new(Ns + "font");
        if (font.Bold)
        {
            fontElement.Add(new XElement(Ns + "b"));
        }

        if (font.Italic)
        {
            fontElement.Add(new XElement(Ns + "i"));
        }

        if (font.Strikethrough)
        {
            fontElement.Add(new XElement(Ns + "strike"));
        }

        if (font.Underline != CellUnderline.None)
        {
            fontElement.Add(font.Underline == CellUnderline.Single ? new XElement(Ns + "u") : new XElement(Ns + "u", new XAttribute("val", Camel(font.Underline))));
        }

        if (font.Color != normal.Color)
        {
            fontElement.Add(WriteColor("color", font.Color));
        }

        if (fontElement.HasElements)
        {
            dxf.Add(fontElement);
        }

        if (style.NumberFormat != "General")
        {
            int id = table.NumberFormats.Add(style.NumberFormat);
            dxf.Add(new XElement(Ns + "numFmt", Attr("numFmtId", id), new XAttribute("formatCode", style.NumberFormat)));
        }

        if (style.Fill != CellFill.None)
        {
            XElement pattern = new(Ns + "patternFill");
            if (style.Fill.Pattern == CellFillPattern.Solid)
            {
                pattern.Add(WriteColor("bgColor", style.Fill.Foreground));
            }
            else
            {
                pattern.Add(new XAttribute("patternType", Camel(style.Fill.Pattern)), WriteColor("fgColor", style.Fill.Foreground), WriteColor("bgColor", style.Fill.Background));
            }

            dxf.Add(new XElement(Ns + "fill", pattern));
        }

        if (style.Borders != CellBorders.None)
        {
            dxf.Add(WriteBorders(style.Borders));
        }

        return dxf;
    }

    private static XElement NewStyleSheet() => new(Ns + "styleSheet");

    private static IEnumerable<XElement> Children(XElement root, string container, string name) =>
        root.Element(Ns + container)?.Elements(Ns + name) ?? [];

    private static int Index(XElement xf, string attribute, int count)
    {
        int index = XlsxNames.Int((string?)xf.Attribute(attribute), 0);
        return (uint)index < (uint)count ? index : 0;
    }

    /// <summary>The style sheet's child of a name, added in its place when missing.</summary>
    private static XElement Container(XElement root, string name)
    {
        if (root.Element(Ns + name) is { } found)
        {
            return found;
        }

        XElement added = new(Ns + name);
        int rank = Array.IndexOf(StyleSheetOrder, name);
        XElement? next = root.Elements().FirstOrDefault(e => e.Name.Namespace == Ns && Array.IndexOf(StyleSheetOrder, e.Name.LocalName) > rank);
        if (next is null)
        {
            root.Add(added);
        }
        else
        {
            next.AddBeforeSelf(added);
        }

        return added;
    }

    private static void WriteTable(XElement container, int count, List<XElement>? kept, Func<int, XElement> write)
    {
        container.RemoveNodes();
        for (int i = 0; i < count; i++)
        {
            container.Add(kept is not null && i < kept.Count ? new XElement(kept[i]) : write(i));
        }

        SetCount(container);
    }

    private static void SetCount(XElement container) => container.SetAttributeValue("count", XlsxNames.Integer(container.Elements().Count()));

    private static XAttribute Attr(string name, int value) => new(name, XlsxNames.Integer(value));

    private static string Camel<TEnum>(TEnum value)
        where TEnum : struct, Enum
    {
        string name = value.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    private static bool TryEnum<TEnum>(string? text, out TEnum value)
        where TEnum : struct, Enum => Enum.TryParse(text, ignoreCase: true, out value) && Enum.IsDefined(value) && !int.TryParse(text, out _);

    // Colours (18.8.3, 18.8.19 and the like).
    internal static (CellColor Color, bool Exact) ReadColor(XElement? element)
    {
        if (element is null || XlsxNames.Bool((string?)element.Attribute("auto"), false))
        {
            return (CellColor.Auto, element is null || element.Attributes().Count() == 1);
        }

        double tint = XlsxNames.TryNumber((string?)element.Attribute("tint"), out double t) && t is >= -1 and <= 1 ? t : 0;
        if ((string?)element.Attribute("rgb") is { } rgb)
        {
            if (rgb.Length == 6)
            {
                rgb = "FF" + rgb;
            }

            return uint.TryParse(rgb, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint argb)
                ? (CellColor.FromArgb(argb, tint), true)
                : (CellColor.Auto, false);
        }

        if ((string?)element.Attribute("theme") is { } theme)
        {
            int index = XlsxNames.Int(theme, -1);
            return index is >= 0 and <= 11 ? (CellColor.FromTheme(index, tint), true) : (CellColor.Auto, false);
        }

        if ((string?)element.Attribute("indexed") is { } indexed)
        {
            int index = XlsxNames.Int(indexed, -1);
            return index is >= 0 and <= 65 ? (CellColor.FromIndex(index, tint), true) : (CellColor.Auto, false);
        }

        return (CellColor.Auto, false);
    }

    internal static XElement WriteColor(string name, CellColor color)
    {
        XElement element = new(Ns + name);
        switch (color.Kind)
        {
            case CellColorKind.Rgb:
                element.Add(new XAttribute("rgb", color.Value.ToString("X8", CultureInfo.InvariantCulture)));
                break;
            case CellColorKind.Theme:
                element.Add(new XAttribute("theme", XlsxNames.Integer((int)color.Value)));
                break;
            case CellColorKind.Indexed:
                element.Add(new XAttribute("indexed", XlsxNames.Integer((int)color.Value)));
                break;
            default:
                element.Add(new XAttribute("auto", "1"));
                break;
        }

        if (color.Tint != 0)
        {
            element.Add(new XAttribute("tint", XlsxNames.Number(color.Tint)));
        }

        return element;
    }

    // Fonts (18.8.22).
    private static (CellFont Font, bool Exact) ReadFont(XElement element)
    {
        bool exact = !element.Attributes().Any(a => !a.IsNamespaceDeclaration)
            && element.Elements().All(e => e.Name.Namespace == Ns && FontChildren.Contains(e.Name.LocalName));
        string? Val(string name) => (string?)element.Element(Ns + name)?.Attribute("val");
        bool Flag(string name) => element.Element(Ns + name) is { } flag && XlsxNames.Bool((string?)flag.Attribute("val"), true);

        string name = Val("name") ?? "";
        if (name.Length is 0 or > 31)
        {
            exact = false;
            name = "Calibri";
        }

        double size = XlsxNames.TryNumber(Val("sz"), out double sz) && sz is >= 1 and <= 409 ? sz : 11;
        exact &= XlsxNames.TryNumber(Val("sz"), out double given) && given == size;

        CellUnderline underline = CellUnderline.None;
        if (element.Element(Ns + "u") is { } u)
        {
            string? kind = (string?)u.Attribute("val") ?? "single";
            exact &= TryEnum(kind, out underline);
        }

        CellVerticalTextPosition position = CellVerticalTextPosition.Baseline;
        if (Val("vertAlign") is { } vertAlign)
        {
            exact &= TryEnum(vertAlign, out position);
        }

        CellFontScheme scheme = CellFontScheme.None;
        if (Val("scheme") is { } schemeName)
        {
            exact &= TryEnum(schemeName, out scheme);
        }

        (CellColor color, bool colorExact) = ReadColor(element.Element(Ns + "color"));
        int? charset = Val("charset") is { } cs ? XlsxNames.Int(cs, 0) : null;
        return (new CellFont
        {
            Name = name,
            Size = size,
            Bold = Flag("b"),
            Italic = Flag("i"),
            Strikethrough = Flag("strike"),
            Underline = underline,
            VerticalPosition = position,
            Color = color,
            Family = XlsxNames.Int(Val("family"), 0),
            CharacterSet = charset,
            Scheme = scheme,
        }, exact && colorExact);
    }

    private static XElement WriteFont(CellFont font)
    {
        XElement element = new(Ns + "font");
        if (font.Bold)
        {
            element.Add(new XElement(Ns + "b"));
        }

        if (font.Italic)
        {
            element.Add(new XElement(Ns + "i"));
        }

        if (font.Strikethrough)
        {
            element.Add(new XElement(Ns + "strike"));
        }

        if (font.Underline != CellUnderline.None)
        {
            element.Add(font.Underline == CellUnderline.Single ? new XElement(Ns + "u") : new XElement(Ns + "u", new XAttribute("val", Camel(font.Underline))));
        }

        if (font.VerticalPosition != CellVerticalTextPosition.Baseline)
        {
            element.Add(new XElement(Ns + "vertAlign", new XAttribute("val", Camel(font.VerticalPosition))));
        }

        element.Add(new XElement(Ns + "sz", new XAttribute("val", XlsxNames.Number(font.Size))));
        if (font.Color.Kind != CellColorKind.Auto || font.Color.Tint != 0)
        {
            element.Add(WriteColor("color", font.Color));
        }

        element.Add(new XElement(Ns + "name", new XAttribute("val", font.Name)));
        if (font.Family != 0)
        {
            element.Add(new XElement(Ns + "family", new XAttribute("val", XlsxNames.Integer(font.Family))));
        }

        if (font.CharacterSet is { } charset)
        {
            element.Add(new XElement(Ns + "charset", new XAttribute("val", XlsxNames.Integer(charset))));
        }

        if (font.Scheme != CellFontScheme.None)
        {
            element.Add(new XElement(Ns + "scheme", new XAttribute("val", Camel(font.Scheme))));
        }

        return element;
    }

    // Fills (18.8.20).
    private static (CellFill Fill, bool Exact) ReadFill(XElement element)
    {
        if (element.Element(Ns + "patternFill") is not { } pattern || element.Elements().Count() != 1)
        {
            return (CellFill.None, false);
        }

        bool exact = true;
        CellFillPattern kind = CellFillPattern.None;
        if ((string?)pattern.Attribute("patternType") is { } type)
        {
            exact &= TryEnum(type, out kind);
        }

        (CellColor foreground, bool fgExact) = ReadColor(pattern.Element(Ns + "fgColor"));
        (CellColor background, bool bgExact) = ReadColor(pattern.Element(Ns + "bgColor"));
        exact &= fgExact && bgExact && pattern.Elements().All(e => e.Name == Ns + "fgColor" || e.Name == Ns + "bgColor");
        return (new CellFill { Pattern = kind, Foreground = foreground, Background = background }, exact);
    }

    private static XElement WriteFill(CellFill fill)
    {
        XElement pattern = new(Ns + "patternFill", new XAttribute("patternType", Camel(fill.Pattern)));
        if (fill.Foreground.Kind != CellColorKind.Auto || fill.Foreground.Tint != 0)
        {
            pattern.Add(WriteColor("fgColor", fill.Foreground));
        }

        if (fill.Background.Kind != CellColorKind.Auto || fill.Background.Tint != 0)
        {
            pattern.Add(WriteColor("bgColor", fill.Background));
        }

        return new XElement(Ns + "fill", pattern);
    }

    // Borders (18.8.4).
    private static (CellBorders Borders, bool Exact) ReadBorders(XElement element)
    {
        bool exact = element.Attributes().All(a => a.IsNamespaceDeclaration || a.Name.LocalName is "diagonalUp" or "diagonalDown");
        CellBorder Edge(string name, string alternative)
        {
            XElement? edge = element.Element(Ns + name) ?? element.Element(Ns + alternative);
            if (edge is null)
            {
                return CellBorder.None;
            }

            CellBorderStyle style = CellBorderStyle.None;
            if ((string?)edge.Attribute("style") is { } text)
            {
                exact &= TryEnum(text, out style);
            }

            (CellColor color, bool colorExact) = ReadColor(edge.Element(Ns + "color"));
            exact &= colorExact;
            return new CellBorder(style, color);
        }

        CellBorders borders = new()
        {
            Left = Edge("left", "start"),
            Right = Edge("right", "end"),
            Top = Edge("top", "top"),
            Bottom = Edge("bottom", "bottom"),
            Diagonal = Edge("diagonal", "diagonal"),
            DiagonalUp = XlsxNames.Bool((string?)element.Attribute("diagonalUp"), false),
            DiagonalDown = XlsxNames.Bool((string?)element.Attribute("diagonalDown"), false),
        };
        exact &= element.Elements().All(e => e.Name.Namespace == Ns && e.Name.LocalName is "left" or "right" or "top" or "bottom" or "diagonal");
        return (borders, exact);
    }

    private static XElement WriteBorders(CellBorders borders)
    {
        XElement element = new(Ns + "border");
        if (borders.DiagonalUp)
        {
            element.Add(new XAttribute("diagonalUp", "1"));
        }

        if (borders.DiagonalDown)
        {
            element.Add(new XAttribute("diagonalDown", "1"));
        }

        foreach ((string name, CellBorder edge) in (ReadOnlySpan<(string, CellBorder)>)[("left", borders.Left), ("right", borders.Right), ("top", borders.Top), ("bottom", borders.Bottom), ("diagonal", borders.Diagonal)])
        {
            XElement line = new(Ns + name);
            if (edge.Style != CellBorderStyle.None)
            {
                line.Add(new XAttribute("style", Camel(edge.Style)));
                if (edge.Color.Kind != CellColorKind.Auto || edge.Color.Tint != 0)
                {
                    line.Add(WriteColor("color", edge.Color));
                }
            }

            element.Add(line);
        }

        return element;
    }

    // Cell formats (18.8.45), alignment (18.8.1) and protection (18.8.33).
    private static (CellAlignment Alignment, bool Exact) ReadAlignment(XElement? element)
    {
        if (element is null)
        {
            return (CellAlignment.Default, true);
        }

        bool exact = element.Attributes().All(a => a.IsNamespaceDeclaration || (a.Name.Namespace == XNamespace.None && AlignmentAttributes.Contains(a.Name.LocalName)));
        CellHorizontalAlignment horizontal = CellHorizontalAlignment.General;
        if ((string?)element.Attribute("horizontal") is { } h)
        {
            exact &= TryEnum(h, out horizontal);
        }

        CellVerticalAlignment vertical = CellVerticalAlignment.Bottom;
        if ((string?)element.Attribute("vertical") is { } v)
        {
            exact &= TryEnum(v, out vertical);
        }

        int indent = XlsxNames.Int((string?)element.Attribute("indent"), 0);
        int rotation = XlsxNames.Int((string?)element.Attribute("textRotation"), 0);
        int order = XlsxNames.Int((string?)element.Attribute("readingOrder"), 0);
        exact &= indent is >= 0 and <= 250 && rotation is (>= 0 and <= 180) or 255 && order is >= 0 and <= 2;
        return (new CellAlignment
        {
            Horizontal = horizontal,
            Vertical = vertical,
            WrapText = XlsxNames.Bool((string?)element.Attribute("wrapText"), false),
            ShrinkToFit = XlsxNames.Bool((string?)element.Attribute("shrinkToFit"), false),
            Indent = Math.Clamp(indent, 0, 250),
            TextRotation = rotation is (>= 0 and <= 180) or 255 ? rotation : 0,
            ReadingOrder = order is >= 0 and <= 2 ? (CellReadingOrder)order : CellReadingOrder.Context,
        }, exact);
    }

    private static CellProtection ReadProtection(XElement? element) => element is null
        ? CellProtection.Default
        : new CellProtection
        {
            Locked = XlsxNames.Bool((string?)element.Attribute("locked"), true),
            Hidden = XlsxNames.Bool((string?)element.Attribute("hidden"), false),
        };

    private static XElement WriteCellFormat(CellFormatRecord record)
    {
        XElement xf = new(Ns + "xf", Attr("numFmtId", record.NumberFormat), Attr("fontId", record.Font), Attr("fillId", record.Fill),
            Attr("borderId", record.Border), Attr("xfId", 0));
        if (record.NumberFormat != 0)
        {
            xf.Add(new XAttribute("applyNumberFormat", "1"));
        }

        if (record.Font != 0)
        {
            xf.Add(new XAttribute("applyFont", "1"));
        }

        if (record.Fill != 0)
        {
            xf.Add(new XAttribute("applyFill", "1"));
        }

        if (record.Border != 0)
        {
            xf.Add(new XAttribute("applyBorder", "1"));
        }

        CellAlignment a = record.Alignment;
        if (a != CellAlignment.Default)
        {
            xf.Add(new XAttribute("applyAlignment", "1"));
            XElement alignment = new(Ns + "alignment");
            if (a.Horizontal != CellHorizontalAlignment.General)
            {
                alignment.Add(new XAttribute("horizontal", Camel(a.Horizontal)));
            }

            if (a.Vertical != CellVerticalAlignment.Bottom)
            {
                alignment.Add(new XAttribute("vertical", Camel(a.Vertical)));
            }

            if (a.TextRotation != 0)
            {
                alignment.Add(Attr("textRotation", a.TextRotation));
            }

            if (a.WrapText)
            {
                alignment.Add(new XAttribute("wrapText", "1"));
            }

            if (a.Indent != 0)
            {
                alignment.Add(Attr("indent", a.Indent));
            }

            if (a.ShrinkToFit)
            {
                alignment.Add(new XAttribute("shrinkToFit", "1"));
            }

            if (a.ReadingOrder != CellReadingOrder.Context)
            {
                alignment.Add(Attr("readingOrder", (int)a.ReadingOrder));
            }

            xf.Add(alignment);
        }

        if (record.Protection != CellProtection.Default)
        {
            xf.Add(new XAttribute("applyProtection", "1"));
            XElement protection = new(Ns + "protection");
            if (!record.Protection.Locked)
            {
                protection.Add(new XAttribute("locked", "0"));
            }

            if (record.Protection.Hidden)
            {
                protection.Add(new XAttribute("hidden", "1"));
            }

            xf.Add(protection);
        }

        return xf;
    }
}
