using System.Globalization;
using System.Xml.Linq;

using Egap.Documents.Docx;
using Egap.Spreadsheets.Storage;
using Egap.Spreadsheets.Xls;
using Egap.Spreadsheets.Xlsx;

namespace Egap.Spreadsheets.Xlsb;

/// <summary>The records of one worksheet part ([MS-XLSB] 2.1.7.62): cells, formulas, rows and columns, and what the sheet holds besides.</summary>
internal sealed class XlsbSheet(XlsbWorkbook.State state, Worksheet sheet, OpcPart part)
{
    private static readonly XNamespace Ns = XlsxNames.Main;

    private readonly Workbook book = state.Book;
    private readonly List<(RangeAddress Range, byte[] Tokens, byte[] Extra)> sharedFormulas = [];
    private readonly Dictionary<(int, int), List<int>> sharedByFirstCell = [];
    private readonly Dictionary<(int, int), (RangeAddress Range, byte[] Tokens, byte[] Extra)> arrays = [];
    private readonly List<Pending> pending = [];
    private int row = -1;
    private int cellMeta;
    private bool inView;
    private bool firstView = true;
    private XElement? conditional;
    private XElement? rule;
    private XElement? ruleChild;
    private (int Row, int Column) conditionalBase;
    private bool rowBreaks;
    private string? vmlId;
    private string? drawingId;

    private sealed class Pending
    {
        public int Row;
        public int Column;
        public int Style;
        public (int Row, int Column) Anchor;
        public CellValue Result;
        public bool Dynamic;
    }

    public void Read()
    {
        XlsbRecords records = new(part.Data);
        while (records.Next())
        {
            XlsbReader record = records.Reader;
            Record(records.Type, ref record);
        }

        Resolve();
        HashSet<CellAddress> visible = state.Target(part, vmlId) is { } vml ? SheetObjectReader.VisibleNotes(vml) : [];
        state.ReadComments(sheet, part, visible);
        if (state.Target(part, drawingId) is { } drawing)
        {
            XlsbDrawings.Read(sheet, drawing, state.Package);
        }
    }

    private void Record(int type, ref XlsbReader record)
    {
        switch (type)
        {
            case 0: // BrtRowHdr
                Row(ref record);
                break;
            case 1: // BrtCellBlank
                Set(ref record, CellValue.Empty);
                break;
            case 2: // BrtCellRk
                {
                    (int column, int style) = Cell(ref record);
                    Set(column, style, Number(Rk(record.UInt32())));
                    break;
                }

            case 3: // BrtCellError
                {
                    (int column, int style) = Cell(ref record);
                    Set(column, style, CellValue.FromError(XlsFormula.ErrorValue(record.Byte())));
                    break;
                }

            case 4: // BrtCellBool
                {
                    (int column, int style) = Cell(ref record);
                    Set(column, style, CellValue.FromBoolean(record.Byte() != 0));
                    break;
                }

            case 5: // BrtCellReal
                {
                    (int column, int style) = Cell(ref record);
                    Set(column, style, Number(record.Double()));
                    break;
                }

            case 6: // BrtCellSt
                {
                    (int column, int style) = Cell(ref record);
                    Set(column, style, CellValue.FromText(Cap(record.WideString())));
                    break;
                }

            case 7: // BrtCellIsst
                {
                    (int column, int style) = Cell(ref record);
                    uint index = record.UInt32();
                    if (index < (uint)book.SharedStrings.Count && (uint)row < CellAddress.MaxRows && (uint)column < CellAddress.MaxColumns)
                    {
                        sheet.SetRead(row, column, new CellRecord { Style = style, Kind = CellKind.SharedString, Payload = index }, null);
                    }

                    break;
                }

            case 62: // BrtCellRString
                {
                    (int column, int style) = Cell(ref record);
                    (string text, _) = state.RichText(ref record, "si");
                    Set(column, style, CellValue.FromText(Cap(text)));
                    break;
                }

            case 8: // BrtFmlaString
                {
                    (int column, int style) = Cell(ref record);
                    Formula(column, style, CellValue.FromText(Cap(record.WideString())), ref record);
                    break;
                }

            case 9: // BrtFmlaNum
                {
                    (int column, int style) = Cell(ref record);
                    Formula(column, style, Number(record.Double()), ref record);
                    break;
                }

            case 10: // BrtFmlaBool
                {
                    (int column, int style) = Cell(ref record);
                    Formula(column, style, CellValue.FromBoolean(record.Byte() != 0), ref record);
                    break;
                }

            case 11: // BrtFmlaError
                {
                    (int column, int style) = Cell(ref record);
                    Formula(column, style, CellValue.FromError(XlsFormula.ErrorValue(record.Byte())), ref record);
                    break;
                }

            case 49: // BrtCellMeta: the next cell's metadata, which marks a dynamic array formula.
                cellMeta = (int)Math.Min(record.UInt32(), int.MaxValue);
                return;
            case 427: // BrtShrFmla
                {
                    (int r1, int r2, int c1, int c2) = record.Range();
                    (byte[] tokens, byte[] extra) = record.Formula();
                    if (Valid(r1, r2, c1, c2))
                    {
                        if (!sharedByFirstCell.TryGetValue((r1, c1), out List<int>? list))
                        {
                            sharedByFirstCell[(r1, c1)] = list = [];
                        }

                        list.Add(sharedFormulas.Count);
                        sharedFormulas.Add((new RangeAddress(r1, c1, r2, c2), tokens, extra));
                    }

                    break;
                }

            case 426: // BrtArrFmla
                {
                    (int r1, int r2, int c1, int c2) = record.Range();
                    record.Byte();
                    (byte[] tokens, byte[] extra) = record.Formula();
                    if (Valid(r1, r2, c1, c2))
                    {
                        arrays[(r1, c1)] = (new RangeAddress(r1, c1, r2, c2), tokens, extra);
                    }

                    break;
                }

            case 147: // BrtWsProp
                Properties(ref record);
                break;
            case 137: // BrtBeginWsView
                View(ref record);
                break;
            case 138: // BrtEndWsView
                inView = false;
                break;
            case 151 when inView: // BrtPane
                Pane(ref record);
                break;
            case 485: // BrtWsFmtInfo
                FormatInfo(ref record);
                break;
            case 60: // BrtColInfo
                Column(ref record);
                break;
            case 176: // BrtMergeCell
                {
                    (int r1, int r2, int c1, int c2) = record.Range();
                    if (Valid(r1, r2, c1, c2) && (r1 != r2 || c1 != c2))
                    {
                        sheet.MergedCells.AddRead(new RangeAddress(r1, c1, r2, c2));
                    }

                    break;
                }

            case 494: // BrtHLink
                Hyperlink(ref record);
                break;
            case 535: // BrtSheetProtection
                Protection(ref record);
                break;
            case 573: // BrtBeginDVals
                sheet.DataValidations.Source = new XElement(Ns + "dataValidations");
                break;
            case 64: // BrtDVal
                Validation(ref record);
                break;
            case 461: // BrtBeginConditionalFormatting
                BeginConditionalFormat(ref record);
                break;
            case 463 when conditional is not null: // BrtBeginCFRule
                BeginRule(ref record);
                break;
            case 469 when rule is not null: // BrtBeginColorScale
                rule.Add(ruleChild = new XElement(Ns + "colorScale"));
                break;
            case 467 when rule is not null: // BrtBeginDatabar
                {
                    int min = record.Byte(), max = record.Byte();
                    bool show = record.Byte() != 0;
                    rule.Add(ruleChild = new XElement(Ns + "dataBar", min != 10 ? new XAttribute("minLength", Integer(min)) : null,
                        max != 90 ? new XAttribute("maxLength", Integer(max)) : null, show ? null : new XAttribute("showValue", "0")));
                    break;
                }

            case 465 when rule is not null: // BrtBeginIconSet
                {
                    int set = record.Int32();
                    int flags = record.UInt16();
                    rule.Add(ruleChild = new XElement(Ns + "iconSet", set != 3 && IconSetName(set) is { } name ? new XAttribute("iconSet", name) : null,
                        (flags & 0x02) != 0 ? new XAttribute("showValue", "0") : null, (flags & 0x04) != 0 ? new XAttribute("reverse", "1") : null));
                    break;
                }

            case 471 when ruleChild is not null: // BrtCFVO
                ValueObject(ref record);
                break;
            case 564 when ruleChild is not null: // BrtColor
                if (XlsbStyles.Color("color", ref record) is { } color)
                {
                    ruleChild.Add(color);
                }

                break;
            case 470 or 468 or 466: // BrtEndColorScale, BrtEndDatabar, BrtEndIconSet
                ruleChild = null;
                break;
            case 464: // BrtEndCFRule
                rule = null;
                ruleChild = null;
                break;
            case 462: // BrtEndConditionalFormatting
                if (conditional is not null && conditional.HasElements)
                {
                    SheetObjectReader.ReadConditionalFormat(book, sheet, XlsbXml.Safe(conditional));
                }

                conditional = null;
                break;
            case 477: // BrtPrintOptions
                {
                    int flags = record.UInt16();
                    PageSetup page = sheet.PageSetup;
                    page.CenterHorizontally = (flags & 0x01) != 0;
                    page.CenterVertically = (flags & 0x02) != 0;
                    page.PrintGridLines = (flags & 0x04) != 0;
                    page.PrintHeadings = (flags & 0x08) != 0;
                    break;
                }

            case 476: // BrtMargins
                Margins(ref record);
                break;
            case 478: // BrtPageSetup
                PageSetup(ref record);
                break;
            case 479: // BrtBeginHeaderFooter
                {
                    record.UInt16();
                    string? header = record.NullableWideString(), footer = record.NullableWideString();
                    sheet.PageSetup.Header = string.IsNullOrEmpty(header) ? null : header;
                    sheet.PageSetup.Footer = string.IsNullOrEmpty(footer) ? null : footer;
                    break;
                }

            case 392: // BrtBeginRwBrk
                rowBreaks = true;
                break;
            case 394: // BrtBeginColBrk
                rowBreaks = false;
                break;
            case 396: // BrtBrk
                {
                    uint at = record.UInt32();
                    record.Skip(8);
                    bool manual = record.UInt32() != 0;
                    int limit = rowBreaks ? CellAddress.MaxRows : CellAddress.MaxColumns;
                    if (manual && at > 0 && at < limit)
                    {
                        (rowBreaks ? sheet.PageSetup.RowBreaks : sheet.PageSetup.ColumnBreaks).Add((int)at);
                    }

                    break;
                }

            case 550: // BrtDrawing
                drawingId = record.WideString();
                break;
            case 551: // BrtLegacyDrawing
                vmlId = record.WideString();
                break;
        }

        cellMeta = 0;
    }

    private static bool Valid(int r1, int r2, int c1, int c2) =>
        r1 >= 0 && c1 >= 0 && r1 <= r2 && c1 <= c2 && r2 < CellAddress.MaxRows && c2 < CellAddress.MaxColumns;

    /// <summary>A Cell structure ([MS-XLSB] 2.5.9): the column, then a 24-bit style index and flags.</summary>
    private (int Column, int Style) Cell(ref XlsbReader record)
    {
        int column = (int)Math.Min(record.UInt32(), int.MaxValue);
        int style = (int)(record.UInt32() & 0xFFFFFF);
        return (column, style < book.Styles.Count ? style : 0);
    }

    private void Set(ref XlsbReader record, CellValue value)
    {
        (int column, int style) = Cell(ref record);
        Set(column, style, value);
    }

    private void Set(int column, int style, CellValue value)
    {
        if ((uint)row >= CellAddress.MaxRows || (uint)column >= CellAddress.MaxColumns)
        {
            return;
        }

        CellRecord record = state.Record(value, style);
        if (!record.IsVacant)
        {
            sheet.SetRead(row, column, record, null);
        }
    }

    /// <summary>A number as Excel holds it: a denormal one is 0, and one that is no number is #NUM!.</summary>
    internal static CellValue Number(double value) =>
        double.IsFinite(value) ? CellValue.FromNumber(double.IsSubnormal(value) ? 0 : value) : CellValue.FromError(CellError.Number);

    internal static string Cap(string text) => text.Length > CellValue.MaxTextLength ? text[..CellValue.MaxTextLength] : text;

    /// <summary>An RkNumber ([MS-XLSB] 2.5.122): a 30-bit integer or the top of a double, maybe divided by 100.</summary>
    private static double Rk(uint rk)
    {
        double value = (rk & 0x02) != 0 ? (int)rk >> 2 : BitConverter.Int64BitsToDouble((long)(rk & 0xFFFFFFFC) << 32);
        return (rk & 0x01) != 0 ? value / 100 : value;
    }

    /// <summary>A formula cell: its result, flags and formula (BrtFmlaNum and the like).</summary>
    private void Formula(int column, int style, CellValue result, ref XlsbReader record)
    {
        bool dynamic = cellMeta > 0;
        record.UInt16();
        (byte[] tokens, byte[] extra) = record.Formula();
        if ((uint)row >= CellAddress.MaxRows || (uint)column >= CellAddress.MaxColumns)
        {
            return;
        }

        if (XlsbFormula.Anchor(tokens, extra) is { } anchor)
        {
            pending.Add(new Pending { Row = row, Column = column, Style = style, Anchor = anchor, Result = result, Dynamic = dynamic });
            return;
        }

        FinishFormula(row, column, style, XlsbFormula.Decode(state.Links, tokens, extra, row, column), result);
    }

    private void FinishFormula(int r, int c, int style, string? text, CellValue result)
    {
        if (text is null)
        {
            // A formula Egap cannot read keeps the value Excel showed.
            CellRecord record = state.Record(result, style);
            if (!record.IsVacant)
            {
                sheet.SetRead(r, c, record, null);
            }

            return;
        }

        sheet.SetRead(r, c, new CellRecord { Style = style }, new FormulaEntry(text, result));
    }

    /// <summary>The cells of shared and array formulas, once the sheet's BrtShrFmla and BrtArrFmla records are read.</summary>
    private void Resolve()
    {
        foreach (Pending cell in pending)
        {
            string? text = null;
            if (arrays.TryGetValue(cell.Anchor, out var array))
            {
                if (cell.Row == cell.Anchor.Row && cell.Column == cell.Anchor.Column
                    && XlsbFormula.Decode(state.Links, array.Tokens, array.Extra, cell.Row, cell.Column) is { } arrayText)
                {
                    FormulaEntry entry = new(arrayText, cell.Result)
                    {
                        Attributes = [new XAttribute("t", "array"), new XAttribute("ref", array.Range.ToString())],
                    };
                    sheet.SetRead(cell.Row, cell.Column, new CellRecord { Style = cell.Style }, entry, cell.Dynamic);
                    continue;
                }
            }
            else if (SharedFormula(cell.Anchor, cell.Row, cell.Column) is { } shared)
            {
                text = XlsbFormula.Decode(state.Links, shared.Tokens, shared.Extra, cell.Row, cell.Column);
            }

            FinishFormula(cell.Row, cell.Column, cell.Style, text, cell.Result);
        }
    }

    /// <summary>
    /// The shared formula of a cell whose PtgExp names <paramref name="anchor"/>: the first cell that uses it, where its
    /// BrtShrFmla range starts. When several ranges start there, the first one that holds the cell is it.
    /// </summary>
    private (RangeAddress Range, byte[] Tokens, byte[] Extra)? SharedFormula((int Row, int Column) anchor, int r, int c)
    {
        if ((uint)anchor.Row >= CellAddress.MaxRows || (uint)anchor.Column >= CellAddress.MaxColumns)
        {
            return null;
        }

        CellAddress cell = new(r, c);
        if (sharedByFirstCell.TryGetValue(anchor, out List<int>? list))
        {
            foreach (int index in list)
            {
                if (sharedFormulas[index].Range.Contains(cell))
                {
                    return sharedFormulas[index];
                }
            }

            return sharedFormulas[list[0]];
        }

        CellAddress first = new(anchor.Row, anchor.Column);
        foreach (var shared in sharedFormulas)
        {
            if (shared.Range.Contains(first) && shared.Range.Contains(cell))
            {
                return shared;
            }
        }

        return null;
    }

    /// <summary>BrtRowHdr ([MS-XLSB] 2.4.726): the row the next cells are in, with its height, style and outline.</summary>
    private void Row(ref XlsbReader record)
    {
        row = (int)Math.Min(record.UInt32(), int.MaxValue);
        if ((uint)row >= CellAddress.MaxRows)
        {
            return;
        }

        int xf = (int)Math.Min(record.UInt32(), int.MaxValue);
        int height = record.UInt16();
        record.Byte();
        int flags = record.Byte();
        RowRecord made = new()
        {
            OutlineLevel = flags & 0x07,
            Collapsed = (flags & 0x08) != 0,
            Hidden = (flags & 0x10) != 0,
        };
        double points = height / 20.0;
        if ((flags & 0x20) != 0 && points <= 409.5)
        {
            made.Height = points;
        }
        else if ((flags & 0x20) != 0)
        {
            // A height past the model's range is kept as it was, as the XLSX reader keeps one.
            made.Extra = [new XAttribute("ht", XlsxNames.Number(points)), new XAttribute("customHeight", "1")];
        }
        else if (sheet.DefaultRowHeight is not { } standard || Math.Abs(standard - points) > 0.001)
        {
            if (points <= 409.5)
            {
                made.FittedHeight = points;
            }

            made.Extra = [new XAttribute("ht", XlsxNames.Number(points))];
        }

        if ((flags & 0x40) != 0 && xf > 0 && xf < book.Styles.Count)
        {
            made.Style = xf;
        }

        if (!made.IsDefault || !double.IsNaN(made.FittedHeight))
        {
            sheet.RowRecords[row] = made;
        }
    }

    /// <summary>BrtColInfo ([MS-XLSB] 2.4.323): a run of columns' width, style, visibility and outline.</summary>
    private void Column(ref XlsbReader record)
    {
        uint first = record.UInt32();
        uint last = Math.Min(record.UInt32(), CellAddress.MaxColumns - 1);
        uint width = record.UInt32();
        uint xf = record.UInt32();
        int flags = record.UInt16();
        if (first > last)
        {
            return;
        }

        List<XAttribute> extra = [];
        if ((flags & 0x04) != 0)
        {
            extra.Add(new XAttribute("bestFit", "1"));
        }

        if ((flags & 0x02) != 0)
        {
            extra.Add(new XAttribute("customWidth", "1"));
        }

        sheet.ColumnSpans.AddRead(new ColumnSpan
        {
            First = (int)first,
            Last = (int)last,
            Width = Math.Min(width / 256.0, 255),
            Hidden = (flags & 0x01) != 0,
            OutlineLevel = (flags >> 8) & 0x07,
            Collapsed = (flags & 0x1000) != 0,
            Style = xf > 0 && xf < book.Styles.Count ? (int)xf : -1,
            Extra = extra.Count == 0 ? null : [.. extra],
        });
    }

    /// <summary>BrtWsFmtInfo: the sheet's default column width and row height.</summary>
    private void FormatInfo(ref XlsbReader record)
    {
        uint width = record.UInt32();
        record.UInt16();
        int height = record.UInt16();
        int flags = record.UInt16();
        if (width != 0xFFFFFFFF && width / 256.0 <= 255)
        {
            sheet.DefaultColumnWidth = width / 256.0;
        }

        if (height / 20.0 <= 409)
        {
            sheet.DefaultRowHeight = height / 20.0;
            sheet.DefaultRowFollowsFont = (flags & 0x01) == 0;
        }
    }

    /// <summary>BrtWsProp: the tab colour and whether the sheet prints fitted to pages.</summary>
    private void Properties(ref XlsbReader record)
    {
        int flags = record.UInt16();
        record.Byte();
        XElement? color = XlsbStyles.Color("tabColor", ref record);
        sheet.PageSetup.FitToPage = (flags & 0x100) != 0;
        if (color is not null && color.Attribute("auto") is null)
        {
            sheet.TabColor = XlsxStyles.ReadColor(color).Color;
        }
    }

    /// <summary>BrtBeginWsView: the sheet's first window's settings.</summary>
    private void View(ref XlsbReader record)
    {
        inView = firstView;
        if (!firstView)
        {
            return;
        }

        firstView = false;
        SheetView view = sheet.View;
        int flags = record.UInt16();
        record.Skip(12);
        record.Skip(4);
        int zoom = record.UInt16();
        view.ShowFormulas = (flags & 0x02) != 0;
        view.ShowGridLines = (flags & 0x04) != 0;
        view.ShowHeadings = (flags & 0x08) != 0;
        view.ShowZeros = (flags & 0x10) != 0;
        view.RightToLeft = (flags & 0x20) != 0;
        view.Selected = (flags & 0x40) != 0;
        view.ZoomScale = zoom is >= 10 and <= 400 ? zoom : 100;
        view.SourceFrozen = (view.FrozenRows, view.FrozenColumns);
    }

    /// <summary>BrtPane: frozen rows and columns.</summary>
    private void Pane(ref XlsbReader record)
    {
        double x = record.Double(), y = record.Double();
        record.Skip(12);
        int flags = record.Byte();
        if ((flags & 0x01) == 0)
        {
            return;
        }

        SheetView view = sheet.View;
        view.FrozenRows = double.IsFinite(y) ? (int)Math.Clamp(y, 0, CellAddress.MaxRows - 1) : 0;
        view.FrozenColumns = double.IsFinite(x) ? (int)Math.Clamp(x, 0, CellAddress.MaxColumns - 1) : 0;
        view.SourceFrozen = (view.FrozenRows, view.FrozenColumns);
    }

    /// <summary>BrtHLink: a hyperlink's range, target, place, tip and display text.</summary>
    private void Hyperlink(ref XlsbReader record)
    {
        (int r1, int r2, int c1, int c2) = record.Range();
        string? id = record.NullableWideString();
        string location = record.WideString();
        string tip = record.WideString();
        string display = record.WideString();
        if (!Valid(r1, r2, c1, c2))
        {
            return;
        }

        string? address = string.IsNullOrEmpty(id) ? null : part.GetRelationship(id)?.Target;
        RangeAddress range = new(r1, c1, r2, c2);
        Hyperlink? link = !string.IsNullOrEmpty(address) ? sheet.Hyperlinks.Add(range, address)
            : location.Length > 0 ? sheet.Hyperlinks.AddInternal(range, location)
            : null;
        if (link is null)
        {
            return;
        }

        link.Location = location.Length > 0 ? location : null;
        link.ToolTip = tip.Length > 0 ? tip : null;
        link.Display = display.Length > 0 ? display : null;
    }

    /// <summary>BrtSheetProtection: kept only when the sheet is protected; its flags are XLSX's attributes negated.</summary>
    private void Protection(ref XlsbReader record)
    {
        int password = record.UInt16();
        if (record.UInt32() == 0)
        {
            return;
        }

        XElement element = new(Ns + "sheetProtection");
        if (password != 0)
        {
            element.Add(new XAttribute("password", password.ToString("X4", CultureInfo.InvariantCulture)));
        }

        element.Add(new XAttribute("sheet", "1"));
        (string Name, bool Default)[] names =
        [
            ("objects", false), ("scenarios", false), ("formatCells", true), ("formatColumns", true), ("formatRows", true),
            ("insertColumns", true), ("insertRows", true), ("insertHyperlinks", true), ("deleteColumns", true), ("deleteRows", true),
            ("selectLockedCells", false), ("sort", true), ("autoFilter", true), ("pivotTables", true), ("selectUnlockedCells", false),
        ];
        foreach ((string name, bool fallback) in names)
        {
            if (record.Remaining < 4)
            {
                break;
            }

            bool value = record.UInt32() == 0;
            if (value != fallback)
            {
                element.Add(new XAttribute(name, value ? "1" : "0"));
            }
        }

        XlsxProtection.ReadSheet(sheet.Protection, XlsbXml.Safe(element));
    }

    private static readonly string[] ValidationTypes = ["none", "whole", "decimal", "list", "date", "time", "textLength", "custom"];

    private static readonly string[] Operators = ["between", "notBetween", "equal", "notEqual", "greaterThan", "lessThan", "greaterThanOrEqual", "lessThanOrEqual"];

    /// <summary>BrtDVal: a data validation rule, read as the dataValidation element an XLSX file holds.</summary>
    private void Validation(ref XlsbReader record)
    {
        uint flags = record.UInt32();
        List<RangeAddress> ranges = Ranges(ref record);
        string? errorTitle = record.NullableWideString(), error = record.NullableWideString();
        string? title = record.NullableWideString(), prompt = record.NullableWideString();
        (byte[] tokens1, byte[] extra1) = record.Formula();
        (byte[] tokens2, byte[] extra2) = record.Formula();
        if (ranges.Count == 0)
        {
            return;
        }

        int kind = (int)(flags & 0x0F);
        int style = (int)((flags >> 4) & 0x07);
        int op = (int)((flags >> 20) & 0x0F);
        XElement element = new(Ns + "dataValidation");
        if (kind is > 0 and < 8)
        {
            element.Add(new XAttribute("type", ValidationTypes[kind]));
        }

        if (style is 1 or 2)
        {
            element.Add(new XAttribute("errorStyle", style == 1 ? "warning" : "information"));
        }

        if (op is > 0 and < 8)
        {
            element.Add(new XAttribute("operator", Operators[op]));
        }

        Flag(element, "allowBlank", flags, 0x100);
        Flag(element, "showDropDown", flags, 0x200);
        Flag(element, "showInputMessage", flags, 0x40000);
        Flag(element, "showErrorMessage", flags, 0x80000);
        Text(element, "errorTitle", errorTitle);
        Text(element, "error", error);
        Text(element, "promptTitle", title);
        Text(element, "prompt", prompt);
        element.Add(new XAttribute("sqref", string.Join(' ', ranges)));

        // Relative references count from the top left of the ranges' bounds.
        (int baseRow, int baseColumn) = (ranges.Min(r => r.First.Row), ranges.Min(r => r.First.Column));
        if (tokens1.Length > 0 && XlsbFormula.Decode(state.Links, tokens1, extra1, baseRow, baseColumn) is { } formula1)
        {
            // A list typed into the rule is a string of its items with NUL between them, as Excel shows them with commas.
            element.Add(new XElement(Ns + "formula1", (flags & 0x80) != 0 ? formula1.Replace('\0', ',') : formula1));
        }

        if (tokens2.Length > 0 && XlsbFormula.Decode(state.Links, tokens2, extra2, baseRow, baseColumn) is { } formula2)
        {
            element.Add(new XElement(Ns + "formula2", formula2));
        }

        SheetObjectReader.ReadValidation(sheet, XlsbXml.Safe(element));
    }

    private static void Flag(XElement element, string name, uint flags, uint bit)
    {
        if ((flags & bit) != 0)
        {
            element.Add(new XAttribute(name, "1"));
        }
    }

    private static void Text(XElement element, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            element.Add(new XAttribute(name, value));
        }
    }

    /// <summary>A Sqrfx ([MS-XLSB] 2.5.?): a count of ranges, then the ranges.</summary>
    private static List<RangeAddress> Ranges(ref XlsbReader record)
    {
        uint count = record.UInt32();
        List<RangeAddress> ranges = [];
        for (uint i = 0; i < count; i++)
        {
            (int r1, int r2, int c1, int c2) = record.Range();
            if (Valid(r1, r2, c1, c2))
            {
                ranges.Add(new RangeAddress(r1, c1, r2, c2));
            }
        }

        return ranges;
    }

    /// <summary>BrtBeginConditionalFormatting: the ranges its rules format.</summary>
    private void BeginConditionalFormat(ref XlsbReader record)
    {
        record.UInt32();
        record.UInt32();
        List<RangeAddress> ranges = Ranges(ref record);
        conditional = ranges.Count == 0 ? null : new XElement(Ns + "conditionalFormatting", new XAttribute("sqref", string.Join(' ', ranges)));
        conditionalBase = ranges.Count == 0 ? (0, 0) : (ranges[0].First.Row, ranges[0].First.Column);
    }

    private static readonly string[] TimePeriods = ["today", "tomorrow", "yesterday", "last7Days", "lastMonth", "nextMonth", "thisWeek", "nextWeek", "lastWeek", "thisMonth"];

    /// <summary>BrtBeginCFRule: a rule's kind, template, format, priority and formulas, as a cfRule element.</summary>
    private void BeginRule(ref XlsbReader record)
    {
        int type = record.Int32();
        int template = record.Int32();
        int dxf = record.Int32();
        int priority = record.Int32();
        int parameter = record.Int32();
        record.Skip(8);
        int flags = record.UInt16();
        int size1 = record.Int32(), size2 = record.Int32(), size3 = record.Int32();
        string? text = record.NullableWideString();
        List<string> formulas = [];
        foreach (int size in new[] { size1, size2, size3 })
        {
            if (size > 0 && record.Remaining >= 8)
            {
                (byte[] tokens, byte[] extra) = record.Formula();
                if (XlsbFormula.Decode(state.Links, tokens, extra, conditionalBase.Row, conditionalBase.Column) is { } formula)
                {
                    formulas.Add(formula);
                }
            }
        }

        (string kind, XAttribute[] attributes) = RuleKind(type, template, parameter, flags, text);
        XElement made = new(Ns + "cfRule", new XAttribute("type", kind));
        if (dxf >= 0 && type is not (3 or 4 or 6))
        {
            made.Add(new XAttribute("dxfId", Integer(dxf)));
        }

        made.Add(new XAttribute("priority", Integer(priority)));
        if ((flags & 0x02) != 0)
        {
            made.Add(new XAttribute("stopIfTrue", "1"));
        }

        made.Add(attributes);
        made.Add(formulas.Select(f => new XElement(Ns + "formula", f)));
        conditional!.Add(made);
        rule = made;
    }

    private static (string Kind, XAttribute[] Attributes) RuleKind(int type, int template, int parameter, int flags, string? text)
    {
        switch (type)
        {
            case 1:
                return ("cellIs", (uint)(parameter - 1) < (uint)Operators.Length ? [new XAttribute("operator", Operators[parameter - 1])] : []);
            case 3:
                return ("colorScale", []);
            case 4:
                return ("dataBar", []);
            case 6:
                return ("iconSet", []);
            case 5:
                {
                    List<XAttribute> top = [];
                    if ((flags & 0x10) != 0)
                    {
                        top.Add(new XAttribute("percent", "1"));
                    }

                    if ((flags & 0x08) != 0)
                    {
                        top.Add(new XAttribute("bottom", "1"));
                    }

                    top.Add(new XAttribute("rank", Integer(parameter)));
                    return ("top10", [.. top]);
                }
        }

        XAttribute[] Text(string op) => text is null ? [new XAttribute("operator", op)] : [new XAttribute("operator", op), new XAttribute("text", text)];
        return template switch
        {
            7 => ("uniqueValues", []),
            8 => parameter switch
            {
                1 => ("notContainsText", Text("notContains")),
                2 => ("beginsWith", Text("beginsWith")),
                3 => ("endsWith", Text("endsWith")),
                _ => ("containsText", Text("containsText")),
            },
            9 => ("containsBlanks", []),
            10 => ("notContainsBlanks", []),
            11 => ("containsErrors", []),
            12 => ("notContainsErrors", []),
            >= 15 and <= 24 => ("timePeriod", [new XAttribute("timePeriod", TimePeriods[template - 15])]),
            25 or 26 or 29 or 30 => ("aboveAverage", Average(template, parameter)),
            27 => ("duplicateValues", []),
            _ => ("expression", []),
        };
    }

    private static XAttribute[] Average(int template, int deviations)
    {
        List<XAttribute> attributes = [];
        if (template is 26 or 30)
        {
            attributes.Add(new XAttribute("aboveAverage", "0"));
        }

        if (template is 29 or 30)
        {
            attributes.Add(new XAttribute("equalAverage", "1"));
        }

        if (deviations > 0)
        {
            attributes.Add(new XAttribute("stdDev", Integer(deviations)));
        }

        return [.. attributes];
    }

    private static readonly string[] ValueTypes = ["", "num", "min", "max", "percent", "percentile", "formula", "autoMin", "autoMax"];

    /// <summary>BrtCFVO: a threshold of a colour scale, data bar or icon set.</summary>
    private void ValueObject(ref XlsbReader record)
    {
        int type = record.Int32();
        double number = record.Double();
        record.UInt32();
        bool greaterOrEqual = record.UInt32() != 0;
        int size = record.Int32();
        string? formula = null;
        if (size > 0 && record.Remaining >= 8)
        {
            (byte[] tokens, byte[] extra) = record.Formula();
            formula = XlsbFormula.Decode(state.Links, tokens, extra, conditionalBase.Row, conditionalBase.Column);
        }

        XElement cfvo = new(Ns + "cfvo", new XAttribute("type", (uint)type < (uint)ValueTypes.Length && type > 0 ? ValueTypes[type] : "num"));
        if (type is 1 or 4 or 5 or 6)
        {
            cfvo.Add(new XAttribute("val", formula ?? XlsxNames.Number(number)));
        }

        if (!greaterOrEqual && ruleChild?.Name.LocalName == "iconSet")
        {
            cfvo.Add(new XAttribute("gte", "0"));
        }

        // The thresholds go before the colours.
        if (ruleChild!.Elements(Ns + "color").FirstOrDefault() is { } firstColor)
        {
            firstColor.AddBeforeSelf(cfvo);
        }
        else
        {
            ruleChild.Add(cfvo);
        }
    }

    private static string? IconSetName(int set) => set switch
    {
        0 => "3Arrows",
        1 => "3ArrowsGray",
        2 => "3Flags",
        3 => "3TrafficLights1",
        4 => "3TrafficLights2",
        5 => "3Signs",
        6 => "3Symbols",
        7 => "3Symbols2",
        8 => "4Arrows",
        9 => "4ArrowsGray",
        10 => "4RedToBlack",
        11 => "4Rating",
        12 => "4TrafficLights",
        13 => "5Arrows",
        14 => "5ArrowsGray",
        15 => "5Rating",
        16 => "5Quarters",
        _ => null,
    };

    /// <summary>BrtMargins: left, right, top, bottom, header and footer margins, in inches.</summary>
    private void Margins(ref XlsbReader record)
    {
        PageSetup page = sheet.PageSetup;
        static double Margin(double value, double fallback) => double.IsFinite(value) && value >= 0 ? value : fallback;
        page.LeftMargin = Margin(record.Double(), page.LeftMargin);
        page.RightMargin = Margin(record.Double(), page.RightMargin);
        page.TopMargin = Margin(record.Double(), page.TopMargin);
        page.BottomMargin = Margin(record.Double(), page.BottomMargin);
        page.HeaderMargin = Margin(record.Double(), page.HeaderMargin);
        page.FooterMargin = Margin(record.Double(), page.FooterMargin);
    }

    /// <summary>BrtPageSetup: paper, scale, fitting, orientation, page order and first page number.</summary>
    private void PageSetup(ref XlsbReader record)
    {
        PageSetup page = sheet.PageSetup;
        int paper = record.Int32();
        int scale = record.Int32();
        record.Skip(8);
        record.UInt32();
        int first = record.Int32();
        int width = record.Int32();
        int height = record.Int32();
        int flags = record.UInt16();
        page.PaperSize = paper == 0 ? null : paper;
        page.Scale = scale is >= 10 and <= 400 && scale != 100 ? scale : null;
        page.FitToWidth = Math.Max(0, width);
        page.FitToHeight = Math.Max(0, height);
        page.Orientation = (flags & 0x40) != 0 ? PrintOrientation.Default : (flags & 0x02) != 0 ? PrintOrientation.Landscape : PrintOrientation.Portrait;
        page.PageOrder = (flags & 0x01) != 0 ? PrintPageOrder.OverThenDown : PrintPageOrder.DownThenOver;
        if ((flags & 0x80) != 0)
        {
            page.FirstPageNumber = first;
        }
    }

    private static string Integer(int value) => value.ToString(CultureInfo.InvariantCulture);
}
