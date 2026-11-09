# Egap.Spreadsheets

Spreadsheets for .NET 10: workbooks you create and edit from code, and Excel files (XLSX, XLSM, XLTX, XLTM) loaded and
saved with what they hold kept. OpenDocument spreadsheets (ODS, FODS) load too, their formulas translated to Excel's. Sheets are sparse, up to 1,048,576 rows by 16,384 columns, and keep text and cell styles once
per workbook, as Excel does. Fully managed and MIT-licensed; part of [Egap](https://github.com/inexin/Egap).

```csharp
using Egap.Spreadsheets;

Workbook book = Workbook.Create();
Worksheet sheet = book.Worksheets[0];
sheet["A1"].Value = "Region";
sheet["B1"].Value = "Sales";
sheet["A2"].Value = "North";
sheet["B2"].Value = 1250.5;
sheet["B3"].Formula = "SUM(B2:B2)";
sheet.Range("A1:B1").UpdateStyle(s => s with { Font = s.Font with { Bold = true } });
sheet["B2"].NumberFormat = "#,##0.00";
string shown = sheet["B2"].Text;                     // "1,250.50", as Excel shows it
book.Save("sales.xlsx");
sheet.SaveCsv("sales.csv");

Workbook loaded = Workbook.Load("sales.xlsx");
```

Tables, data validation, conditional formatting, notes, hyperlinks, pictures and charts are read, edited and written, and a
loaded workbook saves its pivot tables, macros and the rest as they were. `ConditionalFormats.Evaluate` and `DataValidations.IsValid` work out
the format each cell shows and whether its value passes, as Excel does. `InsertRows`, `DeleteRows`, `InsertColumns` and `DeleteColumns`
edit a sheet as Excel does, with every formula, name, rule, table and chart that refers to the cells following them. `CellRange.CopyTo` and `MoveTo` copy and move ranges as Excel's Copy, Cut and Paste do. `CellRange.Sort`, `AutoFilter.Apply` and `Find` and `Replace` sort, filter and search as Excel does. `AutoFitColumns` and `AutoFitRows` fit widths and heights to the text as Excel's AutoFit does. Printing, PDF and images draw the charts, chart
sheets too. `SheetReader` and `SheetWriter` stream
rows through XLSX and CSV in constant memory.
Number format codes show values as Excel does (`cell.Text`, `NumberFormatCode`), in any culture, and CSV and TSV are
read and written with type detection. `Cell.ImportData` writes lists of objects, `DataTable`s and data readers into cells,
and `ToDataTable` and `ToList<T>` read ranges back. Sheets and workbooks are protected and unlocked with passwords hashed
as Excel hashes them. Formulas are read and checked against Excel's grammar, in A1 or R1C1 style
(`cell.FormulaR1C1`), `sheet.Evaluate` evaluates one with Excel's rules for values, errors, references and dates, and
`book.Calculate()` recalculates what changed through a dependency graph, with shared and array formulas, circular
references and iteration, dynamic arrays that spill, LET, LAMBDA and structured references. Over 470 of Excel's functions are built in and checked against Excel itself;
`FunctionRegistry` adds functions of your own. `sheet.SaveAsPdf` and `book.SaveAsPdf` print as Excel prints, with its page
breaks, print titles, fit to pages, headers and footers, cell formatting, conditional colours and pictures, to PDF (tagged,
PDF/A or PDF/UA when asked), and `Paginate` lays the pages out to draw on any Egami canvas or render to images.
`SpreadsheetFormat.Register()` lets `Document.Load` read XLSX and CSV, a section per sheet with its table of displayed values,
so HTML, Markdown, DOCX, text and RAG chunks work on them, and `Workbook.FromTables` puts the tables of a document or a PDF on
sheets.
Source, guide and changelog: [github.com/inexin/Egap](https://github.com/inexin/Egap).
