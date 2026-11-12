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

    /// <summary>The workbook's protection: whether its structure is locked, and the hash of the password that unlocks it.</summary>
    public WorkbookProtection Protection { get; } = new();

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

    /// <summary>
    /// Refreshes every pivot table that can be refreshed, as Excel's Refresh All does for pivot tables: each cache reads its
    /// source again and its pivot tables are laid out again. A table that cannot be refreshed (see
    /// <see cref="PivotTable.CanRefresh"/>), or whose source no longer has the cache's fields, stays as it was.
    /// </summary>
    /// <returns>How many pivot tables were refreshed.</returns>
    public int RefreshPivotTables()
    {
        int refreshed = 0;
        foreach (Pivots.PivotCache cache in Pivots.Caches)
        {
            if (cache.Tables.FirstOrDefault(t => Worksheets.Any(s => s == t.Worksheet)) is not { CanRefresh: true })
            {
                continue;
            }

            try
            {
                Pivots.Refresh(cache);
                refreshed += cache.Tables.Count(t => Worksheets.Any(s => s == t.Worksheet));
            }
            catch (InvalidOperationException)
            {
                // Left as it was, as Excel leaves a table it cannot refresh.
            }
        }

        return refreshed;
    }

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

    /// <summary>The pivot tables and caches read from the file, read when first needed.</summary>
    internal PivotModel Pivots => field ??= PivotModel.Load(this);

    /// <summary>An XLS file's pivot tables as .xlsx parts, made when the pivot tables are first needed; null for other files.</summary>
    internal Func<Xls.PivotParts?>? MadePivots { get; set; }

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
        if (Source is not null)
        {
            foreach (Pivots.PivotCache cache in Pivots.Caches)
            {
                cache.SheetRenamed(from, to);
            }
        }

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
        if (Source is not null)
        {
            foreach (Pivots.PivotCache cache in Pivots.Caches)
            {
                cache.SheetEdited(edited, edit);
            }
        }

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
            string text = Formulas.ReferenceShift.Apply(name.Formula, default, name.Sheet, edited, edit, lost, name: true);
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

    /// <summary>
    /// Moves the references of every formula, name, rule, link and chart of the workbook for cells cut and pasted, before
    /// the cells move: see <see cref="Formulas.ReferenceMove"/>.
    /// </summary>
    internal void CellsMovingTo(Formulas.CellMove move)
    {
        string Move(string text, CellAddress origin, Worksheet? home, bool relocated = false) => Formulas.ReferenceMove.Apply(text, origin, home, move, relocated);
        string? MoveOrNull(string? text, CellAddress origin, Worksheet? home) => text is null ? null : Move(text, origin, home);

        foreach (DefinedName name in Names)
        {
            string text = Formulas.ReferenceMove.Apply(name.Formula, default, name.Sheet, move, name: true);
            if (text != name.Formula)
            {
                bool typed = name.Typed;
                name.Formula = text;
                name.Typed = typed;
            }
        }

        foreach (Worksheet sheet in Worksheets)
        {
            // A formula without a sheet's name means its own sheet.
            bool named = sheet != move.From && sheet != move.To;
            sheet.RewriteCellFormulas((text, cell) => named && !text.Contains('!', StringComparison.Ordinal)
                ? text
                : Move(text, cell, sheet, move.Across && sheet == move.From && move.Source.Contains(cell)));

            foreach (Hyperlink link in sheet.Hyperlinks)
            {
                link.Location = MoveOrNull(link.Location, default, sheet);
            }

            foreach (DataValidation rule in sheet.DataValidations)
            {
                CellAddress anchor = rule.Ranges.Count > 0 ? rule.Ranges[0].First : default;
                rule.Formula1 = MoveOrNull(rule.Formula1, anchor, sheet);
                rule.Formula2 = MoveOrNull(rule.Formula2, anchor, sheet);
            }

            foreach (ConditionalFormat format in sheet.ConditionalFormats)
            {
                CellAddress anchor = format.Ranges.Count > 0 ? format.Ranges[0].First : default;
                foreach (ConditionalFormatRule rule in format.Rules)
                {
                    for (int i = 0; i < rule.Formulas.Count; i++)
                    {
                        rule.Formulas[i] = Move(rule.Formulas[i], anchor, sheet);
                    }
                }
            }

            foreach (SheetTable table in sheet.Tables)
            {
                foreach (SheetTableColumn column in table.Columns)
                {
                    column.TotalsRowFormula = MoveOrNull(column.TotalsRowFormula, table.Range.First, sheet);
                    column.CalculatedFormula = MoveOrNull(column.CalculatedFormula, table.Range.First, sheet);
                }
            }

            foreach (SheetDrawing drawing in sheet.Drawings)
            {
                drawing.Chart?.RewriteReferences(text => Move(text, default, sheet));
            }

            foreach ((_, System.Xml.Linq.XElement kept) in sheet.Source?.Kept ?? [])
            {
                foreach (System.Xml.Linq.XElement formula in kept.DescendantsAndSelf().Where(e => e.Name.LocalName == "f"))
                {
                    formula.Value = Move(formula.Value, default, sheet);
                }
            }
        }

        foreach (ChartSheet page in ChartSheets)
        {
            page.Chart?.RewriteReferences(text => Move(text, default, null));
        }

        Calculator = null;
        ChangeCount++;
    }

    /// <summary>Drops what the calculator compiled, for cells that moved or were written in bulk.</summary>
    internal void Recompile()
    {
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
    /// Loads an XLSX, XLSM, XLTX or XLTM file, an Excel binary workbook (XLSB), an Excel 97-2003 workbook (XLS), an OpenDocument spreadsheet (ODS, OTS or flat FODS), or a <c>.csv</c>,
    /// <c>.tsv</c> or <c>.tab</c> file as <see cref="LoadCsv(string, CsvOptions?)"/> does, with <see cref="WorkbookLoadOptions.Csv"/>.
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

    /// <summary>
    /// Loads an XLSX, XLSM, XLTX or XLTM file, an Excel binary workbook (XLSB), an Excel 97-2003 workbook (XLS), or an
    /// OpenDocument spreadsheet (ODS, OTS or flat FODS), from a stream, which is left open. The kind is told from the file's
    /// first bytes and, in a package, from its main part. An XLSB or XLS workbook saves as XLSX. An OpenDocument spreadsheet's formulas are translated from
    /// OpenFormula to Excel's syntax, and the workbook's <see cref="Format"/> is <see cref="WorkbookFormat.Ods"/>,
    /// <see cref="WorkbookFormat.Ots"/> or <see cref="WorkbookFormat.Fods"/>. An encrypted one needs
    /// <see cref="WorkbookLoadOptions.Password"/>.
    /// </summary>
    /// <exception cref="DocumentException">The file is not a workbook Egap can read.</exception>
    /// <exception cref="DocumentLimitException">The file goes past one of <see cref="WorkbookLoadOptions.Limits"/>.</exception>
    /// <exception cref="DocumentPasswordException">The file is encrypted, and the options give no password or a wrong one.</exception>
    public static Workbook Load(Stream stream, WorkbookLoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        DocumentLimits limits = options?.Limits ?? new DocumentLimits();
        if (!stream.CanSeek)
        {
            // The kind is told from the first bytes, so a stream that cannot go back is read into memory first, as the ZIP reader would.
            using MemoryStream copy = new();
            stream.CopyTo(copy);
            copy.Position = 0;
            return Load(copy, options);
        }

        Workbook book = new(StyleTable.ForLoading());
        if (IsCompoundFile(stream))
        {
            Xls.XlsReader.Read(book, stream, limits, options?.Password);
        }
        else if (OdsBytes(stream, limits) is { } ods)
        {
            Ods.OdsReader.Read(book, ods, limits, options?.Password);
        }
        else
        {
            XlsxReader.Read(book, stream, limits);
        }

        book.ChangeCount = 0;
        return book;
    }

    /// <summary>Whether the stream holds a compound file, as an Excel 97-2003 workbook is, with the stream where it was.</summary>
    private static bool IsCompoundFile(Stream stream)
    {
        long start = stream.Position;
        Span<byte> head = stackalloc byte[8];
        int length = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        stream.Position = start;
        return Xls.XlsReader.Sniff(head[..length]);
    }

    /// <summary>The whole file when the stream holds an OpenDocument spreadsheet, or null with the stream where it was.</summary>
    private static byte[]? OdsBytes(Stream stream, DocumentLimits limits)
    {
        long start = stream.Position;
        byte[] head = new byte[8192];
        int length = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        stream.Position = start;
        if (!Ods.OdsReader.Sniff(head.AsSpan(0, length)))
        {
            return null;
        }

        long size = stream.Length - start;
        if (size > limits.MaxTotalSize || size > Array.MaxLength)
        {
            throw new DocumentLimitException($"The file is larger than the {limits.MaxTotalSize:N0} bytes the limits allow.");
        }

        byte[] bytes = new byte[size];
        stream.ReadExactly(bytes);
        return bytes;
    }

    /// <summary>
    /// Saves the workbook to a file as the kind its extension names (.xlsx, .xlsm, .xltx, .xltm, .ods, .ots or .fods), unless
    /// <see cref="WorkbookSaveOptions.Format"/> names one. A workbook with macros saved as .xlsx or .xltx leaves the macros out.
    /// An OpenDocument spreadsheet gets its formulas in OpenFormula; see <see cref="WorkbookFormat.Ods"/>.
    /// A <c>.csv</c>, <c>.tsv</c> or <c>.tab</c> path writes the first visible sheet as text, as
    /// <see cref="Worksheet.SaveCsv(string, CsvOptions?)"/> does, with <see cref="WorkbookSaveOptions.Csv"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The workbook has no worksheets, or no visible one for CSV.</exception>
    /// <exception cref="ArgumentException">The extension names no workbook kind and the options name none.</exception>
    /// <exception cref="NotSupportedException">A password is given for a kind of file that cannot be encrypted.</exception>
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
        Write(buffer, format, options?.Password, options?.Encryption ?? WorkbookEncryption.Agile);
        using FileStream stream = File.Create(path);
        buffer.Position = 0;
        buffer.CopyTo(stream);
    }

    /// <summary>Saves the workbook to a stream, which is left open, as <see cref="WorkbookSaveOptions.Format"/> or else <see cref="Format"/>.</summary>
    /// <exception cref="InvalidOperationException">The workbook has no worksheets.</exception>
    /// <exception cref="NotSupportedException">A password is given for a kind of file that cannot be encrypted.</exception>
    public void Save(Stream stream, WorkbookSaveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Write(stream, options?.Format ?? Format, options?.Password, options?.Encryption ?? WorkbookEncryption.Agile);
    }

    private void Write(Stream stream, WorkbookFormat format, string? password, WorkbookEncryption encryption = WorkbookEncryption.Agile)
    {
        bool encrypt = !string.IsNullOrEmpty(password);
        if (encrypt && format == WorkbookFormat.Fods)
        {
            throw new NotSupportedException("A flat FODS file cannot be encrypted; save as ODS or OTS to use a password.");
        }

        if (format is WorkbookFormat.Ods or WorkbookFormat.Ots or WorkbookFormat.Fods)
        {
            Ods.OdsWriter.Write(this, stream, format, encrypt ? password : null);
        }
        else if (encrypt)
        {
            // Excel's encrypted files: the package, encrypted, in a compound file.
            using MemoryStream package = new();
            XlsxWriter.Write(this, package, format);
            ReadOnlySpan<byte> bytes = package.GetBuffer().AsSpan(0, (int)package.Length);
            if (encryption == WorkbookEncryption.Standard)
            {
                Egap.Documents.Office.OfficeEncryption.EncryptStandard(bytes, password!, stream);
            }
            else
            {
                Egap.Documents.Office.OfficeEncryption.Encrypt(bytes, password!, stream);
            }
        }
        else
        {
            XlsxWriter.Write(this, stream, format);
        }
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
        ".ODS" => WorkbookFormat.Ods,
        ".OTS" => WorkbookFormat.Ots,
        ".FODS" => WorkbookFormat.Fods,
        _ => null,
    };
}

/// <summary>The kinds of workbook file: Excel's workbooks and templates, with or without macros, and OpenDocument spreadsheets.</summary>
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

    /// <summary>An OpenDocument spreadsheet (.ods).</summary>
    Ods,

    /// <summary>An OpenDocument spreadsheet template (.ots).</summary>
    Ots,

    /// <summary>A flat OpenDocument spreadsheet, one XML file (.fods).</summary>
    Fods,
}

/// <summary>How a workbook is loaded.</summary>
public sealed class WorkbookLoadOptions
{
    /// <summary>The limits the reader keeps to, against files made to use up memory; the defaults suit any real workbook.</summary>
    public DocumentLimits Limits { get; init; } = new();

    /// <summary>How a <c>.csv</c>, <c>.tsv</c> or <c>.tab</c> path is read, or null for the defaults.</summary>
    public CsvOptions? Csv { get; init; }

    /// <summary>
    /// The password of an encrypted workbook: an XLSX file Excel encrypted (agile or standard encryption), an XLS file
    /// encrypted with RC4 or CryptoAPI, or an OpenDocument spreadsheet; or null. Without one, Excel's own default password is
    /// tried, as Excel does for a workbook it encrypted only to protect it from writing.
    /// </summary>
    public string? Password { get; init; }
}

/// <summary>How a workbook is saved.</summary>
public sealed class WorkbookSaveOptions
{
    /// <summary>The kind of file to write, or null to go by the file's extension (or <see cref="Workbook.Format"/> for a stream).</summary>
    public WorkbookFormat? Format { get; init; }

    /// <summary>How a <c>.csv</c>, <c>.tsv</c> or <c>.tab</c> path is written, or null for the defaults.</summary>
    public CsvOptions? Csv { get; init; }

    /// <summary>
    /// A password to encrypt the file with, or null or empty to write it plain. An XLSX, XLSM, XLTX or XLTM file is encrypted
    /// as Excel does, the way <see cref="Encryption"/> names, and Excel opens it with the password. An ODS or OTS file is
    /// encrypted as ODF 1.2 and later do: AES-256 in CBC mode, its key made from the password's SHA-256 hash with PBKDF2. A
    /// flat FODS file cannot be encrypted.
    /// </summary>
    public string? Password { get; init; }

    /// <summary>How an Excel file saved with a <see cref="Password"/> is encrypted; agile encryption unless set.</summary>
    public WorkbookEncryption Encryption { get; init; } = WorkbookEncryption.Agile;
}

/// <summary>How Excel's encrypted files are encrypted ([MS-OFFCRYPTO]).</summary>
public enum WorkbookEncryption
{
    /// <summary>
    /// Agile encryption, as Excel 2010 and later write it: AES-256 in CBC mode, a key made from the password's SHA-512 hash
    /// spun 100,000 times, and an HMAC that checks the file has not been changed.
    /// </summary>
    Agile,

    /// <summary>
    /// Standard encryption, as Excel 2007 writes it: AES-128 in ECB mode with a key made from the password's SHA-1 hash spun
    /// 50,000 times, and no integrity check. Use it only for programs that do not read agile encryption.
    /// </summary>
    Standard,
}
