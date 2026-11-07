using System.Globalization;

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
public sealed partial class Workbook
{
    private Workbook(StyleTable styles)
    {
        Worksheets = new WorksheetCollection(this);
        Styles = styles;
    }

    /// <summary>The worksheets, in tab order.</summary>
    public WorksheetCollection Worksheets { get; }

    /// <summary>The sheets of a loaded workbook that hold one chart and no cells.</summary>
    public IReadOnlyList<ChartSheet> ChartSheets => ChartSheetList;

    internal List<ChartSheet> ChartSheetList { get; } = [];

    /// <summary>The defined names, the print areas and print titles among them.</summary>
    public DefinedNameCollection Names { get; } = new();

    /// <summary>
    /// Whether dates count days from 1904-01-01, as old Mac workbooks do, rather than from 1900-01-01. Changing it changes the
    /// date every date cell shows; the numbers stay.
    /// </summary>
    public bool Date1904 { get; set; }

    /// <summary>How the workbook calculates: when, whether circular references iterate, and whether in parallel.</summary>
    public CalculationSettings Calculation { get; } = new();

    /// <summary>
    /// Calculates the formulas that need it, as Excel's F9 does: those reading a cell whose value or formula changed since
    /// the last calculation, the volatile ones (NOW, RAND, INDIRECT and the like), and the formulas reading their results,
    /// each after what it reads. A loaded workbook keeps the results Excel saved until its cells change; one changed since it
    /// was loaded or made has every formula calculated the first time. Independent formulas are calculated in parallel when
    /// <see cref="CalculationSettings.Concurrent"/> is on, and circular references iterate when
    /// <see cref="CalculationSettings.Iterate"/> is. A function Egap does not have gives #NAME?.
    /// </summary>
    /// <returns>How many formulas were calculated, and the circular references met.</returns>
    public CalculationResult Calculate() => (Calculator ??= new Recalculation.Calculator(this)).Calculate(full: false);

    /// <summary>Calculates every formula in the workbook, as Excel's Ctrl+Alt+F9 does, whatever changed.</summary>
    /// <returns>How many formulas were calculated, and the circular references met.</returns>
    public CalculationResult CalculateFull() => (Calculator ??= new Recalculation.Calculator(this)).Calculate(full: true);

    /// <summary>The workbook's calculation once one has run: compiled formulas and their dependency graph, kept up to date.</summary>
    internal Recalculation.Calculator? Calculator { get; private set; }

    /// <summary>The values kept for linked workbooks, in the order formulas number them from 1.</summary>
    internal List<Xlsx.ExternalBook> ExternalBooks { get; } = [];

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

    /// <summary>
    /// The culture <see cref="Cell.Text"/> shows numbers and dates in: its decimal and group separators, month and day names
    /// and short date. The invariant culture, the default, shows them as Excel in English does. Formulas are always English.
    /// </summary>
    /// <exception cref="ArgumentNullException">The culture set is null.</exception>
    public CultureInfo Culture
    {
        get;
        set => field = value ?? throw new ArgumentNullException(nameof(value));
    } = CultureInfo.InvariantCulture;

    /// <summary>Writes a sheet's new name into whatever refers to the sheet.</summary>
    internal void SheetRenamed(string from, string to)
    {
        string Renamed(string text) => Formulas.SheetRename.Apply(text, from, to);
        string? RenamedOrNull(string? text) => text is null ? null : Renamed(text);
        foreach (DefinedName name in Names)
        {
            string text = Renamed(name.Formula);
            if (text != name.Formula)
            {
                bool typed = name.Typed;
                name.Formula = text;
                name.Typed = typed;
            }
        }

        foreach (Worksheet sheet in Worksheets)
        {
            foreach (Storage.FormulaEntry entry in sheet.Formulas.Entries)
            {
                entry.Text = Renamed(entry.Text);
            }

            foreach (Hyperlink link in sheet.Hyperlinks)
            {
                link.Location = RenamedOrNull(link.Location);
            }

            foreach (DataValidation rule in sheet.DataValidations)
            {
                rule.Formula1 = RenamedOrNull(rule.Formula1);
                rule.Formula2 = RenamedOrNull(rule.Formula2);
            }

            foreach (ConditionalFormat format in sheet.ConditionalFormats)
            {
                foreach (ConditionalFormatRule rule in format.Rules)
                {
                    for (int i = 0; i < rule.Formulas.Count; i++)
                    {
                        rule.Formulas[i] = Renamed(rule.Formulas[i]);
                    }
                }
            }

            foreach (SheetTableColumn column in sheet.Tables.SelectMany(t => t.Columns))
            {
                column.TotalsRowFormula = RenamedOrNull(column.TotalsRowFormula);
                column.CalculatedFormula = RenamedOrNull(column.CalculatedFormula);
            }

            foreach (SheetDrawing drawing in sheet.Drawings)
            {
                if (drawing.Chart is { } chart)
                {
                    chart.RewriteReferences(Renamed);
                }
            }

            // Newer rules and sparklines the model does not read, in the sheet's extension list.
            foreach ((_, System.Xml.Linq.XElement kept) in sheet.Source?.Kept ?? [])
            {
                foreach (System.Xml.Linq.XElement formula in kept.Descendants(ExcelMain + "f"))
                {
                    formula.Value = Renamed(formula.Value);
                }
            }
        }

        foreach (ChartSheet page in ChartSheets)
        {
            page.Chart?.RewriteReferences(Renamed);
        }

        // What it compiled names the old sheet.
        Calculator = null;
    }

    /// <summary>
    /// Moves whatever refers to cells of <paramref name="edited"/> as rows or columns go into or out of it: the formulas of
    /// every sheet, names, rules, links and charts. Run before the sheet's cells move, as it reads where formulas are.
    /// </summary>
    /// <param name="edited">The sheet edited.</param>
    /// <param name="edit">The edit.</param>
    /// <param name="lost">The tables the edit takes columns from, as <see cref="Formulas.ReferenceShift.Apply"/> takes them.</param>
    internal void CellsMoving(Worksheet edited, Formulas.SheetEdit edit, Dictionary<string, HashSet<string>?> lost)
    {
        string Shift(string text, CellAddress origin, Worksheet? home) => Formulas.ReferenceShift.Apply(text, origin, home, edited, edit, lost);
        string? ShiftOrNull(string? text, CellAddress origin, Worksheet? home) => text is null ? null : Shift(text, origin, home);

        // A rule's formula is written for the first cell of its ranges; when the edit takes that cell, it is written for the
        // cell that comes first after the edit, as if moved there, before its references move.
        string? Rule(string? text, List<RangeAddress> ranges, Worksheet sheet)
        {
            if (text is null || ranges.Count == 0)
            {
                return text;
            }

            CellAddress anchor = ranges[0].First;
            if (sheet == edited && edit.Moved(anchor) is null && ranges.Select(edit.Moved).FirstOrDefault(r => r is not null) is { } kept)
            {
                CellAddress before = edit.Before(kept.First);
                if (Formulas.FormulaParser.TryParse(text, r1c1: false, anchor) is { } parsed)
                {
                    text = Formulas.FormulaPrinter.Print(parsed, Formulas.ReferenceStyle.A1, before);
                }

                anchor = before;
            }

            return Shift(text, anchor, sheet);
        }

        foreach (DefinedName name in Names)
        {
            string text = Shift(name.Formula, default, name.Sheet);
            if (text != name.Formula)
            {
                bool typed = name.Typed;
                name.Formula = text;
                name.Typed = typed;
            }
        }

        foreach (Worksheet sheet in Worksheets)
        {
            // Another sheet's formula can name the edited sheet only with a prefix, or its tables.
            bool other = sheet != edited && lost.Count == 0;
            sheet.RewriteCellFormulas((text, cell) => other && !text.Contains('!', StringComparison.Ordinal) ? text : Shift(text, cell, sheet));

            foreach (Hyperlink link in sheet.Hyperlinks)
            {
                link.Location = ShiftOrNull(link.Location, default, sheet);
            }

            foreach (DataValidation rule in sheet.DataValidations)
            {
                rule.Formula1 = Rule(rule.Formula1, rule.Ranges, sheet);
                rule.Formula2 = Rule(rule.Formula2, rule.Ranges, sheet);
            }

            foreach (ConditionalFormat format in sheet.ConditionalFormats)
            {
                foreach (ConditionalFormatRule rule in format.Rules)
                {
                    for (int i = 0; i < rule.Formulas.Count; i++)
                    {
                        rule.Formulas[i] = Rule(rule.Formulas[i], format.Ranges, sheet)!;
                    }
                }
            }

            foreach (SheetTable table in sheet.Tables)
            {
                foreach (SheetTableColumn column in table.Columns)
                {
                    column.TotalsRowFormula = ShiftOrNull(column.TotalsRowFormula, table.Range.First, sheet);
                    column.CalculatedFormula = ShiftOrNull(column.CalculatedFormula, table.Range.First, sheet);
                }
            }

            foreach (SheetDrawing drawing in sheet.Drawings)
            {
                drawing.Chart?.RewriteReferences(text => Shift(text, default, sheet));
            }

            foreach ((_, System.Xml.Linq.XElement kept) in sheet.Source?.Kept ?? [])
            {
                foreach (System.Xml.Linq.XElement formula in kept.DescendantsAndSelf().Where(e => e.Name.LocalName == "f"))
                {
                    formula.Value = Shift(formula.Value, default, sheet);
                }
            }
        }

        foreach (ChartSheet page in ChartSheets)
        {
            page.Chart?.RewriteReferences(text => Shift(text, default, null));
        }

        // What it compiled has the old places.
        Calculator = null;
        ChangeCount++;
    }

    private static readonly System.Xml.Linq.XNamespace ExcelMain = "http://schemas.microsoft.com/office/excel/2006/main";

    /// <summary>A new workbook with one empty sheet, <c>Sheet1</c>, as Excel starts one.</summary>
    public static Workbook Create()
    {
        Workbook book = new(new StyleTable());
        book.Worksheets.Add();
        return book;
    }

    /// <summary>
    /// Reads comma- or tab-separated text into a new workbook with one sheet. The separator comes from the options, a
    /// <c>sep=</c> line, the <c>.tsv</c> or <c>.tab</c> extension or the first line; see <see cref="CsvOptions"/> for the
    /// encoding, culture and type detection.
    /// </summary>
    /// <exception cref="ArgumentNullException">The path is null.</exception>
    /// <exception cref="InvalidDataException">The text has more rows or fields than a sheet holds, or a field longer than a cell holds.</exception>
    public static Workbook LoadCsv(string path, CsvOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        using FileStream stream = File.OpenRead(path);
        return Csv.CsvReader.Read(stream, path, options);
    }

    /// <summary>Reads comma- or tab-separated text from a stream into a new workbook with one sheet.</summary>
    /// <exception cref="ArgumentNullException">The stream is null.</exception>
    /// <exception cref="InvalidDataException">The text has more rows or fields than a sheet holds, or a field longer than a cell holds.</exception>
    public static Workbook LoadCsv(Stream stream, CsvOptions? options = null) => Csv.CsvReader.Read(stream, null, options);

    /// <summary>A new workbook with no sheets; add one before saving, as a workbook file needs at least one.</summary>
    public static Workbook CreateEmpty() => new(new StyleTable());

    /// <summary>
    /// Loads an XLSX, XLSM, XLTX or XLTM file, or a <c>.csv</c>, <c>.tsv</c> or <c>.tab</c> file as
    /// <see cref="LoadCsv(string, CsvOptions?)"/> does, with <see cref="WorkbookLoadOptions.Csv"/>.
    /// </summary>
    /// <exception cref="DocumentException">The file is not a workbook Egap can read.</exception>
    /// <exception cref="DocumentLimitException">The file goes past one of <see cref="WorkbookLoadOptions.Limits"/>.</exception>
    /// <exception cref="InvalidDataException">A CSV file has more rows or fields than a sheet holds, or a field longer than a cell holds.</exception>
    public static Workbook Load(string path, WorkbookLoadOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (IsCsv(path))
        {
            return LoadCsv(path, options?.Csv);
        }

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
    /// A <c>.csv</c>, <c>.tsv</c> or <c>.tab</c> path writes the first visible sheet as text, as
    /// <see cref="Worksheet.SaveCsv(string, CsvOptions?)"/> does, with <see cref="WorkbookSaveOptions.Csv"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The workbook has no worksheets, or no visible one for CSV.</exception>
    /// <exception cref="ArgumentException">The extension names no workbook kind and the options name none.</exception>
    public void Save(string path, WorkbookSaveOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (options?.Format is null && IsCsv(path))
        {
            Worksheet sheet = Worksheets.FirstOrDefault(s => s.Visibility == SheetVisibility.Visible)
                ?? throw new InvalidOperationException("The workbook has no visible sheet to save as CSV.");
            sheet.SaveCsv(path, options?.Csv);
            return;
        }

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

    /// <summary>
    /// The visible sheets laid out on pages as Excel prints the entire workbook, sheet after sheet; see
    /// <see cref="Worksheet.Paginate(SheetPrintOptions?)"/>.
    /// </summary>
    public SheetPrintLayout Paginate(SheetPrintOptions? options = null) => SheetPrintLayout.Create(this, VisibleSheets, options, chartSheets: true);

    /// <summary>Writes the visible sheets' printed pages to a PDF file, as Excel's export of the entire workbook does.</summary>
    /// <exception cref="ArgumentException">The path is null or empty.</exception>
    public void SaveAsPdf(string path, SheetPdfOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Printing.SheetPdf.Save(this, VisibleSheets, path, null, options, chartSheets: true);
    }

    /// <summary>Writes the visible sheets' printed pages as a PDF to a stream, which is left open.</summary>
    /// <exception cref="ArgumentNullException">The stream is null.</exception>
    public void SaveAsPdf(Stream stream, SheetPdfOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Printing.SheetPdf.Save(this, VisibleSheets, null, stream, options, chartSheets: true);
    }

    internal IEnumerable<Worksheet> VisibleSheets => Worksheets.Where(s => s.Visibility == SheetVisibility.Visible);

    internal static bool IsCsv(string path) => Path.GetExtension(path).ToUpperInvariant() is ".CSV" or ".TSV" or ".TAB";

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

    /// <summary>How a <c>.csv</c>, <c>.tsv</c> or <c>.tab</c> path is read, or null for the defaults.</summary>
    public CsvOptions? Csv { get; init; }
}

/// <summary>How a workbook is saved.</summary>
public sealed class WorkbookSaveOptions
{
    /// <summary>The kind of file to write, or null to go by the file's extension (or <see cref="Workbook.Format"/> for a stream).</summary>
    public WorkbookFormat? Format { get; init; }

    /// <summary>How a <c>.csv</c>, <c>.tsv</c> or <c>.tab</c> path is written, or null for the defaults.</summary>
    public CsvOptions? Csv { get; init; }
}
