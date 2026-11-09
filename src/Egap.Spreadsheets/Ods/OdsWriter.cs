using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Xml;
using System.Xml.Linq;

using Egap.Documents.Odf;
using Egap.Spreadsheets.Formulas;
using Egap.Spreadsheets.Printing;
using Egap.Spreadsheets.Storage;
using Egap.Spreadsheets.Xlsx;

using static Egap.Documents.Odf.OdfNames;

namespace Egap.Spreadsheets.Ods;

/// <summary>
/// Writes a <see cref="Workbook"/> as an OpenDocument spreadsheet (ODF 1.3 Part 3, chapter 9): an ODS or OTS package, or a flat
/// FODS file. Its worksheets become tables with their cells, formulas translated to OpenFormula, merged and array ranges,
/// what-if tables as MULTIPLE.OPERATIONS, styles and number formats, column widths and row heights, hidden and grouped rows and
/// columns, hidden sheets and tab colours, names and print ranges, and the cached sheets of linked files. A package can be
/// encrypted with a password, as ODF 1.2 and later encrypt (Part 2, 3.4).
/// </summary>
internal sealed class OdsWriter
{
    private static readonly XNamespace CalcExtNs = "urn:org:documentfoundation:names:experimental:calc:xmlns:calcext:1.0";

    private static readonly string Generator = "Egap.Spreadsheets/" + (typeof(OdsWriter).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0");

    private static readonly XmlWriterSettings Settings = new() { Encoding = new UTF8Encoding(false), Indent = false, NewLineHandling = NewLineHandling.Entitize };

    /// <summary>Excel's width of a column in characters when a sheet gives none.</summary>
    private const double DefaultWidth = 8.43;

    private readonly Workbook book;
    private readonly SheetColors colors;
    private readonly OdsDataStyles dataStyles = new();
    private readonly DateTime nullDate;

    /// <summary>Each cell style index's name and the kind of value its number format shows.</summary>
    private readonly Dictionary<int, (string Name, OdsValueKind Kind, string? Code)> cellStyles = [];

    /// <summary>The cell styles that also show booleans, with a boolean data style for a General format.</summary>
    private readonly Dictionary<int, string> booleanStyles = [];

    private readonly Dictionary<double, string> columnStyles = [];
    private readonly Dictionary<double, string> rowStyles = [];
    private readonly Dictionary<(bool Hidden, uint? Tab), string> tableStyles = [];
    private readonly SortedSet<string> fonts = new(StringComparer.Ordinal);

    /// <summary>The linked books' files by the number formulas give them, <c>[1]</c> for the first.</summary>
    private readonly Dictionary<string, string?> files = new(StringComparer.Ordinal);

    private readonly StringBuilder text = new();

    private OdsWriter(Workbook book)
    {
        this.book = book;
        colors = SheetColors.For(book);
        nullDate = book.Date1904 ? new DateTime(1904, 1, 1) : new DateTime(1899, 12, 30);
        for (int i = 0; i < book.ExternalBooks.Count; i++)
        {
            files[(i + 1).ToString(CultureInfo.InvariantCulture)] = book.ExternalBooks[i].File is { } file ? FileUrl(file) : null;
        }
    }

    public static void Write(Workbook book, Stream stream, WorkbookFormat format, string? password)
    {
        if (book.Worksheets.Count == 0)
        {
            throw new InvalidOperationException("The workbook has no worksheets.");
        }

        OdsWriter writer = new(book);
        writer.Prepare();
        string mediaType = format == WorkbookFormat.Ots ? SpreadsheetTemplateMediaType : SpreadsheetMediaType;
        if (format == WorkbookFormat.Fods)
        {
            using XmlWriter xml = XmlWriter.Create(stream, Settings);
            writer.Flat(xml, mediaType);
            return;
        }

        if (password is null)
        {
            writer.Package(stream, mediaType);
            return;
        }

        using MemoryStream plain = new();
        writer.Package(plain, mediaType);
        OdfEncryption.Encrypt(plain.ToArray(), password, OdfEncryptionMethod.AesCbc, stream);
    }

    /// <summary>An Excel link target as a URL: <c>C:\dir\a.xlsx</c> as <c>file:///C:/dir/a.xlsx</c>; a relative path stays relative.</summary>
    internal static string FileUrl(string target)
    {
        string path = target.Replace('\\', '/');
        if (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '/')
        {
            return "file:///" + path;
        }

        return path.StartsWith("//", StringComparison.Ordinal) ? "file:" + path : path;
    }

    // ----- Styles found before writing -----

    /// <summary>Finds the styles the tables use, so that the automatic styles can come before the body.</summary>
    private void Prepare()
    {
        foreach (Worksheet sheet in book.Worksheets)
        {
            TableStyle(sheet);
            ColumnStyle(sheet.DefaultColumnWidth ?? DefaultWidth);
            RowStyle(double.NaN);
            foreach (ColumnSpan span in sheet.ColumnSpans.Spans)
            {
                ColumnStyle(double.IsNaN(span.Width) ? sheet.DefaultColumnWidth ?? DefaultWidth : span.Width);
                if (span.Style >= 0)
                {
                    CellStyle(span.Style);
                }
            }

            foreach (RowRecord record in sheet.RowRecords.Values)
            {
                RowStyle(record.Height);
                if (record.Style >= 0)
                {
                    CellStyle(record.Style);
                }
            }

            PrepareCells(sheet);
        }

        foreach (ExternalBook external in book.ExternalBooks)
        {
            if (external.File is not null)
            {
                foreach (Worksheet sheet in external.Sheets)
                {
                    PrepareCells(sheet);
                }
            }
        }

        CellStyle(0);
    }

    private void PrepareCells(Worksheet sheet)
    {
        if (sheet.Store.Bounds() is not { } bounds)
        {
            return;
        }

        foreach ((int row, int column) in sheet.Store.Occupied(bounds))
        {
            ref readonly CellRecord record = ref sheet.Store.Get(row, column);
            CellStyle(record.Style);
            bool boolean = record.Kind == CellKind.Boolean
                || (record.Kind == CellKind.Formula && sheet.Formulas[(int)record.Payload].Result.Kind == CellValueKind.Boolean);
            if (boolean)
            {
                BooleanStyle(record.Style);
            }
        }
    }

    private (string Name, OdsValueKind Kind, string? Code) CellStyle(int index)
    {
        if ((uint)index >= (uint)book.Styles.Count)
        {
            index = 0;
        }

        if (!cellStyles.TryGetValue(index, out (string Name, OdsValueKind Kind, string? Code) known))
        {
            string code = book.Styles[index].NumberFormat;
            (string? _, OdsValueKind kind) = dataStyles.For(code);
            known = (index == 0 ? "Default" : "ce" + index.ToString(CultureInfo.InvariantCulture), kind, code is "General" ? null : code);
            cellStyles[index] = known;
            fonts.Add(book.Styles[index].Font.Name);
        }

        return known;
    }

    private string BooleanStyle(int index)
    {
        (string name, _, string? code) = CellStyle(index);
        if (code is not null)
        {
            return name;
        }

        if (!booleanStyles.TryGetValue(index, out string? boolean))
        {
            boolean = booleanStyles[index] = "ce" + index.ToString(CultureInfo.InvariantCulture) + "b";
            dataStyles.Boolean();
        }

        return boolean;
    }

    private string ColumnStyle(double width)
    {
        if (!columnStyles.TryGetValue(width, out string? name))
        {
            name = columnStyles[width] = "co" + (columnStyles.Count + 1).ToString(CultureInfo.InvariantCulture);
        }

        return name;
    }

    /// <summary>The style of a row of its own height in points, or of the default height for NaN.</summary>
    private string RowStyle(double height)
    {
        if (!rowStyles.TryGetValue(height, out string? name))
        {
            name = rowStyles[height] = "ro" + (rowStyles.Count + 1).ToString(CultureInfo.InvariantCulture);
        }

        return name;
    }

    private string TableStyle(Worksheet sheet)
    {
        uint? tab = sheet.TabColor is { } color ? Rgb(colors.Resolve(color, default)) : null;
        (bool, uint?) key = (sheet.Visibility != SheetVisibility.Visible, tab);
        if (!tableStyles.TryGetValue(key, out string? name))
        {
            name = tableStyles[key] = "ta" + (tableStyles.Count + 1).ToString(CultureInfo.InvariantCulture);
        }

        return name;
    }

    private static uint Rgb(Egami.Pixels.Rgba8 color) => ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;

    private static string Hex(uint rgb) => "#" + rgb.ToString("x6", CultureInfo.InvariantCulture);

    /// <summary>A column width in Excel's characters as points: seven pixels a character and five of padding, at 96 dpi.</summary>
    private static double Points(double characters) => (Math.Max(0, characters) * 7 + 5) * 72 / 96;

    private static string Pt(double points) => Math.Round(points, 4).ToString("0.####", CultureInfo.InvariantCulture) + "pt";

    // ----- Files -----

    private void Package(Stream stream, string mediaType)
    {
        using ZipArchive zip = new(stream, ZipArchiveMode.Create, leaveOpen: true);

        // The mimetype comes first, stored, so the file's first bytes name its type (ODF 1.3 Part 2, 3.3).
        ZipArchiveEntry mimetype = zip.CreateEntry("mimetype", CompressionLevel.NoCompression);
        using (Stream entry = mimetype.Open())
        {
            entry.Write(Encoding.ASCII.GetBytes(mediaType));
        }

        Entry(zip, "content.xml", xml =>
        {
            Root(xml, "document-content");
            FontFaces(xml);
            AutomaticStyles(xml);
            Body(xml);
            xml.WriteEndElement();
        });
        Entry(zip, "styles.xml", xml =>
        {
            Root(xml, "document-styles");
            FontFaces(xml);
            CommonStyles(xml);
            xml.WriteEndElement();
        });
        Entry(zip, "meta.xml", xml =>
        {
            Root(xml, "document-meta");
            Meta(xml);
            xml.WriteEndElement();
        });
        Entry(zip, "META-INF/manifest.xml", xml =>
        {
            xml.WriteStartElement("manifest", "manifest", ManifestNs.NamespaceName);
            xml.WriteAttributeString("manifest", "version", ManifestNs.NamespaceName, "1.3");
            ManifestEntry(xml, "/", mediaType, "1.3");
            ManifestEntry(xml, "content.xml", "text/xml", null);
            ManifestEntry(xml, "styles.xml", "text/xml", null);
            ManifestEntry(xml, "meta.xml", "text/xml", null);
            xml.WriteEndElement();
        });
    }

    private static void ManifestEntry(XmlWriter xml, string path, string mediaType, string? version)
    {
        xml.WriteStartElement("manifest", "file-entry", ManifestNs.NamespaceName);
        xml.WriteAttributeString("manifest", "full-path", ManifestNs.NamespaceName, path);
        if (version is not null)
        {
            xml.WriteAttributeString("manifest", "version", ManifestNs.NamespaceName, version);
        }

        xml.WriteAttributeString("manifest", "media-type", ManifestNs.NamespaceName, mediaType);
        xml.WriteEndElement();
    }

    private static void Entry(ZipArchive zip, string path, Action<XmlWriter> write)
    {
        ZipArchiveEntry entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using Stream output = entry.Open();
        using XmlWriter xml = XmlWriter.Create(output, Settings);
        xml.WriteStartDocument();
        write(xml);
        xml.WriteEndDocument();
    }

    private void Flat(XmlWriter xml, string mediaType)
    {
        xml.WriteStartDocument();
        Root(xml, "document");
        xml.WriteAttributeString("office", "mimetype", OfficeNs.NamespaceName, mediaType);
        Meta(xml);
        FontFaces(xml);
        CommonStyles(xml);
        AutomaticStyles(xml);
        Body(xml);
        xml.WriteEndElement();
        xml.WriteEndDocument();
    }

    private static void Root(XmlWriter xml, string name)
    {
        xml.WriteStartElement("office", name, OfficeNs.NamespaceName);
        foreach ((string prefix, XNamespace ns) in (ReadOnlySpan<(string, XNamespace)>)[
            ("office", OfficeNs), ("style", StyleNs), ("text", TextNs), ("table", TableNs), ("fo", FoNs), ("xlink", XLinkNs),
            ("dc", DcNs), ("meta", MetaNs), ("number", NumberNs), ("svg", SvgNs), ("of", "urn:oasis:names:tc:opendocument:xmlns:of:1.2"),
            ("calcext", CalcExtNs)])
        {
            xml.WriteAttributeString("xmlns", prefix, null, ns.NamespaceName);
        }

        xml.WriteAttributeString("office", "version", OfficeNs.NamespaceName, "1.3");
    }

    private static void Meta(XmlWriter xml)
    {
        xml.WriteStartElement("office", "meta", OfficeNs.NamespaceName);
        xml.WriteElementString("meta", "generator", MetaNs.NamespaceName, Generator);
        xml.WriteEndElement();
    }

    private void FontFaces(XmlWriter xml)
    {
        xml.WriteStartElement("office", "font-face-decls", OfficeNs.NamespaceName);
        foreach (string font in fonts)
        {
            xml.WriteStartElement("style", "font-face", StyleNs.NamespaceName);
            xml.WriteAttributeString("style", "name", StyleNs.NamespaceName, font);
            xml.WriteAttributeString("svg", "font-family", SvgNs.NamespaceName, font.Contains(' ', StringComparison.Ordinal) ? "'" + font + "'" : font);
            xml.WriteEndElement();
        }

        xml.WriteEndElement();
    }

    /// <summary>The default cell style, "Default", with the workbook's first style; the other styles take it as their parent.</summary>
    private void CommonStyles(XmlWriter xml)
    {
        xml.WriteStartElement("office", "styles", OfficeNs.NamespaceName);
        xml.WriteStartElement("style", "default-style", StyleNs.NamespaceName);
        xml.WriteAttributeString("style", "family", StyleNs.NamespaceName, "table-cell");
        TextProperties(xml, book.Styles[0].Font);
        xml.WriteEndElement();
        CellStyleElement(xml, "Default", 0, null, common: true);
        xml.WriteEndElement();
    }

    private void AutomaticStyles(XmlWriter xml)
    {
        xml.WriteStartElement("office", "automatic-styles", OfficeNs.NamespaceName);
        foreach ((double width, string name) in columnStyles)
        {
            xml.WriteStartElement("style", "style", StyleNs.NamespaceName);
            xml.WriteAttributeString("style", "name", StyleNs.NamespaceName, name);
            xml.WriteAttributeString("style", "family", StyleNs.NamespaceName, "table-column");
            xml.WriteStartElement("style", "table-column-properties", StyleNs.NamespaceName);
            xml.WriteAttributeString("fo", "break-before", FoNs.NamespaceName, "auto");
            xml.WriteAttributeString("style", "column-width", StyleNs.NamespaceName, Pt(Points(width)));
            xml.WriteEndElement();
            xml.WriteEndElement();
        }

        foreach ((double height, string name) in rowStyles)
        {
            xml.WriteStartElement("style", "style", StyleNs.NamespaceName);
            xml.WriteAttributeString("style", "name", StyleNs.NamespaceName, name);
            xml.WriteAttributeString("style", "family", StyleNs.NamespaceName, "table-row");
            xml.WriteStartElement("style", "table-row-properties", StyleNs.NamespaceName);
            bool custom = !double.IsNaN(height);
            xml.WriteAttributeString("style", "row-height", StyleNs.NamespaceName, Pt(custom ? height : DefaultHeight()));
            xml.WriteAttributeString("fo", "break-before", FoNs.NamespaceName, "auto");
            xml.WriteAttributeString("style", "use-optimal-row-height", StyleNs.NamespaceName, custom ? "false" : "true");
            xml.WriteEndElement();
            xml.WriteEndElement();
        }

        foreach (((bool hidden, uint? tab), string name) in tableStyles)
        {
            TableStyleElement(xml, name, hidden, tab);
        }

        if (book.ExternalBooks.Any(b => b.File is not null))
        {
            TableStyleElement(xml, "ta_extref", hidden: true, null);
        }

        foreach (XElement data in dataStyles.Elements)
        {
            data.WriteTo(xml);
        }

        foreach ((int index, (string name, _, string? code)) in cellStyles.OrderBy(s => s.Key))
        {
            if (index != 0)
            {
                CellStyleElement(xml, name, index, code is null ? null : dataStyles.For(code).Name, common: false);
            }
        }

        foreach ((int index, string name) in booleanStyles.OrderBy(s => s.Key))
        {
            CellStyleElement(xml, name, index, dataStyles.Boolean(), common: false);
        }

        xml.WriteEndElement();
    }

    private double DefaultHeight() => book.Worksheets[0].DefaultRowHeight ?? 15;

    private static void TableStyleElement(XmlWriter xml, string name, bool hidden, uint? tab)
    {
        xml.WriteStartElement("style", "style", StyleNs.NamespaceName);
        xml.WriteAttributeString("style", "name", StyleNs.NamespaceName, name);
        xml.WriteAttributeString("style", "family", StyleNs.NamespaceName, "table");
        xml.WriteStartElement("style", "table-properties", StyleNs.NamespaceName);
        xml.WriteAttributeString("table", "display", TableNs.NamespaceName, hidden ? "false" : "true");
        if (tab is { } colour)
        {
            xml.WriteAttributeString("table", "tab-color", TableNs.NamespaceName, Hex(colour));
        }

        xml.WriteEndElement();
        xml.WriteEndElement();
    }

    /// <summary>A cell style (ODF 1.3 Part 3, 16.2) with all of a workbook style's properties, which its parent then only backs up.</summary>
    private void CellStyleElement(XmlWriter xml, string name, int index, string? dataStyle, bool common)
    {
        CellStyle style = book.Styles[index];
        xml.WriteStartElement("style", "style", StyleNs.NamespaceName);
        xml.WriteAttributeString("style", "name", StyleNs.NamespaceName, name);
        xml.WriteAttributeString("style", "family", StyleNs.NamespaceName, "table-cell");
        if (!common)
        {
            xml.WriteAttributeString("style", "parent-style-name", StyleNs.NamespaceName, "Default");
        }

        if (common && style.NumberFormat != "General")
        {
            dataStyle = null;
        }

        if (dataStyle is not null)
        {
            xml.WriteAttributeString("style", "data-style-name", StyleNs.NamespaceName, dataStyle);
        }

        CellProperties(xml, style);
        ParagraphProperties(xml, style.Alignment);
        TextProperties(xml, style.Font);
        xml.WriteEndElement();
    }

    private void CellProperties(XmlWriter xml, CellStyle style)
    {
        xml.WriteStartElement("style", "table-cell-properties", StyleNs.NamespaceName);
        CellFill fill = style.Fill;
        if (fill.Pattern == CellFillPattern.Solid || (fill.Pattern != CellFillPattern.None && fill.Foreground.Kind != CellColorKind.Auto))
        {
            Egami.Pixels.Rgba8 color = colors.Resolve(fill.Foreground, new Egami.Pixels.Rgba8(255, 255, 255, 255));
            xml.WriteAttributeString("fo", "background-color", FoNs.NamespaceName, Hex(Rgb(color)));
        }

        CellBorders borders = style.Borders;
        Border(xml, FoNs, "border-left", borders.Left);
        Border(xml, FoNs, "border-right", borders.Right);
        Border(xml, FoNs, "border-top", borders.Top);
        Border(xml, FoNs, "border-bottom", borders.Bottom);
        if (borders.DiagonalUp)
        {
            Border(xml, StyleNs, "diagonal-bl-tr", borders.Diagonal);
        }

        if (borders.DiagonalDown)
        {
            Border(xml, StyleNs, "diagonal-tl-br", borders.Diagonal);
        }

        CellAlignment alignment = style.Alignment;
        xml.WriteAttributeString("style", "text-align-source", StyleNs.NamespaceName, alignment.Horizontal == CellHorizontalAlignment.General ? "value-type" : "fix");
        xml.WriteAttributeString("style", "vertical-align", StyleNs.NamespaceName, alignment.Vertical switch
        {
            CellVerticalAlignment.Top => "top",
            CellVerticalAlignment.Center => "middle",
            _ => "bottom",
        });
        if (alignment.WrapText)
        {
            xml.WriteAttributeString("fo", "wrap-option", FoNs.NamespaceName, "wrap");
        }

        if (alignment.ShrinkToFit)
        {
            xml.WriteAttributeString("style", "shrink-to-fit", StyleNs.NamespaceName, "true");
        }

        if (alignment.TextRotation is > 0 and <= 180)
        {
            // Excel's 1 to 90 turn the text up, counter-clockwise; 91 to 180 turn it down by the amount over 90.
            int degrees = alignment.TextRotation <= 90 ? alignment.TextRotation : 360 - (alignment.TextRotation - 90);
            xml.WriteAttributeString("style", "rotation-angle", StyleNs.NamespaceName, degrees.ToString(CultureInfo.InvariantCulture));
        }
        else if (alignment.TextRotation == 255)
        {
            xml.WriteAttributeString("style", "direction", StyleNs.NamespaceName, "ttb");
        }

        CellProtection protection = style.Protection;
        xml.WriteAttributeString("style", "cell-protect", StyleNs.NamespaceName, (protection.Locked, protection.Hidden) switch
        {
            (true, true) => "protected formula-hidden",
            (true, false) => "protected",
            (false, true) => "formula-hidden",
            _ => "none",
        });
        xml.WriteEndElement();
    }

    private void Border(XmlWriter xml, XNamespace ns, string name, CellBorder border)
    {
        if (border.Style == CellBorderStyle.None)
        {
            return;
        }

        (double width, string line) = border.Style switch
        {
            CellBorderStyle.Hair => (0.26, "solid"),
            CellBorderStyle.Medium => (1.75, "solid"),
            CellBorderStyle.Thick => (2.5, "solid"),
            CellBorderStyle.Double => (2.01, "double"),
            CellBorderStyle.Dotted => (0.74, "dotted"),
            CellBorderStyle.Dashed or CellBorderStyle.DashDot or CellBorderStyle.DashDotDot => (0.74, "dashed"),
            CellBorderStyle.MediumDashed or CellBorderStyle.MediumDashDot or CellBorderStyle.MediumDashDotDot or CellBorderStyle.SlantDashDot => (1.75, "dashed"),
            _ => (0.74, "solid"),
        };
        string color = Hex(Rgb(colors.Resolve(border.Color, new Egami.Pixels.Rgba8(0, 0, 0, 255))));
        xml.WriteAttributeString(ns == FoNs ? "fo" : "style", name, ns.NamespaceName, Pt(width) + " " + line + " " + color);
    }

    private static void ParagraphProperties(XmlWriter xml, CellAlignment alignment)
    {
        string? align = alignment.Horizontal switch
        {
            CellHorizontalAlignment.Left => "start",
            CellHorizontalAlignment.Center or CellHorizontalAlignment.CenterContinuous => "center",
            CellHorizontalAlignment.Right => "end",
            CellHorizontalAlignment.Justify or CellHorizontalAlignment.Distributed => "justify",
            _ => null,
        };
        if (align is null && alignment.Indent == 0)
        {
            return;
        }

        xml.WriteStartElement("style", "paragraph-properties", StyleNs.NamespaceName);
        if (align is not null)
        {
            xml.WriteAttributeString("fo", "text-align", FoNs.NamespaceName, align);
        }

        if (alignment.Indent > 0)
        {
            // An indent level is three characters of the default font, Calibri 11's seven pixels each.
            xml.WriteAttributeString("fo", "margin-left", FoNs.NamespaceName, Pt(alignment.Indent * 3 * 7 * 0.75));
        }

        xml.WriteEndElement();
    }

    private void TextProperties(XmlWriter xml, CellFont font)
    {
        xml.WriteStartElement("style", "text-properties", StyleNs.NamespaceName);
        xml.WriteAttributeString("style", "font-name", StyleNs.NamespaceName, font.Name);
        xml.WriteAttributeString("fo", "font-size", FoNs.NamespaceName, Pt(font.Size));
        xml.WriteAttributeString("fo", "font-weight", FoNs.NamespaceName, font.Bold ? "bold" : "normal");
        xml.WriteAttributeString("fo", "font-style", FoNs.NamespaceName, font.Italic ? "italic" : "normal");
        if (colors.Resolve(font.Color, default) is { A: > 0 } color)
        {
            xml.WriteAttributeString("fo", "color", FoNs.NamespaceName, Hex(Rgb(color)));
        }

        if (font.Underline != CellUnderline.None)
        {
            xml.WriteAttributeString("style", "text-underline-style", StyleNs.NamespaceName, "solid");
            xml.WriteAttributeString("style", "text-underline-width", StyleNs.NamespaceName, "auto");
            xml.WriteAttributeString("style", "text-underline-color", StyleNs.NamespaceName, "font-color");
            if (font.Underline is CellUnderline.Double or CellUnderline.DoubleAccounting)
            {
                xml.WriteAttributeString("style", "text-underline-type", StyleNs.NamespaceName, "double");
            }
        }

        if (font.Strikethrough)
        {
            xml.WriteAttributeString("style", "text-line-through-style", StyleNs.NamespaceName, "solid");
        }

        if (font.VerticalPosition != CellVerticalTextPosition.Baseline)
        {
            xml.WriteAttributeString("style", "text-position", StyleNs.NamespaceName, font.VerticalPosition == CellVerticalTextPosition.Superscript ? "super 58%" : "sub 58%");
        }

        xml.WriteEndElement();
    }

    // ----- Body -----

    private void Body(XmlWriter xml)
    {
        xml.WriteStartElement("office", "body", OfficeNs.NamespaceName);
        xml.WriteStartElement("office", "spreadsheet", OfficeNs.NamespaceName);
        if (book.Date1904)
        {
            xml.WriteStartElement("table", "calculation-settings", TableNs.NamespaceName);
            xml.WriteStartElement("table", "null-date", TableNs.NamespaceName);
            xml.WriteAttributeString("table", "date-value", TableNs.NamespaceName, "1904-01-01");
            xml.WriteEndElement();
            xml.WriteEndElement();
        }

        foreach (Worksheet sheet in book.Worksheets)
        {
            Table(xml, sheet);
        }

        for (int i = 0; i < book.ExternalBooks.Count; i++)
        {
            if (files[(i + 1).ToString(CultureInfo.InvariantCulture)] is { } file)
            {
                foreach (Worksheet sheet in book.ExternalBooks[i].Sheets)
                {
                    LinkedTable(xml, file, sheet);
                }
            }
        }

        Names(xml, null);
        xml.WriteEndElement();
        xml.WriteEndElement();
    }

    private void Table(XmlWriter xml, Worksheet sheet)
    {
        xml.WriteStartElement("table", "table", TableNs.NamespaceName);
        xml.WriteAttributeString("table", "name", TableNs.NamespaceName, sheet.Name);
        xml.WriteAttributeString("table", "style-name", TableNs.NamespaceName, TableStyle(sheet));
        if (book.Names.FirstOrDefault(n => n.Name == "_xlnm.Print_Area" && n.Sheet == sheet) is { } print && PrintRanges(print) is { } ranges)
        {
            xml.WriteAttributeString("table", "print-ranges", TableNs.NamespaceName, ranges);
        }

        Grid grid = new(sheet);
        Columns(xml, sheet, grid);
        Rows(xml, sheet, grid);
        Names(xml, sheet);
        xml.WriteEndElement();
    }

    /// <summary>A linked file's sheet, as office suites keep it for the formulas that read it: its values, under the file's name.</summary>
    private void LinkedTable(XmlWriter xml, string file, Worksheet sheet)
    {
        xml.WriteStartElement("table", "table", TableNs.NamespaceName);
        xml.WriteAttributeString("table", "name", TableNs.NamespaceName, "'" + file.Replace("'", "''", StringComparison.Ordinal) + "'#" + sheet.Name);
        xml.WriteAttributeString("table", "print", TableNs.NamespaceName, "false");
        xml.WriteAttributeString("table", "style-name", TableNs.NamespaceName, "ta_extref");
        xml.WriteStartElement("table", "table-source", TableNs.NamespaceName);
        xml.WriteAttributeString("xlink", "type", XLinkNs.NamespaceName, "simple");
        xml.WriteAttributeString("xlink", "href", XLinkNs.NamespaceName, file);
        xml.WriteAttributeString("table", "table-name", TableNs.NamespaceName, sheet.Name);
        xml.WriteAttributeString("table", "mode", TableNs.NamespaceName, "copy-results-only");
        xml.WriteEndElement();
        Grid grid = new(sheet);
        Columns(xml, sheet, grid);
        Rows(xml, sheet, grid);
        xml.WriteEndElement();
    }

    /// <summary>What a sheet's tables need to know of its cells: how far they reach, its merged ranges and its what-if tables.</summary>
    private sealed class Grid
    {
        public Grid(Worksheet sheet)
        {
            RangeAddress? bounds = sheet.Store.Bounds();
            Bounds = bounds;
            int lastColumn = bounds?.Last.Column ?? 0;
            int lastRow = bounds?.Last.Row ?? -1;
            foreach (RangeAddress merged in sheet.MergedCells)
            {
                Merges.Add(merged);
                lastColumn = Math.Max(lastColumn, merged.Last.Column);
                lastRow = Math.Max(lastRow, merged.Last.Row);
                for (int row = merged.First.Row; row <= merged.Last.Row; row++)
                {
                    if (!MergesByRow.TryGetValue(row, out List<RangeAddress>? list))
                    {
                        MergesByRow[row] = list = [];
                    }

                    list.Add(merged);
                }
            }

            foreach (int row in sheet.RowRecords.Keys)
            {
                lastRow = Math.Max(lastRow, row);
            }

            LastColumn = lastColumn;
            LastRow = lastRow;
            foreach (FormulaEntry entry in sheet.Formulas.Entries)
            {
                if (entry.Attribute("t") == "dataTable" && RangeAddress.TryParse(entry.Attribute("ref"), out RangeAddress range))
                {
                    DataTable(entry, range);
                }
            }
        }

        public RangeAddress? Bounds { get; }

        public int LastColumn { get; }

        public int LastRow { get; }

        public List<RangeAddress> Merges { get; } = [];

        public Dictionary<int, List<RangeAddress>> MergesByRow { get; } = [];

        /// <summary>The MULTIPLE.OPERATIONS formula of each cell of a data table.</summary>
        public Dictionary<(int Row, int Column), string> Operations { get; } = [];

        /// <summary>
        /// A data table as MULTIPLE.OPERATIONS in each of its cells (ODF 1.3 Part 4, 6.15.48): the formula the table varies,
        /// its input cells and the cells their values come from, as a table of one input down a column or along a row, or of
        /// two inputs along its top row and down its left column.
        /// </summary>
        private void DataTable(FormulaEntry entry, RangeAddress range)
        {
            int top = range.First.Row - 1, left = range.First.Column - 1;
            if (top < 0 || left < 0 || !CellAddress.TryParse(entry.Attribute("r1"), out CellAddress input1))
            {
                return;
            }

            bool twoD = entry.Attribute("dt2D") is "1" or "true";
            bool alongRow = entry.Attribute("dtr") is "1" or "true";
            CellAddress input2 = default;
            if (twoD && !CellAddress.TryParse(entry.Attribute("r2"), out input2))
            {
                return;
            }

            for (int row = range.First.Row; row <= range.Last.Row; row++)
            {
                for (int column = range.First.Column; column <= range.Last.Column; column++)
                {
                    string formula = twoD
                        ? Cell(top, left, true, true) + ";" + Cell(input1.Row, input1.Column, true, true) + ";" + Cell(top, column, true, false)
                            + ";" + Cell(input2.Row, input2.Column, true, true) + ";" + Cell(row, left, false, true)
                        : alongRow
                            ? Cell(row, left, false, true) + ";" + Cell(input1.Row, input1.Column, true, true) + ";" + Cell(top, column, true, false)
                            : Cell(top, column, true, false) + ";" + Cell(input1.Row, input1.Column, true, true) + ";" + Cell(row, left, false, true);
                    Operations[(row, column)] = "of:=MULTIPLE.OPERATIONS(" + formula + ")";
                }
            }
        }

        private static string Cell(int row, int column, bool rowAbsolute, bool columnAbsolute) =>
            "[." + (columnAbsolute ? "$" : "") + CellAddress.ColumnName(column) + (rowAbsolute ? "$" : "") + (row + 1).ToString(CultureInfo.InvariantCulture) + "]";
    }

    /// <summary>The columns, in runs of the same width, visibility and style, inside their outline groups.</summary>
    private void Columns(XmlWriter xml, Worksheet sheet, Grid grid)
    {
        List<ColumnSpan> spans = [.. sheet.ColumnSpans.Spans.OrderBy(s => s.First)];
        int last = Math.Max(grid.LastColumn, spans.Count > 0 ? Math.Min(spans[^1].Last, grid.LastColumn + 1) : 0);
        string defaultStyle = ColumnStyle(sheet.DefaultColumnWidth ?? DefaultWidth);
        int depth = 0;
        int column = 0;
        int next = 0;
        while (column <= last)
        {
            while (next < spans.Count && spans[next].Last < column)
            {
                next++;
            }

            int end;
            ColumnSpan? span = null;
            if (next < spans.Count && spans[next].First <= column)
            {
                span = spans[next];
                end = Math.Min(span.Last, last);
            }
            else
            {
                end = next < spans.Count ? Math.Min(spans[next].First - 1, last) : last;
            }

            int level = span?.OutlineLevel ?? 0;
            Group(xml, ref depth, level, "table-column-group");
            xml.WriteStartElement("table", "table-column", TableNs.NamespaceName);
            xml.WriteAttributeString("table", "style-name", TableNs.NamespaceName,
                span is null || double.IsNaN(span.Width) ? defaultStyle : ColumnStyle(span.Width));
            if (end > column)
            {
                xml.WriteAttributeString("table", "number-columns-repeated", TableNs.NamespaceName, (end - column + 1).ToString(CultureInfo.InvariantCulture));
            }

            if (span?.Hidden == true)
            {
                xml.WriteAttributeString("table", "visibility", TableNs.NamespaceName, "collapse");
            }

            xml.WriteAttributeString("table", "default-cell-style-name", TableNs.NamespaceName, CellStyle(span is { Style: >= 0 } ? span.Style : 0).Name);
            xml.WriteEndElement();
            column = end + 1;
        }

        Group(xml, ref depth, 0, "table-column-group");
    }

    /// <summary>Opens or closes outline groups to reach <paramref name="level"/>.</summary>
    private static void Group(XmlWriter xml, ref int depth, int level, string name)
    {
        while (depth < level)
        {
            xml.WriteStartElement("table", name, TableNs.NamespaceName);
            depth++;
        }

        while (depth > level)
        {
            xml.WriteEndElement();
            depth--;
        }
    }

    private void Rows(XmlWriter xml, Worksheet sheet, Grid grid)
    {
        // The rows that need writing: those with cells, with a record of their own, or crossed by a merged range.
        using SortedSet<int>.Enumerator listed = new SortedSet<int>([.. sheet.RowRecords.Keys, .. grid.MergesByRow.Keys]).GetEnumerator();
        bool hasListed = listed.MoveNext();
        using IEnumerator<(int Row, int Column)> occupied = grid.Bounds is { } bounds
            ? sheet.Store.Occupied(bounds).GetEnumerator()
            : Enumerable.Empty<(int, int)>().GetEnumerator();
        bool more = occupied.MoveNext();
        List<int> cells = [];
        int depth = 0;
        int written = 0;
        while (more || hasListed)
        {
            int row = Math.Min(more ? occupied.Current.Row : int.MaxValue, hasListed ? listed.Current : int.MaxValue);
            while (hasListed && listed.Current <= row)
            {
                hasListed = listed.MoveNext();
            }

            cells.Clear();
            while (more && occupied.Current.Row == row)
            {
                cells.Add(occupied.Current.Column);
                more = occupied.MoveNext();
            }

            if (row > written)
            {
                Group(xml, ref depth, 0, "table-row-group");
                EmptyRows(xml, row - written);
            }

            sheet.RowRecords.TryGetValue(row, out RowRecord? record);
            Group(xml, ref depth, record?.OutlineLevel ?? 0, "table-row-group");
            Row(xml, sheet, grid, row, record, cells);
            written = row + 1;
        }

        Group(xml, ref depth, 0, "table-row-group");
        if (written == 0)
        {
            EmptyRows(xml, 1);
        }
    }

    private void EmptyRows(XmlWriter xml, int count)
    {
        xml.WriteStartElement("table", "table-row", TableNs.NamespaceName);
        xml.WriteAttributeString("table", "style-name", TableNs.NamespaceName, RowStyle(double.NaN));
        if (count > 1)
        {
            xml.WriteAttributeString("table", "number-rows-repeated", TableNs.NamespaceName, count.ToString(CultureInfo.InvariantCulture));
        }

        xml.WriteStartElement("table", "table-cell", TableNs.NamespaceName);
        xml.WriteEndElement();
        xml.WriteEndElement();
    }

    private void Row(XmlWriter xml, Worksheet sheet, Grid grid, int row, RowRecord? record, List<int> cells)
    {
        xml.WriteStartElement("table", "table-row", TableNs.NamespaceName);
        xml.WriteAttributeString("table", "style-name", TableNs.NamespaceName, RowStyle(record?.Height ?? double.NaN));
        if (record?.Hidden == true)
        {
            xml.WriteAttributeString("table", "visibility", TableNs.NamespaceName, "collapse");
        }

        if (record is { Style: >= 0 })
        {
            xml.WriteAttributeString("table", "default-cell-style-name", TableNs.NamespaceName, CellStyle(record.Style).Name);
        }

        List<RangeAddress>? merges = grid.MergesByRow.GetValueOrDefault(row);
        int column = 0;
        int index = 0;
        bool any = false;
        while (true)
        {
            int nextCell = index < cells.Count ? cells[index] : int.MaxValue;
            RangeAddress? merge = null;
            if (merges is not null)
            {
                foreach (RangeAddress m in merges)
                {
                    if (m.Last.Column >= column && (merge is null || m.First.Column < merge.Value.First.Column))
                    {
                        merge = m;
                    }
                }
            }

            int nextMerge = merge is { } found ? Math.Max(found.First.Column, column) : int.MaxValue;
            int next = Math.Min(nextCell, nextMerge);
            if (next == int.MaxValue)
            {
                break;
            }

            if (next > column)
            {
                Blank(xml, "table-cell", next - column);
                any = true;
            }

            column = next;
            if (merge is { } range && range.First.Column <= column)
            {
                // A merged range: its first cell spans it in its top row; every other cell is covered, keeping what it holds.
                for (; column <= range.Last.Column; column++)
                {
                    bool anchor = row == range.First.Row && column == range.First.Column;
                    bool held = index < cells.Count && cells[index] == column;
                    if (held)
                    {
                        index++;
                    }

                    if (anchor)
                    {
                        Cell(xml, sheet, grid, row, column, "table-cell", range, held);
                    }
                    else if (held)
                    {
                        Cell(xml, sheet, grid, row, column, "covered-table-cell", null, held);
                    }
                    else
                    {
                        int end = column;
                        while (end + 1 <= range.Last.Column && !(index < cells.Count && cells[index] == end + 1))
                        {
                            end++;
                        }

                        Blank(xml, "covered-table-cell", end - column + 1);
                        column = end;
                    }

                    any = true;
                }

                merges!.Remove(range);
                continue;
            }

            Cell(xml, sheet, grid, row, column, "table-cell", null, true);
            index++;
            column++;
            any = true;
        }

        if (!any)
        {
            Blank(xml, "table-cell", 1);
        }

        if (merges is not null && merges.Count == 0)
        {
            grid.MergesByRow.Remove(row);
        }

        xml.WriteEndElement();
    }

    private static void Blank(XmlWriter xml, string name, int count)
    {
        xml.WriteStartElement("table", name, TableNs.NamespaceName);
        if (count > 1)
        {
            xml.WriteAttributeString("table", "number-columns-repeated", TableNs.NamespaceName, count.ToString(CultureInfo.InvariantCulture));
        }

        xml.WriteEndElement();
    }

    private void Cell(XmlWriter xml, Worksheet sheet, Grid grid, int row, int column, string name, RangeAddress? merged, bool held)
    {
        xml.WriteStartElement("table", name, TableNs.NamespaceName);
        if (!held)
        {
            Span(xml, merged!.Value);
            xml.WriteEndElement();
            return;
        }

        ref readonly CellRecord record = ref sheet.Store.Get(row, column);
        (string styleName, OdsValueKind kind, string? code) = CellStyle(record.Style);
        FormulaEntry? entry = record.Kind == CellKind.Formula ? sheet.Formulas[(int)record.Payload] : null;
        CellValue value = record.Kind switch
        {
            CellKind.Number => CellValue.FromNumber(BitConverter.Int64BitsToDouble(record.Payload)),
            CellKind.SharedString => CellValue.TextUnchecked(book.SharedStrings[(int)record.Payload]),
            CellKind.Boolean => CellValue.FromBoolean(record.Payload != 0),
            CellKind.Error => CellValue.FromError((CellError)record.Payload),
            CellKind.Formula => entry!.Result,
            _ => CellValue.Empty,
        };
        if (value.Kind == CellValueKind.Boolean)
        {
            styleName = BooleanStyle(record.Style);
        }

        if (record.Style != 0 || value.Kind == CellValueKind.Boolean)
        {
            xml.WriteAttributeString("table", "style-name", TableNs.NamespaceName, styleName);
        }

        string? formula = null;
        if (grid.Operations.TryGetValue((row, column), out string? operation))
        {
            formula = operation;
        }
        else if (entry is not null && sheet.GetFormula(row, column) is { Length: > 0 } excel)
        {
            formula = OpenFormulaWriter.Translate(excel, sheet, new CellAddress(row, column), book => files.GetValueOrDefault(book))
                ?? "msoxl:=" + excel;
            if (entry.ArrayRange is { } array)
            {
                xml.WriteAttributeString("table", "number-matrix-columns-spanned", TableNs.NamespaceName, (array.Last.Column - array.First.Column + 1).ToString(CultureInfo.InvariantCulture));
                xml.WriteAttributeString("table", "number-matrix-rows-spanned", TableNs.NamespaceName, (array.Last.Row - array.First.Row + 1).ToString(CultureInfo.InvariantCulture));
            }
        }
        else if (record.Kind == CellKind.Error)
        {
            // An error typed into a cell is the formula of that error, as office suites keep it.
            formula = "of:=" + CellValue.ErrorText(value.Error);
        }

        if (formula is not null)
        {
            xml.WriteAttributeString("table", "formula", TableNs.NamespaceName, formula);
        }

        if (merged is { } range)
        {
            Span(xml, range);
        }

        string? shown;
        if (value.Kind == CellValueKind.Boolean && formula is "of:=TRUE()" or "of:=FALSE()")
        {
            // A boolean cell whose formula is TRUE() alone is how office suites write the constant TRUE, so this formula's
            // result goes as the number its boolean style shows, which keeps the formula a formula when read.
            xml.WriteAttributeString("office", "value-type", OfficeNs.NamespaceName, "float");
            xml.WriteAttributeString("office", "value", OfficeNs.NamespaceName, value.Boolean ? "1" : "0");
            shown = value.Boolean ? "TRUE" : "FALSE";
        }
        else
        {
            shown = Value(xml, value, kind, code);
        }

        if (shown is not null)
        {
            Paragraphs(xml, shown);
        }

        xml.WriteEndElement();
    }

    private static void Span(XmlWriter xml, RangeAddress range)
    {
        xml.WriteAttributeString("table", "number-columns-spanned", TableNs.NamespaceName, (range.Last.Column - range.First.Column + 1).ToString(CultureInfo.InvariantCulture));
        xml.WriteAttributeString("table", "number-rows-spanned", TableNs.NamespaceName, (range.Last.Row - range.First.Row + 1).ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>A cell's value type and value (ODF 1.3 Part 3, 19.385 to 19.389), and the text it shows; null for no value.</summary>
    private string? Value(XmlWriter xml, CellValue value, OdsValueKind kind, string? code)
    {
        switch (value.Kind)
        {
            case CellValueKind.Number:
                double number = value.Number;
                switch (kind)
                {
                    case OdsValueKind.Date when DateValue(number) is { } date:
                        xml.WriteAttributeString("office", "value-type", OfficeNs.NamespaceName, "date");
                        xml.WriteAttributeString("office", "date-value", OfficeNs.NamespaceName, date);
                        break;
                    case OdsValueKind.Time:
                        xml.WriteAttributeString("office", "value-type", OfficeNs.NamespaceName, "time");
                        xml.WriteAttributeString("office", "time-value", OfficeNs.NamespaceName, TimeValue(number));
                        break;
                    default:
                        xml.WriteAttributeString("office", "value-type", OfficeNs.NamespaceName, kind == OdsValueKind.Percentage ? "percentage" : "float");
                        xml.WriteAttributeString("office", "value", OfficeNs.NamespaceName, number.ToString("R", CultureInfo.InvariantCulture));
                        break;
                }

                xml.WriteAttributeString("calcext", "value-type", CalcExtNs.NamespaceName, kind switch
                {
                    OdsValueKind.Date => "date",
                    OdsValueKind.Time => "time",
                    OdsValueKind.Percentage => "percentage",
                    _ => "float",
                });
                return code is null ? NumberFormatCode.General.Format(value) : NumberFormatCode.Get(code).Format(value, null, book.Date1904);
            case CellValueKind.Text:
                xml.WriteAttributeString("office", "value-type", OfficeNs.NamespaceName, "string");
                xml.WriteAttributeString("calcext", "value-type", CalcExtNs.NamespaceName, "string");
                return value.Text;
            case CellValueKind.Boolean:
                xml.WriteAttributeString("office", "value-type", OfficeNs.NamespaceName, "boolean");
                xml.WriteAttributeString("office", "boolean-value", OfficeNs.NamespaceName, value.Boolean ? "true" : "false");
                xml.WriteAttributeString("calcext", "value-type", CalcExtNs.NamespaceName, "boolean");
                return value.Boolean ? "TRUE" : "FALSE";
            case CellValueKind.Error:
                xml.WriteAttributeString("office", "value-type", OfficeNs.NamespaceName, "string");
                xml.WriteAttributeString("office", "string-value", OfficeNs.NamespaceName, "");
                xml.WriteAttributeString("calcext", "value-type", CalcExtNs.NamespaceName, "error");
                return CellValue.ErrorText(value.Error);
            default:
                return null;
        }
    }

    /// <summary>A serial date as an xsd:dateTime from the null date, years counted astronomically; null past ±999,999 years.</summary>
    private string? DateValue(double serial)
    {
        double days = Math.Floor(serial);
        if (!double.IsFinite(serial) || Math.Abs(days) > 365_000_000)
        {
            return null;
        }

        long epoch = DaysFromCivil(nullDate.Year, nullDate.Month, nullDate.Day) + (long)days;
        (long year, int month, int day) = CivilFromDays(epoch);
        StringBuilder output = text.Clear();
        if (year < 0)
        {
            output.Append('-');
        }

        output.Append(Math.Abs(year).ToString("0000", CultureInfo.InvariantCulture)).Append('-')
            .Append(month.ToString("00", CultureInfo.InvariantCulture)).Append('-').Append(day.ToString("00", CultureInfo.InvariantCulture));
        double fraction = serial - days;
        if (fraction > 0)
        {
            output.Append('T');
            Clock(output, fraction * 86400);
        }

        return output.ToString();
    }

    /// <summary>A time of day or a duration in seconds as hh:mm:ss with the seconds' fraction, for a date's time.</summary>
    private static void Clock(StringBuilder output, double seconds)
    {
        int hours = (int)Math.Floor(seconds / 3600);
        seconds -= hours * 3600.0;
        int minutes = (int)Math.Floor(seconds / 60);
        seconds -= minutes * 60.0;
        output.Append(hours.ToString("00", CultureInfo.InvariantCulture)).Append(':').Append(minutes.ToString("00", CultureInfo.InvariantCulture)).Append(':');
        if (seconds < 10)
        {
            output.Append('0');
        }

        output.Append(Math.Max(0, seconds).ToString("0.###############", CultureInfo.InvariantCulture));
    }

    /// <summary>A number of days as an ISO 8601 duration such as <c>PT36H30M00S</c>, negative with a leading minus.</summary>
    private string TimeValue(double days)
    {
        StringBuilder output = text.Clear();
        if (days < 0)
        {
            output.Append('-');
            days = -days;
        }

        double seconds = days * 86400;
        long hours = (long)Math.Floor(seconds / 3600);
        seconds -= hours * 3600.0;
        int minutes = (int)Math.Floor(seconds / 60);
        seconds -= minutes * 60.0;
        output.Append("PT").Append(hours.ToString(CultureInfo.InvariantCulture)).Append('H')
            .Append(minutes.ToString("00", CultureInfo.InvariantCulture)).Append('M')
            .Append(Math.Max(0, seconds).ToString("00.###############", CultureInfo.InvariantCulture)).Append('S');
        return output.ToString();
    }

    private static long DaysFromCivil(long year, int month, int day)
    {
        year -= month <= 2 ? 1 : 0;
        long era = (year >= 0 ? year : year - 399) / 400;
        long yearOfEra = year - era * 400;
        long dayOfYear = (153 * (month + (month > 2 ? -3 : 9)) + 2) / 5 + day - 1;
        long dayOfEra = yearOfEra * 365 + yearOfEra / 4 - yearOfEra / 100 + dayOfYear;
        return era * 146097 + dayOfEra - 719468;
    }

    /// <summary>The proleptic Gregorian date of a day counted from 1970-01-01, by the civil calendar's 400-year cycle.</summary>
    private static (long Year, int Month, int Day) CivilFromDays(long days)
    {
        days += 719468;
        long era = (days >= 0 ? days : days - 146096) / 146097;
        long dayOfEra = days - era * 146097;
        long yearOfEra = (dayOfEra - dayOfEra / 1460 + dayOfEra / 36524 - dayOfEra / 146096) / 365;
        long year = yearOfEra + era * 400;
        long dayOfYear = dayOfEra - (365 * yearOfEra + yearOfEra / 4 - yearOfEra / 100);
        long mp = (5 * dayOfYear + 2) / 153;
        int day = (int)(dayOfYear - (153 * mp + 2) / 5 + 1);
        int month = (int)(mp < 10 ? mp + 3 : mp - 9);
        return (year + (month <= 2 ? 1 : 0), month, day);
    }

    /// <summary>A cell's text as paragraphs, one a line, with runs of spaces and tabs as text:s and text:tab.</summary>
    private static void Paragraphs(XmlWriter xml, string value)
    {
        int start = 0;
        while (true)
        {
            int end = value.IndexOf('\n', start);
            ReadOnlySpan<char> line = end < 0 ? value.AsSpan(start) : value.AsSpan(start, end - start);
            if (line.EndsWith("\r"))
            {
                line = line[..^1];
            }

            xml.WriteStartElement("text", "p", TextNs.NamespaceName);
            Line(xml, line);
            xml.WriteEndElement();
            if (end < 0)
            {
                break;
            }

            start = end + 1;
        }
    }

    private static void Line(XmlWriter xml, ReadOnlySpan<char> line)
    {
        int run = 0;
        for (int i = 0; i < line.Length;)
        {
            char c = line[i];
            if (c == '\t')
            {
                Flush(xml, line, run, i);
                xml.WriteStartElement("text", "tab", TextNs.NamespaceName);
                xml.WriteEndElement();
                run = ++i;
                continue;
            }

            if (c == ' ' && (i == 0 || line[i - 1] == ' ' || i == line.Length - 1 || line[i - 1] == '\t'))
            {
                // White space XML would fold: a leading, trailing or second space is text:s.
                Flush(xml, line, run, i);
                int count = 1;
                while (i + count < line.Length && line[i + count] == ' ')
                {
                    count++;
                }

                xml.WriteStartElement("text", "s", TextNs.NamespaceName);
                if (count > 1)
                {
                    xml.WriteAttributeString("text", "c", TextNs.NamespaceName, count.ToString(CultureInfo.InvariantCulture));
                }

                xml.WriteEndElement();
                i += count;
                run = i;
                continue;
            }

            if (char.IsControl(c) && c is not ('\t' or '\n' or '\r'))
            {
                // XML 1.0 cannot hold other control characters; they are left out.
                Flush(xml, line, run, i);
                run = ++i;
                continue;
            }

            i++;
        }

        Flush(xml, line, run, line.Length);
    }

    private static void Flush(XmlWriter xml, ReadOnlySpan<char> line, int from, int to)
    {
        if (to > from)
        {
            xml.WriteString(line[from..to].ToString());
        }
    }

    // ----- Names -----

    /// <summary>
    /// The workbook's names (ODF 1.3 Part 3, 9.4): a plain range as table:named-range, anything else as table:named-expression;
    /// Excel's built-in names other than the print area as office suites name them, <c>Excel_BuiltIn_</c> and the name.
    /// </summary>
    private void Names(XmlWriter xml, Worksheet? scope)
    {
        bool open = false;
        foreach (DefinedName name in book.Names)
        {
            if (name.Sheet != scope || name.Name.Equals("_xlnm.Print_Area", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string written = name.Name.StartsWith("_xlnm.", StringComparison.OrdinalIgnoreCase) ? "Excel_BuiltIn_" + name.Name[6..] : name.Name;
            string formula = name.Formula.StartsWith('=') ? name.Formula[1..] : name.Formula;
            string baseCell = "$" + OpenFormulaWriter.SheetName(scope?.Name ?? book.Worksheets[0].Name) + ".$A$1";
            XElement? element = null;
            if (FormulaParser.TryParse(formula, r1c1: false, default)?.Root is ReferenceNode { Prefix: { Book: null, FirstSheet: { } first, LastSheet: var lastSheet }, Area: { Kind: AreaKind.Cell or AreaKind.Range } area }
                && (lastSheet is null || lastSheet == first))
            {
                element = new XElement(TableNs + "named-range",
                    new XAttribute(TableNs + "name", written),
                    new XAttribute(TableNs + "base-cell-address", baseCell),
                    new XAttribute(TableNs + "cell-range-address", Address(first, area)));
            }
            else if (OpenFormulaWriter.Translate(formula, scope, default, book => files.GetValueOrDefault(book)) is { } expression)
            {
                element = new XElement(TableNs + "named-expression",
                    new XAttribute(TableNs + "name", written),
                    new XAttribute(TableNs + "base-cell-address", baseCell),
                    new XAttribute(TableNs + "expression", expression));
            }

            if (element is null)
            {
                continue;
            }

            if (!open)
            {
                xml.WriteStartElement("table", "named-expressions", TableNs.NamespaceName);
                open = true;
            }

            element.WriteTo(xml);
        }

        if (open)
        {
            xml.WriteEndElement();
        }
    }

    /// <summary>A reference's area as a cell range address such as <c>$Sheet1.$A$1:.$B$2</c>, its anchors kept.</summary>
    private static string Address(string sheet, Area area)
    {
        StringBuilder output = new();
        string name = "$" + OpenFormulaWriter.SheetName(sheet) + ".";
        output.Append(name);
        Corner(output, area.FirstRow, area.FirstColumn, area.FirstRowAbsolute, area.FirstColumnAbsolute);
        if (area.Kind == AreaKind.Range)
        {
            output.Append(':').Append(name);
            Corner(output, area.LastRow, area.LastColumn, area.LastRowAbsolute, area.LastColumnAbsolute);
        }

        return output.ToString();
    }

    private static void Corner(StringBuilder output, int row, int column, bool rowAbsolute, bool columnAbsolute)
    {
        output.Append(columnAbsolute ? "$" : "").Append(CellAddress.ColumnName(column)).Append(rowAbsolute ? "$" : "").Append((row + 1).ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>A print area's ranges as table:print-ranges holds them, separated by spaces; null when one is not a plain range.</summary>
    private static string? PrintRanges(DefinedName print)
    {
        string formula = print.Formula.StartsWith('=') ? print.Formula[1..] : print.Formula;
        List<string> parts = [];
        foreach (string part in TopLevel(formula))
        {
            if (FormulaParser.TryParse(part, r1c1: false, default)?.Root is not ReferenceNode { Prefix: { Book: null, FirstSheet: { } sheet }, Area: { Kind: AreaKind.Cell or AreaKind.Range } area })
            {
                return null;
            }

            parts.Add(Address(sheet, area));
        }

        return parts.Count == 0 ? null : string.Join(' ', parts);
    }

    /// <summary>The parts of a list such as <c>Sheet1!$A$1:$B$2,'My sheet'!$C$3</c>, split at the commas outside quotes.</summary>
    private static List<string> TopLevel(string list)
    {
        List<string> parts = [];
        bool quoted = false;
        int start = 0;
        for (int i = 0; i <= list.Length; i++)
        {
            if (i < list.Length && list[i] == '\'')
            {
                quoted = !quoted;
            }
            else if (i == list.Length || (list[i] == ',' && !quoted))
            {
                parts.Add(list[start..i].Trim());
                start = i + 1;
            }
        }

        return parts;
    }
}
