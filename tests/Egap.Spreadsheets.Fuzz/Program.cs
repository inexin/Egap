using System.Globalization;
using System.Text;

using Egap.Documents;
using Egap.Documents.Layout;
using Egap.Pdf.Fuzz;
using Egap.Spreadsheets;
using Egap.Spreadsheets.Formulas;
using Egap.Spreadsheets.Printing;

using SharpFuzz;

// Fuzz targets for Egap.Spreadsheets: number format codes, CSV, XLSX, ODS, XLS, formulas and header and footer codes. A format code
// or a header and footer code never throws; CSV may refuse its input only with an InvalidDataException, XLSX only with a
// DocumentException (a DocumentLimitException among them), and a formula only with a FormulaException. Anything else is a
// finding.
Dictionary<string, Action<Stream>> targets = new(StringComparer.Ordinal)
{
    ["numberformat"] = NumberFormat,
    ["csv"] = Csv,
    ["xlsx"] = Xlsx,
    ["formula"] = Formula,
    ["headerfooter"] = HeaderFooter,
    ["document"] = AsDocument,
    ["tables"] = FromTables,
    ["links"] = Links,
    ["sheetcharts"] = SheetCharts,
    ["encrypted"] = Encrypted,
    ["edits"] = Edits,
    ["shift"] = Shift,
    ["copies"] = Copies,
    ["move"] = Move,
    ["sorts"] = Sorts,
    ["find"] = Find,
    ["conditional"] = Conditional,
    ["autofit"] = AutoFit,
    ["ods"] = input => Ods(input, null),
    ["odscrypt"] = input => Ods(input, "fuzz"),
    ["xls"] = Xls,
};

if (args is ["mutate", string mutateTarget, string seconds, string seeds, string findings])
{
    return Mutator.Run(targets[mutateTarget], TimeSpan.FromSeconds(double.Parse(seconds, CultureInfo.InvariantCulture)), seeds, findings);
}

if (args is ["replay", string replayTarget, string folder])
{
    // Runs every finding in a folder and prints each distinct failure once, with its first input.
    Dictionary<string, string> seen = [];
    foreach (string path in Directory.EnumerateFiles(folder).Order(StringComparer.Ordinal))
    {
        try
        {
            using FileStream input = File.OpenRead(path);
            targets[replayTarget](input);
        }
        catch (Exception error)
        {
            string key = $"{error.GetType().Name}: {error.Message}";
            key = key.Length > 160 ? key[..160] : key;
            if (seen.TryAdd(key, path))
            {
                Console.WriteLine($"{key} ({Path.GetFileName(path)})");
            }
        }
    }

    Console.WriteLine($"{seen.Count} distinct failures.");
    return 0;
}

if (args is [string target, string file])
{
    using FileStream input = File.OpenRead(file);
    targets[target](input);
    return 0;
}

if (args is [string stdinTarget])
{
    Fuzzer.OutOfProcess.Run(targets[stdinTarget]);
    return 0;
}

Console.Error.WriteLine($"Usage: Egap.Spreadsheets.Fuzz <target> [file] | mutate <target> <seconds> <seeds> <findings> | replay <target> <folder>. Targets: {string.Join(", ", targets.Keys)}.");
return 2;

// An XLSX package read as a document: Document.Load refuses it only with a DocumentException, or for a format that isn't registered, a NotSupportedException.
static void AsDocument(Stream input)
{
    SpreadsheetFormat.Register();
    byte[] bytes = Bytes(input);
    using MemoryStream stream = new(bytes, writable: false);
    Document document;
    try
    {
        document = Document.Load(stream);
    }
    catch (Exception error) when (error is DocumentException or NotSupportedException)
    {
        // What isn't a package reads as a format this harness has not registered.
        return;
    }

    _ = document.Sections.Sum(section => section.Body.Tables.Sum(table => table.Rows.Count));
    Workbook.FromTables(document);
}

// Any bytes read as CSV, shown as a document and put back on sheets; nothing but CSV's own refusal may throw.
static void FromTables(Stream input)
{
    Workbook book;
    try
    {
        book = Workbook.LoadCsv(input);
    }
    catch (InvalidDataException)
    {
        return;
    }

    Workbook.FromTables(book.ToDocument());
}

// An XLSX package whose formulas read a linked book's kept values; only loading it may refuse it, with a DocumentException.
static void Links(Stream input)
{
    Workbook book;
    try
    {
        book = Workbook.Load(input);
    }
    catch (DocumentException)
    {
        return;
    }

    if (book.Worksheets.Count == 0)
    {
        return;
    }

    Worksheet sheet = book.Worksheets[0];
    sheet["XFC1"].Formula = "[1]Prices!A1";
    sheet["XFC2"].Formula = "SUM([1]Prices!A1:B3)";
    sheet["XFC3"].Formula = "[1]!Rate";
    sheet["XFC4"].Formula = "VLOOKUP(7,[1]!Table,1,FALSE)";
    sheet["XFC5"].Formula = "INDIRECT(\"[1]Prices!A2\")";
    book.CalculateFull();
}

static byte[] Bytes(Stream input)
{
    using MemoryStream buffer = new();
    input.CopyTo(buffer);
    return buffer.ToArray();
}

// The input is a format code; its bytes, read eight at a time, are also numbers to show through it.
static void NumberFormat(Stream input)
{
    byte[] bytes = Bytes(input);
    NumberFormatCode code = NumberFormatCode.Parse(Encoding.UTF8.GetString(bytes));
    List<double> values = [0, 1, -1, 0.5, 1e-300, 1e300, -double.MaxValue, 45366.75, 2958465.99999999];
    for (int i = 0; i + 8 <= bytes.Length && values.Count < 32; i += 8)
    {
        double value = BitConverter.ToDouble(bytes, i);
        if (double.IsFinite(value))
        {
            values.Add(value);
        }
    }

    CultureInfo[] cultures = [CultureInfo.InvariantCulture, CultureInfo.GetCultureInfo("de-DE"), CultureInfo.GetCultureInfo("ar-SA")];
    foreach (CultureInfo culture in cultures)
    {
        foreach (double value in values)
        {
            _ = code.Format(value, culture, date1904: false);
            _ = code.Format(value, culture, date1904: true);
            _ = code.GetColor(value);
        }

        _ = code.Format("text", culture);
    }
}

// The input is a header or footer code (ECMA-376 Part 1, 18.3.1.46): parsed, then printed to PDF as a sheet's header, footer
// and first-page header, with page numbers, dates and the file name filled in; must never throw.
static void HeaderFooter(Stream input)
{
    string text = Encoding.UTF8.GetString(Bytes(input));
    HeaderFooterCode code = HeaderFooterCode.Parse(text);
    _ = code.Sections.Sum(s => s.Count);
    Workbook book = Workbook.Create();
    Worksheet sheet = book.Worksheets[0];
    sheet["A1"].Value = 1;
    sheet["C40"].Value = "x";
    sheet.PageSetup.Header = text;
    sheet.PageSetup.Footer = text.Length > 1 ? text[(text.Length / 2)..] : text;
    SheetPrintOptions print = new() { Fonts = FontSettings.Portable, PrintTime = new DateTime(2026, 11, 5, 12, 0, 0), FilePath = @"C:\dataook.xlsx" };
    using MemoryStream pdf = new();
    sheet.SaveAsPdf(pdf, new SheetPdfOptions { Print = print, Tagged = false });
}

// The input is CSV text: read with type detection, written back, and read again.
static void Csv(Stream input)
{
    Workbook book;
    try
    {
        book = Workbook.LoadCsv(input);
    }
    catch (InvalidDataException)
    {
        return;
    }

    MemoryStream output = new();
    book.Worksheets[0].SaveCsv(output);
    output.Position = 0;
    Workbook.LoadCsv(output);

    input.Position = 0;
    using SheetReader reader = SheetReader.OpenCsv(input);
    while (reader.Read())
    {
        for (int column = 0; column < reader.ColumnCount; column++)
        {
            _ = reader.GetText(column);
        }
    }
}

// The input is an XLSX package: loaded, saved and loaded again, and every sheet streamed through SheetReader.
// A workbook's charts: read, filled from the cells, edited and saved. Only a DocumentException may come out.
// An encrypted XLS or XLSX file, opened with the password the seeds were encrypted with: the FILEPASS record or the
// EncryptionInfo stream, the decryption, and the workbook read from what it gives; may throw only DocumentException.
static void Encrypted(Stream input)
{
    try
    {
        Workbook book = Workbook.Load(new MemoryStream(Bytes(input), writable: false), new WorkbookLoadOptions
        {
            Password = "Egap p\u00E4ssword 1",
            Limits = new DocumentLimits { MaxPartSize = 16 * 1024 * 1024, MaxTotalSize = 64 * 1024 * 1024 },
        });
        foreach (Worksheet sheet in book.Worksheets)
        {
            _ = sheet.UsedRange;
        }

        using MemoryStream saved = new();
        book.Save(saved);
    }
    catch (DocumentException)
    {
    }
}

static void SheetCharts(Stream input)
{
    try
    {
        Workbook book = Workbook.Load(new MemoryStream(Bytes(input), writable: false), new WorkbookLoadOptions { Limits = new DocumentLimits { MaxPartSize = 16 * 1024 * 1024, MaxTotalSize = 64 * 1024 * 1024 } });
        foreach (ChartSheet page in book.ChartSheets)
        {
            if (page.Chart is not null)
            {
                page.RefreshChart();
                page.Chart.Title.Text = "Edited";
            }
        }

        foreach (Worksheet sheet in book.Worksheets)
        {
            foreach (SheetDrawing drawing in sheet.Drawings)
            {
                if (drawing.Chart is { } chart)
                {
                    drawing.RefreshChart();
                    foreach (Egap.Documents.Charts.ChartSeries series in chart.Series)
                    {
                        _ = series.Name;
                        _ = series.Values.Numbers.Count;
                        _ = series.Categories.TextLevels.Count;
                    }

                    chart.Title.Text = "Edited";
                }
            }
        }

        using MemoryStream saved = new();
        book.Save(saved);

        // Every chart prints, on the sheets' pages and on the chart sheets' own.
        SheetPrintLayout layout = book.Paginate();
        foreach (SheetPage page in layout.Pages.Take(4))
        {
            using Egami.Raster<Egami.Pixels.Rgba8> image = page.Render(36);
        }

        foreach (ChartSheetPage page in layout.ChartPages.Take(4))
        {
            using Egami.Raster<Egami.Pixels.Rgba8> image = page.Render(36);
        }
    }
    catch (DocumentException)
    {
    }
}

static void Ods(Stream input, string? password)
{
    // An OpenDocument spreadsheet, zipped or flat, or a password-protected one: loaded, cells shown and their translated
    // formulas parsed, calculated, then saved in its own format and as XLSX and loaded again.
    DocumentLimits limits = new() { MaxPartSize = 16 * 1024 * 1024, MaxTotalSize = 64 * 1024 * 1024 };
    byte[] bytes = Bytes(input);
    try
    {
        Workbook book = Workbook.Load(new MemoryStream(bytes, writable: false), new WorkbookLoadOptions { Limits = limits, Password = password });
        foreach (Worksheet sheet in book.Worksheets)
        {
            if (sheet.UsedRange?.Address is { } used)
            {
                for (int row = used.First.Row; row <= Math.Min(used.Last.Row, used.First.Row + 200); row++)
                {
                    for (int column = used.First.Column; column <= Math.Min(used.Last.Column, used.First.Column + 50); column++)
                    {
                        Egap.Spreadsheets.Cell cell = sheet[row, column];
                        _ = cell.Text;
                        _ = cell.FormulaR1C1;
                    }
                }
            }
        }

        if (book.Worksheets.Sum(s => (long)s.Cells.Count) < 20_000)
        {
            book.Calculate();
        }

        foreach (WorkbookFormat format in (WorkbookFormat[])[book.Format, WorkbookFormat.Xlsx])
        {
            using MemoryStream saved = new();
            book.Save(saved, new WorkbookSaveOptions { Format = format, Password = format == WorkbookFormat.Ods ? password : null });
            saved.Position = 0;
            _ = Workbook.Load(saved, new WorkbookLoadOptions { Limits = limits, Password = password });
        }
    }
    catch (DocumentException)
    {
    }
}

// An Excel 97-2003 workbook (BIFF8): the compound file, its records and the formulas decoded from their tokens. It goes the
// same way as an ODS: loaded, cells shown and their formulas written in R1C1 style, calculated, then saved as XLSX and
// loaded again; only a DocumentException may be thrown.
static void Xls(Stream input) => Ods(input, null);

static void Xlsx(Stream input)
{
    DocumentLimits limits = new() { MaxPartSize = 16 * 1024 * 1024, MaxTotalSize = 64 * 1024 * 1024 };
    byte[] bytes = Bytes(input);
    try
    {
        Workbook book = Workbook.Load(new MemoryStream(bytes, writable: false), new WorkbookLoadOptions { Limits = limits });
        foreach (Worksheet sheet in book.Worksheets)
        {
            if (sheet.UsedRange?.Address is { } used)
            {
                for (int row = used.First.Row; row <= Math.Min(used.Last.Row, used.First.Row + 200); row++)
                {
                    for (int column = used.First.Column; column <= Math.Min(used.Last.Column, used.First.Column + 50); column++)
                    {
                        _ = sheet[row, column].Text;
                    }
                }
            }

            foreach (SheetTable table in sheet.Tables)
            {
                table.Name = table.Name;
            }

            // Protection: hashes with few spins are checked, and the editable ranges move with an edit.
            if (sheet.Protection.Password is not { SpinCount: > 1000 })
            {
                _ = sheet.Protection.Verify("fuzz");
            }

            foreach (EditableRange range in sheet.Protection.EditableRanges)
            {
                if (range.Password is not { SpinCount: > 1000 })
                {
                    _ = range.Verify("fuzz");
                }
            }

            _ = sheet.Protection.CanEdit(new CellAddress(0, 0));
            if (sheet.UsedRange?.Address is { } box)
            {
                RangeAddress part = new(box.First, new CellAddress(Math.Min(box.Last.Row, box.First.Row + 200), Math.Min(box.Last.Column, box.First.Column + 30)));
                _ = sheet.Range(part).ToDataTable();
            }

            CellAddress last = new(CellAddress.MaxRows - 1, 0);
            if (sheet.Comments[last] is null)
            {
                sheet.Comments.Add(last, "fuzz");
            }
        }

        if (book.Protection.Password is not { SpinCount: > 1000 })
        {
            _ = book.Protection.Verify("fuzz");
        }

        MemoryStream saved = new();
        book.Save(saved);
        saved.Position = 0;
        Workbook.Load(saved);
    }
    catch (DocumentException)
    {
    }
    catch (InvalidOperationException e) when (e.Message.Contains("sheet", StringComparison.Ordinal))
    {
        // A workbook with no worksheets cannot be saved.
    }

    try
    {
        using SheetReader reader = SheetReader.Open(new MemoryStream(bytes, writable: false), new WorkbookLoadOptions { Limits = limits });
        for (int sheet = 0; sheet < reader.SheetNames.Count; sheet++)
        {
            reader.SelectSheet(sheet);
            while (reader.Read())
            {
                for (int column = 0; column < Math.Min(reader.ColumnCount, 64); column++)
                {
                    _ = reader.GetText(column);
                }
            }
        }
    }
    catch (DocumentException)
    {
    }
}

// The input is a workbook: each worksheet gets rows and columns put in and taken out, which may be refused only with an
// InvalidOperationException, and the workbook must save and load again.
static void Edits(Stream input)
{
    DocumentLimits limits = new() { MaxPartSize = 16 * 1024 * 1024, MaxTotalSize = 64 * 1024 * 1024 };
    Workbook book;
    try
    {
        book = Workbook.Load(new MemoryStream(Bytes(input), writable: false), new WorkbookLoadOptions { Limits = limits });
    }
    catch (DocumentException)
    {
        return;
    }

    if (book.Worksheets.Count == 0)
    {
        return;
    }

    foreach (Worksheet sheet in book.Worksheets)
    {
        foreach (Action edit in (Action[])[() => sheet.InsertRows(2, 3), () => sheet.DeleteColumns(1), () => sheet.DeleteRows(0, 2), () => sheet.InsertColumns(3), () => sheet.DeleteRows(CellAddress.MaxRows - 1)])
        {
            try
            {
                edit();
            }
            catch (InvalidOperationException)
            {
                // Excel refuses it too: cells pushed off the sheet, part of an array formula, a table's header row alone.
            }
        }

        _ = sheet.UsedRange;
    }

    book.Calculate();
    MemoryStream saved = new();
    try
    {
        book.Save(saved);
    }
    catch (DocumentException)
    {
        // A part kept as it was read is read again to save it, and may not be well-formed.
        return;
    }

    saved.Position = 0;
    Workbook.Load(saved);
}

// The input is a formula: its references moved for rows and columns put in and taken out. What parses must still parse
// after each edit, and an edit that moves nothing gives the text back as it was.
static void Shift(Stream input)
{
    string text = Encoding.UTF8.GetString(Bytes(input));
    if (text.Length > 8192)
    {
        text = text[..8192];
    }

    CellAddress cell = new(text.Length % 40, text.Length % 7);
    if (FormulaParser.TryParse(text, r1c1: false, cell) is null)
    {
        return;
    }

    Workbook book = Workbook.Create();
    Worksheet sheet = book.Worksheets[0];
    Worksheet other = book.Worksheets.Add("Other");
    SheetEdit[] edits = [new(true, 2, 3, true), new(true, 1, 2, false), new(false, 0, 1, true), new(false, 3, 4, false), new(true, CellAddress.MaxRows - 2, 2, false)];
    foreach (SheetEdit edit in edits)
    {
        string moved = ReferenceShift.Apply(text, cell, sheet, sheet, edit, new(StringComparer.OrdinalIgnoreCase) { ["Table1"] = null });
        if (FormulaParser.TryParse(moved, r1c1: false, cell) is null)
        {
            throw new InvalidOperationException("A moved formula does not parse: " + moved);
        }

        if (ReferenceShift.Apply(text, cell, other, sheet, edit) is var elsewhere && elsewhere != text && !text.Contains('!', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A formula with no sheet named changed on another sheet: " + elsewhere);
        }
    }
}

// The input is an XLSX package: ranges of each sheet copied and moved, on the sheet and to another, then calculated, saved
// and loaded again. A copy or move Excel refuses may throw InvalidOperationException.
static void Copies(Stream input)
{
    DocumentLimits limits = new() { MaxPartSize = 16 * 1024 * 1024, MaxTotalSize = 64 * 1024 * 1024 };
    Workbook book;
    try
    {
        book = Workbook.Load(new MemoryStream(Bytes(input), writable: false), new WorkbookLoadOptions { Limits = limits });
    }
    catch (DocumentException)
    {
        return;
    }

    if (book.Worksheets.Count == 0)
    {
        return;
    }

    Worksheet first = book.Worksheets[0];
    foreach (Worksheet sheet in book.Worksheets.ToList())
    {
        foreach (Action paste in (Action[])[
            () => sheet.Range("A1:C5").CopyTo(sheet["B3"]), () => sheet.Range("B2:D4").MoveTo(sheet["C1"]), () => sheet.Range("A2:B9").MoveTo(first["F20"]),
            () => sheet.Range("A1:Z40").CopyTo(first["AA1"]), () => sheet.Range("C3").MoveTo(sheet["A1"]), () => sheet.Range(sheet.UsedRange?.Address ?? RangeAddress.Parse("A1")).MoveTo(sheet["B2"])])
        {
            try
            {
                paste();
            }
            catch (InvalidOperationException)
            {
                // Excel refuses it too: part of an array formula or merged cell, part of a table, a pivot table.
            }
            catch (ArgumentException) when (sheet.UsedRange is not null)
            {
                // The used range reaches the end of the sheet, so it can't move on.
            }
        }
    }

    book.Calculate();
    MemoryStream saved = new();
    try
    {
        book.Save(saved);
    }
    catch (DocumentException)
    {
        // A part kept as it was read is read again to save it, and may not be well-formed.
        return;
    }

    saved.Position = 0;
    Workbook.Load(saved);
}

// The input is an XLSX package: each sheet's used range and part of it sorted, its filter and its tables' filters applied
// and cleared, and its cells searched and replaced; then calculated, saved and loaded again. May throw only
// DocumentException, or InvalidOperationException for a sort or replace Excel refuses.
static void AutoFit(Stream input)
{
    DocumentLimits limits = new() { MaxPartSize = 16 * 1024 * 1024, MaxTotalSize = 64 * 1024 * 1024 };
    Workbook book;
    try
    {
        book = Workbook.Load(new MemoryStream(Bytes(input), writable: false), new WorkbookLoadOptions { Limits = limits });
    }
    catch (DocumentException)
    {
        return;
    }

    foreach (Worksheet sheet in book.Worksheets.ToList())
    {
        RangeAddress used = sheet.UsedRange?.Address ?? RangeAddress.Parse("A1");
        RangeAddress part = new(used.First.Row, used.First.Column, Math.Min(used.Last.Row, used.First.Row + 199), Math.Min(used.Last.Column, used.First.Column + 39));
        sheet.Range(part).AutoFitColumns();
        sheet.Range(part).AutoFitRows();
        foreach (SheetColumn column in sheet.Columns.ToList())
        {
            if (column.Width is { } width && !(width is >= 0 and <= 255))
            {
                throw new InvalidOperationException($"Column {column.Index} fitted to {width}.");
            }
        }

        foreach (SheetRow row in sheet.Rows.ToList())
        {
            double height = row.AutoFit();
            if (!(height is >= 0 and <= 409.5))
            {
                throw new InvalidOperationException($"Row {row.Index} fitted to {height}.");
            }
        }
    }

    MemoryStream saved = new();
    try
    {
        book.Save(saved);
    }
    catch (DocumentException)
    {
        // A part the load kept unread, such as a drawing's relationships, is read on saving and may be malformed.
        return;
    }

    saved.Position = 0;
    _ = Workbook.Load(saved);
}

static void Sorts(Stream input)
{
    DocumentLimits limits = new() { MaxPartSize = 16 * 1024 * 1024, MaxTotalSize = 64 * 1024 * 1024 };
    Workbook book;
    try
    {
        book = Workbook.Load(new MemoryStream(Bytes(input), writable: false), new WorkbookLoadOptions { Limits = limits });
    }
    catch (DocumentException)
    {
        return;
    }

    foreach (Worksheet sheet in book.Worksheets.ToList())
    {
        RangeAddress used = sheet.UsedRange?.Address ?? RangeAddress.Parse("A1");
        foreach (Action edit in (Action[])[
            () => sheet.Range(used).Sort(new SortOptions { HasHeader = true }, new SortField(0)),
            () => sheet.Range(used).Sort(new SortOptions { MatchCase = true, Orientation = SortOrientation.Columns }, new SortField(0, Descending: true)),
            () => sheet.Range("A2:D30").Sort(new SortField(3), new SortField(1, Descending: true)),
            () => sheet.AutoFilter?.Apply(),
            () => (sheet.Tables.Count > 0 ? sheet.Tables[0].AutoFilter : null)?.Apply(),
            () => sheet.Find("a*", new Egap.Spreadsheets.FindOptions { LookIn = FindLookIn.Values, Order = FindOrder.ByColumns }),
            () => sheet.Find("~*?", new Egap.Spreadsheets.FindOptions { LookIn = FindLookIn.Notes, MatchCase = true }),
            () => sheet.Replace("1", "2"),
            () => sheet.Replace("A", "=", new Egap.Spreadsheets.FindOptions { MatchEntireCell = true }),
            () => sheet.AutoFilter?.ShowAll()])
        {
            try
            {
                edit();
            }
            catch (InvalidOperationException)
            {
                // Excel refuses it too: merges, part of an array formula, a pivot table, a formula its grammar refuses.
            }
        }
    }

    book.Calculate();
    MemoryStream saved = new();
    try
    {
        book.Save(saved);
    }
    catch (DocumentException)
    {
        return;
    }

    saved.Position = 0;
    Workbook.Load(saved);
}

// The input is text to find: searched for on a small sheet in every place and order, and replaced; may throw only
// ArgumentException for empty text, or InvalidOperationException for a replace that makes a formula Excel refuses.
static void Find(Stream input)
{
    string what = Encoding.UTF8.GetString(Bytes(input));
    Workbook book = Workbook.Create();
    Worksheet sheet = book.Worksheets[0];
    sheet["A1"].Value = "Apple pie * ? ~";
    sheet["A2"].Formula = "SUM(B1:B2)&\"x~y\"";
    sheet["B1"].Value = 1234.5;
    sheet["B2"].Value = 43831;
    sheet["B2"].NumberFormat = "yyyy-mm-dd";
    sheet["C3"].Value = new string('a', 200);
    sheet.Comments.Add("D4", "note " + what);
    book.Calculate();
    try
    {
        foreach (FindLookIn place in Enum.GetValues<FindLookIn>())
        {
            foreach (bool whole in (bool[])[false, true])
            {
                _ = sheet.Find(what, new Egap.Spreadsheets.FindOptions { LookIn = place, MatchEntireCell = whole, MatchCase = whole, Order = whole ? FindOrder.ByColumns : FindOrder.ByRows });
            }
        }

        try
        {
            sheet.Replace(what, what + "1");
            sheet.Range("A1:C3").Replace(what, "", new Egap.Spreadsheets.FindOptions { MatchEntireCell = true });
        }
        catch (InvalidOperationException)
        {
            // The replace made a formula Excel refuses.
        }
    }
    catch (ArgumentException) when (what.Length == 0)
    {
    }
}

// The input is an XLSX package, or else text used as the formulas of conditional formatting and data validation rules of
// every kind on a small sheet. Each cell's conditional format and each value's validity is worked out and the first pages
// print; a package may throw only DocumentException, and a rule's formula that does not parse or gives an error only
// leaves its cells as they are.
static void Conditional(Stream input)
{
    byte[] bytes = Bytes(input);
    Workbook book;
    if (bytes is [(byte)'P', (byte)'K', ..])
    {
        try
        {
            book = Workbook.Load(new MemoryStream(bytes, writable: false), new WorkbookLoadOptions { Limits = new() { MaxPartSize = 16 * 1024 * 1024, MaxTotalSize = 64 * 1024 * 1024 } });
        }
        catch (DocumentException)
        {
            return;
        }
    }
    else
    {
        string text = Encoding.UTF8.GetString(bytes);
        book = Workbook.Create();
        Worksheet made = book.Worksheets[0];
        CellValue[] values = [CellValue.FromNumber(1), CellValue.FromNumber(-2.5), CellValue.FromNumber(0), CellValue.FromText("a"), CellValue.FromText(""), CellValue.FromBoolean(true), CellValue.FromText("Text " + text[..Math.Min(text.Length, 1000)]), CellValue.FromNumber(45000)];
        for (int row = 0; row < 8; row++)
        {
            for (int column = 0; column < 4; column++)
            {
                made[row, column].Value = values[(row + column) % values.Length];
            }
        }

        made["D2"].Formula = "1/0";
        book.Names.Add("Fuzz", "Sheet1!$A$1:$A$9");
        CellStyle bold = CellStyle.Default with { Font = CellStyle.Default.Font with { Bold = true } };
        made.ConditionalFormats.Add("A1:B8", ConditionalFormatRule.Expression(text.Length == 0 ? "A1" : text, bold));
        made.ConditionalFormats.Add("B1:C8", ConditionalFormatRule.CellIs(ComparisonOperator.Between, text.Length == 0 ? "0" : text, bold, "B2")).Ranges.Add(RangeAddress.Parse("A3:A4"));
        made.ConditionalFormats.Add("A1:D8", ConditionalFormatRule.ColorScale(CellColor.Black, CellColor.White), ConditionalFormatRule.DataBar(CellColor.Black), ConditionalFormatRule.IconSet("5Arrows"));
        foreach (DataValidationType type in Enum.GetValues<DataValidationType>())
        {
            DataValidation rule = made.DataValidations.Add(RangeAddress.Parse("A1:D8"), type, text, text.Length % 2 == 0 ? "Fuzz" : text);
            rule.Operator = (ComparisonOperator)(text.Length % 8);
        }
    }

    foreach (Worksheet sheet in book.Worksheets)
    {
        if (sheet.UsedRange?.Address is not { } used)
        {
            continue;
        }

        ConditionalFormatEvaluation evaluation = sheet.ConditionalFormats.Evaluate();
        for (int row = used.First.Row; row <= Math.Min(used.Last.Row, used.First.Row + 60); row++)
        {
            for (int column = used.First.Column; column <= Math.Min(used.Last.Column, used.First.Column + 20); column++)
            {
                _ = evaluation[row, column].Style;
                _ = sheet.DataValidations.IsValid(new CellAddress(row, column));
                _ = sheet.DataValidations.ListItems(new CellAddress(row, column));
            }
        }

        _ = sheet.DataValidations.FindInvalid();
    }

    foreach (SheetPage page in book.Paginate().Pages.Take(2))
    {
        using Egami.Raster<Egami.Pixels.Rgba8> image = page.Render(36);
    }
}

// The input is a formula: its references moved for cells cut and pasted, on the sheet and to another. What parses must
// still parse after each move, and a move of cells the formula does not name gives the text back as it was.
static void Move(Stream input)
{
    string text = Encoding.UTF8.GetString(Bytes(input));
    if (text.Length > 8192)
    {
        text = text[..8192];
    }

    CellAddress cell = new(text.Length % 40, text.Length % 7);
    if (FormulaParser.TryParse(text, r1c1: false, cell) is null)
    {
        return;
    }

    Workbook book = Workbook.Create();
    Worksheet sheet = book.Worksheets[0];
    Worksheet other = book.Worksheets.Add("Other");
    CellMove[] moves =
    [
        new(sheet, RangeAddress.Parse("A5:A6"), sheet, 35, 2), new(sheet, RangeAddress.Parse("A1:C9"), sheet, 1, 0), new(sheet, RangeAddress.Parse("B2:D4"), other, -1, 3),
        new(sheet, RangeAddress.Parse("A7:A8"), sheet, -4, 0), new(sheet, RangeAddress.Parse("E5"), sheet, 0, 1),
    ];
    foreach (CellMove move in moves)
    {
        foreach (bool relocated in (bool[])[false, true])
        {
            string moved = ReferenceMove.Apply(text, cell, sheet, move, relocated && move.Across);
            if (FormulaParser.TryParse(moved, r1c1: false, cell) is null)
            {
                throw new InvalidOperationException("A moved formula does not parse: " + moved);
            }
        }
    }

    CellMove elsewhere = new(other, RangeAddress.Parse("A1:C9"), other, 1, 0);
    if (!text.Contains('!', StringComparison.Ordinal) && ReferenceMove.Apply(text, cell, sheet, elsewhere) != text)
    {
        throw new InvalidOperationException("A formula naming no moved cell changed.");
    }
}

// The input is a formula: parsed in A1 and R1C1 style for a cell taken from its length. What parses must print back exactly
// as written; its A1 form must parse again to the same A1 form, and its R1C1 form must parse; and evaluating it on a small
// workbook with sheets, names and a table must give a value, never throw.
static void Formula(Stream input)
{
    string text = Encoding.UTF8.GetString(Bytes(input));
    if (text.Length > 8192)
    {
        text = text[..8192];
    }

    CellAddress cell = new(text.Length % 40, text.Length % 7);
    foreach (bool r1c1 in (bool[])[false, true])
    {
        ParsedFormula? parsed = FormulaParser.TryParse(text, r1c1, cell);
        if (parsed is null)
        {
            continue;
        }

        if (FormulaPrinter.Print(parsed, ReferenceStyle.Written) != text)
        {
            throw new InvalidOperationException("The formula does not print as written.");
        }

        string a1 = FormulaPrinter.Print(parsed, ReferenceStyle.A1, cell);
        ParsedFormula again = FormulaParser.TryParse(a1, r1c1: false, cell)
            ?? throw new InvalidOperationException($"The A1 form does not parse: {a1}");
        if (FormulaPrinter.Print(again, ReferenceStyle.A1, cell) != a1)
        {
            throw new InvalidOperationException($"The A1 form does not read back to itself: {a1}");
        }

        string r1c1Form = FormulaPrinter.Print(again, ReferenceStyle.R1C1);
        _ = FormulaParser.TryParse(r1c1Form, r1c1: true, cell)
            ?? throw new InvalidOperationException($"The R1C1 form does not parse: {r1c1Form}");
        try
        {
            _ = FormulaEvaluator.Evaluate(FormulaCompiler.Compile(parsed), new EvalContext(FormulaBook.Book.Value.Worksheets[0], cell.Row, cell.Column));
        }
        catch (IndexOutOfRangeException error)
        {
            throw new InvalidOperationException("The evaluator's stack overflowed the depth the compiler gave it.", error);
        }
    }
}

static class FormulaBook
{
    // Built once: the formula target only reads it.
    public static readonly Lazy<Workbook> Book = new(() =>
    {
        Workbook book = Workbook.Create();
        Worksheet data = book.Worksheets[0];
        data.Name = "Data";
        for (int row = 0; row < 8; row++)
        {
            data.Cells[row, 0].Value = row == 0 ? "Name" : $"Item {row}";
            data.Cells[row, 1].Value = row == 0 ? "Amount" : row * 1.5;
            data.Cells[row, 2].Value = row % 2 == 0;
        }

        data["D1"].Value = CellError.NotAvailable;
        data.Tables.Add("A1:B8", "Sales");
        Worksheet other = book.Worksheets.Add("Sheet 2");
        other["A1"].Value = 42;
        book.Names.Add("Rate", "Data!$B$2");
        book.Names.Add("Self", "Self+1");
        book.Names.Add("Up", "Data!A1048576");
        return book;
    });
}
