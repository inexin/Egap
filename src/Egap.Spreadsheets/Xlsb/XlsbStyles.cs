using System.Globalization;
using System.Xml.Linq;

using Egap.Spreadsheets.Xlsx;

namespace Egap.Spreadsheets.Xlsb;

/// <summary>
/// The style part of an XLSB file ([MS-XLSB] 2.1.7.50): its number formats, fonts, fills, borders, cell formats, cell styles,
/// differential formats and palette, turned into the styleSheet element an XLSX file holds for the same styles, which the
/// XLSX style reader then reads. Fonts also give the run properties of rich text.
/// </summary>
internal sealed class XlsbStyles
{
    private static readonly XNamespace Ns = XlsxNames.Main;

    private static readonly string[] Patterns =
    [
        "none", "solid", "mediumGray", "darkGray", "lightGray", "darkHorizontal", "darkVertical", "darkDown", "darkUp", "darkGrid",
        "darkTrellis", "lightHorizontal", "lightVertical", "lightDown", "lightUp", "lightGrid", "lightTrellis", "gray125", "gray0625",
    ];

    private static readonly string[] BorderStyles =
    [
        "none", "thin", "medium", "dashed", "dotted", "thick", "double", "hair", "mediumDashed", "dashDot", "mediumDashDot",
        "dashDotDot", "mediumDashDotDot", "slantDashDot",
    ];

    private static readonly string[] Horizontal = ["general", "left", "center", "right", "fill", "justify", "centerContinuous", "distributed"];

    private static readonly string[] Vertical = ["top", "center", "bottom", "justify", "distributed"];

    private readonly List<XElement> fonts = [];

    /// <summary>The styleSheet element; null until <see cref="Read"/>.</summary>
    public XDocument? Document { get; private set; }

    /// <summary>The workbook's default table style, which a table that names none takes.</summary>
    public string? DefaultTableStyle { get; private set; }

    /// <summary>The run properties (<c>rPr</c>) of a font, for rich text; null for a font the part lacks.</summary>
    public XElement? RunProperties(int font)
    {
        if ((uint)font >= (uint)fonts.Count)
        {
            return null;
        }

        // A run's font names its typeface with rFont where a style's font has name.
        XElement rPr = new(Ns + "rPr");
        foreach (XElement child in fonts[font].Elements())
        {
            rPr.Add(child.Name == Ns + "name" ? new XElement(Ns + "rFont", child.Attributes()) : new XElement(child));
        }

        return rPr;
    }

    public void Read(ReadOnlySpan<byte> data)
    {
        XElement numFmts = new(Ns + "numFmts");
        XElement fontList = new(Ns + "fonts");
        XElement fills = new(Ns + "fills");
        XElement borders = new(Ns + "borders");
        XElement styleXfs = new(Ns + "cellStyleXfs");
        XElement cellXfs = new(Ns + "cellXfs");
        XElement cellStyles = new(Ns + "cellStyles");
        XElement dxfs = new(Ns + "dxfs");
        XElement indexed = new(Ns + "indexedColors");
        XElement mru = new(Ns + "mruColors");
        XElement? tableStyles = null;
        bool inStyleXfs = false;
        bool inMru = false;
        XlsbRecords records = new(data);
        while (records.Next())
        {
            XlsbReader record = records.Reader;
            switch (records.Type)
            {
                case 44: // BrtFmt
                    {
                        int id = record.UInt16();
                        string code = record.WideString();
                        if (code.Length is > 0 and <= 255)
                        {
                            numFmts.Add(new XElement(Ns + "numFmt", new XAttribute("numFmtId", Integer(id)), new XAttribute("formatCode", code)));
                        }

                        break;
                    }

                case 43: // BrtFont
                    XElement font = Font(ref record);
                    fonts.Add(font);
                    fontList.Add(font);
                    break;
                case 45: // BrtFill
                    fills.Add(Fill(ref record));
                    break;
                case 46: // BrtBorder
                    borders.Add(Border(ref record));
                    break;
                case 626: // BrtBeginCellStyleXFs
                    inStyleXfs = true;
                    break;
                case 627: // BrtEndCellStyleXFs
                    inStyleXfs = false;
                    break;
                case 47: // BrtXF
                    (inStyleXfs ? styleXfs : cellXfs).Add(Xf(ref record, inStyleXfs));
                    break;
                case 48: // BrtStyle
                    cellStyles.Add(Style(ref record));
                    break;
                case 507: // BrtDXF
                    dxfs.Add(Differential(ref record));
                    break;
                case 475: // BrtIndexedColor
                    indexed.Add(new XElement(Ns + "rgbColor", new XAttribute("rgb", Rgb(record.Byte(), record.Byte(), record.Byte(), 0xFF))));
                    break;
                case 568: // BrtBeginMRUColors
                    inMru = true;
                    break;
                case 569: // BrtEndMRUColors
                    inMru = false;
                    break;
                case 564 when inMru: // BrtColor
                    mru.Add(Color("color", ref record) ?? new XElement(Ns + "color", new XAttribute("auto", "1")));
                    break;
                case 508: // BrtBeginTableStyles
                    {
                        int count = record.Int32();
                        DefaultTableStyle = record.WideString();
                        string pivot = record.WideString();
                        tableStyles = new XElement(Ns + "tableStyles", new XAttribute("count", Integer(Math.Max(count, 0))),
                            new XAttribute("defaultTableStyle", DefaultTableStyle), new XAttribute("defaultPivotStyle", pivot));
                        break;
                    }
            }
        }

        XElement root = new(Ns + "styleSheet", new XAttribute("xmlns", Ns.NamespaceName));
        foreach (XElement list in new[] { numFmts, fontList, fills, borders, styleXfs, cellXfs, cellStyles, dxfs })
        {
            if (list.HasElements || list == cellXfs)
            {
                list.Add(new XAttribute("count", Integer(list.Elements().Count())));
                root.Add(list);
            }
        }

        if (tableStyles is not null)
        {
            root.Add(tableStyles);
        }

        if (indexed.HasElements || mru.HasElements)
        {
            root.Add(new XElement(Ns + "colors", indexed.HasElements ? indexed : null, mru.HasElements ? mru : null));
        }

        Document = new XDocument(XlsbXml.Safe(root));
    }

    /// <summary>A BrtFont ([MS-XLSB] 2.4.661) as a font element, its children in the schema's order.</summary>
    private static XElement Font(ref XlsbReader record)
    {
        int height = record.UInt16();
        int flags = record.UInt16();
        int weight = record.UInt16();
        int script = record.UInt16();
        int underline = record.Byte();
        int family = record.Byte();
        int charset = record.Byte();
        record.Byte();
        XElement? color = Color("color", ref record);
        int scheme = record.Byte();
        string name = record.WideString();
        XElement font = new(Ns + "font");
        if (weight >= 700)
        {
            font.Add(new XElement(Ns + "b"));
        }

        Flag(font, "i", flags, 0x02);
        Flag(font, "strike", flags, 0x08);
        Flag(font, "condense", flags, 0x40);
        Flag(font, "extend", flags, 0x80);
        Flag(font, "outline", flags, 0x10);
        Flag(font, "shadow", flags, 0x20);
        if (UnderlineName(underline) is { } u)
        {
            font.Add(u == "single" ? new XElement(Ns + "u") : new XElement(Ns + "u", new XAttribute("val", u)));
        }

        if (script is 1 or 2)
        {
            font.Add(new XElement(Ns + "vertAlign", new XAttribute("val", script == 1 ? "superscript" : "subscript")));
        }

        font.Add(new XElement(Ns + "sz", new XAttribute("val", XlsxNames.Number(height / 20.0))));
        if (color is not null)
        {
            font.Add(color);
        }

        font.Add(new XElement(Ns + "name", new XAttribute("val", name)));
        if (family != 0)
        {
            font.Add(new XElement(Ns + "family", new XAttribute("val", Integer(family))));
        }

        if (charset != 0)
        {
            font.Add(new XElement(Ns + "charset", new XAttribute("val", Integer(charset))));
        }

        if (scheme is 1 or 2)
        {
            font.Add(new XElement(Ns + "scheme", new XAttribute("val", scheme == 1 ? "major" : "minor")));
        }

        return font;
    }

    private static void Flag(XElement font, string name, int flags, int bit)
    {
        if ((flags & bit) != 0)
        {
            font.Add(new XElement(Ns + name));
        }
    }

    private static string? UnderlineName(int underline) => underline switch
    {
        1 => "single",
        2 => "double",
        0x21 => "singleAccounting",
        0x22 => "doubleAccounting",
        _ => null,
    };

    /// <summary>A BrtFill ([MS-XLSB] 2.4.649): a pattern with its colours, or a gradient with its stops.</summary>
    private static XElement Fill(ref XlsbReader record)
    {
        int pattern = record.Int32();
        XElement? foreground = Color("fgColor", ref record, out int foregroundIndex);
        XElement? background = Color("bgColor", ref record, out int backgroundIndex);
        if (pattern == 0x28)
        {
            int type = record.Int32();
            double degree = record.Double();
            double left = record.Double(), right = record.Double(), top = record.Double(), bottom = record.Double();
            int stops = record.Int32();
            XElement gradient = new(Ns + "gradientFill");
            if (type == 1)
            {
                gradient.Add(new XAttribute("type", "path"));
            }

            if (degree != 0)
            {
                gradient.Add(new XAttribute("degree", XlsxNames.Number(degree)));
            }

            AddIf(gradient, "left", left);
            AddIf(gradient, "right", right);
            AddIf(gradient, "top", top);
            AddIf(gradient, "bottom", bottom);
            for (int i = 0; i < stops && record.Remaining >= 16; i++)
            {
                XElement? color = Color("color", ref record);
                double position = record.Double();
                gradient.Add(new XElement(Ns + "stop", new XAttribute("position", XlsxNames.Number(position)), color));
            }

            return new XElement(Ns + "fill", gradient);
        }

        XElement patternFill = new(Ns + "patternFill", new XAttribute("patternType", (uint)pattern < (uint)Patterns.Length ? Patterns[pattern] : "none"));
        if (pattern != 0)
        {
            // The defaults, system foreground and system background, are left out as Excel's XLSX leaves them out.
            if (foreground is not null && foregroundIndex != 64)
            {
                patternFill.Add(foreground);
            }

            if (background is not null && backgroundIndex != 65)
            {
                patternFill.Add(background);
            }
        }

        return new XElement(Ns + "fill", patternFill);
    }

    private static void AddIf(XElement element, string name, double value)
    {
        if (value != 0)
        {
            element.Add(new XAttribute(name, XlsxNames.Number(value)));
        }
    }

    /// <summary>A BrtBorder ([MS-XLSB] 2.4.627): top, bottom, left, right and diagonal lines, written left, right, top, bottom, diagonal.</summary>
    private static XElement Border(ref XlsbReader record)
    {
        int flags = record.Byte();
        XElement top = Line("top", ref record), bottom = Line("bottom", ref record);
        XElement left = Line("left", ref record), right = Line("right", ref record);
        XElement diagonal = Line("diagonal", ref record);
        XElement border = new(Ns + "border");
        if ((flags & 0x02) != 0)
        {
            border.Add(new XAttribute("diagonalUp", "1"));
        }

        if ((flags & 0x01) != 0)
        {
            border.Add(new XAttribute("diagonalDown", "1"));
        }

        border.Add(left, right, top, bottom, diagonal);
        return border;
    }

    private static XElement Line(string name, ref XlsbReader record)
    {
        int style = record.Byte();
        record.Byte();
        XElement? color = Color("color", ref record);
        XElement line = new(Ns + name);
        if (style is > 0 && style < BorderStyles.Length)
        {
            line.Add(new XAttribute("style", BorderStyles[style]));
            if (color is not null)
            {
                line.Add(color);
            }
        }

        return line;
    }

    /// <summary>
    /// A BrtXF ([MS-XLSB] 2.4.812). Its attribute flags say, for a cell format, which parts it applies over its cell style,
    /// and for a cell style's format, which parts the style leaves out.
    /// </summary>
    private static XElement Xf(ref XlsbReader record, bool styleFormat)
    {
        int parent = record.UInt16();
        int format = record.UInt16();
        int font = record.UInt16();
        int fill = record.UInt16();
        int border = record.UInt16();
        int rotation = record.Byte();
        int indent = record.Byte();
        int flags = record.UInt16();
        int applied = record.Byte();
        XElement xf = new(Ns + "xf", new XAttribute("numFmtId", Integer(format)), new XAttribute("fontId", Integer(font)),
            new XAttribute("fillId", Integer(fill)), new XAttribute("borderId", Integer(border)));
        if (!styleFormat)
        {
            xf.Add(new XAttribute("xfId", Integer(parent == 0xFFFF ? 0 : parent)));
        }

        string[] names = ["applyNumberFormat", "applyFont", "applyAlignment", "applyBorder", "applyFill", "applyProtection"];
        XAttribute?[] apply = new XAttribute?[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            if ((applied & (1 << i)) != 0)
            {
                apply[i] = new XAttribute(names[i], styleFormat ? "0" : "1");
            }
        }

        // The schema's order: number format, font, fill, border, alignment, protection.
        xf.Add(apply[0], apply[1], apply[4], apply[3], apply[2], apply[5]);
        XElement alignment = new(Ns + "alignment");
        int horizontal = flags & 0x07;
        int vertical = (flags >> 3) & 0x07;
        if (horizontal != 0)
        {
            alignment.Add(new XAttribute("horizontal", Horizontal[horizontal]));
        }

        if (vertical != 2 && vertical < Vertical.Length)
        {
            alignment.Add(new XAttribute("vertical", Vertical[vertical]));
        }

        if (rotation != 0)
        {
            alignment.Add(new XAttribute("textRotation", Integer(rotation)));
        }

        if ((flags & 0x40) != 0)
        {
            alignment.Add(new XAttribute("wrapText", "1"));
        }

        if (indent != 0)
        {
            alignment.Add(new XAttribute("indent", Integer(indent)));
        }

        if ((flags & 0x80) != 0)
        {
            alignment.Add(new XAttribute("justifyLastLine", "1"));
        }

        if ((flags & 0x100) != 0)
        {
            alignment.Add(new XAttribute("shrinkToFit", "1"));
        }

        int order = (flags >> 10) & 0x03;
        if (order != 0)
        {
            alignment.Add(new XAttribute("readingOrder", Integer(order)));
        }

        if (alignment.HasAttributes)
        {
            xf.Add(alignment);
        }

        XElement protection = new(Ns + "protection");
        if ((flags & 0x1000) == 0)
        {
            protection.Add(new XAttribute("locked", "0"));
        }

        if ((flags & 0x2000) != 0)
        {
            protection.Add(new XAttribute("hidden", "1"));
        }

        if (protection.HasAttributes)
        {
            xf.Add(protection);
        }

        return xf;
    }

    /// <summary>A BrtStyle ([MS-XLSB] 2.4.759): a named cell style and its format.</summary>
    private static XElement Style(ref XlsbReader record)
    {
        uint xf = record.UInt32();
        int flags = record.UInt16();
        int builtIn = record.Byte();
        int level = record.Byte();
        string name = record.WideString();
        XElement style = new(Ns + "cellStyle", new XAttribute("name", name), new XAttribute("xfId", Integer((int)Math.Min(xf, int.MaxValue))));
        if ((flags & 0x01) != 0)
        {
            style.Add(new XAttribute("builtinId", Integer(builtIn)));
            if (builtIn is 1 or 2)
            {
                style.Add(new XAttribute("iLevel", Integer(level)));
            }
        }

        if ((flags & 0x02) != 0)
        {
            style.Add(new XAttribute("hidden", "1"));
        }

        if ((flags & 0x04) != 0 && (flags & 0x01) != 0)
        {
            style.Add(new XAttribute("customBuiltin", "1"));
        }

        return style;
    }

    /// <summary>A BrtDXF ([MS-XLSB] 2.4.640): the properties it sets ([MS-XLSB] 2.5.161 XFProp), as a dxf element.</summary>
    private static XElement Differential(ref XlsbReader record)
    {
        record.UInt16();
        record.UInt16();
        int count = record.UInt16();
        List<XElement> properties = [];
        XElement font = new(Ns + "font");
        XElement? numFmt = null;
        XElement? patternFill = null;
        XElement alignment = new(Ns + "alignment");
        XElement[] lines = new XElement[5];
        XElement? border = null;
        XElement protection = new(Ns + "protection");
        string? fontName = null;
        XElement? fontColor = null;
        double? size = null;
        int? underline = null, script = null, family = null, charset = null, scheme = null;
        bool? bold = null, italic = null, strike = null, outline = null, shadow = null, condense = null, extend = null;
        for (int i = 0; i < count && record.Remaining >= 4; i++)
        {
            int type = record.UInt16();
            int size4 = record.UInt16();
            if (size4 < 4 || size4 - 4 > record.Remaining)
            {
                break;
            }

            XlsbReader data = new(record.Bytes(size4 - 4));
            switch (type)
            {
                case 0x00:
                    patternFill ??= new XElement(Ns + "patternFill");
                    int pattern = data.Byte();
                    patternFill.Add(new XAttribute("patternType", pattern < Patterns.Length ? Patterns[pattern] : "none"));
                    break;
                case 0x01:
                    (patternFill ??= new XElement(Ns + "patternFill")).Add(Color("fgColor", ref data));
                    break;
                case 0x02:
                    (patternFill ??= new XElement(Ns + "patternFill")).Add(Color("bgColor", ref data));
                    break;
                case 0x05:
                    fontColor = Color("color", ref data);
                    break;
                case >= 0x06 and <= 0x0A:
                    {
                        XElement? color = Color("color", ref data);
                        int style = data.UInt16();
                        string name = type switch { 0x06 => "top", 0x07 => "bottom", 0x08 => "left", 0x09 => "right", _ => "diagonal" };
                        XElement line = new(Ns + name);
                        if (style > 0 && style < BorderStyles.Length)
                        {
                            line.Add(new XAttribute("style", BorderStyles[style]), color);
                        }

                        lines[type - 0x06] = line;
                        border ??= new XElement(Ns + "border");
                        break;
                    }

                case 0x0D:
                    (border ??= new XElement(Ns + "border")).Add(new XAttribute("diagonalUp", data.Byte() != 0 ? "1" : "0"));
                    break;
                case 0x0E:
                    (border ??= new XElement(Ns + "border")).Add(new XAttribute("diagonalDown", data.Byte() != 0 ? "1" : "0"));
                    break;
                case 0x0F:
                    {
                        int value = data.Byte();
                        alignment.Add(new XAttribute("horizontal", value < Horizontal.Length ? Horizontal[value] : "general"));
                        break;
                    }

                case 0x10:
                    {
                        int value = data.Byte();
                        alignment.Add(new XAttribute("vertical", value < Vertical.Length ? Vertical[value] : "bottom"));
                        break;
                    }

                case 0x11:
                    alignment.Add(new XAttribute("textRotation", Integer(data.Byte())));
                    break;
                case 0x12:
                    alignment.Add(new XAttribute("indent", Integer(data.UInt16())));
                    break;
                case 0x13:
                    alignment.Add(new XAttribute("readingOrder", Integer(data.Byte())));
                    break;
                case 0x14:
                    alignment.Add(new XAttribute("wrapText", data.Byte() != 0 ? "1" : "0"));
                    break;
                case 0x15:
                    alignment.Add(new XAttribute("justifyLastLine", data.Byte() != 0 ? "1" : "0"));
                    break;
                case 0x16:
                    alignment.Add(new XAttribute("shrinkToFit", data.Byte() != 0 ? "1" : "0"));
                    break;
                case 0x18:
                    {
                        int length = data.UInt16();
                        fontName = length <= data.Remaining / 2 ? System.Text.Encoding.Unicode.GetString(data.Bytes(length * 2)) : null;
                        break;
                    }

                case 0x19:
                    bold = data.UInt16() >= 700;
                    break;
                case 0x1A:
                    underline = data.UInt16();
                    break;
                case 0x1B:
                    script = data.UInt16();
                    break;
                case 0x1C:
                    italic = data.Byte() != 0;
                    break;
                case 0x1D:
                    strike = data.Byte() != 0;
                    break;
                case 0x1E:
                    outline = data.Byte() != 0;
                    break;
                case 0x1F:
                    shadow = data.Byte() != 0;
                    break;
                case 0x20:
                    condense = data.Byte() != 0;
                    break;
                case 0x21:
                    extend = data.Byte() != 0;
                    break;
                case 0x22:
                    charset = data.Byte();
                    break;
                case 0x23:
                    family = data.Byte();
                    break;
                case 0x24:
                    size = data.UInt32() / 20.0;
                    break;
                case 0x25:
                    scheme = data.Byte();
                    break;
                case 0x26:
                    {
                        int id = data.UInt16();
                        int length = data.UInt16();
                        if (length <= data.Remaining / 2)
                        {
                            string code = System.Text.Encoding.Unicode.GetString(data.Bytes(length * 2));
                            numFmt = new XElement(Ns + "numFmt", new XAttribute("numFmtId", Integer(id)), new XAttribute("formatCode", code));
                        }

                        break;
                    }

                case 0x2B:
                    protection.Add(new XAttribute("locked", data.Byte() != 0 ? "1" : "0"));
                    break;
                case 0x2C:
                    protection.Add(new XAttribute("hidden", data.Byte() != 0 ? "1" : "0"));
                    break;
            }
        }

        void Bool(string name, bool? value)
        {
            if (value is { } set)
            {
                font.Add(set ? new XElement(Ns + name) : new XElement(Ns + name, new XAttribute("val", "0")));
            }
        }

        Bool("b", bold);
        Bool("i", italic);
        Bool("strike", strike);
        Bool("condense", condense);
        Bool("extend", extend);
        Bool("outline", outline);
        Bool("shadow", shadow);
        if (underline is { } u)
        {
            font.Add(u == 1 ? new XElement(Ns + "u") : new XElement(Ns + "u", new XAttribute("val", UnderlineName(u) ?? "none")));
        }

        if (script is { } s)
        {
            font.Add(new XElement(Ns + "vertAlign", new XAttribute("val", s switch { 1 => "superscript", 2 => "subscript", _ => "baseline" })));
        }

        if (size is { } points)
        {
            font.Add(new XElement(Ns + "sz", new XAttribute("val", XlsxNames.Number(points))));
        }

        if (fontColor is not null)
        {
            font.Add(fontColor);
        }

        if (fontName is not null)
        {
            font.Add(new XElement(Ns + "name", new XAttribute("val", fontName)));
        }

        if (family is { } f)
        {
            font.Add(new XElement(Ns + "family", new XAttribute("val", Integer(f))));
        }

        if (charset is { } c)
        {
            font.Add(new XElement(Ns + "charset", new XAttribute("val", Integer(c))));
        }

        if (scheme is 1 or 2)
        {
            font.Add(new XElement(Ns + "scheme", new XAttribute("val", scheme == 1 ? "major" : "minor")));
        }

        XElement dxf = new(Ns + "dxf");
        if (font.HasElements)
        {
            dxf.Add(font);
        }

        if (numFmt is not null)
        {
            dxf.Add(numFmt);
        }

        if (patternFill is not null)
        {
            // The pattern type goes first, then the colours, foreground before background.
            XElement ordered = new(Ns + "patternFill", patternFill.Attributes(), patternFill.Elements(Ns + "fgColor"), patternFill.Elements(Ns + "bgColor"));
            dxf.Add(new XElement(Ns + "fill", ordered));
        }

        if (alignment.HasAttributes)
        {
            dxf.Add(alignment);
        }

        if (border is not null)
        {
            border.Add(lines[2], lines[3], lines[0], lines[1], lines[4]);
            dxf.Add(border);
        }

        if (protection.HasAttributes)
        {
            dxf.Add(protection);
        }

        return dxf;
    }

    /// <summary>A BrtColor ([MS-XLSB] 2.4.324) as a colour element of a name; null for an automatic colour without an index.</summary>
    internal static XElement? Color(string name, ref XlsbReader record) => Color(name, ref record, out _);

    private static XElement? Color(string name, ref XlsbReader record, out int index)
    {
        int flags = record.Byte();
        index = record.Byte();
        short tint = record.Int16();
        byte red = record.Byte(), green = record.Byte(), blue = record.Byte(), alpha = record.Byte();
        XElement color = new(Ns + name);
        switch (flags >> 1)
        {
            case 0:
                color.Add(new XAttribute("auto", "1"));
                break;
            case 1:
                color.Add(new XAttribute("indexed", Integer(index)));
                break;
            case 2:
                color.Add(new XAttribute("rgb", Rgb(red, green, blue, alpha)));
                index = -1;
                break;
            case 3:
                color.Add(new XAttribute("theme", Integer(index)));
                index = -1;
                break;
            default:
                return null;
        }

        if (tint != 0)
        {
            color.Add(new XAttribute("tint", XlsxNames.Number(tint / 32767.0)));
        }

        return color;
    }

    private static string Rgb(byte red, byte green, byte blue, byte alpha) =>
        ((uint)alpha << 24 | (uint)red << 16 | (uint)green << 8 | blue).ToString("X8", CultureInfo.InvariantCulture);

    private static string Integer(int value) => value.ToString(CultureInfo.InvariantCulture);
}
