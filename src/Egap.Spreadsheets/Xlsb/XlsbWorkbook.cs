using System.Xml.Linq;

using Egap.Documents;
using Egap.Documents.Docx;
using Egap.Spreadsheets.Storage;
using Egap.Spreadsheets.Xls;
using Egap.Spreadsheets.Xlsx;

namespace Egap.Spreadsheets.Xlsb;

/// <summary>
/// Reads an Excel binary workbook (.xlsb, [MS-XLSB]): the BIFF12 records of its workbook, style, shared string, sheet, table
/// and comment parts, which an OPC package holds as an XLSX file holds its XML parts. It reads the sheets, their cells with
/// their values, styles and formulas (shared, array and dynamic array formulas too, with the results Excel kept), defined
/// names, links to other workbooks, merged cells, row heights and column widths, hyperlinks, notes, tables, data validation,
/// conditional formats, page settings, and the drawings and charts the file keeps as DrawingML parts. Pivot tables, which an
/// XLSB file keeps as binary parts, are not read. The workbook keeps no parts of the file: a save writes an XLSX file.
/// </summary>
internal static class XlsbWorkbook
{
    /// <summary>The content type of an XLSB file's workbook part.</summary>
    public const string ContentType = "application/vnd.ms-excel.sheet.binary.macroEnabled.main";

    private const string TableRelationship = SheetObjectReader.TableRelationship;
    private const string CommentsRelationship = SheetObjectReader.CommentsRelationship;

    public static void Read(Workbook book, OpcPackage package, OpcPart workbookPart)
    {
        try
        {
            new State(book, package, workbookPart).Read();
        }
        catch (InvalidDataException e)
        {
            throw new DocumentException($"The XLSB workbook is damaged: {e.Message}", e);
        }
    }

    /// <summary>What reading the workbook needs across its parts.</summary>
    internal sealed class State(Workbook book, OpcPackage package, OpcPart workbookPart)
    {
        private readonly List<(int Visibility, string RelationshipId, string Name)> bundles = [];
        private readonly List<(string Name, uint Sheet, byte[] Tokens, byte[] Extra, bool Hidden, string? Comment)> names = [];
        private readonly List<(XlsSupBook Book, OpcPart? Part)> externalParts = [];
        private readonly Dictionary<OpcPart, List<(uint Id, XlsbTable Table)>> tablesByPart = [];

        public Workbook Book => book;

        public OpcPackage Package => package;

        public XlsLinks Links { get; } = new();

        public XlsbStyles Styles { get; } = new();

        /// <summary>The width of the normal font's widest digit, in pixels, for default column widths.</summary>
        public int Digit => digit ??= Math.Max(1, new Printing.TextMeter(book).MaxDigitWidth);

        private int? digit;

        public void Read()
        {
            ReadWorkbookPart();
            if (Related(workbookPart, XlsxNames.StylesRelationship) is { } styles)
            {
                Styles.Read(styles.Data);
                XlsxStyles.Read(book.Styles, Styles.Document);
            }
            else
            {
                XlsxStyles.Read(book.Styles, null);
            }

            if (Related(workbookPart, XlsxNames.SharedStringsRelationship) is { } strings)
            {
                ReadSharedStrings(strings.Data);
            }

            // The sheets in tab order: worksheets get their place now, so names and formulas can refer to them.
            var sheets = new (Worksheet? Sheet, OpcPart? Part, bool Chart)[bundles.Count];
            for (int i = 0; i < bundles.Count; i++)
            {
                (int visibility, string id, string name) = bundles[i];
                Links.SheetNames.Add(name);
                OpcRelationship? relationship = workbookPart.GetRelationship(id);
                OpcPart? part = relationship is { External: false } ? package.GetPart(relationship.TargetName) : null;
                if (part is null)
                {
                    continue;
                }

                if (XlsxNames.SameType(relationship!.Type, XlsxNames.WorksheetRelationship))
                {
                    Worksheet sheet = AddSheet(name);
                    sheet.Visibility = Visibility(visibility);
                    sheets[i] = (sheet, part, false);
                    ReadTableParts(part);
                }
                else if (XlsxNames.SameType(relationship.Type, XlsxNames.ChartsheetRelationship))
                {
                    sheets[i] = (null, part, true);
                }
            }

            if (book.Worksheets.Count == 0 && !sheets.Any(s => s.Chart))
            {
                throw new DocumentException("The workbook has no worksheets Egap reads.");
            }

            FinishLinks();
            AddNames(sheets);
            Worksheet? after = null;
            for (int i = 0; i < sheets.Length; i++)
            {
                if (sheets[i] is { Sheet: { } sheet, Part: { } part })
                {
                    new XlsbSheet(this, sheet, part).Read();
                    AddTables(sheet, part);
                    after = sheet;
                }
                else if (sheets[i] is { Chart: true, Part: { } chartPart })
                {
                    ReadChartSheet(bundles[i].Name, bundles[i].Visibility, chartPart, after);
                }
            }
        }

        private static SheetVisibility Visibility(int state) => state switch
        {
            1 => SheetVisibility.Hidden,
            2 => SheetVisibility.VeryHidden,
            _ => SheetVisibility.Visible,
        };

        private Worksheet AddSheet(string name)
        {
            try
            {
                return book.Worksheets.Add(string.IsNullOrEmpty(name) || book.Worksheets.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ? null : name);
            }
            catch (ArgumentException e)
            {
                throw new DocumentException($"The workbook has a sheet name Excel does not allow: {e.Message}", e);
            }
        }

        public OpcPart? Related(OpcPart part, string type) => package.GetRelatedPart(part.Relationships, type);

        /// <summary>The part a relationship id of <paramref name="part"/> names, inside the package.</summary>
        public OpcPart? Target(OpcPart part, string? id)
        {
            OpcRelationship? relationship = string.IsNullOrEmpty(id) ? null : part.GetRelationship(id);
            return relationship is { External: false } ? package.GetPart(relationship.TargetName) : null;
        }

        /// <summary>The workbook part ([MS-XLSB] 2.1.7.61): its properties, sheets, links and names.</summary>
        private void ReadWorkbookPart()
        {
            XlsbRecords records = new(workbookPart.Data);
            while (records.Next())
            {
                XlsbReader record = records.Reader;
                switch (records.Type)
                {
                    case 153: // BrtWbProp
                        book.Date1904 = (record.UInt32() & 0x01) != 0;
                        break;
                    case 156: // BrtBundleSh
                        {
                            int state = (int)record.UInt32();
                            record.UInt32();
                            string id = record.WideString();
                            bundles.Add((state, id, record.WideString()));
                            break;
                        }

                    case 157: // BrtCalcProp
                        ReadCalculation(ref record);
                        break;
                    case 357: // BrtSupSelf
                    case 358: // BrtSupSame
                        Links.Books.Add(new XlsSupBook { Kind = XlsSupBookKind.Self });
                        break;
                    case 667: // BrtSupAddin
                        Links.Books.Add(new XlsSupBook { Kind = XlsSupBookKind.AddIn });
                        break;
                    case 361 when Links.Books.Count > 0: // BrtPlaceholderName: a name an add-in gives a function.
                        Links.Books[^1].Names.Add(record.WideString());
                        break;
                    case 355: // BrtSupBookSrc
                        ReadExternalLink(Target(workbookPart, record.WideString()));
                        break;
                    case 362: // BrtExternSheet
                        {
                            uint count = record.UInt32();
                            for (uint i = 0; i < count && record.Remaining >= 12; i++)
                            {
                                Links.Sheets.Add(((int)Math.Min(record.UInt32(), int.MaxValue), record.Int32(), record.Int32()));
                            }

                            break;
                        }

                    case 39: // BrtName
                        ReadName(ref record);
                        break;
                }
            }
        }

        /// <summary>BrtCalcProp ([MS-XLSB] 2.4.304): the calculation mode, iteration and precision.</summary>
        private void ReadCalculation(ref XlsbReader record)
        {
            CalculationSettings settings = book.Calculation;
            record.UInt32();
            uint mode = record.UInt32();
            uint count = record.UInt32();
            double delta = record.Double();
            record.Int32();
            int flags = record.UInt16();
            settings.Mode = mode switch
            {
                0 => CalculationMode.Manual,
                2 => CalculationMode.AutomaticExceptTables,
                _ => CalculationMode.Automatic,
            };
            settings.Iterate = (flags & 0x04) != 0;
            settings.MaxIterations = (int)Math.Clamp(count, 1, (uint)CalculationSettings.IterationLimit);
            if (double.IsFinite(delta) && delta >= 0)
            {
                settings.MaxChange = delta;
            }

            settings.FullPrecision = (flags & 0x08) != 0;
            settings.Concurrent = (flags & 0x40) != 0;
        }

        /// <summary>BrtName ([MS-XLSB] 2.4.709): a defined name, its scope and its formula.</summary>
        private void ReadName(ref XlsbReader record)
        {
            uint flags = record.UInt32();
            record.Byte();
            uint sheet = record.UInt32();
            string name = record.WideString();
            (byte[] tokens, byte[] extra) = record.Formula();
            string? comment = record.Remaining >= 4 ? record.NullableWideString() : null;
            Links.Names.Add(name);
            Links.NameSheets.Add(sheet == uint.MaxValue ? -1 : (int)Math.Min(sheet, int.MaxValue));
            names.Add((name, sheet, tokens, extra, (flags & 0x01) != 0, comment));
        }

        private void AddNames((Worksheet? Sheet, OpcPart? Part, bool Chart)[] sheets)
        {
            foreach ((string name, uint sheetIndex, byte[] tokens, byte[] extra, bool hidden, string? comment) in names)
            {
                // Excel's newer functions are names of an older file's sort, which an XLSX file does not list.
                if (name.StartsWith("_xlfn.", StringComparison.OrdinalIgnoreCase) || name.StartsWith("_xleta.", StringComparison.OrdinalIgnoreCase)
                    || tokens.Length == 0 || XlsbFormula.Decode(Links, tokens, extra, 0, 0, name: true) is not { Length: > 0 } formula)
                {
                    continue;
                }

                Worksheet? sheet = null;
                if (sheetIndex != 0xFFFFFFFF)
                {
                    if (sheetIndex >= (uint)sheets.Length || sheets[sheetIndex].Sheet is null)
                    {
                        continue;
                    }

                    sheet = sheets[sheetIndex].Sheet;
                }

                string full = IsBuiltIn(name) ? "_xlnm." + name : name;
                if (book.Names.Any(n => n.Name.Equals(full, StringComparison.OrdinalIgnoreCase) && n.Sheet == sheet))
                {
                    continue;
                }

                book.Names.AddRead(new DefinedName(full, formula, sheet) { Hidden = hidden, Comment = string.IsNullOrEmpty(comment) ? null : comment });
            }
        }

        private static bool IsBuiltIn(string name) => name is "Consolidate_Area" or "Auto_Open" or "Auto_Close" or "Extract" or "Database"
            or "Criteria" or "Print_Area" or "Print_Titles" or "Recorder" or "Data_Form" or "Auto_Activate" or "Auto_Deactivate"
            or "Sheet_Title" or "_FilterDatabase";

        /// <summary>
        /// A linked workbook's part ([MS-XLSB] 2.1.7.20 External Link): the file, its sheets and the values kept for them.
        /// A DDE or OLE link counts in the formulas' numbering of books but is not read.
        /// </summary>
        private void ReadExternalLink(OpcPart? part)
        {
            XlsSupBook supBook = new() { Kind = XlsSupBookKind.Other };
            if (part is null)
            {
                Links.Books.Add(supBook);
                return;
            }

            string? file = null;
            List<string> sheetNames = [];
            List<string> names = [];
            List<int> nameSheets = [];
            List<(int Sheet, int Row, int Column, CellValue Value)> cells = [];
            int sheet = -1, row = -1;
            bool isBook = false;
            XlsbRecords records = new(part.Data);
            while (records.Next())
            {
                XlsbReader record = records.Reader;
                switch (records.Type)
                {
                    case 360: // BrtBeginSupBook
                        isBook = record.UInt16() == 0;
                        file = part.GetRelationship(record.WideString()) is { External: true } path ? path.Target : null;
                        break;
                    case 359: // BrtSupTabs
                        {
                            uint count = record.UInt32();
                            for (uint i = 0; i < count && record.Remaining >= 4; i++)
                            {
                                sheetNames.Add(record.WideString());
                            }

                            break;
                        }

                    case 577: // BrtSupNameStart: a name the linked file defines, which formulas call by number.
                        names.Add(record.WideString());
                        nameSheets.Add(-1);
                        break;
                    case 586 when nameSheets.Count > 0 && record.Remaining >= 6: // BrtSupNameBits: the name's sheet, from 1; 0 for the workbook.
                        {
                            record.UInt16();
                            uint itab = record.UInt32();
                            nameSheets[^1] = itab == 0 ? -1 : (int)Math.Min(itab - 1, int.MaxValue);
                            break;
                        }

                    case 363: // BrtExternTableStart
                        sheet = (int)Math.Min(record.UInt32(), int.MaxValue);
                        break;
                    case 366: // BrtExternRowHdr
                        row = (int)Math.Min(record.UInt32(), int.MaxValue);
                        break;
                    case >= 368 and <= 371 when sheet >= 0 && (uint)row < CellAddress.MaxRows: // BrtExternCell...
                        {
                            uint column = record.UInt32();
                            CellValue value = records.Type switch
                            {
                                368 => XlsbSheet.Number(record.Double()),
                                369 => CellValue.FromBoolean(record.Byte() != 0),
                                370 => CellValue.FromError(XlsFormula.ErrorValue(record.Byte())),
                                _ => CellValue.FromText(XlsbSheet.Cap(record.NullableWideString() ?? string.Empty)),
                            };
                            if (column < CellAddress.MaxColumns)
                            {
                                cells.Add((sheet, row, (int)column, value));
                            }

                            break;
                        }
                }
            }

            // Formulas number the linked parts in order, a DDE or OLE link's among them, as [1], [2] and so on.
            supBook = isBook ? new XlsSupBook { Kind = XlsSupBookKind.External, File = file ?? string.Empty } : supBook;
            supBook.Number = ++linkCount;
            supBook.Names.AddRange(names);
            supBook.NameSheets.AddRange(nameSheets);
            if (isBook)
            {
                supBook.Sheets.AddRange(sheetNames);
                pendingCells[supBook] = cells;
            }

            Links.Books.Add(supBook);
            externalParts.Add((supBook, part));
        }

        private readonly Dictionary<XlsSupBook, List<(int Sheet, int Row, int Column, CellValue Value)>> pendingCells = [];

        private int linkCount;

        /// <summary>Numbers the linked files as Excel's formula text does and adds them to the workbook with their kept values.</summary>
        private void FinishLinks()
        {
            foreach (XlsSupBook supBook in Links.Books)
            {
                if (supBook.Kind != XlsSupBookKind.External)
                {
                    continue;
                }

                ExternalBook external = new(book, supBook.File ?? string.Empty);
                book.ExternalBooks.Add(external);
                foreach (string sheet in supBook.Sheets)
                {
                    external.MakeSheet(book, sheet);
                }

                foreach ((int sheet, int row, int column, CellValue value) in pendingCells.GetValueOrDefault(supBook) ?? [])
                {
                    if ((uint)sheet < (uint)supBook.Sheets.Count)
                    {
                        external.MakeSheet(book, supBook.Sheets[sheet]).SetRead(row, column, Record(value, 0), null);
                    }
                }
            }
        }

        /// <summary>The shared string part ([MS-XLSB] 2.1.7.48): each BrtSSTItem's text, with its runs of formatting.</summary>
        private void ReadSharedStrings(ReadOnlySpan<byte> data)
        {
            XlsbRecords records = new(data);
            while (records.Next())
            {
                if (records.Type == 19)
                {
                    XlsbReader record = records.Reader;
                    (string text, XElement? rich) = RichText(ref record, "si");
                    book.SharedStrings.AddRead(XlsbSheet.Cap(text), rich);
                }
            }
        }

        /// <summary>
        /// A RichStr ([MS-XLSB] 2.5.143): its text, and for text with runs of formatting the element an XLSX file holds for it
        /// (<c>si</c> or a comment's <c>text</c>), each run with its font's properties.
        /// </summary>
        public (string Text, XElement? Rich) RichText(ref XlsbReader record, string element)
        {
            int flags = record.Byte();
            string text = record.WideString();
            if ((flags & 0x01) == 0 || record.Remaining < 4)
            {
                return (text, null);
            }

            uint count = record.UInt32();
            List<(int Start, int Font)> runs = [];
            for (uint i = 0; i < count && record.Remaining >= 4; i++)
            {
                runs.Add((record.UInt16(), record.UInt16()));
            }

            XElement rich = new(XlsxNames.Main + element);
            int at = 0;
            for (int i = -1; i < runs.Count; i++)
            {
                int start = i < 0 ? 0 : Math.Min(runs[i].Start, text.Length);
                int end = i + 1 < runs.Count ? Math.Min(runs[i + 1].Start, text.Length) : text.Length;
                if (end <= start || start < at)
                {
                    continue;
                }

                XElement run = new(XlsxNames.Main + "r");
                if (i >= 0 && Styles.RunProperties(runs[i].Font) is { } properties)
                {
                    run.Add(properties);
                }

                run.Add(Text(text[start..end]));
                rich.Add(run);
                at = end;
            }

            return (text, rich.HasElements ? XlsbXml.Safe(rich) : null);
        }

        private static XElement Text(string text)
        {
            XElement t = new(XlsxNames.Main + "t", XlsxNames.Encode(text));
            if (text.Length > 0 && (char.IsWhiteSpace(text[0]) || char.IsWhiteSpace(text[^1])))
            {
                t.Add(new XAttribute(XNamespace.Xml + "space", "preserve"));
            }

            return t;
        }

        public CellRecord Record(CellValue value, int style)
        {
            CellRecord record = new() { Style = style };
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
                case CellValueKind.Text:
                    record.Kind = CellKind.SharedString;
                    record.Payload = book.SharedStrings.Add(value.Text);
                    break;
            }

            return record;
        }

        /// <summary>The table parts of a sheet ([MS-XLSB] 2.1.7.56), read before any formula so structured references find them.</summary>
        private void ReadTableParts(OpcPart sheetPart)
        {
            List<(uint, XlsbTable)> list = [];
            foreach (OpcRelationship relationship in sheetPart.Relationships)
            {
                if (relationship.External || !XlsxNames.SameType(relationship.Type, TableRelationship) || package.GetPart(relationship.TargetName) is not { } part)
                {
                    continue;
                }

                if (XlsbTable.Read(part.Data) is { } table)
                {
                    Links.Tables.TryAdd(table.Id, (table.DisplayName, [.. table.Columns.Select(c => c.Name)]));
                    list.Add((table.Id, table));
                }
            }

            tablesByPart[sheetPart] = list;
        }

        private void AddTables(Worksheet sheet, OpcPart sheetPart)
        {
            foreach ((_, XlsbTable table) in tablesByPart.GetValueOrDefault(sheetPart) ?? [])
            {
                string name = table.DisplayName;
                if (name.Length == 0 || book.Worksheets.SelectMany(s => s.Tables).Any(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                SheetTable made = new(sheet, name, table.Range)
                {
                    ShowHeaderRow = table.HeaderRows != 0,
                    ShowTotalsRow = table.TotalsRows != 0,
                    ShowFilterButtons = table.HasFilter,
                    StyleName = table.StyleName,
                    ShowRowStripes = table.RowStripes,
                    ShowColumnStripes = table.ColumnStripes,
                    ShowFirstColumn = table.FirstColumn,
                    ShowLastColumn = table.LastColumn,
                };
                foreach (XlsbTable.Column column in table.Columns)
                {
                    made.Columns.Add(new SheetTableColumn(column.Name)
                    {
                        TotalsRowFunction = column.Function,
                        TotalsRowLabel = column.Label,
                    });
                }

                sheet.Tables.AddRead(made);
            }
        }

        /// <summary>A chart sheet's part: its drawing's chart, placed after the worksheet before it.</summary>
        private void ReadChartSheet(string name, int visibility, OpcPart part, Worksheet? after)
        {
            if (string.IsNullOrEmpty(name) || book.Worksheets.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                || book.ChartSheets.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            string? drawingId = null;
            XlsbRecords records = new(part.Data);
            while (records.Next())
            {
                if (records.Type == 550)
                {
                    drawingId = records.Reader.WideString();
                }
            }

            if (Target(part, drawingId) is { } drawing && XlsbDrawings.FirstChart(drawing, package) is { } chart)
            {
                book.ChartSheetList.Add(new ChartSheet(book, name, Visibility(visibility), chart, after));
            }
        }

        /// <summary>The comments part of a sheet ([MS-XLSB] 2.1.7.8): its notes, as the sheet's comments.</summary>
        public void ReadComments(Worksheet sheet, OpcPart sheetPart, HashSet<CellAddress> visible)
        {
            if (Related(sheetPart, CommentsRelationship) is not { } part)
            {
                return;
            }

            List<string> authors = [];
            (int Row, int Column, int Author)? current = null;
            XlsbRecords records = new(part.Data);
            while (records.Next())
            {
                XlsbReader record = records.Reader;
                switch (records.Type)
                {
                    case 632: // BrtCommentAuthor
                        authors.Add(record.WideString());
                        break;
                    case 635: // BrtBeginComment
                        {
                            int author = (int)Math.Min(record.UInt32(), int.MaxValue);
                            (int r1, _, int c1, _) = record.Range();
                            current = (r1, c1, author);
                            break;
                        }

                    case 637 when current is { } note: // BrtCommentText
                        {
                            (string text, XElement? rich) = RichText(ref record, "text");
                            current = null;
                            if ((uint)note.Row >= CellAddress.MaxRows || (uint)note.Column >= CellAddress.MaxColumns)
                            {
                                break;
                            }

                            CellAddress cell = new(note.Row, note.Column);
                            if (sheet.Comments[cell] is not null)
                            {
                                break;
                            }

                            CellComment comment = sheet.Comments.Add(cell, text, (uint)note.Author < (uint)authors.Count ? authors[note.Author] : "");
                            comment.Visible = visible.Contains(cell);
                            comment.RichText = rich;
                            break;
                        }
                }
            }

            sheet.Comments.Changed = false;
        }
    }
}
