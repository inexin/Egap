using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

using Egap.Documents;
using Egap.Documents.Odf;
using Egap.Spreadsheets.Storage;

using static Egap.Documents.Odf.OdfNames;

namespace Egap.Spreadsheets.Ods;

/// <summary>
/// Reads an OpenDocument spreadsheet (ODS, OTS or flat FODS; ODF 1.3 Part 3, chapter 9) into a <see cref="Workbook"/>: its
/// tables as worksheets, cells with their values, formulas translated from OpenFormula, styles and number formats, merged
/// and array ranges, column widths and row heights, hidden and grouped rows and columns, and named ranges and expressions.
/// </summary>
internal static class OdsReader
{
    /// <summary>A run of empty rows or cells longer than this is the filler ODF writers put to the sheet's end, and is skipped.</summary>
    private const int FillerRun = 1024;

    /// <summary>The most cells a file may make, against a small file whose repeated runs fill the whole sheet.</summary>
    private const long MaxRepeatedCells = 1L << 26;

    private static readonly XNamespace CalcExtNs = "urn:org:documentfoundation:names:experimental:calc:xmlns:calcext:1.0";

    public static void Read(Workbook book, byte[] bytes, DocumentLimits limits)
    {
        OdfPackage package = OdfPackage.Read(bytes, limits, spreadsheet: true);
        XElement spreadsheet = package.Content.Element(OfficeNs + "body")?.Element(OfficeNs + "spreadsheet")
            ?? throw new DocumentException("The OpenDocument spreadsheet has no office:spreadsheet body.");
        book.Format = WorkbookFormat.Xlsx;

        DateTime nullDate = new(1899, 12, 30);
        if (spreadsheet.Element(TableNs + "calculation-settings")?.Element(TableNs + "null-date")?.Attr(TableNs, "date-value") is { } nullText
            && DateTime.TryParse(nullText, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed))
        {
            nullDate = parsed.Date;
            book.Date1904 = nullDate == new DateTime(1904, 1, 1);
        }

        OdsStyles styles = new(book, package.Styles, package.IsFlat ? null : package.Content);
        Context context = new(book, styles, nullDate);
        List<(Worksheet Sheet, XElement Table)> sheets = [];
        foreach (XElement table in spreadsheet.Elements(TableNs + "table"))
        {
            // A copy of another file's sheet that office suites keep for the formulas that refer to it, named for the file and
            // sheet, is not a sheet of this workbook.
            if (table.Element(TableNs + "table-source") is not null && table.Attr(TableNs, "name") is ['\'', ..] name && name.Contains("'#", StringComparison.Ordinal))
            {
                continue;
            }

            Worksheet sheet = AddSheet(book, table.Attr(TableNs, "name"));
            sheets.Add((sheet, table));
            ReadTable(context, sheet, table);
        }

        if (sheets.Count == 0)
        {
            throw new DocumentException("The OpenDocument spreadsheet has no tables.");
        }

        ReadNames(book, spreadsheet.Element(TableNs + "named-expressions"), null);
        foreach ((Worksheet sheet, XElement table) in sheets)
        {
            ReadNames(book, table.Element(TableNs + "named-expressions"), sheet);
            if (table.Attr(TableNs, "print-ranges") is { Length: > 0 } print && Ranges(print) is { } area
                && !book.Names.Any(n => n.Name == "_xlnm.Print_Area" && n.Sheet == sheet))
            {
                book.Names.AddRead(new DefinedName("_xlnm.Print_Area", area, sheet));
            }
        }

        if (book.Worksheets.All(s => s.Visibility != SheetVisibility.Visible))
        {
            book.Worksheets[0].Visibility = SheetVisibility.Visible;
        }
    }

    /// <summary>Whether a file's first bytes are an ODS or OTS package, or a flat FODS document.</summary>
    public static bool Sniff(ReadOnlySpan<byte> head) =>
        IsSpreadsheet(OdfPackage.MediaTypeOfZip(head)) || OdfPackage.IsFlatSpreadsheet(head);

    private static Worksheet AddSheet(Workbook book, string? name)
    {
        try
        {
            return book.Worksheets.Add(string.IsNullOrEmpty(name) || book.Worksheets.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ? null : name);
        }
        catch (ArgumentException)
        {
            // A name Excel does not allow, such as one longer than 31 characters or with a bracket: the sheet takes a default name.
            return book.Worksheets.Add(null);
        }
    }

    private sealed class Context(Workbook book, OdsStyles styles, DateTime nullDate)
    {
        public Workbook Book { get; } = book;

        public OdsStyles Styles { get; } = styles;

        public DateTime NullDate { get; } = nullDate;

        public long Repeated;

        public StringBuilder Text { get; } = new();
    }

    private sealed class TableState(Worksheet sheet)
    {
        public Worksheet Sheet { get; } = sheet;

        public int Row;

        public int Column;

        /// <summary>Each column's default cell style index, for its cells without a style of their own.</summary>
        public List<(int First, int Last, int Style)> ColumnStyles { get; } = [];

        public int ColumnStyle(int column)
        {
            foreach ((int first, int last, int style) in ColumnStyles)
            {
                if (column >= first && column <= last)
                {
                    return style;
                }
            }

            return 0;
        }
    }

    private static void ReadTable(Context context, Worksheet sheet, XElement table)
    {
        (bool hidden, Egami.Pixels.Rgba8? tab) = context.Styles.Table(table.Attr(TableNs, "style-name"));
        if (hidden)
        {
            sheet.Visibility = SheetVisibility.Hidden;
        }

        if (tab is { } colour)
        {
            sheet.TabColor = CellColor.FromRgb(colour.R, colour.G, colour.B);
        }

        TableState state = new(sheet);
        Columns(context, state, table, 0);
        Rows(context, state, table, 0);
    }

    /// <summary>The table's columns, inside their groups and header sections, in order.</summary>
    private static void Columns(Context context, TableState state, XElement parent, int level)
    {
        foreach (XElement element in parent.Elements())
        {
            if (element.Name.Namespace != TableNs)
            {
                continue;
            }

            switch (element.Name.LocalName)
            {
                case "table-column-group":
                    Columns(context, state, element, Math.Min(level + 1, 7));
                    break;
                case "table-header-columns" or "table-columns":
                    Columns(context, state, element, level);
                    break;
                case "table-column":
                    int count = Math.Max(1, OdfUnits.Int(element.Attr(TableNs, "number-columns-repeated")) ?? 1);
                    int first = state.Column;
                    if (first >= CellAddress.MaxColumns)
                    {
                        break;
                    }

                    int last = (int)Math.Min((long)first + count - 1, CellAddress.MaxColumns - 1);
                    state.Column = last + 1;
                    int style = element.Attr(TableNs, "default-cell-style-name") is { } styleName ? context.Styles.Index(styleName) : 0;
                    if (style != 0)
                    {
                        state.ColumnStyles.Add((first, last, style));
                    }

                    double? width = context.Styles.ColumnWidth(element.Attr(TableNs, "style-name"));
                    bool hidden = element.Attr(TableNs, "visibility") is "collapse" or "filter";
                    if (width is null && !hidden && level == 0 && style == 0)
                    {
                        break;
                    }

                    state.Sheet.ColumnSpans.AddRead(new ColumnSpan
                    {
                        First = first,
                        Last = last,
                        Width = width is double points ? Characters(points) : double.NaN,
                        Hidden = hidden,
                        OutlineLevel = level,
                        Style = style == 0 ? -1 : style,
                    });
                    break;
            }
        }

        if (level == 0)
        {
            state.Column = 0;
        }
    }

    /// <summary>A column width in points as Excel's width in characters: seven pixels a character and five of padding, at 96 dpi.</summary>
    private static double Characters(double points)
    {
        double pixels = points * 96 / 72;
        return Math.Clamp(Math.Round(Math.Max(0, pixels - 5) / 7 * 256) / 256, 0, 255);
    }

    private static void Rows(Context context, TableState state, XElement parent, int level)
    {
        foreach (XElement element in parent.Elements())
        {
            if (element.Name.Namespace != TableNs)
            {
                continue;
            }

            switch (element.Name.LocalName)
            {
                case "table-row-group":
                    Rows(context, state, element, Math.Min(level + 1, 7));
                    break;
                case "table-header-rows" or "table-rows":
                    Rows(context, state, element, level);
                    break;
                case "table-row":
                    Row(context, state, element, level);
                    break;
            }
        }
    }

    private static void Row(Context context, TableState state, XElement row, int level)
    {
        int count = Math.Max(1, OdfUnits.Int(row.Attr(TableNs, "number-rows-repeated")) ?? 1);
        int first = state.Row;
        if (first >= CellAddress.MaxRows)
        {
            return;
        }

        int last = (int)Math.Min((long)first + count - 1, CellAddress.MaxRows - 1);
        state.Row = last + 1;
        bool hidden = row.Attr(TableNs, "visibility") is "collapse" or "filter";
        bool empty = !row.Elements().Any(c => HasContent(c));
        if (empty && count > FillerRun)
        {
            return;
        }

        (double? height, bool custom) = context.Styles.RowHeight(row.Attr(TableNs, "style-name"));
        int rowStyle = row.Attr(TableNs, "default-cell-style-name") is { } styleName ? context.Styles.Index(styleName) : 0;
        if (hidden || custom || level > 0 || rowStyle != 0)
        {
            for (int index = first; index <= last; index++)
            {
                state.Sheet.RowRecords[index] = new RowRecord
                {
                    Height = custom && height is double points ? Math.Round(points * 4) / 4 : double.NaN,
                    Hidden = hidden,
                    OutlineLevel = level,
                    Style = rowStyle == 0 ? -1 : rowStyle,
                };
            }
        }

        // Cells are read once for the first row of the run and copied to the others.
        int column = 0;
        foreach (XElement cell in row.Elements())
        {
            if (cell.Name.Namespace != TableNs || cell.Name.LocalName is not ("table-cell" or "covered-table-cell"))
            {
                continue;
            }

            int repeat = Math.Max(1, OdfUnits.Int(cell.Attr(TableNs, "number-columns-repeated")) ?? 1);
            if (column >= CellAddress.MaxColumns)
            {
                break;
            }

            int lastColumn = (int)Math.Min((long)column + repeat - 1, CellAddress.MaxColumns - 1);
            Cell(context, state, cell, first, last, column, lastColumn, rowStyle);
            column = lastColumn + 1;
        }
    }

    private static bool HasContent(XElement cell) =>
        cell.HasElements || cell.Attribute(OfficeNs + "value-type") is not null || cell.Attribute(TableNs + "formula") is not null
        || cell.Attribute(TableNs + "number-columns-spanned") is not null;

    private static void Cell(Context context, TableState state, XElement cell, int firstRow, int lastRow, int firstColumn, int lastColumn, int rowStyle)
    {
        string? styleName = cell.Attr(TableNs, "style-name");
        bool content = HasContent(cell);
        bool covered = cell.Name.LocalName == "covered-table-cell";
        long cells = (long)(lastRow - firstRow + 1) * (lastColumn - firstColumn + 1);
        if (!content && styleName is null)
        {
            return;
        }

        // An empty cell only carries a style. Long runs of them are whole rows formatted, which the model keeps no cells for,
        // and a run in the style its row or columns give anyway adds nothing.
        if (!content && (lastColumn - firstColumn >= FillerRun || cells > FillerRun * 16
            || (context.Styles.Index(styleName) == rowStyle && !state.ColumnStyles.Any(c => c.First <= lastColumn && c.Last >= firstColumn))))
        {
            return;
        }

        CellRecord record = default;
        FormulaEntry? formula = null;
        CellValue result = CellValue.Empty;
        if (content && !covered)
        {
            result = Value(context, cell, out bool text);
            // LibreOffice writes a TRUE or FALSE typed into a cell as the formula TRUE() or FALSE() of a boolean cell: those
            // are values. A cell that only shows a number as TRUE or FALSE keeps the formula as it was written.
            if (cell.Attr(TableNs, "formula") is { } source && OpenFormula.ToExcel(source, out _) is { Length: > 0 } excel
                && !(cell.Attr(OfficeNs, "value-type") == "boolean" && excel is "TRUE" or "FALSE"))
            {
                if (excel is "TRUE" or "FALSE")
                {
                    excel += "()";
                    result = CellValue.FromBoolean(excel == "TRUE()");
                }

                formula = new FormulaEntry(excel, result);
                int matrixColumns = OdfUnits.Int(cell.Attr(TableNs, "number-matrix-columns-spanned")) ?? 0;
                int matrixRows = OdfUnits.Int(cell.Attr(TableNs, "number-matrix-rows-spanned")) ?? 0;
                if (matrixColumns > 0 && matrixRows > 0)
                {
                    RangeAddress range = new(firstRow, firstColumn, Math.Min(firstRow + matrixRows - 1, CellAddress.MaxRows - 1),
                        Math.Min(firstColumn + matrixColumns - 1, CellAddress.MaxColumns - 1));
                    formula.Attributes = [new XAttribute("t", "array"), new XAttribute("ref", range.ToString())];
                }
            }
            else
            {
                Store(context.Book, result, text, ref record);
            }

            int spanColumns = OdfUnits.Int(cell.Attr(TableNs, "number-columns-spanned")) ?? 1;
            int spanRows = OdfUnits.Int(cell.Attr(TableNs, "number-rows-spanned")) ?? 1;
            if ((spanColumns > 1 || spanRows > 1) && firstRow == lastRow && firstColumn == lastColumn)
            {
                state.Sheet.MergedCells.AddRead(new RangeAddress(firstRow, firstColumn,
                    Math.Min(firstRow + Math.Max(1, spanRows) - 1, CellAddress.MaxRows - 1),
                    Math.Min(firstColumn + Math.Max(1, spanColumns) - 1, CellAddress.MaxColumns - 1)));
            }
        }
        else if (content && covered)
        {
            // A covered cell's value is kept, as Excel keeps the values under a merged range.
            result = Value(context, cell, out bool text);
            Store(context.Book, result, text, ref record);
        }

        if (record.Kind == CellKind.Empty && formula is null && styleName is null)
        {
            return;
        }

        if ((record.Kind != CellKind.Empty || formula is not null) && context.Repeated + cells > MaxRepeatedCells)
        {
            throw new DocumentLimitException($"The spreadsheet makes more than {MaxRepeatedCells:N0} cells.");
        }

        for (int column = firstColumn; column <= lastColumn; column++)
        {
            int style = styleName is not null ? context.Styles.Index(styleName) : rowStyle != 0 ? rowStyle : state.ColumnStyle(column);
            if (record.Kind == CellKind.Empty && formula is null && style == (rowStyle != 0 ? rowStyle : state.ColumnStyle(column)))
            {
                continue;
            }

            record.Style = style;
            context.Repeated += lastRow - firstRow + 1;
            if (context.Repeated > MaxRepeatedCells)
            {
                throw new DocumentLimitException($"The spreadsheet makes more than {MaxRepeatedCells:N0} cells.");
            }

            for (int row = firstRow; row <= lastRow; row++)
            {
                FormulaEntry? entry = formula is null ? null : row == firstRow && column == firstColumn
                    ? formula : new FormulaEntry(formula.Text, formula.Result) { Attributes = formula.Attributes };
                state.Sheet.SetRead(row, column, record, entry);
            }
        }
    }

    private static void Store(Workbook book, CellValue value, bool text, ref CellRecord record)
    {
        switch (value.Kind)
        {
            case CellValueKind.Number:
                record.Kind = CellKind.Number;
                record.Payload = BitConverter.DoubleToInt64Bits(value.Number == 0 ? 0 : value.Number);
                break;
            case CellValueKind.Boolean:
                record.Kind = CellKind.Boolean;
                record.Payload = value.Boolean ? 1 : 0;
                break;
            case CellValueKind.Error:
                record.Kind = CellKind.Error;
                record.Payload = (long)value.Error;
                break;
            case CellValueKind.Text when text:
                record.Kind = CellKind.SharedString;
                record.Payload = book.SharedStrings.Add(value.Text);
                break;
        }
    }

    /// <summary>A cell's value as its value type gives it (ODF 1.3 Part 3, 19.385), or its text for a cell without one.</summary>
    private static CellValue Value(Context context, XElement cell, out bool text)
    {
        text = false;
        string? type = cell.Attr(OfficeNs, "value-type");
        if (cell.Attr(CalcExtNs, "value-type") == "error")
        {
            string shown = Paragraphs(context, cell);
            return CellValue.TryParseError(shown, out CellError error) ? CellValue.FromError(error) : CellValue.FromError(CellError.Value);
        }

        switch (type)
        {
            case "float" or "percentage" or "currency":
                if (!double.TryParse(cell.Attr(OfficeNs, "value"), NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number))
                {
                    return CellValue.Empty;
                }

                // A number shown with a boolean data style is a boolean, as office suites keep the results of logical formulas.
                return context.Styles.IsBoolean(cell.Attr(TableNs, "style-name")) ? CellValue.FromBoolean(number != 0) : CellValue.FromNumber(number);
            case "date":
                return Date(cell.Attr(OfficeNs, "date-value"), context.NullDate) is double serial ? CellValue.FromNumber(serial) : CellValue.Empty;
            case "time":
                return Time(cell.Attr(OfficeNs, "time-value")) is double days ? CellValue.FromNumber(days) : CellValue.Empty;
            case "boolean":
                return CellValue.FromBoolean(cell.Attr(OfficeNs, "boolean-value") is "true" or "1");
            case "string":
            case null when cell.Element(TextNs + "p") is not null:
                string value = cell.Attr(OfficeNs, "string-value") ?? Paragraphs(context, cell);
                if (value.Length == 0 && type is null)
                {
                    return CellValue.Empty;
                }

                text = true;
                return CellValue.FromText(value.Length > CellValue.MaxTextLength ? value[..CellValue.MaxTextLength] : value);
            default:
                return CellValue.Empty;
        }
    }

    /// <summary>A date value, with or without a time, as days since the file's null date.</summary>
    public static double? Date(string? value, DateTime nullDate)
    {
        if (value is null || !DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind | DateTimeStyles.AllowWhiteSpaces, out DateTime date))
        {
            return null;
        }

        return (date - nullDate).TotalDays;
    }

    /// <summary>A time value, an ISO 8601 duration such as <c>PT12H30M00S</c>, as a fraction of a day.</summary>
    public static double? Time(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        try
        {
            return XmlConvert.ToTimeSpan(value).TotalDays;
        }
        catch (FormatException)
        {
            return null;
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    /// <summary>A cell's paragraphs as text, one line each.</summary>
    private static string Paragraphs(Context context, XElement cell)
    {
        StringBuilder text = context.Text.Clear();
        bool first = true;
        foreach (XElement paragraph in cell.Elements(TextNs + "p"))
        {
            if (!first)
            {
                text.Append('\n');
            }

            first = false;
            Inline(text, paragraph, 0);
            if (text.Length > CellValue.MaxTextLength)
            {
                break;
            }
        }

        return text.ToString();
    }

    private static void Inline(StringBuilder text, XElement element, int depth)
    {
        foreach (XNode node in element.Nodes())
        {
            if (node is XText run)
            {
                text.Append(run.Value);
            }
            else if (node is XElement child && child.Name.Namespace == TextNs && depth < 32)
            {
                switch (child.Name.LocalName)
                {
                    case "s":
                        text.Append(' ', Math.Clamp(OdfUnits.Int(child.Attr(TextNs, "c")) ?? 1, 1, 1024));
                        break;
                    case "tab":
                        text.Append('\t');
                        break;
                    case "line-break":
                        text.Append('\n');
                        break;
                    case "note" or "annotation" or "bookmark" or "bookmark-start" or "bookmark-end" or "soft-page-break":
                        break;
                    default:
                        Inline(text, child, depth + 1);
                        break;
                }
            }
        }
    }

    /// <summary>Named ranges and expressions (ODF 1.3 Part 3, 9.4), global or local to a sheet, as defined names.</summary>
    private static void ReadNames(Workbook book, XElement? names, Worksheet? sheet)
    {
        foreach (XElement element in names?.Elements() ?? [])
        {
            string? name = element.Attr(TableNs, "name");

            // Excel's built-in names, such as Print_Titles, as LibreOffice names them when it converts a workbook.
            if (name is not null && name.StartsWith("Excel_BuiltIn_", StringComparison.Ordinal))
            {
                name = "_xlnm." + name["Excel_BuiltIn_".Length..];
            }

            string? formula = element.Name.LocalName switch
            {
                "named-range" => element.Attr(TableNs, "cell-range-address") is { } address ? Ranges(address) : null,
                "named-expression" => element.Attr(TableNs, "expression") is { } expression ? OpenFormula.ToExcel(expression, out _) : null,
                _ => null,
            };
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(formula) || book.Names.Any(n => n.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && n.Sheet == sheet))
            {
                continue;
            }

            try
            {
                book.Names.AddRead(new DefinedName(name, formula, sheet));
            }
            catch (ArgumentException)
            {
                // A name Excel does not allow is left out.
            }
        }
    }

    /// <summary>A list of cell range addresses, as table:cell-range-address and table:print-ranges hold them, as Excel's formula text.</summary>
    private static string? Ranges(string addresses)
    {
        List<string> parts = [];
        int start = 0;
        bool quoted = false;
        for (int i = 0; i <= addresses.Length; i++)
        {
            if (i < addresses.Length && addresses[i] == '\'')
            {
                quoted = !quoted;
            }
            else if (i == addresses.Length || (addresses[i] == ' ' && !quoted))
            {
                if (i > start)
                {
                    parts.Add(OpenFormula.ToExcel("[" + addresses[start..i] + "]", out _) ?? "#REF!");
                }

                start = i + 1;
            }
        }

        return parts.Count == 0 ? null : string.Join(',', parts);
    }
}
