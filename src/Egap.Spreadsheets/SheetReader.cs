using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

using Egap.Documents;
using Egap.Documents.Docx;
using Egap.Spreadsheets.Csv;
using Egap.Spreadsheets.Storage;
using Egap.Spreadsheets.Xlsx;

namespace Egap.Spreadsheets;

/// <summary>
/// Reads a sheet's rows one at a time from an XLSX file or CSV text, without building a <see cref="Workbook"/>: memory stays
/// the same however many rows there are. An XLSX file's shared strings and styles are read first, as every cell may use
/// them; the rows are then read from the sheet's part as they come.
/// </summary>
/// <example>
/// <code>
/// using SheetReader reader = SheetReader.Open("orders.xlsx");
/// while (reader.Read())
/// {
///     Console.WriteLine($"{reader[0]}: {reader.GetText(2)}");
/// }
/// </code>
/// </example>
public sealed class SheetReader : IDisposable
{
    private const string OfficeDocument = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument";
    private const string StrictOfficeDocument = "http://purl.oclc.org/ooxml/officeDocument/relationships/officeDocument";

    private readonly Stream? ownedStream;
    private readonly ZipArchive? zip;
    private readonly DocumentLimits limits;
    private readonly List<(string Name, string Part)> sheets = [];
    private readonly List<string> sharedStrings = [];
    private readonly StyleTable styles = StyleTable.ForLoading();
    private readonly List<(int Column, CellValue Value, int Style)> cells = [];
    private readonly NumberFormatCode?[] codeCache = new NumberFormatCode?[64];

    // XLSX state.
    private Stream? sheetStream;
    private XmlReader? xml;
    private int nextRow;

    // CSV state.
    private readonly CsvParser? csv;
    private readonly TypeDetector? detector;
    private readonly Workbook? csvBook;
    private readonly List<string> fields = [];

    private SheetReader(Stream? owned, ZipArchive archive, DocumentLimits limits)
    {
        ownedStream = owned;
        zip = archive;
        this.limits = limits;
    }

    private SheetReader(Stream? owned, Stream stream, string? path, CsvOptions options)
    {
        ownedStream = owned;
        limits = new DocumentLimits();
        Culture = options.Culture;
        StreamReader text = new(stream, options.Encoding ?? new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true, bufferSize: 65536, leaveOpen: owned is null);
        csv = new CsvParser(text);
        char? hint = csv.ReadHint();
        csv.Separator = options.Separator ?? hint ?? options.SeparatorFor(path) ?? csv.DetectSeparator();
        csvBook = Workbook.CreateEmpty();
        detector = options.DetectTypes ? new TypeDetector(options.Culture, csvBook) : null;
        sheets.Add((options.SheetName, ""));
        SheetName = options.SheetName;
    }

    /// <summary>The names of the file's worksheets, in tab order; a CSV file has one.</summary>
    public IReadOnlyList<string> SheetNames => [.. sheets.Select(s => s.Name)];

    /// <summary>The sheet being read.</summary>
    public string SheetName { get; private set; } = "";

    /// <summary>Whether the file counts dates from 1904 rather than 1900.</summary>
    public bool Date1904 { get; private set; }

    /// <summary>The culture <see cref="GetText"/> shows values in. Default the invariant culture, as Excel in English.</summary>
    /// <exception cref="ArgumentNullException">The culture set is null.</exception>
    public CultureInfo Culture
    {
        get;
        set => field = value ?? throw new ArgumentNullException(nameof(value));
    } = CultureInfo.InvariantCulture;

    /// <summary>The zero-based index of the row last read; rows the file leaves out are skipped.</summary>
    public int Row { get; private set; } = -1;

    /// <summary>The number of columns the row last read reaches: its last cell's column plus one.</summary>
    public int ColumnCount => cells.Count == 0 ? 0 : cells[^1].Column + 1;

    /// <summary>The value of a cell of the row last read; empty past its last cell.</summary>
    public CellValue this[int column] => Find(column) is int i ? cells[i].Value : CellValue.Empty;

    /// <summary>Opens an XLSX, XLSM, XLTX or XLTM file, or a CSV, TSV or TAB file by its extension, at its first sheet.</summary>
    /// <exception cref="DocumentException">The file is not a workbook.</exception>
    /// <exception cref="DocumentLimitException">The file is past a limit of <see cref="WorkbookLoadOptions.Limits"/>.</exception>
    public static SheetReader Open(string path, WorkbookLoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        try
        {
            return Workbook.IsCsv(path) ? new SheetReader(file, file, path, options?.Csv ?? new CsvOptions()) : OpenXlsx(file, file, options);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    /// <summary>Opens an XLSX, XLSM, XLTX or XLTM package at its first sheet; the stream is left open. A stream that cannot seek is copied into memory first.</summary>
    /// <exception cref="DocumentException">The stream is not a workbook.</exception>
    /// <exception cref="DocumentLimitException">The package is past a limit of <see cref="WorkbookLoadOptions.Limits"/>.</exception>
    public static SheetReader Open(Stream stream, WorkbookLoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return OpenXlsx(null, stream, options);
    }

    /// <summary>Opens CSV or TSV text; the stream is left open.</summary>
    public static SheetReader OpenCsv(Stream stream, CsvOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return new SheetReader(null, stream, null, options ?? new CsvOptions());
    }

    /// <summary>Starts reading another sheet, from its first row.</summary>
    /// <exception cref="KeyNotFoundException">The file has no worksheet of that name.</exception>
    public void SelectSheet(string name)
    {
        int index = sheets.FindIndex(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            throw new KeyNotFoundException($"The file has no worksheet named {name}.");
        }

        SelectSheet(index);
    }

    /// <summary>Starts reading the sheet at a position in tab order, from its first row.</summary>
    /// <exception cref="ArgumentOutOfRangeException">There is no sheet at that position.</exception>
    /// <exception cref="InvalidOperationException">The reader reads CSV text, which it cannot read again.</exception>
    public void SelectSheet(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, sheets.Count);
        if (zip is null)
        {
            throw new InvalidOperationException("CSV text has one sheet and is read once.");
        }

        xml?.Dispose();
        sheetStream?.Dispose();
        xml = null;
        sheetStream = null;
        SheetName = sheets[index].Name;
        Row = -1;
        nextRow = 0;
        cells.Clear();
        if (Entry(sheets[index].Part) is { } entry)
        {
            CheckEntry(entry);
            Guard(() =>
            {
                sheetStream = entry.Open();
                xml = XmlReader.Create(sheetStream, Settings());
                xml.MoveToContent();
                while (xml.Read())
                {
                    if (xml.NodeType == XmlNodeType.Element && xml.Depth == 1 && xml.LocalName == "sheetData" && XlsxNames.IsMain(xml.NamespaceURI))
                    {
                        if (xml.IsEmptyElement)
                        {
                            Close();
                        }

                        return;
                    }
                }

                Close();
            });
        }
    }

    /// <summary>Reads the next row that has cells, or that the file lists; false at the end of the sheet.</summary>
    /// <exception cref="DocumentException">The sheet's XML is not well formed.</exception>
    public bool Read()
    {
        cells.Clear();
        return csv is not null ? ReadCsv() : Guard(ReadXlsx);
    }

    /// <summary>The number format of a cell of the row last read, such as <c>General</c> or <c>yyyy-mm-dd</c>.</summary>
    public string GetNumberFormat(int column) => Find(column) is int i ? Format(cells[i].Style) : "General";

    /// <summary>A cell of the row last read as Excel shows it, through its number format (<see cref="Cell.Text"/>).</summary>
    public string GetText(int column)
    {
        if (Find(column) is not int i)
        {
            return "";
        }

        int style = cells[i].Style;
        NumberFormatCode code = (uint)style < (uint)codeCache.Length ? codeCache[style] ??= NumberFormatCode.Get(Format(style)) : NumberFormatCode.Get(Format(style));
        return code.Format(cells[i].Value, Culture, Date1904);
    }

    /// <summary>The cells of the row last read that are not empty, by column.</summary>
    public IEnumerable<(int Column, CellValue Value)> Cells => cells.Where(c => !c.Value.IsEmpty).Select(c => (c.Column, c.Value));

    /// <summary>Closes the file, or the stream when the reader opened it.</summary>
    public void Dispose()
    {
        xml?.Dispose();
        sheetStream?.Dispose();
        zip?.Dispose();
        ownedStream?.Dispose();
    }

    private static SheetReader OpenXlsx(Stream? owned, Stream stream, WorkbookLoadOptions? options)
    {
        DocumentLimits limits = options?.Limits ?? new DocumentLimits();
        Stream input = stream;
        if (!stream.CanSeek)
        {
            MemoryStream copy = new();
            stream.CopyTo(copy);
            copy.Position = 0;
            input = copy;
            owned = copy;
        }

        ZipArchive archive;
        try
        {
            archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException e)
        {
            throw new DocumentException("The file is not a valid ZIP package: " + e.Message, e);
        }

        SheetReader reader = new(owned, archive, limits);
        try
        {
            reader.Guard(reader.ReadWorkbook);
            reader.SelectSheet(0);
            return reader;
        }
        catch
        {
            archive.Dispose();
            if (owned != stream)
            {
                owned?.Dispose();
            }

            throw;
        }
    }

    private bool ReadWorkbook()
    {
        if (zip!.Entries.Count > limits.MaxEntries)
        {
            throw new DocumentLimitException($"The package has {zip.Entries.Count} entries, more than the limit of {limits.MaxEntries}.");
        }

        List<(string Id, string Type, string Target)> packageRels = Relationships("/_rels/.rels", "/");
        string workbookPart = packageRels.Find(r => r.Type is OfficeDocument or StrictOfficeDocument).Target
            ?? throw new DocumentException("The package has no main document part.");
        XElement root = LoadXml(workbookPart)?.Root is { } r && r.Name.LocalName == "workbook" && XlsxNames.IsMain(r.Name.NamespaceName)
            ? r : throw new DocumentException("The package's main part is not a workbook.");
        XNamespace ns = root.Name.Namespace;
        Date1904 = XlsxNames.Bool((string?)root.Element(ns + "workbookPr")?.Attribute("date1904"), false);
        List<(string Id, string Type, string Target)> rels = Relationships(OpcPackage.RelationshipsPartName(workbookPart), workbookPart);
        static bool Is(string type, string name) => type.EndsWith("/" + name, StringComparison.Ordinal);
        foreach (XElement sheet in root.Element(ns + "sheets")?.Elements(ns + "sheet") ?? [])
        {
            string? id = sheet.Attributes().FirstOrDefault(a => a.Name.LocalName == "id" && XlsxNames.IsRelationships(a.Name.NamespaceName))?.Value;
            (string Id, string Type, string Target) rel = rels.Find(x => x.Id == id);
            if (rel.Target is not null && Is(rel.Type, "worksheet"))
            {
                sheets.Add(((string?)sheet.Attribute("name") ?? "", rel.Target));
            }
        }

        if (sheets.Count == 0)
        {
            throw new DocumentException("The workbook has no worksheets.");
        }

        (string, string Type, string Target) styleRel = rels.Find(x => Is(x.Type, "styles"));
        XlsxStyles.Read(styles, styleRel.Target is null ? null : LoadXml(styleRel.Target));
        (string, string Type, string Target) stringsRel = rels.Find(x => Is(x.Type, "sharedStrings"));
        if (stringsRel.Target is not null && Entry(stringsRel.Target) is { } strings)
        {
            ReadSharedStrings(strings);
        }

        return true;
    }

    private void ReadSharedStrings(ZipArchiveEntry entry)
    {
        CheckEntry(entry);
        using Stream stream = entry.Open();
        using XmlReader reader = XmlReader.Create(stream, Settings());
        long total = 0;
        StringBuilder text = new();
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "si")
            {
                continue;
            }

            text.Clear();
            if (!reader.IsEmptyElement)
            {
                int depth = reader.Depth;
                bool inPhonetic = false;
                while (reader.Read() && reader.Depth > depth)
                {
                    if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "rPh")
                    {
                        inPhonetic = !reader.IsEmptyElement;
                    }
                    else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "rPh")
                    {
                        inPhonetic = false;
                    }
                    else if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "t" && !inPhonetic && !reader.IsEmptyElement)
                    {
                        text.Append(reader.ReadElementContentAsString());
                        if (reader.Depth <= depth)
                        {
                            break;
                        }
                    }
                }
            }

            total += text.Length;
            if (total > limits.MaxPartSize)
            {
                throw new DocumentLimitException($"The shared strings add up to more than the limit of {limits.MaxPartSize} characters.");
            }

            sharedStrings.Add(XlsxNames.Decode(text.ToString()));
        }
    }

    private bool ReadXlsx()
    {
        XmlReader? reader = xml;
        if (reader is null)
        {
            return false;
        }

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "sheetData")
            {
                Close();
                return false;
            }

            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "row")
            {
                continue;
            }

            int row = XlsxNames.Int(reader.GetAttribute("r"), 0) - 1;
            row = row is >= 0 and < CellAddress.MaxRows ? row : nextRow;
            Row = row;
            nextRow = row + 1;
            if (!reader.IsEmptyElement)
            {
                ReadCells(reader);
            }

            return true;
        }

        Close();
        return false;
    }

    private void ReadCells(XmlReader reader)
    {
        int depth = reader.Depth;
        int column = 0;
        while (reader.Read() && reader.Depth > depth)
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "c")
            {
                continue;
            }

            string? reference = reader.GetAttribute("r");
            if (reference is not null && CellAddress.TryParse(reference, out CellAddress address))
            {
                column = address.Column;
            }

            string? type = reader.GetAttribute("t");
            int style = XlsxNames.Int(reader.GetAttribute("s"), 0);
            string? value = null;
            string? inline = null;
            if (!reader.IsEmptyElement)
            {
                int cellDepth = reader.Depth;
                while (reader.Read() && reader.Depth > cellDepth)
                {
                    if (reader.NodeType != XmlNodeType.Element)
                    {
                        continue;
                    }

                    if (reader.LocalName == "v")
                    {
                        value = reader.ReadElementContentAsString();
                    }
                    else if (reader.LocalName == "is")
                    {
                        inline = InlineText(reader);
                    }

                    if (reader.Depth <= cellDepth)
                    {
                        break;
                    }
                }
            }

            CellValue cell = Value(type, value, inline);
            if (!cell.IsEmpty || style != 0)
            {
                if (cells.Count > 0 && cells[^1].Column >= column)
                {
                    // Cells out of order: the later one wins, as the model reads them.
                    cells.RemoveAll(c => c.Column >= column);
                }

                cells.Add((column, cell, style));
            }

            column = Math.Min(column + 1, CellAddress.MaxColumns - 1);
        }
    }

    private static string InlineText(XmlReader reader)
    {
        StringBuilder text = new();
        if (reader.IsEmptyElement)
        {
            return "";
        }

        int depth = reader.Depth;
        while (reader.Read() && reader.Depth > depth)
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "rPh" && !reader.IsEmptyElement)
            {
                reader.Skip();
                if (reader.Depth <= depth)
                {
                    break;
                }
            }

            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "t" && !reader.IsEmptyElement)
            {
                text.Append(reader.ReadElementContentAsString());
                if (reader.Depth <= depth)
                {
                    break;
                }
            }
        }

        return XlsxNames.Decode(text.ToString());
    }

    private CellValue Value(string? type, string? value, string? inline)
    {
        switch (type)
        {
            case "s":
                int index = XlsxNames.Int(value, -1);
                return (uint)index < (uint)sharedStrings.Count ? Text(sharedStrings[index]) : CellValue.Empty;
            case "inlineStr":
                return Text(inline ?? value ?? "");
            case "str":
                return Text(XlsxNames.Decode(value ?? ""));
            case "b":
                return value is null ? CellValue.Empty : CellValue.FromBoolean(value is "1" or "true");
            case "e":
                return value is not null && CellValue.TryParseError(value, out CellError error) ? CellValue.FromError(error) : CellValue.Empty;
            case "d":
                if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.RoundtripKind, out DateTime date)
                    && DateSerial.From(date) is double serial)
                {
                    serial -= Date1904 ? 1462 : 0;
                    return serial >= 0 ? CellValue.FromNumber(serial) : CellValue.Empty;
                }

                return CellValue.Empty;
            default:
                return XlsxNames.TryNumber(value, out double number) ? CellValue.FromNumber(number) : CellValue.Empty;
        }
    }

    private static CellValue Text(string text) =>
        text.Length == 0 ? CellValue.Empty : CellValue.TextUnchecked(text.Length > CellValue.MaxTextLength ? text[..CellValue.MaxTextLength] : text);

    private bool ReadCsv()
    {
        if (!csv!.ReadRow(fields))
        {
            return false;
        }

        Row++;
        if (Row >= CellAddress.MaxRows)
        {
            throw new InvalidDataException($"The text has more than {CellAddress.MaxRows} rows, the most a sheet holds.");
        }

        if (fields.Count > CellAddress.MaxColumns)
        {
            throw new InvalidDataException($"Row {Row + 1} has more than {CellAddress.MaxColumns} fields, the most a sheet holds.");
        }

        for (int column = 0; column < fields.Count; column++)
        {
            string field = fields[column];
            if (field.Length == 0)
            {
                continue;
            }

            if (field.Length > CellValue.MaxTextLength)
            {
                throw new InvalidDataException($"Row {Row + 1}, field {column + 1} is longer than the {CellValue.MaxTextLength} characters a cell holds.");
            }

            if (detector?.Detect(field) is ({ } value, int style))
            {
                cells.Add((column, value, style));
            }
            else
            {
                cells.Add((column, CellValue.TextUnchecked(field), 0));
            }
        }

        return true;
    }

    private string Format(int style)
    {
        StyleTable table = csvBook?.Styles ?? styles;
        return (uint)style < (uint)table.Count ? table[style].NumberFormat : "General";
    }

    private int? Find(int column)
    {
        // Cells are in column order: a binary search finds a column in a wide row.
        int low = 0, high = cells.Count - 1;
        while (low <= high)
        {
            int middle = (low + high) >>> 1;
            int found = cells[middle].Column;
            if (found == column)
            {
                return middle;
            }

            if (found < column)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return null;
    }

    private void Close()
    {
        xml?.Dispose();
        sheetStream?.Dispose();
        xml = null;
        sheetStream = null;
    }

    private XmlReaderSettings Settings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersFromEntities = 0,
        MaxCharactersInDocument = limits.MaxPartSize,
        IgnoreWhitespace = false,
        CloseInput = false,
    };

    private T Guard<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (XmlException e)
        {
            throw new DocumentException($"The sheet's XML is not well formed: {e.Message}", e);
        }
        catch (InvalidDataException e) when (csv is null)
        {
            throw new DocumentException("The file is not a valid ZIP package: " + e.Message, e);
        }
    }

    private void Guard(Action read) => Guard(() =>
    {
        read();
        return true;
    });

    private ZipArchiveEntry? Entry(string partName)
    {
        string name = partName.TrimStart('/');
        return zip!.GetEntry(name) ?? zip.Entries.FirstOrDefault(e => OpcPackage.NormalizeName(e.FullName).Equals(partName, StringComparison.OrdinalIgnoreCase));
    }

    private void CheckEntry(ZipArchiveEntry entry)
    {
        if (entry.Length > limits.MaxPartSize)
        {
            throw new DocumentLimitException($"Part {entry.FullName} is {entry.Length} bytes, more than the limit of {limits.MaxPartSize}.");
        }

        if (entry.Length > 1024 * 1024 && entry.CompressedLength > 0 && (double)entry.Length / entry.CompressedLength > limits.MaxCompressionRatio)
        {
            throw new DocumentLimitException($"Part {entry.FullName} expands {entry.Length / entry.CompressedLength} times, past the limit of {limits.MaxCompressionRatio}.");
        }
    }

    private XDocument? LoadXml(string partName)
    {
        if (Entry(partName) is not { } entry)
        {
            return null;
        }

        CheckEntry(entry);
        byte[] data = OpcPackage.ReadEntry(entry, limits);
        using XmlReader reader = XmlReader.Create(new MemoryStream(data, writable: false), Settings());
        return XDocument.Load(reader);
    }

    private List<(string Id, string Type, string Target)> Relationships(string relsName, string sourceName)
    {
        List<(string, string, string)> list = [];
        foreach (XElement relationship in LoadXml(relsName)?.Root?.Elements() ?? [])
        {
            if ((string?)relationship.Attribute("Id") is { } id && (string?)relationship.Attribute("Type") is { } type && (string?)relationship.Attribute("Target") is { } target
                && (string?)relationship.Attribute("TargetMode") != "External")
            {
                list.Add((id, type, OpcPackage.ResolveTarget(sourceName, target)));
            }
        }

        return list;
    }
}
