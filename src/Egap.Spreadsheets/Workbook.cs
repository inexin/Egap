using Egap.Documents;
using Egap.Spreadsheets.Storage;
using Egap.Spreadsheets.Xlsx;

namespace Egap.Spreadsheets;

/// <summary>
/// A spreadsheet workbook: its worksheets, defined names, and the text and styles the sheets share. Create one in code, or
/// load an XLSX, XLSM, XLTX or XLTM file and save it again with what it holds kept, the parts this model does not cover
/// included. A workbook is not safe to change from more than one thread at a time.
/// </summary>
/// <example>
/// <code>
/// Workbook book = Workbook.Create();
/// Worksheet sheet = book.Worksheets[0];
/// sheet["A1"].Value = "Total";
/// sheet["B1"].Value = 42;
/// </code>
/// </example>
public sealed class Workbook
{
    private Workbook(StyleTable styles)
    {
        Worksheets = new WorksheetCollection(this);
        Styles = styles;
    }

    /// <summary>The worksheets, in tab order.</summary>
    public WorksheetCollection Worksheets { get; }

    /// <summary>The defined names, the print areas and print titles among them.</summary>
    public DefinedNameCollection Names { get; } = new();

    /// <summary>
    /// Whether dates count days from 1904-01-01, as old Mac workbooks do, rather than from 1900-01-01. Changing it changes the
    /// date every date cell shows; the numbers stay.
    /// </summary>
    public bool Date1904 { get; set; }

    /// <summary>The kind of file the workbook was loaded from, and the kind a save to a stream writes when the options name none.</summary>
    public WorkbookFormat Format { get; set; }

    /// <summary>The text values of every sheet, each kept once.</summary>
    internal SharedStringTable SharedStrings { get; } = new();

    /// <summary>The cell styles of every sheet, each kept once.</summary>
    internal StyleTable Styles { get; }

    /// <summary>The package a loaded workbook came from, whose other parts are written back; null for a workbook made in code.</summary>
    internal WorkbookSource? Source { get; set; }

    /// <summary>How many times a cell's value or formula changed since the workbook was made or loaded.</summary>
    internal long ChangeCount { get; set; }

    /// <summary>A new workbook with one empty sheet, <c>Sheet1</c>, as Excel starts one.</summary>
    public static Workbook Create()
    {
        Workbook book = new(new StyleTable());
        book.Worksheets.Add();
        return book;
    }

    /// <summary>A new workbook with no sheets; add one before saving, as a workbook file needs at least one.</summary>
    public static Workbook CreateEmpty() => new(new StyleTable());

    /// <summary>Loads an XLSX, XLSM, XLTX or XLTM file.</summary>
    /// <exception cref="DocumentException">The file is not a workbook Egap can read.</exception>
    /// <exception cref="DocumentLimitException">The file goes past one of <see cref="WorkbookLoadOptions.Limits"/>.</exception>
    public static Workbook Load(string path, WorkbookLoadOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using FileStream stream = File.OpenRead(path);
        return Load(stream, options);
    }

    /// <summary>Loads an XLSX, XLSM, XLTX or XLTM file from a stream, which is left open.</summary>
    /// <exception cref="DocumentException">The file is not a workbook Egap can read.</exception>
    /// <exception cref="DocumentLimitException">The file goes past one of <see cref="WorkbookLoadOptions.Limits"/>.</exception>
    public static Workbook Load(Stream stream, WorkbookLoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Workbook book = new(StyleTable.ForLoading());
        XlsxReader.Read(book, stream, options?.Limits ?? new DocumentLimits());
        book.ChangeCount = 0;
        return book;
    }

    /// <summary>
    /// Saves the workbook to a file as the kind its extension names (.xlsx, .xlsm, .xltx or .xltm), unless
    /// <see cref="WorkbookSaveOptions.Format"/> names one. A workbook with macros saved as .xlsx or .xltx leaves the macros out.
    /// </summary>
    /// <exception cref="InvalidOperationException">The workbook has no worksheets.</exception>
    /// <exception cref="ArgumentException">The extension names no workbook kind and the options name none.</exception>
    public void Save(string path, WorkbookSaveOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        WorkbookFormat format = options?.Format ?? FormatOfExtension(Path.GetExtension(path))
            ?? throw new ArgumentException($"No workbook kind has the extension \"{Path.GetExtension(path)}\"; name one in the options.", nameof(path));
        using MemoryStream buffer = new();
        XlsxWriter.Write(this, buffer, format);
        using FileStream stream = File.Create(path);
        buffer.Position = 0;
        buffer.CopyTo(stream);
    }

    /// <summary>Saves the workbook to a stream, which is left open, as <see cref="WorkbookSaveOptions.Format"/> or else <see cref="Format"/>.</summary>
    /// <exception cref="InvalidOperationException">The workbook has no worksheets.</exception>
    public void Save(Stream stream, WorkbookSaveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        XlsxWriter.Write(this, stream, options?.Format ?? Format);
    }

    private static WorkbookFormat? FormatOfExtension(string extension) => extension.ToUpperInvariant() switch
    {
        ".XLSX" => WorkbookFormat.Xlsx,
        ".XLSM" => WorkbookFormat.Xlsm,
        ".XLTX" => WorkbookFormat.Xltx,
        ".XLTM" => WorkbookFormat.Xltm,
        _ => null,
    };
}

/// <summary>The kinds of Excel workbook file: workbook or template, with or without macros.</summary>
public enum WorkbookFormat
{
    /// <summary>An Excel workbook (.xlsx).</summary>
    Xlsx,

    /// <summary>An Excel workbook with macros (.xlsm).</summary>
    Xlsm,

    /// <summary>An Excel template (.xltx).</summary>
    Xltx,

    /// <summary>An Excel template with macros (.xltm).</summary>
    Xltm,
}

/// <summary>How a workbook is loaded.</summary>
public sealed class WorkbookLoadOptions
{
    /// <summary>The limits the reader keeps to, against files made to use up memory; the defaults suit any real workbook.</summary>
    public DocumentLimits Limits { get; init; } = new();
}

/// <summary>How a workbook is saved.</summary>
public sealed class WorkbookSaveOptions
{
    /// <summary>The kind of file to write, or null to go by the file's extension (or <see cref="Workbook.Format"/> for a stream).</summary>
    public WorkbookFormat? Format { get; init; }
}
