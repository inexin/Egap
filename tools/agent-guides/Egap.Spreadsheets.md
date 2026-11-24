# Egap.Spreadsheets: guide for coding agents

Egap.Spreadsheets builds and edits workbooks from code and reads and writes Excel files (XLSX, XLSM, XLTX, XLTM) with
what they hold kept; it also reads and writes ODS and CSV, and reads XLSB and XLS. Sheets are sparse, up to 1,048,576 rows
by 16,384 columns. Formulas follow Excel's grammar and calculate through a dependency graph; tables, validation,
conditional formats, pivot tables, charts and printing to PDF are in the model.

Namespace: `Egap.Spreadsheets`, with `Egap.Documents.Charts` for charts and `Egami` and `Egami.Pixels` for rendered pages.

{{shared}}

## Entry points

- `Workbook.Create()` (one sheet, `Sheet1`), `Workbook.CreateEmpty()`, `Workbook.Load(path | stream,
  WorkbookLoadOptions)` and `Workbook.LoadCsv(path, CsvOptions)`. `WorkbookLoadOptions.Password` opens an encrypted file.
- `book.Save(path | stream, WorkbookSaveOptions)`: the extension picks the format, or `Format = WorkbookFormat.Xltx`
  names it; `Password` encrypts.
- `book.Worksheets` (`Add`, `Insert`, `Remove`, `Move`, `["Sales"]`, `[0]`), `book.Names`, `book.Calculate()`,
  `book.CalculateFull()`, `book.SaveAsPdf(path)`.
- `sheet["B2"]` and `sheet[row, column]` (from 0) give a `Cell`: `Value` (a `CellValue`), `Formula` (no `=`),
  `FormulaR1C1`, `Text` (as Excel shows it), `NumberFormat`, `Style`, `ImportData`.
- `sheet.Range("A1:C9")` gives a `CellRange`: `SetValues`, `GetValues`, `UpdateStyle`, `SetStyle`, `Clear`,
  `ClearContents`, `CopyTo`, `MoveTo`, `Sort`, `AutoFitColumns`, `ToDataTable`, `ToList<T>`.
- `sheet.Evaluate(formula)`, `InsertRows`, `DeleteRows`, `InsertColumns`, `DeleteColumns`, `FindAll`, `Replace`,
  `Tables`, `DataValidations`, `ConditionalFormats`, `PivotTables`, `Drawings`, `Comments`, `Hyperlinks`, `Protection`,
  `PageSetup`, `Paginate()`, `SaveAsPdf`, `SaveAsCsv`.
- `CellStyle.Default with { Font = CellFont.Default with { Bold = true } }`, `CellFill.Solid`, `CellColor.FromRgb`:
  immutable styles changed with `with`.
- `SheetWriter.Create(path)` and `SheetReader.Open(path)`: rows streamed through XLSX and CSV in constant memory.
- `SpreadsheetFormat.Register()` lets `Document.Load` read XLSX and CSV as documents; `Workbook.FromTables(document | pdf)`
  puts a document's tables on sheets.

## Samples

Build a sheet:

{{snippet guides/spreadsheets.md#Spreadsheets}}

Load, change and save:

{{snippet guides/spreadsheets.md#Loading and saving}}

Calculate:

{{snippet guides/spreadsheets.md#Calculating a workbook}}

Print to PDF and images:

{{snippet guides/spreadsheets.md#Printing to PDF and images}}

Millions of rows:

{{snippet guides/spreadsheets.md#Streaming large files}}

## Common mistakes

- Reading a formula's result straight after setting it. A formula's value is empty until `book.Calculate()`;
  `sheet.Evaluate` gives a value without recalculating the workbook.
- Writing formulas as a user types them in a localized Excel. `Formula` takes Excel's stored form: English function names,
  commas between arguments, no leading `=` (one given is dropped). Bad grammar throws a `FormulaException` with its
  `Position`.
- Mixing addresses: `sheet[3, 1]` is row 4, column B (both from 0), the same cell as `sheet["B4"]`.
- Confusing `Value` and `Text`. Dates are numbers shown through a date number format; `cell.Text` is what Excel would
  show, `cell.Value` the stored number.
- Expecting XLS or XLSB to be written. They are read only and save as XLSX (or ODS).
- `Cell` clashes with Egap.Documents' table `Cell` in a file that has both `using Egap.Documents;` and
  `using Egap.Spreadsheets;`: qualify one, or keep them in separate files.
- Building a workbook for a huge export. Use `SheetWriter` and dispose it to finish the file; use `SheetReader` to read
  rows without loading the workbook. `SheetWriter` does not stream an encrypted file.
- Using a workbook from several threads at once: a workbook is not safe to change from more than one thread.
- Catching the wrong exceptions: a file past `WorkbookLoadOptions.Limits` throws a `DocumentLimitException`, a wrong
  password a `DocumentPasswordException`, and a file that is no workbook a `DocumentException`, all from
  `Egap.Documents`.

## More

- Spreadsheets guide: https://inexin.github.io/Egap/v2/articles/guides/spreadsheets.html
- Functions: https://inexin.github.io/Egap/v2/articles/guides/spreadsheet-functions.html
- Charts: https://inexin.github.io/Egap/v2/articles/guides/charts.html
- Every page in one list: https://inexin.github.io/Egap/v2/llms.txt
- API reference: https://inexin.github.io/Egap/v2/api/Egap.Spreadsheets.html
