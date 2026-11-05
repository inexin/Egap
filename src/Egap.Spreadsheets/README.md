# Egap.Spreadsheets

Spreadsheets for .NET 10: workbooks you create and edit from code, and Excel files (XLSX, XLSM, XLTX, XLTM) loaded and
saved with what they hold kept. Sheets are sparse, up to 1,048,576 rows by 16,384 columns, and keep text and cell styles once
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

Tables, data validation, conditional formatting, notes, hyperlinks and pictures are read, edited and written, and a
loaded workbook saves its charts, pivot tables, macros and the rest as they were. `SheetReader` and `SheetWriter` stream
rows through XLSX and CSV in constant memory.
Number format codes show values as Excel does (`cell.Text`, `NumberFormatCode`), in any culture, and CSV and TSV are
read and written with type detection. Formulas are read and checked against Excel's grammar, in A1 or R1C1 style
(`cell.FormulaR1C1`), `sheet.Evaluate` evaluates one with Excel's rules for values, errors, references and dates, and
`book.Calculate()` recalculates what changed through a dependency graph, with shared and array formulas, circular
references and iteration; the common functions come in the next releases. Source, guide and
changelog: [github.com/inexin/Egap](https://github.com/inexin/Egap).
