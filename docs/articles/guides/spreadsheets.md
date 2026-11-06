# Spreadsheets

Egap.Spreadsheets holds workbooks in memory and lets you build and edit them from code: sheets, cells, values, formulas,
cell styles, rows and columns, names and print settings, as Excel keeps them. It loads and saves Excel's XLSX, XLSM, XLTX and
XLTM files. The types are in `Egap.Spreadsheets`.

```csharp
using Egap.Spreadsheets;

Workbook book = Workbook.Create();                   // one empty sheet, Sheet1
Worksheet sheet = book.Worksheets[0];
sheet.Name = "Sales";

sheet["A1"].Value = "Region";
sheet["B1"].Value = "Amount";
sheet["A2"].Value = "North";
sheet["B2"].Value = 1250.5;
sheet["A3"].Value = "South";
sheet["B3"].Value = 980;
sheet["B4"].Formula = "SUM(B2:B3)";

sheet.Range("A1:B1").UpdateStyle(s => s with { Font = s.Font with { Bold = true } });
sheet.Range("B2:B4").UpdateStyle(s => s with { NumberFormat = "#,##0.00" });
```

## Loading and saving

`Workbook.Load` reads an Excel workbook or template, with or without macros, from a path or a stream. `Save` writes one; a
path's extension (`.xlsx`, `.xlsm`, `.xltx` or `.xltm`) says which kind, and `WorkbookSaveOptions.Format` names it for a
stream or another extension.

```csharp
using Egap.Spreadsheets;

Workbook book = Workbook.Load("sales.xlsx");
Worksheet sheet = book.Worksheets["Sales"];
sheet["C2"].Value = 1300;
book.Save("sales.xlsx");

using MemoryStream stream = new();
book.Save(stream, new WorkbookSaveOptions { Format = WorkbookFormat.Xltx });
```

A loaded workbook saves with what it held: the parts and settings this package does not model, such as charts, pictures,
comments, tables, conditional formats, data validation, hyperlinks, themes, document properties and macros, are written
back as they were, and so are the parts of a cell style the model does not hold. Shared formulas stay shared, rich text
keeps its runs, and chart sheets keep their place among the worksheets. A removed sheet takes its own parts with it.

What Egap cannot keep up to date is left for Excel: when cells changed and the workbook has formulas, the file asks Excel
to calculate on open, and the calculation chain is left out for Excel to build again. A cell that shared the formula of a
cell you changed gets the formula as its own, as in Excel. Saving a workbook with macros as `.xlsx` or `.xltx` leaves the macros
out, as Excel does.

`Workbook.Format` is the kind of file a workbook came from. `WorkbookLoadOptions.Limits` bounds what a file may hold (part
count, part and total size, compression ratio, XML depth) so a hostile file cannot use up memory; a file that goes past a
limit throws a `DocumentLimitException`, and one that is not a workbook a `DocumentException`, both from Egap.Documents.

## Workbooks and sheets

`Workbook.Create()` starts a workbook with one empty sheet named `Sheet1`, as Excel does; `Workbook.CreateEmpty()` starts
one with none. `book.Worksheets` holds the sheets in tab order: `Add` and `Insert` make a sheet (named `SheetN` when you
give no name), `Remove` takes one out, `Move` changes its position, and the indexers find a sheet by position or by name.

Sheet names follow Excel's rules: 1 to 31 characters, none of `: \ / ? * [ ]`, no apostrophe at the start or end, and no
two sheets with the same name, case ignored. A name that breaks a rule throws an `ArgumentException`.

`sheet.Visibility` hides a sheet's tab (`Hidden`) or hides it so that only code can show it again (`VeryHidden`), and
`sheet.TabColor` colours the tab. `book.Date1904` says whether dates count from 1904, as old Mac workbooks do.

## Cells

A sheet has 1,048,576 rows and 16,384 columns (A to XFD). Reach a cell by its A1 address or by zero-based row and column:

```csharp
using Egap.Spreadsheets;

Worksheet sheet = Workbook.Create().Worksheets[0];
Cell b4 = sheet["B4"];
Cell same = sheet.Cells[3, 1];                       // row 3, column 1: also B4
CellAddress address = CellAddress.Parse("$C$10");    // row 9, column 2
string column = CellAddress.ColumnName(27);          // "AB"
```

`cell.Value` is a `CellValue`: a number, text, `true` or `false`, an error such as `CellError.DivisionByZero`, or empty.
Numbers, strings, booleans and errors convert to it, so `cell.Value = 42` and `cell.Value = "Total"` work. Dates are
numbers that a date number format shows as dates. Excel's limits hold: numbers are finite, and text is at most 32,767
characters.

`cell.Formula` holds a formula in Excel's stored form, with English function names and commas, without the `=` (a
leading `=` you give is dropped). A formula that does not follow Excel's grammar throws a `FormulaException` that says
where the problem is. A formula's result is empty until the workbook is calculated (`book.Calculate()`). Setting `Value` removes a formula;
setting `Formula` to null removes it and its result. [Formulas](#formulas) has more.

`cell.Clear()` removes the value, formula and style. `sheet.Cells` enumerates the cells that hold something, row by row,
and `sheet.UsedRange` is the smallest range that holds them all.

For many cells at once, `sheet.Cells.GetValue(row, column)` and `sheet.Cells.SetValue(row, column, value)` read and write
values without making a `Cell` for each.

## Ranges

`sheet.Range("A1:D20")` is a `CellRange`, a cheap value for a rectangle of cells. Whole columns (`"A:C"`) and whole rows
(`"2:5"`) work too.

```csharp
using Egap.Spreadsheets;

Worksheet sheet = Workbook.Create().Worksheets[0];
CellRange table = sheet.Range("A1:C2");
table.SetValues(new CellValue[,] { { "Item", "Qty", "Price" }, { "Paper", 2, 40 } });
CellValue[,] values = table.GetValues();
table.SetStyle(CellStyle.Default with { Borders = CellBorders.Box(CellBorder.Thin) });
foreach (Cell cell in sheet.Range("A:C").UsedCells)
{
    Console.WriteLine(cell);                         // A1: Item, B1: Qty, ...
}
```

Enumerating a range gives every cell in it, empty ones too; `UsedCells` gives only the cells that hold something.
`Clear` removes values, formulas and styles, and `ClearContents` keeps the styles.

## Styles

A `CellStyle` is an immutable value with a `Font`, `Fill`, `Borders`, `Alignment`, `Protection` and `NumberFormat`. New
cells use `CellStyle.Default`: Calibri 11 in the theme's text colour, no fill or borders, general alignment, locked, and
the `General` number format. Change a style with `with`, for one cell or for a range:

```csharp
using Egap.Spreadsheets;

Worksheet sheet = Workbook.Create().Worksheets[0];
sheet["A1"].Style = CellStyle.Default with
{
    Font = CellFont.Default with { Bold = true, Color = CellColor.FromRgb(0x1F, 0x4E, 0x79) },
    Fill = CellFill.Solid(CellColor.FromTheme(4, 0.8)),
    Alignment = CellAlignment.Default with { Horizontal = CellHorizontalAlignment.Center, WrapText = true },
};
sheet["B1"].NumberFormat = "yyyy-mm-dd";
sheet.Range("A2:A100").UpdateStyle(s => s with { Font = s.Font with { Italic = true } });
```

`UpdateStyle` calls its function once for each distinct style in the range, so a large range of a few styles is quick.

A workbook keeps each distinct style once, however many cells use it, and its fonts, fills, borders and number formats
once each, as Excel's style table does. Text values are kept once per workbook too, in a shared string table. Colours are
automatic, ARGB (`CellColor.FromRgb`, `FromArgb`), a theme colour with a tint (`FromTheme`) or a legacy palette entry
(`FromIndex`).

## Rows, columns and layout

`sheet.Rows[index]` and `sheet.Columns[index]` (or `sheet.Columns["C"]`) set heights and widths, hide rows and columns,
group them with outline levels and give their empty cells a style. Heights are in points and widths in characters of the
default font, as Excel shows them; null means the sheet's default, which `DefaultRowHeight` and `DefaultColumnWidth` set.
Enumerating `Rows` or `Columns` gives those that have any of these settings.

```csharp
using Egap.Spreadsheets;

Worksheet sheet = Workbook.Create().Worksheets[0];
sheet.Columns["A"].Width = 24;
sheet.Columns["D"].Hidden = true;
sheet.Rows[0].Height = 30;
sheet.Rows[5].OutlineLevel = 1;
sheet.MergedCells.Add(RangeAddress.Parse("A1:C1"));
sheet.View.FreezePanes(CellAddress.Parse("B2"));    // row 1 and column A stay in place
sheet.View.ZoomScale = 125;
```

`sheet.MergedCells` holds the merged ranges; adding one that overlaps another throws. `sheet.View` holds what the sheet
shows on screen: gridlines, headings, zeros, direction, zoom, frozen rows and columns, and whether its tab is selected.

## Names and printing

`book.Names` holds the defined names. A name belongs to the workbook, or to one sheet when you give one:

```csharp
using Egap.Spreadsheets;

Workbook book = Workbook.Create();
Worksheet sheet = book.Worksheets[0];
book.Names.Add("TaxRate", "Sheet1!$B$1");
book.Names.Add("_xlnm.Print_Area", "Sheet1!$A$1:$F$40", sheet);
sheet.PageSetup.Orientation = PrintOrientation.Landscape;
sheet.PageSetup.PaperSize = 9;                       // A4
sheet.PageSetup.FitToPage = true;
sheet.PageSetup.FitToHeight = 0;                     // one page wide, as many tall as it needs
sheet.PageSetup.Header = "&CQuarterly report";
sheet.PageSetup.Footer = "&RPage &P of &N";
```

Excel's print area and print titles are names local to their sheet: `_xlnm.Print_Area` and `_xlnm.Print_Titles`.
`PageSetup.PrintArea`, `PrintTitleRows` and `PrintTitleColumns` read and write those names for you, and `RowBreaks` and
`ColumnBreaks` hold the manual page breaks. `sheet.PageSetup` holds the rest of what Excel's Page Setup dialog sets:
paper, orientation, scale or fit, margins in inches, centring, gridlines, headings, page order and the header and footer
in Excel's header codes.

### Printing to PDF and images

`SaveAsPdf` prints a sheet, or every visible sheet of a workbook, as Excel prints it: the print area (or the cells in use,
from A1), page order, manual breaks, scale or fit to pages, print titles on every page, gridlines and headings, and the
header and footer with their fields filled in. Cells draw with their fills, borders, fonts, alignment, wrapping,
rotation, shrinking and overflow into empty neighbours, number formats (`###` where a number does not fit), colour
scales, data bars, icon sets and pictures.

```csharp
using Egami;
using Egami.Pixels;
using Egap.Pdf;
using Egap.Spreadsheets;

Workbook book = Workbook.Create();
Worksheet sheet = book.Worksheets[0];
for (int row = 0; row < 200; row++)
{
    sheet[row, 0].Value = $"Item {row + 1}";
    sheet[row, 1].Value = row * 1.5;
}

sheet.PageSetup.PrintTitleRows = RangeAddress.Parse("1:1");
sheet.PageSetup.RowBreaks.Add(100);                  // row 101 starts a page
sheet.PageSetup.Footer = "&CPage &P of &N";

SheetPrintLayout layout = sheet.Paginate();
int pages = layout.Pages.Count;                      // 6 on Letter: rows 1-44, 45-87, 88-100, 101-143, ...
sheet.SaveAsPdf("items.pdf");
sheet.SaveAsPdf("items-a.pdf", new SheetPdfOptions { Conformance = PdfAConformance.PdfA2a, Language = "en-US" });
Raster<Rgba8> firstPage = layout.Pages[0].Render(150);
```

`Paginate` lays the pages out without drawing them: each `SheetPage` tells the cells it prints, its size in points and
its scale, and draws itself on any Egami canvas (`Draw`) or renders to a raster (`Render`). `book.SaveAsPdf` prints every
visible sheet in tab order, with page numbers counted per sheet.

Excel lays a print out for its printer, and Egap follows its PDF export at 600 dpi: a column prints in whole dots of the
normal font's digit width (Calibri 11: 47 dots, so a default column is 51.6 points on paper against 48 on screen), and a
row's height scales by the normal font's printed row height over its height on screen, which Excel measures per font
and size (a 15 point row prints 14.52 points in Calibri 11 or Arial 10, 15.24 in Aptos Narrow 11). Page breaks then fall
where Excel's fall.

`SheetPdfOptions.Tagged` tags the PDF for accessibility: a table per print area, a row per sheet row,
header cells for the print titles, merged cells spanning, pictures as figures, and gridlines, fills and repeated titles
as artifacts. `Conformance` makes a PDF/A file (the a levels are tagged) and `UniversalAccessibility` a PDF/UA one. Fonts come through
Egap.Documents' font chain; `SheetPrintOptions.Fonts` chooses it, and `layout.FontSubstitutions` lists the fonts used in
place of ones a sheet asks for, such as Carlito for Calibri.

## Formulas

Formulas follow Excel's grammar: A1 references with `$` (`$B$4`, `A:C`, `2:5`), sheet, 3D and external references
(`'Q 1'!A1`, `Jan:Mar!B2`, `[1]Prices!A1`), structured references to tables (`Sales[Amount]`, `Sales[[#Headers],[Region]]`,
`[@Amount]`), array constants (`{1,2;3,4}`), the reference operators (`:`, a space for an intersection, a comma in
parentheses for a union), `@` and the spill operator `#`. `cell.FormulaR1C1` gives and takes the same formula with R1C1
references, relative ones counted from the cell:

```csharp
using Egap.Spreadsheets;

Worksheet sheet = Workbook.Create().Worksheets[0];
sheet["B4"].Formula = "SUM($A$1:B3)";
string r1c1 = sheet["B4"].FormulaR1C1!;              // "SUM(R1C1:R[-1]C)"
sheet["C4"].FormulaR1C1 = "R[-1]C*2";                // C4's formula is "C3*2"

try
{
    sheet["D4"].Formula = "SUM(A1";
}
catch (FormulaException e)
{
    Console.WriteLine(e.Position);                   // 6, where the ) should be
}
```

`sheet.Evaluate` evaluates a formula as if it were in a cell of the sheet and gives the value that cell would show, with
Excel's rules: errors are values that flow through (`#DIV/0!`, `#N/A`), text meets arithmetic as a number when it reads as
one, a range meets the formula's row or column where one value is needed (implicit intersection), and arrays combine
element by element:

```csharp
using Egap.Spreadsheets;

Workbook book = Workbook.Create();
Worksheet sheet = book.Worksheets[0];
sheet["A1"].Value = 10;
sheet["A2"].Value = 20;
sheet["A3"].Value = 30;
book.Names.Add("Rate", "Sheet1!$A$1");

CellValue total = sheet.Evaluate("SUM(A1:A3)*Rate");                         // 600
CellValue row = sheet.Evaluate("A1:A3*2", CellAddress.Parse("C2"));          // 40: A2 meets row 2
CellValue safe = sheet.Evaluate("IFERROR(1/0,\"none\")");                     // "none"
CellValue date = sheet.Evaluate("DATE(2024,3,15)");                           // 45366
```

Other cells give the values they hold; a formula cell gives its last result, so `Evaluate` does not recalculate the
workbook. Dates follow the workbook's date system, 1900 (with the 29 February 1900 Excel keeps) or 1904. A function Egap
does not know gives `#NAME?`.

### Calculating a workbook

`book.Calculate()` calculates the formulas whose results are out of date, as Excel does when you press F9: those reading a
cell changed since the last calculation, those reading their results, and the volatile ones (`NOW`, `TODAY`, `RAND`,
`OFFSET`, `INDIRECT` and the like) every time. `book.CalculateFull()` calculates every formula. Egap keeps a dependency
graph of what each formula reads, through references, names, tables and other sheets, and calculates a formula after
the formulas it reads; large sets of formulas that read none of each other are spread over threads. A workbook as
loaded keeps the results saved with it until something changes, and a change to the sheets, names or tables makes the
next calculation a full one.

```csharp
using Egap.Spreadsheets;

Workbook book = Workbook.Create();
Worksheet sheet = book.Worksheets[0];
sheet["A1"].Value = 2;
sheet["B1"].Formula = "A1*3";
sheet["C1"].Formula = "B1+1";
book.Calculate();                                    // B1 is 6, C1 is 7

sheet["A1"].Value = 5;
CalculationResult result = book.Calculate();         // only B1 and C1 again
long count = result.Calculated;                      // 2

sheet.Range("D1:D3").SetArrayFormula("A1*{1;2;3}");  // D1:D3 are 5, 10, 15 after Calculate
CellRange? array = sheet["D2"].ArrayRange;           // D1:D3
```

`range.SetArrayFormula` makes an array formula, as Ctrl+Shift+Enter does: one formula, kept in the range's first cell,
whose result fills the range; a single row or column is repeated across it and cells past the result's end get `#N/A`.
Cells sharing a formula in an XLSX file are calculated from the first cell's formula, and `cell.Formula` gives each the
formula moved to it.

Formulas that read each other in a ring are circular references. With `book.Calculation.Iterate` off, as Excel starts,
`result.CircularReferences` lists them and their cells keep their values. With it on, each ring is calculated over and
over until no value moves by more than `Calculation.MaxChange` or `Calculation.MaxIterations` passes have run;
`result.Converged` says which. `book.Calculation` also holds the calculation mode and the other settings of the file's
`calcPr`, read and written with it:

```csharp
using Egap.Spreadsheets;

Workbook book = Workbook.Create();
Worksheet sheet = book.Worksheets[0];
book.Calculation.Iterate = true;
book.Calculation.MaxIterations = 100;
book.Calculation.MaxChange = 0.0001;
sheet["A1"].Formula = "B1+1";
sheet["B1"].Formula = "A1/2";

CalculationResult result = book.Calculate();         // A1 near 2, B1 near 1
bool settled = result.Converged;                     // true
```

### Dynamic arrays

A formula set through `cell.Formula` means what it means typed in Excel today: one that can give more than one value is a
dynamic array formula, and when the workbook is calculated its result spills over the cells beside and below it.
`cell.SpillRange` is the range it fills, and `A1#` stands for that range in another formula:

```csharp
using Egap.Spreadsheets;

Workbook book = Workbook.Create();
Worksheet sheet = book.Worksheets[0];
sheet["A1"].Value = "pear";
sheet["A2"].Value = "apple";
sheet["A3"].Value = "pear";

sheet["C1"].Formula = "=SORT(UNIQUE(A1:A3))";
sheet["E1"].Formula = "=COUNTA(C1#)";
book.Calculate();

string first = sheet["C1"].Value.Text;                      // apple
string second = sheet["C2"].Value.Text;                     // pear
string filled = sheet["C1"].SpillRange!.Value.Address.ToString(); // C1:C2
double count = sheet["E1"].Value.Number;                    // 2
```

Something in the way, a merged cell or a table gives `#SPILL!` in the formula's cell until there is room; writing over a
spilled cell does the same. `=@A1:A3` meets the formula's cell by implicit intersection, as every formula in Excel before
dynamic arrays did. Formulas read from a file stay what the file made them: Excel marks the dynamic ones, and the others
keep implicit intersection.

### LET and LAMBDA

`LET` names values for one formula, and each is worked out once. `LAMBDA` makes a function, which you call where you write
it or keep in a defined name; `MAP`, `REDUCE`, `SCAN`, `BYROW`, `BYCOL` and `MAKEARRAY` call one for each part of an array,
and take a function's own name too:

```csharp
using Egap.Spreadsheets;

Workbook book = Workbook.Create();
Worksheet sheet = book.Worksheets[0];
sheet["A1"].Value = 1;
sheet["A2"].Value = 2;
sheet["A3"].Value = 3;
book.Names.Add("TAX", "=LAMBDA(amount,[rate],amount*IF(ISOMITTED(rate),0.25,rate))");
sheet["C1"].Formula = "=LET(total,SUM(A1:A3),total*total)";     // 36
sheet["C2"].Formula = "=LAMBDA(x,x+1)(2)";                      // 3
sheet["C3"].Formula = "=TAX(100)+TAX(100,0.1)";                 // 35
sheet["E1"].Formula = "=MAP(A1:A3,LAMBDA(v,v*10))";             // spills 10, 20, 30
sheet["F1"].Formula = "=BYROW(A1:A3,SUM)";                      // spills 1, 2, 3
book.Calculate();
```

A LAMBDA in a name may call itself. Excel's limit on how deep that goes depends on the formula, as its stack is what runs out,
at 5,000 to 9,000 calls in what we measured; here calls end with `#NUM!` at 8,192 deep, and a recursion that branches ends
with `#NUM!` after about 5,000,000 calls, fewer the deeper they are. A LAMBDA left in a cell shows `#CALC!`. Files store these with prefixes on names (`_xlpm.x`), which Egap reads and
writes as Excel does.

### Structured references

A table's name and its parts work in formulas as in Excel: `Sales` or `Sales[]` for its data, `Sales[Qty]`,
`Sales[[Qty]:[Price]]`, `Sales[#All]`, `Sales[#Headers]`, `Sales[#Data]`, `Sales[#Totals]`,
`Sales[[#Headers],[#Data],[Qty]]` and `Sales[[#Totals],[Qty]]`. `Sales[@Qty]` is the column's cell in the formula's own row,
from any sheet, and gives `#VALUE!` in a row the table's data does not reach. In a cell of the table the table's name can go:
`[@Qty]*2`, `[Qty]`. Inside a name, escape `[`, `]`, `#` and `'` with `'`, as in `Sales[a'#b]`. Saved, each has the form Excel
stores, such as `Sales[[#This Row],[Qty]]*2`. A column or table that does not exist gives `#REF!`, where Excel refuses the
formula when it is typed.

### Functions

Over 470 of Excel's functions are built in, and [Spreadsheet functions](spreadsheet-functions.md) lists them and those that
are not: math and trigonometry, logic, text, date and time, lookup and reference, financial, engineering, database,
information and statistics, with the COUNTIF, SUMIF and AVERAGEIF families and their criteria (`">20"`,
`"a*"`, `"<>"`). `FunctionRegistry.BuiltInFunctions` lists them. Each follows Microsoft's function reference and is
checked against what Excel calculates: `tools/excel-function-oracle.ps1` runs the cases in
`tests/Egap.Spreadsheets.Tests/Oracle/functions*.txt` through Excel, and the tests compare Egap with its answers;
`tools/excel-workbook-oracle.ps1` has Excel build, calculate and save whole workbooks, which the tests calculate again
with `CalculateFull` and compare cell by cell. Text that depends on the culture, such as FIXED, DOLLAR, dates read from
text and numbers joined into text, follows `book.Culture`.

`FunctionRegistry.Add` adds a function of your own, as a user-defined function in Excel; files that call one (stored as
`_xludf.NAME`) then calculate too. The function gets its arguments as given: `Value` reads one value, `Values` the cells
of a range, and an exception gives `#VALUE!` in the cell:

```csharp
using Egap.Spreadsheets;

FunctionRegistry.Add("HYPOT", 2, 2, arguments =>
    Math.Sqrt(Math.Pow(arguments[0].Value.Number, 2) + Math.Pow(arguments[1].Value.Number, 2)));
FunctionRegistry.Add("COUNTWORDS", 1, 1, arguments =>
{
    int words = 0;
    foreach (CellValue value in arguments[0].Values)
    {
        words += value.Kind == CellValueKind.Text ? value.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length : 0;
    }

    return words;
});

Worksheet sheet = Workbook.Create().Worksheets[0];
sheet["A1"].Value = "one two";
sheet["A2"].Value = "three";
CellValue length = sheet.Evaluate("HYPOT(3,4)");            // 5
CellValue words = sheet.Evaluate("COUNTWORDS(A1:A2)");      // 3
```

The registry is shared by the whole process: a function added is known to every workbook until `FunctionRegistry.Remove`
takes it away. Built-in functions cannot be replaced.

## Number formats and cell text

`cell.Text` is the value as Excel shows it in the cell, through the cell's number format: `1,234.50` for 1234.5 with
`#,##0.00`, `2024-03-15` for the date serial 45366 with `yyyy-mm-dd`, `TRUE` for a boolean and `#DIV/0!` for an error. A
formula shows its last calculated value. The text is not cut to the column's width, as Excel's `###` is.

```csharp
using System.Globalization;
using Egap.Spreadsheets;

Workbook book = Workbook.Create();
Cell cell = book.Worksheets[0]["A1"];
cell.Value = 1234.5;
cell.NumberFormat = "#,##0.00;[Red]-#,##0.00";
string english = cell.Text;                          // "1,234.50"
book.Culture = CultureInfo.GetCultureInfo("de-DE");
string german = cell.Text;                           // "1.234,50"

NumberFormatCode code = NumberFormatCode.Parse("[h]:mm");
string elapsed = code.Format(1.5);                   // "36:00"
```

`NumberFormatCode` reads every part of Excel's format codes: up to four sections (positive, negative, zero, text),
conditions such as `[>=100]`, colours (`GetColor` tells which), digit placeholders `0`, `#` and `?`, thousands separators
and scaling by 1,000 with trailing commas, percent, scientific notation, fractions such as `# ?/?` and `# ?/8`, dates and
times with month and day names, AM/PM, elapsed times such as `[h]:mm`, fractions of a second, text with `@`, literal text,
padding with `_` and locale tags such as `[$-414]`. Numbers are rounded half away from zero after the 15 significant
digits Excel keeps, so `2.675` with `0.00` shows `2.68`, as in Excel.

`Workbook.Culture` gives the decimal and group separators, month and day names and the short date that the built-in
formats `mm-dd-yy` and `m/d/yy h:mm` follow, as Excel's follow the system's. The invariant culture, the default, shows
them as Excel in English does: `3/15/2024`. A locale tag in a code overrides the culture for that section. Dates follow
Excel's 1900 date system, 29 February 1900 included, or the 1904 system when `book.Date1904` is set; a date before the
system's start or after 9999-12-31 shows as `#` signs.

## CSV and TSV

`Workbook.LoadCsv` reads comma- or tab-separated text (RFC 4180) into a new workbook with one sheet, and
`Worksheet.SaveCsv` writes a sheet as text, from A1 to the last used cell. Quoted fields hold separators, doubled quotes
and line breaks.

```csharp
using System.Globalization;
using Egap.Spreadsheets;

Workbook book = Workbook.LoadCsv("prices.csv");      // separator, types and dates detected
Worksheet sheet = book.Worksheets[0];
sheet.SaveCsv("prices.tsv");                         // a tab for .tsv

CsvOptions german = new() { Culture = CultureInfo.GetCultureInfo("de-DE") };
Workbook local = Workbook.LoadCsv("preise.csv", german);   // 1.234,5 and 15.03.2024
local.Worksheets[0].SaveCsv("preise-kopie.csv", german);   // semicolons, as Excel in German
```

Reading picks the separator from `CsvOptions.Separator`, a `sep=` first line, the `.tsv` or `.tab` extension, or
whichever of comma, semicolon and tab the first line holds most of. It reads UTF-8 unless a byte order mark or
`CsvOptions.Encoding` says otherwise. With `DetectTypes`, the default, fields become numbers (in the options' culture,
with thousands separators and exponents), percentages, `TRUE` and `FALSE`, errors such as `#N/A`, ISO dates and times,
the culture's short dates and times of day, each with a number format that shows it as written. A number with a leading
zero, such as `007`, stays text, and so does a field that starts with `=`: reading never makes formulas. A text with more
rows or fields than a sheet holds, or a field longer than 32,767 characters, throws an `InvalidDataException`.

Writing shows each value as its cell does (`cell.Text`), as Excel saves CSV; set `WriteFormattedText` to false to write
numbers in full and dates as serial numbers. It writes UTF-8 with a byte order mark, so Excel reads it as UTF-8, uses the
culture's list separator unless you name one (a semicolon where the comma is the decimal separator), ends rows with
`\r\n` and pads every row to the same number of fields. `WriteSeparatorHint` adds a `sep=` line for Excel.

`Workbook.Load` and `Workbook.Save` take `.csv`, `.tsv` and `.tab` paths too, with `WorkbookLoadOptions.Csv` and
`WorkbookSaveOptions.Csv` for the options; saving writes the first visible sheet.

## Spreadsheets as documents

`Document.Load` reads XLSX and CSV, and `Workbook.FromTables` puts the tables of a document or a PDF on sheets: see
[Spreadsheets and documents](spreadsheets-and-documents.md).

## Tables, validation and conditional formatting

`sheet.Tables` holds Excel tables: a named range with a header row, filter buttons, a style and an optional totals row,
which formulas can name, as in `SUM(Sales[Amount])`. `Add` names empty header cells `Column1`, `Column2` and so on and
turns other values into text, as Excel does; saving keeps the header cells and the column names the same.
`sheet.DataValidations` says what cells take and shows drop-down lists and input messages, and
`sheet.ConditionalFormats` holds rules that style cells by their values or draw bars, colour scales and icons in them.

```csharp
using Egap.Spreadsheets;

Workbook book = Workbook.Create();
Worksheet sheet = book.Worksheets[0];
sheet["A1"].Value = "Region";
sheet["B1"].Value = "Amount";
sheet["A2"].Value = "North";
sheet["B2"].Value = 1250.5;
sheet["A3"].Value = "South";
sheet["B3"].Value = 980;

SheetTable sales = sheet.Tables.Add("A1:B4", "Sales");     // the header row, two rows of data and a totals row
sales.ShowTotalsRow = true;
sales.Columns[1].TotalsRowFunction = TableTotalsFunction.Sum;
sales.StyleName = "TableStyleMedium9";

DataValidation region = sheet.DataValidations.AddList(RangeAddress.Parse("A2:A3"), ["North", "South", "East", "West"]);
region.Prompt = "Pick a region";
sheet.DataValidations.Add(RangeAddress.Parse("B2:B3"), DataValidationType.DecimalNumber, "0").Operator = ComparisonOperator.GreaterThanOrEqual;

CellStyle high = CellStyle.Default with { Font = CellFont.Default with { Bold = true }, Fill = CellFill.Solid(CellColor.FromRgb(198, 239, 206)) };
sheet.ConditionalFormats.Add("B2:B3",
    ConditionalFormatRule.CellIs(ComparisonOperator.GreaterThan, "1000", high),
    ConditionalFormatRule.DataBar(CellColor.FromRgb(99, 142, 198)));
book.Save("sales.xlsx");
```

A rule's `Style` applies only what differs from `CellStyle.Default`: the font's colour, bold, italic, underline and
strikethrough, the fill, the borders and the number format, as Excel's differential formats hold. Rules Excel made that
the model does not describe in full, such as an icon set's thresholds, data bars' extensions or a table's filter
criteria, are written back as they were. Rules without a `Priority` get the places after the sheet's other rules.

## Notes, hyperlinks and pictures

`sheet.Comments` holds notes (Excel's classic comments), `sheet.Hyperlinks` links to web addresses, files and places in
the workbook, and `sheet.Drawings` the pictures, shapes and charts on the sheet. Pictures can be added and moved, and a
picture's image replaced; shapes, charts, groups and other objects keep everything but their place, name and alternative
text as the file had them.

```csharp
using Egap.Spreadsheets;

Workbook book = Workbook.Load("report.xlsx");
Worksheet sheet = book.Worksheets[0];

sheet.Comments.Add("B2", "Checked against the ledger", author: "Ann").Visible = true;
sheet.Hyperlinks.Add("A10", "https://example.com/report").Display = "Full report";
sheet.Hyperlinks.AddInternal(RangeAddress.Parse("A11"), "'Data'!A1");

byte[] logo = File.ReadAllBytes("logo.png");
SheetDrawing picture = sheet.Drawings.AddPicture(logo, CellAddress.Parse("E2"), width: 120, height: 40);
picture.Description = "Company logo";

foreach (SheetDrawing drawing in sheet.Drawings)
{
    Console.WriteLine($"{drawing.Kind} {drawing.Name} at {drawing.From.Cell}");
}

book.Save("report.xlsx");
```

Parts nothing changed in are saved byte for byte: a table, the notes or the drawing layer are written again only when
something in them changed. Charts, pivot tables, slicers, macros and other parts the model does not hold are always
kept as they were.

## Streaming large files

`SheetReader` and `SheetWriter` stream rows through XLSX files and CSV text without building a workbook, so memory stays
the same however many rows there are. Use them for exports and imports of more rows than fit comfortably in memory; use
`Workbook` to edit a file.

```csharp
using Egap.Spreadsheets;

using (SheetWriter writer = SheetWriter.Create("orders.xlsx"))      // .csv, .tsv and .tab write text
{
    writer.StartSheet("Orders");
    writer.SetColumnWidth(0, 14);
    writer.SetColumnFormat(1, "yyyy-mm-dd");
    writer.FreezeTopRows(1);
    writer.WriteRow(CellStyle.Default with { Font = CellFont.Default with { Bold = true } }, "Order", "Date", "Amount");
    for (int i = 0; i < 1_000_000; i++)
    {
        writer.WriteRow($"A-{i}", 45000 + i % 365, i * 0.25);
    }
}

using SheetReader reader = SheetReader.Open("orders.xlsx");
double total = 0;
while (reader.Read())
{
    if (reader.Row > 0 && reader[2].Kind == CellValueKind.Number)
    {
        total += reader[2].Number;
    }
}

Console.WriteLine($"{reader.SheetName}: {total} ({reader.GetText(1)} was the last date)");
```

The writer writes each row as it comes, and the workbook's own parts when it is disposed: dispose it to finish the file.
Text goes into the cells rather than a shared string table, column widths and frozen rows come before a sheet's first
row, and `SetColumnFormat` and a row's style set the cells' number formats and looks. The reader reads an XLSX file's
shared strings and styles first, then each row of the sheet as it reads it; `GetText` shows a cell through its number
format, and `SelectSheet` moves to another sheet. A stream that cannot seek is copied into memory first, as a ZIP file
is read from its end.

## Memory

Sheets store cells in blocks of 32 rows by 32 columns, made only where cells are: a sheet with values in A1 and
XFD1048576 holds two blocks. A cell takes 16 bytes, and a block makes its rows of 32 cells as cells arrive, so a full block
is about 16 KB and a lone cell far from the others a few KB at most. A cell with no value and the default style takes no
room, and a block goes again when its last cell is cleared.

A workbook is not safe to change from more than one thread at a time.
