# Egap.Spreadsheets

Spreadsheets for .NET 10: workbooks you create and edit from code. Sheets are sparse, up to 1,048,576 rows by 16,384
columns, and keep text and cell styles once per workbook, as Excel does. Fully managed and MIT-licensed; part of
[Egap](https://github.com/inexin/Egap).

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
```

Reading and writing XLSX and CSV, number formats and the formula engine come in the next releases. Source, guide and
changelog: [github.com/inexin/Egap](https://github.com/inexin/Egap).
