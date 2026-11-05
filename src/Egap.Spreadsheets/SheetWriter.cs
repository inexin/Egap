using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

using Egap.Documents.Docx;
using Egap.Spreadsheets.Csv;
using Egap.Spreadsheets.Xlsx;

namespace Egap.Spreadsheets;

/// <summary>
/// Writes rows one at a time to an XLSX file or CSV text, without building a <see cref="Workbook"/>: memory stays the same
/// however many rows there are. Each row goes straight to the file; the workbook's own parts are written when the writer is
/// disposed. Text is written in the cells (inline strings), so no table of strings grows with the file.
/// </summary>
/// <example>
/// <code>
/// using (SheetWriter writer = SheetWriter.Create("orders.xlsx"))
/// {
///     writer.StartSheet("Orders");
///     writer.SetColumnFormat(1, "yyyy-mm-dd");
///     writer.WriteRow(CellStyle.Default with { Font = CellFont.Default with { Bold = true } }, "Order", "Date");
///     writer.WriteRow("A-1", 45000);
/// }
/// </code>
/// </example>
public sealed class SheetWriter : IDisposable
{
    private const string Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/";
    private const string Ml = "application/vnd.openxmlformats-officedocument.spreadsheetml.";
    private const string ContentTypes = "http://schemas.openxmlformats.org/package/2006/content-types";
    private const string Relationships = "http://schemas.openxmlformats.org/package/2006/relationships";

    private readonly Stream stream;
    private readonly bool ownsStream;
    private readonly Workbook scratch = Workbook.CreateEmpty();
    private readonly Dictionary<int, int> columnStyles = [];
    private readonly SortedDictionary<int, double> columnWidths = [];
    private readonly List<string> columnNames = [];
    private readonly char[] number = new char[32];
    private bool disposed;

    // XLSX.
    private readonly ZipArchive? zip;
    private readonly List<string> sheetNames = [];
    private Stream? entry;
    private XmlWriter? xml;
    private int frozenRows;

    // CSV.
    private readonly StreamWriter? csv;
    private readonly CsvOptions? csvOptions;
    private readonly char separator;

    private SheetWriter(Stream stream, bool owns)
    {
        this.stream = stream;
        ownsStream = owns;
        zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
    }

    private SheetWriter(Stream stream, bool owns, string? path, CsvOptions options)
    {
        this.stream = stream;
        ownsStream = owns;
        csvOptions = options;
        separator = options.SeparatorFor(path) ?? (options.Culture.TextInfo.ListSeparator is [char list] ? list : ',');
        if (separator is '"' or '\r' or '\n')
        {
            throw new ArgumentException("The separator cannot be a quote or a line break.", nameof(options));
        }

        csv = new StreamWriter(stream, options.Encoding ?? new UTF8Encoding(true), bufferSize: 65536, leaveOpen: true) { NewLine = options.NewLine };
        if (options.WriteSeparatorHint)
        {
            csv.Write("sep=");
            csv.Write(separator);
            csv.Write(options.NewLine);
        }
    }

    /// <summary>Whether the workbook counts dates from 1904 rather than 1900; set before the first row. Default false.</summary>
    /// <exception cref="InvalidOperationException">A row was written already.</exception>
    public bool Date1904
    {
        get;
        set
        {
            if (Row > 0 || sheetNames.Count > 1)
            {
                throw new InvalidOperationException("Set the date system before the first row.");
            }

            field = value;
        }
    }

    /// <summary>The zero-based index of the row the next <see cref="WriteRow(ReadOnlySpan{CellValue})"/> writes.</summary>
    public int Row { get; private set; }

    /// <summary>Starts writing an XLSX file, or CSV or TSV text when the path ends in .csv, .tsv or .tab.</summary>
    /// <param name="path">The file to make or overwrite.</param>
    /// <param name="csvOptions">How CSV text is written; not used for XLSX.</param>
    public static SheetWriter Create(string path, CsvOptions? csvOptions = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        FileStream file = new(path, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
        try
        {
            return Workbook.IsCsv(path) ? new SheetWriter(file, true, path, csvOptions ?? new CsvOptions()) : new SheetWriter(file, true);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    /// <summary>Starts writing an XLSX package to a stream, which is left open; the stream need not seek.</summary>
    public static SheetWriter Create(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return new SheetWriter(stream, false);
    }

    /// <summary>Starts writing CSV or TSV text to a stream, which is left open.</summary>
    public static SheetWriter CreateCsv(Stream stream, CsvOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return new SheetWriter(stream, false, null, options ?? new CsvOptions());
    }

    /// <summary>
    /// Starts a new sheet; the rows written next go to it. The first row written without a sheet started goes to
    /// <c>Sheet1</c>. CSV text holds one sheet, whose name is not written.
    /// </summary>
    /// <exception cref="ArgumentException">The name is not one Excel allows, or another sheet has it.</exception>
    /// <exception cref="InvalidOperationException">The writer writes CSV and has a sheet already.</exception>
    public void StartSheet(string name)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        scratch.Worksheets.Add(name);
        if (csv is not null)
        {
            if (sheetNames.Count > 0)
            {
                throw new InvalidOperationException("CSV text holds one sheet.");
            }

            sheetNames.Add(name);
            return;
        }

        EndSheet();
        sheetNames.Add(name);
        Row = 0;
        frozenRows = 0;
        columnStyles.Clear();
        columnWidths.Clear();
    }

    /// <summary>Sets a column's width in characters for the sheet being written; before its first row.</summary>
    /// <exception cref="InvalidOperationException">A row of the sheet was written already.</exception>
    public void SetColumnWidth(int column, double width)
    {
        CheckColumn(column);
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(width, 255);
        BeforeRows();
        columnWidths[column] = width;
    }

    /// <summary>Keeps the top rows of the sheet being written in view as it scrolls; before its first row.</summary>
    /// <exception cref="InvalidOperationException">A row of the sheet was written already.</exception>
    public void FreezeTopRows(int rows)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rows);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(rows, CellAddress.MaxRows);
        BeforeRows();
        frozenRows = rows;
    }

    /// <summary>Gives a column's cells a number format, such as <c>yyyy-mm-dd</c> for dates, in the rows written from now on.</summary>
    public void SetColumnFormat(int column, string numberFormat)
    {
        CheckColumn(column);
        ArgumentNullException.ThrowIfNull(numberFormat);
        ObjectDisposedException.ThrowIf(disposed, this);
        columnStyles[column] = scratch.Styles.Add(CellStyle.Default with { NumberFormat = numberFormat });
    }

    /// <summary>Leaves rows empty.</summary>
    /// <exception cref="InvalidOperationException">The sheet would grow past its last row.</exception>
    public void SkipRows(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ObjectDisposedException.ThrowIf(disposed, this);
        if ((long)Row + count > CellAddress.MaxRows)
        {
            throw new InvalidOperationException($"A sheet holds {CellAddress.MaxRows} rows.");
        }

        if (csv is not null)
        {
            for (int i = 0; i < count; i++)
            {
                csv.Write(csv.NewLine);
            }
        }

        Row += count;
    }

    /// <summary>Writes a row from column A; empty values leave their cells empty.</summary>
    /// <exception cref="InvalidOperationException">The sheet is full.</exception>
    /// <exception cref="ArgumentException">The row has more cells than a sheet's columns.</exception>
    public void WriteRow(params ReadOnlySpan<CellValue> values) => WriteRow(null, values);

    /// <summary>Writes a row from column A with every cell in a style, which wins over the columns' number formats.</summary>
    /// <exception cref="InvalidOperationException">The sheet is full.</exception>
    /// <exception cref="ArgumentException">The row has more cells than a sheet's columns.</exception>
    public void WriteRow(CellStyle? style, params ReadOnlySpan<CellValue> values)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Row >= CellAddress.MaxRows)
        {
            throw new InvalidOperationException($"A sheet holds {CellAddress.MaxRows} rows.");
        }

        if (values.Length > CellAddress.MaxColumns)
        {
            throw new ArgumentException($"A row holds {CellAddress.MaxColumns} cells.", nameof(values));
        }

        int rowStyle = style is null ? -1 : scratch.Styles.Add(style);
        if (csv is not null)
        {
            WriteCsvRow(values, rowStyle);
        }
        else
        {
            WriteXlsxRow(values, rowStyle);
        }

        Row++;
    }

    /// <summary>Finishes the file: the sheet being written and the workbook's parts. The writer's stream is closed only when the writer opened it.</summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        try
        {
            if (zip is not null)
            {
                if (sheetNames.Count == 0)
                {
                    sheetNames.Add("Sheet1");
                }

                EndSheet();
                WriteWorkbookParts();
                zip.Dispose();
            }
            else
            {
                csv!.Dispose();
            }
        }
        finally
        {
            if (ownsStream)
            {
                stream.Dispose();
            }
        }
    }

    private static void CheckColumn(int column)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(column);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(column, CellAddress.MaxColumns);
    }

    private void BeforeRows()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (xml is not null || (csv is not null && Row > 0))
        {
            throw new InvalidOperationException("Set columns and frozen rows before the sheet's first row.");
        }
    }

    private int StyleOf(int column, int rowStyle) => rowStyle >= 0 ? rowStyle : columnStyles.GetValueOrDefault(column);

    private void WriteCsvRow(ReadOnlySpan<CellValue> values, int rowStyle)
    {
        CultureInfo culture = csvOptions!.Culture;
        for (int column = 0; column < values.Length; column++)
        {
            if (column > 0)
            {
                csv!.Write(separator);
            }

            CellValue value = values[column];
            if (value.IsEmpty)
            {
                continue;
            }

            string text = csvOptions.WriteFormattedText
                ? NumberFormatCode.Get(scratch.Styles[StyleOf(column, rowStyle)].NumberFormat).Format(value, culture, Date1904)
                : value.Kind == CellValueKind.Number ? value.Number.ToString("R", culture) : NumberFormatCode.General.Format(value, culture);
            CsvWriter.WriteField(csv!, text, separator);
        }

        csv!.Write(csv.NewLine);
    }

    private void WriteXlsxRow(ReadOnlySpan<CellValue> values, int rowStyle)
    {
        if (sheetNames.Count == 0)
        {
            scratch.Worksheets.Add("Sheet1");
            sheetNames.Add("Sheet1");
        }

        XmlWriter writer = xml ?? BeginSheet();
        string row = (Row + 1).ToString(CultureInfo.InvariantCulture);
        writer.WriteStartElement("row", XlsxNames.MainUri);
        writer.WriteAttributeString("r", row);
        for (int column = 0; column < values.Length; column++)
        {
            CellValue value = values[column];
            int style = StyleOf(column, rowStyle);
            if (value.IsEmpty && style == 0)
            {
                continue;
            }

            writer.WriteStartElement("c", XlsxNames.MainUri);
            writer.WriteAttributeString("r", ColumnName(column) + row);
            if (style != 0)
            {
                writer.WriteAttributeString("s", style.ToString(CultureInfo.InvariantCulture));
            }

            switch (value.Kind)
            {
                case CellValueKind.Number:
                    writer.WriteStartElement("v", XlsxNames.MainUri);
                    value.Number.TryFormat(number, out int written, "R", CultureInfo.InvariantCulture);
                    writer.WriteChars(number, 0, written);
                    writer.WriteEndElement();
                    break;
                case CellValueKind.Text:
                    writer.WriteAttributeString("t", "inlineStr");
                    writer.WriteStartElement("is", XlsxNames.MainUri);
                    WorksheetPartWriter.WriteText(writer, "t", value.Text);
                    writer.WriteEndElement();
                    break;
                case CellValueKind.Boolean:
                    writer.WriteAttributeString("t", "b");
                    writer.WriteElementString("v", XlsxNames.MainUri, value.Boolean ? "1" : "0");
                    break;
                case CellValueKind.Error:
                    writer.WriteAttributeString("t", "e");
                    writer.WriteElementString("v", XlsxNames.MainUri, CellValue.ErrorText(value.Error));
                    break;
            }

            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    private string ColumnName(int column)
    {
        while (columnNames.Count <= column)
        {
            columnNames.Add(CellAddress.ColumnName(columnNames.Count));
        }

        return columnNames[column];
    }

    private XmlWriter BeginSheet()
    {
        entry = zip!.CreateEntry("xl/worksheets/sheet" + sheetNames.Count.ToString(CultureInfo.InvariantCulture) + ".xml", CompressionLevel.Fastest).Open();
        XmlWriter writer = XmlWriter.Create(entry, WorksheetPartWriter.Settings);
        writer.WriteStartDocument(standalone: true);
        writer.WriteStartElement("worksheet", XlsxNames.MainUri);
        writer.WriteStartElement("sheetViews", XlsxNames.MainUri);
        writer.WriteStartElement("sheetView", XlsxNames.MainUri);
        if (sheetNames.Count == 1)
        {
            writer.WriteAttributeString("tabSelected", "1");
        }

        writer.WriteAttributeString("workbookViewId", "0");
        if (frozenRows > 0)
        {
            string topLeft = "A" + (frozenRows + 1).ToString(CultureInfo.InvariantCulture);
            writer.WriteStartElement("pane", XlsxNames.MainUri);
            writer.WriteAttributeString("ySplit", frozenRows.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("topLeftCell", topLeft);
            writer.WriteAttributeString("activePane", "bottomLeft");
            writer.WriteAttributeString("state", "frozen");
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteStartElement("sheetFormatPr", XlsxNames.MainUri);
        writer.WriteAttributeString("defaultRowHeight", "15");
        writer.WriteEndElement();
        if (columnWidths.Count > 0)
        {
            writer.WriteStartElement("cols", XlsxNames.MainUri);
            foreach ((int column, double width) in columnWidths)
            {
                string index = (column + 1).ToString(CultureInfo.InvariantCulture);
                writer.WriteStartElement("col", XlsxNames.MainUri);
                writer.WriteAttributeString("min", index);
                writer.WriteAttributeString("max", index);
                writer.WriteAttributeString("width", XlsxNames.Number(width));
                writer.WriteAttributeString("customWidth", "1");
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
        }

        writer.WriteStartElement("sheetData", XlsxNames.MainUri);
        xml = writer;
        return writer;
    }

    private void EndSheet()
    {
        if (zip is null || sheetNames.Count == 0)
        {
            return;
        }

        XmlWriter writer = xml ?? BeginSheet();
        writer.WriteEndElement();
        writer.WriteStartElement("pageMargins", XlsxNames.MainUri);
        foreach ((string name, string value) in (ReadOnlySpan<(string, string)>)[("left", "0.7"), ("right", "0.7"), ("top", "0.75"), ("bottom", "0.75"), ("header", "0.3"), ("footer", "0.3")])
        {
            writer.WriteAttributeString(name, value);
        }

        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.Dispose();
        entry!.Dispose();
        xml = null;
        entry = null;
    }

    private void WriteWorkbookParts()
    {
        void Part(string name, Action<XmlWriter> write)
        {
            using Stream part = zip!.CreateEntry(name, CompressionLevel.Fastest).Open();
            using XmlWriter writer = XmlWriter.Create(part, WorksheetPartWriter.Settings);
            writer.WriteStartDocument(standalone: true);
            write(writer);
        }

        Part("[Content_Types].xml", w =>
        {
            w.WriteStartElement("Types", ContentTypes);
            Default(w, "rels", "application/vnd.openxmlformats-package.relationships+xml");
            Default(w, "xml", "application/xml");
            Override(w, "/xl/workbook.xml", Ml + "sheet.main+xml");
            Override(w, "/xl/styles.xml", XlsxNames.StylesType);
            for (int i = 1; i <= sheetNames.Count; i++)
            {
                Override(w, "/xl/worksheets/sheet" + i.ToString(CultureInfo.InvariantCulture) + ".xml", XlsxNames.WorksheetType);
            }

            w.WriteEndElement();
        });
        Part("_rels/.rels", w =>
        {
            w.WriteStartElement("Relationships", Relationships);
            Relationship(w, "rId1", Rel + "officeDocument", "xl/workbook.xml");
            w.WriteEndElement();
        });
        Part("xl/_rels/workbook.xml.rels", w =>
        {
            w.WriteStartElement("Relationships", Relationships);
            for (int i = 1; i <= sheetNames.Count; i++)
            {
                string n = i.ToString(CultureInfo.InvariantCulture);
                Relationship(w, "rId" + n, XlsxNames.WorksheetRelationship, "worksheets/sheet" + n + ".xml");
            }

            Relationship(w, "rId" + (sheetNames.Count + 1).ToString(CultureInfo.InvariantCulture), XlsxNames.StylesRelationship, "styles.xml");
            w.WriteEndElement();
        });
        Part("xl/workbook.xml", w =>
        {
            w.WriteStartElement("workbook", XlsxNames.MainUri);
            w.WriteAttributeString("xmlns", "r", null, XlsxNames.RUri);
            if (Date1904)
            {
                w.WriteStartElement("workbookPr", XlsxNames.MainUri);
                w.WriteAttributeString("date1904", "1");
                w.WriteEndElement();
            }

            w.WriteStartElement("bookViews", XlsxNames.MainUri);
            w.WriteStartElement("workbookView", XlsxNames.MainUri);
            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteStartElement("sheets", XlsxNames.MainUri);
            for (int i = 0; i < sheetNames.Count; i++)
            {
                string n = (i + 1).ToString(CultureInfo.InvariantCulture);
                w.WriteStartElement("sheet", XlsxNames.MainUri);
                w.WriteAttributeString("name", sheetNames[i]);
                w.WriteAttributeString("sheetId", n);
                w.WriteAttributeString("id", XlsxNames.RUri, "rId" + n);
                w.WriteEndElement();
            }

            w.WriteEndElement();
            w.WriteEndElement();
        });
        using (Stream styles = zip!.CreateEntry("xl/styles.xml", CompressionLevel.Fastest).Open())
        {
            byte[] data = OpcPackageWriter.Serialize(XlsxStyles.Write(scratch.Styles));
            styles.Write(data);
        }
    }

    private static void Default(XmlWriter writer, string extension, string type)
    {
        writer.WriteStartElement("Default", ContentTypes);
        writer.WriteAttributeString("Extension", extension);
        writer.WriteAttributeString("ContentType", type);
        writer.WriteEndElement();
    }

    private static void Override(XmlWriter writer, string part, string type)
    {
        writer.WriteStartElement("Override", ContentTypes);
        writer.WriteAttributeString("PartName", part);
        writer.WriteAttributeString("ContentType", type);
        writer.WriteEndElement();
    }

    private static void Relationship(XmlWriter writer, string id, string type, string target)
    {
        writer.WriteStartElement("Relationship", Relationships);
        writer.WriteAttributeString("Id", id);
        writer.WriteAttributeString("Type", type);
        writer.WriteAttributeString("Target", target);
        writer.WriteEndElement();
    }
}
