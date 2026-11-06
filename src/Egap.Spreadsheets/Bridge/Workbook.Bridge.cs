using Egap.Documents;
using Egap.Documents.Pdf;

namespace Egap.Spreadsheets;

/// <summary>The bridge between workbooks, documents and tables.</summary>
public sealed partial class Workbook
{
    /// <summary>
    /// The workbook as a document: a section per visible sheet, with the sheet's name as a heading and a table of its values as
    /// displayed. Formulas show the value they hold now; hidden rows and columns, and rows and columns that show nothing, are
    /// left out, and the layout finds column widths from the text. Merged cells stay merged; bold, italic, alignment and links
    /// carry over. A sheet is cut off at two million table cells, with a warning.
    /// </summary>
    public Document ToDocument() => ToDocument(out _);

    /// <summary>The workbook as a document, reporting what it could not hold. See <see cref="ToDocument()"/>.</summary>
    public Document ToDocument(out IReadOnlyList<ConversionWarning> warnings) => WorkbookDocument.Build(this, sheetHeadings: true, out warnings);

    /// <summary>
    /// A workbook of one sheet per table in <paramref name="document"/>'s section bodies, named <c>Table 1</c>, <c>Table 2</c>
    /// and so on, in document order with nested tables after the table holding them. Header rows turn bold, merged cells stay
    /// merged, and a cell's paragraphs become lines of one wrapped cell. A document with no tables gives one empty sheet.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is null.</exception>
    public static Workbook FromTables(Document document, TableImportOptions? options = null) => TableImporter.FromDocument(document, options);

    /// <summary>
    /// A workbook of one sheet per table <see cref="PdfLayoutAnalyzer"/> finds in <paramref name="pdf"/>, read as
    /// <see cref="FromTables(IEnumerable{PdfPageLayout}, TableImportOptions?)"/> does.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="pdf"/> is null.</exception>
    public static Workbook FromTables(Egap.Pdf.PdfDocument pdf, TableImportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        return TableImporter.FromLayouts(PdfLayoutAnalyzer.Analyze(pdf), options);
    }

    /// <summary>
    /// A workbook of one sheet per table on the analysed pages; a table carried on from the page before joins the sheet it
    /// carries on, without its repeated header. Sheets are named as in <see cref="FromTables(Document, TableImportOptions?)"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="pages"/> is null.</exception>
    public static Workbook FromTables(IEnumerable<PdfPageLayout> pages, TableImportOptions? options = null) => TableImporter.FromLayouts(pages, options);
}
