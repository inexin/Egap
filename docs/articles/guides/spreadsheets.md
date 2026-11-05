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
cell you changed keeps its last result as a value. Saving a workbook with macros as `.xlsx` or `.xltx` leaves the macros
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
leading `=` you give is dropped). A formula's result is empty until the workbook is calculated. Setting `Value` removes a
formula; setting `Formula` to null removes it and its result.

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
`sheet.PageSetup` holds the rest of what Excel's Page Setup dialog sets: paper, orientation, scale or fit, margins in
inches, centring, gridlines, headings, page order and the header and footer in Excel's header codes.

## Memory

Sheets store cells in blocks of 32 rows by 32 columns, made only where cells are: a sheet with values in A1 and
XFD1048576 holds two blocks. A cell takes 16 bytes, and a block makes its rows of 32 cells as cells arrive, so a full block
is about 16 KB and a lone cell far from the others a few KB at most. A cell with no value and the default style takes no
room, and a block goes again when its last cell is cleared.

A workbook is not safe to change from more than one thread at a time.
