# Egap.Spreadsheets.Fuzz

Fuzzing harness for Egap.Spreadsheets' parsers, run with SharpFuzz and AFL++ in the same Docker image as Egap.Pdf.Fuzz
(`tests/Egap.Pdf.Fuzz/Dockerfile`), or with the managed mutator on any machine.

| Target | Input | Seeds |
|---|---|---|
| `numberformat` | Any bytes read as a number format code; numbers (fixed ones and the input's bytes read as doubles) and text shown through it in three cultures and both date systems; must never throw | `tests/fuzz-seeds/numberformat/*` |
| `csv` | Any bytes read as CSV with type detection; written as CSV that must read again, and streamed through `SheetReader`; may throw only `InvalidDataException` | `tests/fuzz-seeds/csv/*` |
| `xlsx` | Any bytes read as an XLSX package: loaded, cells shown, a note added, saved and loaded again, and every sheet streamed through `SheetReader`; may throw only `DocumentException` | `tests/fuzz-seeds/xlsx/*` (deflated and stored; `dtd.xlsx` has DTDs, which must be refused) |

Daytime runs are 10 minutes per target at most. With the managed mutator:

```
dotnet build tests/Egap.Spreadsheets.Fuzz -c Release
dotnet tests/Egap.Spreadsheets.Fuzz/bin/Release/net10.0/Egap.Spreadsheets.Fuzz.dll mutate numberformat 600 tests/fuzz-seeds/numberformat findings/numberformat
```
