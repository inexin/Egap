# Egap.Spreadsheets.Fuzz

Fuzzing harness for Egap.Spreadsheets' parsers, run with SharpFuzz and AFL++ in the same Docker image as Egap.Pdf.Fuzz
(`tests/Egap.Pdf.Fuzz/Dockerfile`), or with the managed mutator on any machine.

| Target | Input | Seeds |
|---|---|---|
| `numberformat` | Any bytes read as a number format code; numbers (fixed ones and the input's bytes read as doubles) and text shown through it in three cultures and both date systems; must never throw | `tests/fuzz-seeds/numberformat/*` |
| `csv` | Any bytes read as CSV with type detection; written as CSV that must read again, and streamed through `SheetReader`; may throw only `InvalidDataException` | `tests/fuzz-seeds/csv/*` |
| `xlsx` | Any bytes read as an XLSX package: loaded, cells shown, a note added, saved and loaded again, and every sheet streamed through `SheetReader`; may throw only `DocumentException` | `tests/fuzz-seeds/xlsx/*` (deflated and stored; `dtd.xlsx` has DTDs, which must be refused) |
| `document` | Any bytes read by `Document.Load` after `SpreadsheetFormat.Register()`, an XLSX package most of all: its tables put back on sheets with `Workbook.FromTables`; may throw only `DocumentException`, or `NotSupportedException` for a format the harness has not registered. | `tests/fuzz-seeds/xlsx` |
| `tables` | Any bytes read as CSV, written as a document and put back on sheets; may throw only `InvalidDataException`. | `tests/fuzz-seeds/csv` |
| `links` | Any bytes read as an XLSX package, with formulas added that read a linked book's kept values and a full calculation; may throw only `DocumentException`. | `tests/fuzz-seeds/links` |
| `sheetcharts` | Any bytes read as an XLSX package: each chart read, filled from the cells and edited, then the book saved; may throw only `DocumentException`. | `tests/fuzz-seeds/sheetcharts` |
| `encrypted` | Any bytes read as a workbook with the password `Egap pässword 1`: an XLS file's FILEPASS record and RC4 or RC4 CryptoAPI decryption, or an encrypted XLSX file's EncryptionInfo and package, then the workbook read and saved; may throw only `DocumentException` | `tests/fuzz-seeds/encrypted/*` (Excel's agile XLSX and CryptoAPI XLS, LibreOffice's RC4 XLS) |
| `headerfooter` | Any bytes read as a header and footer code (`&L`, `&P+1`, `&"Arial,Bold"`, `&K` colours and the rest): parsed, then printed to PDF as a sheet's header and footer with every field filled in; must never throw | `tests/fuzz-seeds/headerfooter/*` |
| `formula` | Any bytes read as a formula in A1 and R1C1 style; what parses must print back exactly as written, its A1 form must read back to itself, its R1C1 form must parse, and evaluating it on a small workbook must give a value; may throw only `FormulaException` | `tests/fuzz-seeds/formula/*` |
| `edits` | Any bytes read as an XLSX package: rows and columns of each sheet put in and taken out, then calculated, saved and loaded again; may throw only `DocumentException`, or `InvalidOperationException` for an edit Excel refuses | `tests/fuzz-seeds/xlsx/*` |
| `shift` | Any bytes read as a formula, its references moved for rows and columns put in and taken out; what parses must parse after each edit, and a formula naming no sheet must not change for an edit of another sheet | `tests/fuzz-seeds/formula/*` |
| `copies` | Any bytes read as an XLSX package: ranges of each sheet copied and moved, on the sheet and to another, then calculated, saved and loaded again; may throw only `DocumentException`, or `InvalidOperationException` for a paste Excel refuses | `tests/fuzz-seeds/xlsx/*` |
| `move` | Any bytes read as a formula, its references moved for cells cut and pasted; what parses must parse after each move, and a move of cells it does not name must leave it as it was | `tests/fuzz-seeds/formula/*` |
| `sorts` | Any bytes read as an XLSX package: each sheet's used range and part of it sorted, its filter and its tables' filters applied and cleared, its cells found and replaced, then calculated, saved and loaded again; may throw only `DocumentException`, or `InvalidOperationException` for a sort or replace Excel refuses | `tests/fuzz-seeds/xlsx/*` |
| `autofit` | Any bytes read as an XLSX package: each sheet's used range fitted, columns and rows, and every row it has fitted again, then saved and loaded again; every width must stay in 0 to 255 and every height in 0 to 409.5, and only `DocumentException` may be thrown | `tests/fuzz-seeds/xlsx/*` |
| `find` | Any bytes read as text to find, with Excel's wildcards: found in formulas, values and notes, whole and in part, and replaced; may throw only `ArgumentException` for empty text, or `InvalidOperationException` for a replace that makes a formula Excel refuses | `tests/fuzz-seeds/formula/*` |
| `conditional` | Any bytes read as an XLSX package, or else as text used for the formulas of conditional formatting and data validation rules of every kind on a small sheet: each cell's conditional format, validity and list items worked out, the invalid cells found and two pages printed; a package may throw only `DocumentException` | `tests/fuzz-seeds/conditional/*` |
| `ods` | Any bytes read as an OpenDocument spreadsheet, zipped or flat: loaded, cells shown and their formulas, translated from OpenFormula, written in R1C1 style, calculated, saved in its own format and as XLSX and loaded again; may throw only `DocumentException` | `tests/fuzz-seeds/ods/*` (stored packages and flat FODS) |
| `odscrypt` | The same, for a password-protected ODS opened with the password `fuzz`: the encrypted package, its manifest and the decryption; may throw only `DocumentException` | `tests/fuzz-seeds/ods-encrypted/*` (packages Egap and LibreOffice encrypted) |
| `xls` | Any bytes read as an Excel 97-2003 workbook (BIFF8): the compound file, the records, shared strings across CONTINUE records, styles, and formulas decoded from their tokens, shared and array formulas among them; then cells shown, calculated, saved as XLSX and loaded again; may throw only `DocumentException` | `tests/fuzz-seeds/xls/*` (workbooks built by the tests' `XlsBuilder`) |

Daytime runs are 10 minutes per target at most. With the managed mutator:

```
dotnet build tests/Egap.Spreadsheets.Fuzz -c Release
dotnet tests/Egap.Spreadsheets.Fuzz/bin/Release/net10.0/Egap.Spreadsheets.Fuzz.dll mutate numberformat 600 tests/fuzz-seeds/numberformat findings/numberformat
```

To sort a folder of findings, `replay <target> <folder>` runs each one and prints every distinct failure once, with the
first file that shows it:

```
dotnet tests/Egap.Spreadsheets.Fuzz/bin/Release/net10.0/Egap.Spreadsheets.Fuzz.dll replay formula findings/formula
```
