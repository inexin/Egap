# Spreadsheets

Egap.Spreadsheets holds workbooks in memory and lets you build and edit them from code: sheets, cells, values, formulas and
cell styles, as Excel keeps them. The types are in `Egap.Spreadsheets`.

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

## Workbooks and sheets

`Workbook.Create()` starts a workbook with one empty sheet named `Sheet1`, as Excel does; `Workbook.CreateEmpty()` starts
one with none. `book.Worksheets` holds the sheets in tab order: `Add` and `Insert` make a sheet (named `SheetN` when you
give no name), `Remove` takes one out, `Move` changes its position, and the indexers find a sheet by position or by name.

Sheet names follow Excel's rules: 1 to 31 characters, none of `: \ / ? * [ ]`, no apostrophe at the start or end, and no
two sheets with the same name, case ignored. A name that breaks a rule throws an `ArgumentException`.

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

## Memory

Sheets store cells in blocks of 32 rows by 32 columns, made only where cells are: a sheet with values in A1 and
XFD1048576 holds two blocks. A cell takes 16 bytes, and a block makes its rows of 32 cells as cells arrive, so a full block
is about 16 KB and a lone cell far from the others a few KB at most. A cell with no value and the default style takes no
room, and a block goes again when its last cell is cleared.

A workbook is not safe to change from more than one thread at a time.
