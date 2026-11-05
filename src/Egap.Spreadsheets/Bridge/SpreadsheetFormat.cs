using Egap.Documents;

namespace Egap.Spreadsheets;

/// <summary>
/// Connects spreadsheets to <see cref="Document.Load(string)"/>: after <see cref="Register"/>, XLSX and CSV files load as
/// documents, one section per visible sheet with its table of displayed values, so that HTML, Markdown, DOCX, text and RAG
/// chunks work on them. <see cref="Workbook.ToDocument()"/> does the same for a workbook already in hand.
/// </summary>
/// <example>
/// <code>
/// SpreadsheetFormat.Register();
/// Document report = Document.Load("budget.xlsx");
/// report.SaveAsPdf("budget.pdf");
/// </code>
/// </example>
public static class SpreadsheetFormat
{
    /// <summary>
    /// Lets <see cref="Document.Load(string)"/> read XLSX, XLSM, XLTX and XLTM files and <c>.csv</c>, <c>.tsv</c> and
    /// <c>.tab</c> text, whose fields show exactly as written. Formulas show the values saved with the file; a workbook saved with no values, as some libraries
    /// write them, is calculated first. Call it once at startup; calling it again changes nothing.
    /// </summary>
    public static void Register()
    {
        DocumentFormatRegistry.Register(new(
            DocumentFormatKind.Xlsx,
            (byte[] bytes, out IReadOnlyList<ConversionWarning> warnings) => ReadWorkbook(bytes, new DocumentLimits(), out warnings),
            null,
            ReadWithOptions: (byte[] bytes, DocumentLoadOptions options, out IReadOnlyList<ConversionWarning> warnings) => ReadWorkbook(bytes, options.Limits, out warnings)));
        DocumentFormatRegistry.Register(new(DocumentFormatKind.Csv, ReadCsv, null));
    }

    private static Document ReadWorkbook(byte[] bytes, DocumentLimits limits, out IReadOnlyList<ConversionWarning> warnings)
    {
        using MemoryStream stream = new(bytes, writable: false);
        Workbook book = Workbook.Load(stream, new WorkbookLoadOptions { Limits = limits });
        if (book.Worksheets.Any(sheet => sheet.Cells.Any(cell => cell.HasFormula && cell.ValueKind == CellValueKind.Empty)))
        {
            book.CalculateFull();
        }

        return WorkbookDocument.Build(book, sheetHeadings: true, out warnings);
    }

    private static Document ReadCsv(byte[] bytes, out IReadOnlyList<ConversionWarning> warnings)
    {
        using MemoryStream stream = new(bytes, writable: false);
        try
        {
            return WorkbookDocument.Build(Workbook.LoadCsv(stream, new CsvOptions { DetectTypes = false }), sheetHeadings: false, out warnings);
        }
        catch (InvalidDataException e)
        {
            throw new DocumentException(e.Message, e);
        }
    }
}
