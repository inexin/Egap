# Spreadsheets and documents

Workbooks reach Egap's document tools through `SpreadsheetFormat.Register()`, called once at startup: after it,
`Document.Load` takes XLSX and CSV files. Each visible sheet becomes a section with the sheet's name as a heading and a table
of its values as displayed, so HTML, Markdown, DOCX, text and RAG chunks work on them. `book.ToDocument()` does the same for
a workbook in hand.

```csharp
using Egap.Documents;
using Egap.Spreadsheets;

SpreadsheetFormat.Register();

Document document = Document.Load("budget.xlsx", out IReadOnlyList<ConversionWarning> warnings);
document.Save("budget.docx");

Workbook book = Workbook.Load("budget.xlsx");
Document same = book.ToDocument();
```

Hidden sheets, rows and columns are left out, and so are rows and columns that show nothing. Merged cells stay merged, and
bold, italic, alignment and hyperlinks carry over; the layout finds column widths from the text. Formulas show the values
saved with the file, and a workbook saved with no values is calculated first. CSV fields show exactly as written, and a CSV
file has no heading. Pictures, charts and notes are left out with a `ConversionWarning`, and a sheet stops at two million
table cells with one too.

## Tables as spreadsheets

`Workbook.FromTables` goes the other way: one sheet per table of a document or a PDF, named `Table 1`, `Table 2` and so on.
Text becomes numbers, dates and logical values as CSV reading makes them, in the culture of `TableImportOptions`. Header rows
turn bold, merged cells stay merged, and in a PDF a table that runs on across pages is one sheet, without its repeated
header. A table without ruling lines is found only as well as `PdfLayoutAnalyzer` finds it; tagged PDFs do best.

```csharp
using Egap.Documents;
using Egap.Pdf;
using Egap.Spreadsheets;

Workbook fromWord = Workbook.FromTables(Document.Load("report.docx"));
fromWord.Save("report-tables.xlsx");

using PdfDocument pdf = PdfDocument.Open("report.pdf");
Workbook fromPdf = Workbook.FromTables(pdf, new TableImportOptions { DetectTypes = false });
fromPdf.Save("pdf-tables.xlsx");
```
