using System.Buffers.Binary;
using System.Xml.Linq;

using Egap.Documents;
using Egap.Documents.Office;
using Egap.Spreadsheets.Storage;
using Egap.Spreadsheets.Xlsx;

namespace Egap.Spreadsheets.Xls;

/// <summary>
/// Reads an Excel 97-2003 workbook (.xls): the BIFF8 workbook stream of a compound file ([MS-XLS], [MS-CFB]). It reads the
/// sheets, their cells with their values, styles and formulas (shared and array formulas too, with the results Excel kept),
/// merged cells, row heights and column widths, and the defined names. Charts, drawings, comments, data validation,
/// and conditional formats are not read yet. Encrypted workbooks (RC4 and RC4 CryptoAPI) are decrypted, and an encrypted XLSX
/// file, which is a compound file too, is decrypted and read as XLSX.
/// </summary>
internal static class XlsReader
{
    /// <summary>Whether the bytes start a compound file: an XLS file, or another Office 97-2003 document.</summary>
    public static bool Sniff(ReadOnlySpan<byte> head) => CompoundFile.IsCompoundFile(head);

    /// <summary>The password Excel encrypts a workbook with when it has none of its own, such as one only write-protected.</summary>
    internal const string DefaultPassword = "VelvetSweatshop";

    public static void Read(Workbook book, Stream stream, DocumentLimits limits, string? password)
    {
        CompoundFile file;
        try
        {
            file = CompoundFile.Read(stream, limits);
        }
        catch (InvalidDataException e)
        {
            throw new DocumentException("The file is not a compound file Egap can read: " + e.Message, e);
        }

        if (file.Find("EncryptedPackage") is not null)
        {
            // An encrypted XLSX file. Without a password, Excel's default one is tried first, as Excel does.
            byte[] package;
            try
            {
                package = OfficeEncryption.Decrypt(file, password ?? DefaultPassword, null, limits);
            }
            catch (DocumentPasswordException) when (password is null)
            {
                throw new DocumentPasswordException("The workbook is encrypted: give its password in the load options.", wrongPassword: false);
            }

            XlsxReader.Read(book, new MemoryStream(package, writable: false), limits);
            return;
        }

        byte[] data = file.ReadStream("Workbook")
            ?? (file.Find("Book") is not null
                ? throw new DocumentException("The file is an Excel 5.0 or 95 workbook (BIFF5), which Egap does not read; save it as .xlsx or as an Excel 97-2003 workbook.")
                : throw new DocumentException("The compound file has no Workbook stream: it is not an Excel workbook."));
        try
        {
            new State(book, new BiffStream(XlsEncryption.Decrypt(data, password))).Read();
        }
        catch (InvalidDataException e)
        {
            throw new DocumentException("The workbook stream is damaged: " + e.Message, e);
        }
    }

    private sealed class State(Workbook book, BiffStream biff)
    {
        private readonly XlsStyles styles = new();
        private readonly XlsLinks links = new();
        private readonly List<string> sst = [];
        private readonly List<(int Offset, int Type, int State)> boundSheets = [];
        private readonly List<(string Name, int Sheet, byte[] Tokens, byte[] Extra, bool BuiltIn, bool Hidden)> names = [];

        public void Read()
        {
            if (!biff.Read() || biff.Type != BiffType.Bof || biff.Length < 4)
            {
                throw new DocumentException("The workbook stream does not start with a BOF record.");
            }

            int version = BinaryPrimitives.ReadUInt16LittleEndian(biff.Body);
            if (version != 0x0600)
            {
                throw new DocumentException("The workbook is not in Excel 97-2003's format (BIFF8), which Egap reads; save it as .xlsx.");
            }

            ReadGlobals();
            Worksheet?[] sheets = new Worksheet?[boundSheets.Count];
            for (int i = 0; i < boundSheets.Count; i++)
            {
                // Worksheets, and Excel 4.0 macro sheets, which Excel too saves to XLSX as worksheets.
                if (boundSheets[i].Type is 0 or 1)
                {
                    Worksheet sheet = AddSheet(links.SheetNames[i]);
                    sheet.Visibility = boundSheets[i].State switch
                    {
                        1 => SheetVisibility.Hidden,
                        2 => SheetVisibility.VeryHidden,
                        _ => SheetVisibility.Visible,
                    };
                    sheets[i] = sheet;
                }
            }

            if (book.Worksheets.Count == 0)
            {
                throw new DocumentException("The workbook has no worksheets Egap reads.");
            }

            styles.Index(book, XlsStyles.DefaultXf);
            AddNames(sheets);
            Worksheet? after = null;
            for (int i = 0; i < boundSheets.Count; i++)
            {
                if (sheets[i] is { } sheet)
                {
                    biff.Seek(boundSheets[i].Offset);
                    new SheetState(this, sheet, boundSheets[i].Type == 1).Read();
                    after = sheet;
                }
                else if (boundSheets[i].Type == 2)
                {
                    biff.Seek(boundSheets[i].Offset);
                    ReadChartSheet(links.SheetNames[i], boundSheets[i].State, after);
                }
            }
        }

        /// <summary>A chart sheet's substream: its chart, placed after the worksheet before it.</summary>
        private void ReadChartSheet(string name, int visibility, Worksheet? after)
        {
            if (!biff.Read() || biff.Type != BiffType.Bof || string.IsNullOrEmpty(name)
                || book.Worksheets.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                || book.ChartSheets.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            List<(int Type, byte[] Body)> records = [];
            int depth = 1;
            while (depth > 0 && biff.Read())
            {
                if (biff.Type == BiffType.Bof)
                {
                    depth++;
                }
                else if (biff.Type == BiffType.Eof)
                {
                    depth--;
                }
                else if (depth == 1 && records.Count < 200_000)
                {
                    records.Add(((int)biff.Type, biff.Body.ToArray()));
                }
            }

            if (XlsChart.Read(records, tokens => XlsFormula.Decode(links, tokens, [], 0, 0, name: true)) is { } chart)
            {
                SheetVisibility state = visibility switch
                {
                    1 => SheetVisibility.Hidden,
                    2 => SheetVisibility.VeryHidden,
                    _ => SheetVisibility.Visible,
                };
                book.ChartSheetList.Add(new ChartSheet(book, name, state, chart, after));
            }
        }

        private int? maxDigit;
        private readonly List<byte> drawingGroup = [];
        private List<XlsDrawing.Blip?>? blips;

        /// <summary>The width of the normal font's widest digit, in pixels.</summary>
        public int Digit => maxDigit ??= Math.Max(1, new Printing.TextMeter(book).MaxDigitWidth);

        /// <summary>The pictures of the workbook's blip store, read when a sheet first needs them.</summary>
        public List<XlsDrawing.Blip?> Blips => blips ??= XlsDrawing.ReadBlips([.. drawingGroup]);

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

        private void ReadGlobals()
        {
            while (biff.Read())
            {
                switch (biff.Type)
                {
                    case BiffType.Eof:
                        FinishLinks();
                        return;
                    case BiffType.FilePass:
                        // Decrypted before the stream is read.
                        break;
                    case BiffType.MsoDrawingGroup:
                        {
                            drawingGroup.AddRange(biff.Body);
                            while (biff.PeekType() == BiffType.Continue && biff.Read())
                            {
                                drawingGroup.AddRange(biff.Body);
                            }

                            break;
                        }

                    case BiffType.DateMode:
                        book.Date1904 = biff.Length >= 2 && BinaryPrimitives.ReadUInt16LittleEndian(biff.Body) == 1;
                        break;
                    case BiffType.BoundSheet:
                        {
                            RecordReader record = biff.Reader();
                            int offset = (int)Math.Min(record.UInt32(), int.MaxValue);
                            int state = record.Byte() & 0x03;
                            int type = record.Byte();
                            links.SheetNames.Add(record.ShortUnicodeString());
                            boundSheets.Add((offset, type, state));
                            break;
                        }

                    case BiffType.Font:
                        styles.ReadFont(biff.Reader());
                        break;
                    case BiffType.Format:
                        styles.ReadFormat(biff.Continued());
                        break;
                    case BiffType.Xf:
                        styles.ReadXf(biff.Reader());
                        break;
                    case BiffType.Palette:
                        styles.ReadPalette(biff.Reader());
                        break;
                    case BiffType.Sst:
                        ReadSharedStrings(biff.Continued());
                        break;
                    case BiffType.SupBook:
                        ReadSupBook(biff.Continued());
                        break;
                    case BiffType.ExternName:
                        ReadExternName(biff.Continued());
                        break;
                    case BiffType.ExternSheet:
                        {
                            RecordReader record = biff.Continued();
                            int count = record.UInt16();
                            for (int i = 0; i < count && record.Remaining >= 6; i++)
                            {
                                links.Sheets.Add((record.UInt16(), record.Int16(), record.Int16()));
                            }

                            break;
                        }

                    case BiffType.Name:
                        ReadName(biff.Continued());
                        break;
                    case BiffType.Xct:
                        ReadCachedSheet();
                        break;
                }
            }

            FinishLinks();
        }

        private void ReadSharedStrings(RecordReader record)
        {
            record.UInt32();
            uint unique = record.UInt32();
            for (uint i = 0; i < unique && record.Remaining >= 3; i++)
            {
                string text = record.Characters(record.UInt16());
                sst.Add(text.Length > CellValue.MaxTextLength ? text[..CellValue.MaxTextLength] : text);
            }
        }

        private void ReadSupBook(RecordReader record)
        {
            int sheetCount = record.UInt16();
            int marker = record.UInt16();
            if (marker == 0x0401)
            {
                links.Books.Add(new XlsSupBook { Kind = XlsSupBookKind.Self });
                return;
            }

            if (marker == 0x3A01)
            {
                links.Books.Add(new XlsSupBook { Kind = XlsSupBookKind.AddIn });
                return;
            }

            string path = record.Characters(marker);
            List<string> sheets = [];
            for (int i = 0; i < sheetCount && record.Remaining >= 3; i++)
            {
                sheets.Add(record.UnicodeString());
            }

            // A file has sheets or an encoded path; a DDE or OLE link has neither.
            bool file = sheets.Count > 0 || (path.Length > 0 && path[0] is '' or '' or '');
            XlsSupBook book = new() { Kind = file ? XlsSupBookKind.External : XlsSupBookKind.Other, File = DecodePath(path) };
            book.Sheets.AddRange(sheets);
            links.Books.Add(book);
        }

        /// <summary>
        /// A linked file's path as the file encodes it ([MS-XLS] 2.5.277 VirtualPath): a drive, folder steps and the file name,
        /// with control characters for the drive, the separators and the parent folder.
        /// </summary>
        private static string DecodePath(string path)
        {
            if (path.Length == 0 || path[0] is not ('\u0001' or '\u0002' or '\u0005'))
            {
                return path;
            }

            System.Text.StringBuilder text = new();
            int i = 1;
            if (path[0] == '\u0005' && path.Length > 2)
            {
                // A URL: its length, then the URL.
                return path[2..];
            }

            while (i < path.Length)
            {
                char c = path[i++];
                switch (c)
                {
                    case '\u0001':
                        if (i < path.Length)
                        {
                            char drive = path[i++];
                            text.Append(drive == '@' ? "\\\\" : drive + ":\\");
                        }

                        break;
                    case '\u0002':
                    case '\u0003':
                        text.Append('\\');
                        break;
                    case '\u0004':
                        text.Append("..\\");
                        break;
                    case < ' ':
                        break;
                    default:
                        text.Append(c);
                        break;
                }
            }

            return text.ToString();
        }

        private void ReadExternName(RecordReader record)
        {
            if (links.Books.Count == 0)
            {
                return;
            }

            record.UInt16();
            record.UInt32();
            links.Books[^1].Names.Add(record.ShortUnicodeString());
        }

        private void ReadName(RecordReader record)
        {
            ushort options = record.UInt16();
            record.Byte();
            int length = record.Byte();
            int size = record.UInt16();
            record.UInt16();
            int sheet = record.UInt16();
            record.Skip(4);
            bool builtIn = (options & 0x20) != 0;
            string name = record.Characters(length);
            if (builtIn && name.Length > 0)
            {
                name = BuiltInName(name[0]);
            }
            else if (name.Length > 0 && char.IsAsciiDigit(name[0]))
            {
                // A name may not start with a digit in XLSX; Excel saves such a name with "_1_" in front.
                name = "_1_" + name;
            }

            links.Names.Add(name);
            byte[] tokens = size > 0 && size <= record.SegmentRemaining ? record.Bytes(size).ToArray() : [];
            byte[] extra = record.SegmentRemaining > 0 ? record.Rest().ToArray() : [];
            names.Add((name, sheet, tokens, extra, builtIn, (options & 0x0001) != 0));
        }

        private static string BuiltInName(char code) => code switch
        {
            '\u0000' => "Consolidate_Area",
            '\u0001' => "Auto_Open",
            '\u0002' => "Auto_Close",
            '\u0003' => "Extract",
            '\u0004' => "Database",
            '\u0005' => "Criteria",
            '\u0006' => "Print_Area",
            '\u0007' => "Print_Titles",
            '\u0008' => "Recorder",
            '\u0009' => "Data_Form",
            '\u000A' => "Auto_Activate",
            '\u000B' => "Auto_Deactivate",
            '\u000C' => "Sheet_Title",
            '\u000D' => "_FilterDatabase",
            _ => "_" + ((int)code).ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        /// <summary>The values a linked file's sheet keeps ([MS-XLS] 2.4.352 XCT and 2.4.65 CRN).</summary>
        private void ReadCachedSheet()
        {
            RecordReader xct = biff.Reader();
            int count = xct.UInt16();
            int index = xct.Remaining >= 2 ? xct.UInt16() : 0;
            XlsSupBook? source = links.Books.Count > 0 ? links.Books[^1] : null;
            List<(int Row, int Column, CellValue Value)> cells = [];
            for (int i = 0; i < count && biff.PeekType() == BiffType.Crn && biff.Read(); i++)
            {
                RecordReader crn = biff.Reader();
                int last = crn.Byte(), first = crn.Byte(), row = crn.UInt16();
                for (int column = first; column <= last && crn.Remaining > 0; column++)
                {
                    int type = crn.Byte();
                    CellValue value;
                    switch (type)
                    {
                        case 0x01:
                            value = SheetState.Number(crn.Double());
                            break;
                        case 0x02:
                            value = CellValue.FromText(Cap(crn.UnicodeString()));
                            break;
                        case 0x04:
                            value = CellValue.FromBoolean(crn.Byte() != 0);
                            crn.Skip(7);
                            break;
                        case 0x10:
                            value = CellValue.FromError(XlsFormula.ErrorValue(crn.Byte()));
                            crn.Skip(7);
                            break;
                        default:
                            crn.Skip(8);
                            continue;
                    }

                    cells.Add((row, column, value));
                }
            }

            if (source is { Kind: XlsSupBookKind.External } && index < source.Sheets.Count)
            {
                cachedSheets.Add((source, source.Sheets[index], cells));
            }
        }

        private readonly List<(XlsSupBook Book, string Sheet, List<(int Row, int Column, CellValue Value)> Cells)> cachedSheets = [];

        /// <summary>Numbers the linked files as Excel's formula text does and adds them to the workbook with their kept values.</summary>
        private void FinishLinks()
        {
            int number = 0;
            Dictionary<XlsSupBook, ExternalBook> made = [];
            foreach (XlsSupBook supBook in links.Books)
            {
                if (supBook.Kind == XlsSupBookKind.External)
                {
                    supBook.Number = ++number;
                    ExternalBook external = new(book, supBook.File ?? string.Empty);
                    book.ExternalBooks.Add(external);
                    made[supBook] = external;
                    foreach (string sheet in supBook.Sheets)
                    {
                        external.MakeSheet(book, sheet);
                    }
                }
            }

            foreach ((XlsSupBook supBook, string name, List<(int Row, int Column, CellValue Value)> cells) in cachedSheets)
            {
                if (made.TryGetValue(supBook, out ExternalBook? external))
                {
                    Worksheet sheet = external.MakeSheet(book, name);
                    foreach ((int row, int column, CellValue value) in cells)
                    {
                        if (row < CellAddress.MaxRows && column < CellAddress.MaxColumns)
                        {
                            sheet.SetRead(row, column, Record(value, 0), null);
                        }
                    }
                }
            }

            cachedSheets.Clear();
        }

        private void AddNames(Worksheet?[] sheets)
        {
            foreach ((string name, int sheetIndex, byte[] tokens, byte[] extra, bool builtIn, bool hidden) in names)
            {
                if (tokens.Length == 0 || XlsFormula.Decode(links, tokens, extra, 0, 0, name: true) is not { Length: > 0 } formula)
                {
                    continue;
                }

                Worksheet? sheet = sheetIndex > 0 && sheetIndex <= sheets.Length ? sheets[sheetIndex - 1] : null;
                if (sheetIndex > 0 && sheet is null)
                {
                    continue;
                }

                string full = builtIn ? "_xlnm." + name : name;
                if (book.Names.Any(n => n.Name.Equals(full, StringComparison.OrdinalIgnoreCase) && n.Sheet == sheet))
                {
                    continue;
                }

                book.Names.AddRead(new DefinedName(full, formula, sheet) { Hidden = hidden });
            }
        }

        public Workbook Book => book;

        /// <summary>
        /// A COLINFO width in 1/256 characters as Excel keeps it: snapped to whole pixels of the normal font's widest digit,
        /// then back to 1/256 characters.
        /// </summary>
        public double ColumnWidth(int units)
        {
            int digit = maxDigit ??= Math.Max(1, new Printing.TextMeter(book).MaxDigitWidth);
            double pixels = Math.Ceiling((units * digit / 256.0) - 0.5); // to the nearest pixel, a half down
            return Math.Min(Math.Truncate(pixels * 256 / digit) / 256, 255);
        }

        /// <summary>
        /// The default column width of a sheet whose DEFCOLWIDTH is <paramref name="characters"/>: that many digits and five
        /// pixels of padding, rounded up to a multiple of eight pixels, as Excel lays it out.
        /// </summary>
        public double DefaultColumnWidth(int characters)
        {
            int digit = maxDigit ??= Math.Max(1, new Printing.TextMeter(book).MaxDigitWidth);
            int pixels = ((characters * digit) + 5 + 7) / 8 * 8;
            return Math.Min(Math.Truncate(pixels * 256.0 / digit) / 256, 255);
        }

        public BiffStream Biff => biff;

        public XlsStyles Styles => styles;

        public XlsLinks Links => links;

        public List<string> SharedStrings => sst;

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

        public static string Cap(string text) => text.Length > CellValue.MaxTextLength ? text[..CellValue.MaxTextLength] : text;
    }

    /// <summary>One worksheet's substream, from its BOF to its EOF.</summary>
    /// <summary>The records of one sheet. A macro sheet keeps its constants only, as Excel keeps them in XLSX.</summary>
    private sealed class SheetState(State state, Worksheet sheet, bool macro)
    {
        private readonly List<(RangeAddress Range, byte[] Tokens, byte[] Extra)> sharedFormulas = [];
        private readonly Dictionary<(int, int), List<int>> sharedByFirstCell = [];
        private readonly Dictionary<(int, int), (RangeAddress Range, byte[] Tokens, byte[] Extra)> arrays = [];
        private readonly List<Pending> pending = [];
        private (int Row, int Column, int Style, string? Text, bool Deferred, int Pending)? awaitingString;
        // A chart has some dozens of records for each series; this keeps a damaged file from holding on to a whole stream.
        private const int MaxChartRecords = 200_000;

        private int baseWidth = 8;
        private bool drawingGoesOn;
        private readonly XlsObjects objects = new(sheet);

        private sealed class Pending
        {
            public int Row;
            public int Column;
            public int Style;
            public (int Row, int Column) Anchor;
            public CellValue Result;
        }

        public void Read()
        {
            BiffStream biff = state.Biff;
            if (!biff.Read() || biff.Type != BiffType.Bof)
            {
                throw new DocumentException($"The sheet {sheet.Name} does not start with a BOF record.");
            }

            int depth = 1;
            List<(int Type, byte[] Body)>? chart = null;
            while (depth > 0 && biff.Read())
            {
                if (biff.Type == BiffType.Bof)
                {
                    // An embedded chart's substream follows its OBJ record.
                    depth++;
                    chart = depth == 2 ? [] : null;
                    continue;
                }

                if (biff.Type == BiffType.Eof)
                {
                    depth--;
                    if (depth == 1 && chart is not null)
                    {
                        objects.Chart(chart, tokens => XlsFormula.Decode(state.Links, tokens, [], 0, 0, name: true));
                        chart = null;
                    }

                    continue;
                }

                if (depth == 1)
                {
                    Record(biff);
                }
                else if (depth == 2 && chart is not null && chart.Count < MaxChartRecords)
                {
                    chart.Add(((int)biff.Type, biff.Body.ToArray()));
                }
            }

            Resolve();
            objects.Finish();
            XlsDrawing.Add(sheet, objects, state.Blips, state.Digit);
        }

        private void Record(BiffStream biff)
        {
            // OfficeArt records too long for one MSODRAWING, or that come after an OBJ, go on in CONTINUE records.
            bool drawing = drawingGoesOn;
            drawingGoesOn = biff.Type is BiffType.MsoDrawing or BiffType.Obj || (biff.Type == BiffType.Continue && drawing);
            ReadOnlySpan<byte> body = biff.Body;
            switch (biff.Type)
            {
                case BiffType.Continue when drawing:
                    objects.MsoDrawing(body);
                    break;
                case BiffType.Number when body.Length >= 14:
                    Set(body, Number(BinaryPrimitives.ReadDoubleLittleEndian(body[6..])));
                    break;
                case BiffType.Rk when body.Length >= 10:
                    Set(body, Number(Rk(BinaryPrimitives.ReadUInt32LittleEndian(body[6..]))));
                    break;
                case BiffType.MulRk when body.Length >= 6:
                    {
                        int row = BinaryPrimitives.ReadUInt16LittleEndian(body);
                        int column = BinaryPrimitives.ReadUInt16LittleEndian(body[2..]);
                        for (int at = 4; at + 6 <= body.Length - 2; at += 6, column++)
                        {
                            Set(row, column, BinaryPrimitives.ReadUInt16LittleEndian(body[at..]), Number(Rk(BinaryPrimitives.ReadUInt32LittleEndian(body[(at + 2)..]))));
                        }

                        break;
                    }

                case BiffType.Label when body.Length >= 8:
                    {
                        RecordReader record = biff.Reader();
                        record.Skip(6);
                        Set(body, CellValue.FromText(State.Cap(record.UnicodeString())));
                        break;
                    }

                case BiffType.LabelSst when body.Length >= 10:
                    {
                        int index = (int)BinaryPrimitives.ReadUInt32LittleEndian(body[6..]);
                        Set(body, (uint)index < (uint)state.SharedStrings.Count ? CellValue.FromText(state.SharedStrings[index]) : CellValue.Empty);
                        break;
                    }

                case BiffType.BoolErr when body.Length >= 8:
                    Set(body, body[7] == 0 ? CellValue.FromBoolean(body[6] != 0) : CellValue.FromError(XlsFormula.ErrorValue(body[6])));
                    break;
                case BiffType.Blank when body.Length >= 6:
                    Set(body, CellValue.Empty);
                    break;
                case BiffType.MulBlank when body.Length >= 6:
                    {
                        int row = BinaryPrimitives.ReadUInt16LittleEndian(body);
                        int column = BinaryPrimitives.ReadUInt16LittleEndian(body[2..]);
                        for (int at = 4; at + 2 <= body.Length - 2; at += 2, column++)
                        {
                            Set(row, column, BinaryPrimitives.ReadUInt16LittleEndian(body[at..]), CellValue.Empty);
                        }

                        break;
                    }

                case BiffType.Formula when body.Length >= 22 && !macro:
                    Formula(body);
                    break;
                case BiffType.String:
                    if (awaitingString is { } awaiting)
                    {
                        RecordReader record = biff.Continued();
                        string text = State.Cap(record.UnicodeString());
                        awaitingString = null;
                        if (awaiting.Deferred)
                        {
                            pending[awaiting.Pending].Result = CellValue.FromText(text);
                        }
                        else
                        {
                            FinishFormula(awaiting.Row, awaiting.Column, awaiting.Style, awaiting.Text, CellValue.FromText(text));
                        }
                    }

                    break;
                case BiffType.SharedFormula when body.Length >= 10:
                    {
                        RecordReader record = biff.Continued();
                        int firstRow = record.UInt16(), lastRow = record.UInt16();
                        int firstColumn = record.Byte(), lastColumn = record.Byte();
                        record.UInt16();
                        int size = record.UInt16();
                        byte[] tokens = record.Bytes(Math.Min(size, record.SegmentRemaining)).ToArray();
                        if (firstRow <= lastRow && firstColumn <= lastColumn)
                        {
                            if (!sharedByFirstCell.TryGetValue((firstRow, firstColumn), out List<int>? list))
                            {
                                sharedByFirstCell[(firstRow, firstColumn)] = list = [];
                            }

                            list.Add(sharedFormulas.Count);
                            sharedFormulas.Add((new RangeAddress(firstRow, firstColumn, lastRow, lastColumn), tokens, record.Rest().ToArray()));
                        }

                        break;
                    }

                case BiffType.Array when body.Length >= 14:
                    {
                        RecordReader record = biff.Continued();
                        int firstRow = record.UInt16(), lastRow = record.UInt16();
                        int firstColumn = record.Byte(), lastColumn = record.Byte();
                        record.UInt16();
                        record.UInt32();
                        int size = record.UInt16();
                        byte[] tokens = record.Bytes(Math.Min(size, record.SegmentRemaining)).ToArray();
                        if (firstRow <= lastRow && firstColumn <= lastColumn)
                        {
                            arrays[(firstRow, firstColumn)] = (new RangeAddress(firstRow, firstColumn, lastRow, lastColumn), tokens, record.Rest().ToArray());
                        }

                        break;
                    }

                case BiffType.MergeCells when body.Length >= 2:
                    {
                        int count = BinaryPrimitives.ReadUInt16LittleEndian(body);
                        for (int i = 0, at = 2; i < count && at + 8 <= body.Length; i++, at += 8)
                        {
                            int r1 = BinaryPrimitives.ReadUInt16LittleEndian(body[at..]), r2 = BinaryPrimitives.ReadUInt16LittleEndian(body[(at + 2)..]);
                            int c1 = BinaryPrimitives.ReadUInt16LittleEndian(body[(at + 4)..]), c2 = BinaryPrimitives.ReadUInt16LittleEndian(body[(at + 6)..]);
                            if (r1 <= r2 && c1 <= c2 && c2 < CellAddress.MaxColumns && (r1 != r2 || c1 != c2))
                            {
                                sheet.MergedCells.AddRead(new RangeAddress(r1, c1, r2, c2));
                            }
                        }

                        break;
                    }

                case BiffType.Row when body.Length >= 16:
                    Row(body);
                    break;
                case BiffType.ColInfo when body.Length >= 10:
                    Column(body);
                    break;
                case BiffType.DefaultRowHeight when body.Length >= 4:
                    {
                        int height = BinaryPrimitives.ReadUInt16LittleEndian(body[2..]);
                        if (height is > 0 and <= 8190)
                        {
                            sheet.DefaultRowHeight = height / 20.0;
                        }

                        break;
                    }

                case BiffType.DefColWidth when body.Length >= 2:
                    baseWidth = Math.Min((int)BinaryPrimitives.ReadUInt16LittleEndian(body), 255);
                    break;
                case BiffType.MsoDrawing:
                    objects.MsoDrawing(body);
                    break;
                case BiffType.Obj:
                    objects.Obj(body);
                    break;
                case BiffType.Txo:
                    objects.Txo(biff);
                    break;
                case BiffType.Note:
                    objects.Note(biff.Continued());
                    break;
                case BiffType.HLink:
                    objects.Hyperlink(body);
                    break;
                case BiffType.HLinkTooltip:
                    objects.HyperlinkTip(body);
                    break;
                case BiffType.StandardWidth when body.Length >= 2:
                    sheet.DefaultColumnWidth = state.ColumnWidth(BinaryPrimitives.ReadUInt16LittleEndian(body));
                    break;
            }
        }

        /// <summary>A number as Excel holds it: a denormal one is 0, and one that is no number is #NUM!.</summary>
        internal static CellValue Number(double value) =>
            double.IsFinite(value) ? CellValue.FromNumber(double.IsSubnormal(value) ? 0 : value) : CellValue.FromError(CellError.Number);

        /// <summary>An RK number ([MS-XLS] 2.5.217): a 30-bit integer or the top of a double, maybe divided by 100.</summary>
        private static double Rk(uint rk)
        {
            double value = (rk & 0x02) != 0 ? (int)rk >> 2 : BitConverter.Int64BitsToDouble((long)(rk & 0xFFFFFFFC) << 32);
            return (rk & 0x01) != 0 ? value / 100 : value;
        }

        private void Set(ReadOnlySpan<byte> body, CellValue value) =>
            Set(BinaryPrimitives.ReadUInt16LittleEndian(body), BinaryPrimitives.ReadUInt16LittleEndian(body[2..]), BinaryPrimitives.ReadUInt16LittleEndian(body[4..]), value);

        private void Set(int row, int column, int xf, CellValue value)
        {
            if (column >= CellAddress.MaxColumns || row >= CellAddress.MaxRows)
            {
                return;
            }

            CellRecord record = state.Record(value, state.Styles.Index(state.Book, xf));
            if (!record.IsVacant)
            {
                sheet.SetRead(row, column, record, null);
            }
        }

        private void Formula(ReadOnlySpan<byte> body)
        {
            int row = BinaryPrimitives.ReadUInt16LittleEndian(body);
            int column = BinaryPrimitives.ReadUInt16LittleEndian(body[2..]);
            int style = state.Styles.Index(state.Book, BinaryPrimitives.ReadUInt16LittleEndian(body[4..]));
            ReadOnlySpan<byte> value = body.Slice(6, 8);
            bool awaitsString = false;
            CellValue result;
            if (value[6] == 0xFF && value[7] == 0xFF)
            {
                result = value[0] switch
                {
                    0 => CellValue.Empty,
                    1 => CellValue.FromBoolean(value[2] != 0),
                    2 => CellValue.FromError(XlsFormula.ErrorValue(value[2])),
                    _ => CellValue.FromText(string.Empty),
                };
                awaitsString = value[0] == 0;
            }
            else
            {
                result = Number(BinaryPrimitives.ReadDoubleLittleEndian(value));
            }

            int size = BinaryPrimitives.ReadUInt16LittleEndian(body[20..]);
            ReadOnlySpan<byte> rest = body[22..];
            ReadOnlySpan<byte> tokens = rest[..Math.Min(size, rest.Length)];
            ReadOnlySpan<byte> extra = rest[tokens.Length..];
            if (row >= CellAddress.MaxRows || column >= CellAddress.MaxColumns)
            {
                return;
            }

            if (tokens.Length == 5 && tokens[0] == 0x01)
            {
                pending.Add(new Pending
                {
                    Row = row,
                    Column = column,
                    Style = style,
                    Anchor = (BinaryPrimitives.ReadUInt16LittleEndian(tokens[1..]), BinaryPrimitives.ReadUInt16LittleEndian(tokens[3..])),
                    Result = result,
                });
                awaitingString = awaitsString ? (row, column, style, null, true, pending.Count - 1) : null;
                return;
            }

            string? text = XlsFormula.Decode(state.Links, tokens, extra, row, column);
            if (awaitsString)
            {
                awaitingString = (row, column, style, text, false, -1);
                return;
            }

            awaitingString = null;
            FinishFormula(row, column, style, text, result);
        }

        private void FinishFormula(int row, int column, int style, string? text, CellValue result)
        {
            if (text is null)
            {
                // A formula Egap cannot read keeps the value Excel showed.
                CellRecord record = state.Record(result, style);
                if (!record.IsVacant)
                {
                    sheet.SetRead(row, column, record, null);
                }

                return;
            }

            sheet.SetRead(row, column, new CellRecord { Style = style }, new FormulaEntry(text, result));
        }

        /// <summary>The cells of shared and array formulas, once the sheet's SHRFMLA and ARRAY records are read.</summary>
        private void Resolve()
        {
            if (sheet.DefaultColumnWidth is null && baseWidth != 8)
            {
                sheet.DefaultColumnWidth = state.DefaultColumnWidth(baseWidth);
            }

            if (awaitingString is { Deferred: false } awaiting)
            {
                FinishFormula(awaiting.Row, awaiting.Column, awaiting.Style, awaiting.Text, CellValue.FromText(string.Empty));
            }

            foreach (Pending cell in pending)
            {
                string? text = null;
                if (arrays.TryGetValue(cell.Anchor, out var array))
                {
                    if (cell.Row == cell.Anchor.Row && cell.Column == cell.Anchor.Column
                        && XlsFormula.Decode(state.Links, array.Tokens, array.Extra, cell.Row, cell.Column) is { } arrayText)
                    {
                        FormulaEntry entry = new(arrayText, cell.Result)
                        {
                            Attributes = [new XAttribute("t", "array"), new XAttribute("ref", array.Range.ToString())],
                        };
                        sheet.SetRead(cell.Row, cell.Column, new CellRecord { Style = cell.Style }, entry);
                        continue;
                    }
                }
                else if (SharedFormula(cell.Anchor, cell.Row, cell.Column) is { } shared)
                {
                    text = XlsFormula.Decode(state.Links, shared.Tokens, shared.Extra, cell.Row, cell.Column);
                }

                FinishFormula(cell.Row, cell.Column, cell.Style, text, cell.Result);
            }
        }

        /// <summary>
        /// The shared formula of a cell whose PtgExp names <paramref name="anchor"/>: the first cell that uses it, which its
        /// SHRFMLA record's range holds, though that range may start before it ([MS-XLS] 2.4.260). When several ranges start
        /// there, the first one that holds the cell is it, as in Excel.
        /// </summary>
        private (RangeAddress Range, byte[] Tokens, byte[] Extra)? SharedFormula((int Row, int Column) anchor, int row, int column)
        {
            if ((uint)anchor.Row >= CellAddress.MaxRows || (uint)anchor.Column >= CellAddress.MaxColumns)
            {
                return null;
            }

            CellAddress cell = new(row, column);
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

        private void Row(ReadOnlySpan<byte> body)
        {
            int row = BinaryPrimitives.ReadUInt16LittleEndian(body);
            if (row >= CellAddress.MaxRows)
            {
                return;
            }

            int height = BinaryPrimitives.ReadUInt16LittleEndian(body[6..]) & 0x7FFF;
            int options = BinaryPrimitives.ReadUInt16LittleEndian(body[12..]);
            int xf = BinaryPrimitives.ReadUInt16LittleEndian(body[14..]) & 0x0FFF;
            RowRecord record = new()
            {
                Hidden = (options & 0x20) != 0,
                OutlineLevel = options & 0x07,
                Collapsed = (options & 0x10) != 0,
            };
            double points = Math.Min(height / 20.0, 409.5);
            if ((options & 0x40) != 0)
            {
                record.Height = points;
            }
            else if (sheet.DefaultRowHeight is not { } standard || Math.Abs(standard - points) > 0.001)
            {
                record.FittedHeight = points;
            }

            if ((options & 0x80) != 0 && state.Styles.IsCellXf(xf))
            {
                record.Style = state.Styles.Index(state.Book, xf);
            }

            if (!record.IsDefault || !double.IsNaN(record.FittedHeight))
            {
                sheet.RowRecords[row] = record;
            }
        }

        private void Column(ReadOnlySpan<byte> body)
        {
            int first = BinaryPrimitives.ReadUInt16LittleEndian(body);
            int last = Math.Min((int)BinaryPrimitives.ReadUInt16LittleEndian(body[2..]), CellAddress.MaxColumns - 1);
            if (first > last)
            {
                return;
            }

            int width = BinaryPrimitives.ReadUInt16LittleEndian(body[4..]);
            int xf = BinaryPrimitives.ReadUInt16LittleEndian(body[6..]);
            int options = BinaryPrimitives.ReadUInt16LittleEndian(body[8..]);
            int style = xf != XlsStyles.DefaultXf && state.Styles.IsCellXf(xf) ? state.Styles.Index(state.Book, xf) : -1;
            if (last >= 255 && (xf == XlsStyles.DefaultXf || !state.Styles.IsCellXf(xf)) && (options & 0x1701) == 0)
            {
                // A plain span to the sheet's last column is the default width, as Excel saves it to XLSX.
                sheet.DefaultColumnWidth = state.ColumnWidth(width);
                return;
            }

            sheet.ColumnSpans.AddRead(new ColumnSpan
            {
                First = first,
                Last = last,
                Width = state.ColumnWidth(width),
                Hidden = (options & 0x01) != 0,
                OutlineLevel = (options >> 8) & 0x07,
                Collapsed = (options & 0x1000) != 0,
                Style = style > 0 ? style : -1,
            });
        }
    }
}
