# Egap.Presentations.Fuzz

Fuzzing harness for Egap.Presentations' reader and editing, run with SharpFuzz and AFL++ in the same Docker image as
Egap.Pdf.Fuzz (`tests/Egap.Pdf.Fuzz/Dockerfile`, through `tools/fuzz-afl.py`), or with the managed mutator on any machine.

| Target | Input | Seeds |
|---|---|---|
| `pptx` | Any bytes read as a PPTX package: every master, layout and slide walked (shapes, text, tables, placeholders, notes, comments, sections, transitions, animations, properties, show settings), then saved, loaded and walked again; may throw only `DocumentException` | `tests/fuzz-seeds/pptx/*` (deflated and stored) |
| `pptxedit` | The same, with edits between: transitions changed, a shape's animations and a shape removed, tables grown, notes set, a slide added, moved and removed, properties and show settings set; saved as another kind (PPSX or PPTX), loaded, edited and saved again; may throw only `DocumentException` | `tests/fuzz-seeds/pptx/*` |
| `pptxcrypt` | Any bytes read as an encrypted presentation with the password `Egap pässword 1`: the compound file, the EncryptionInfo, agile or standard decryption, then the deck walked and saved; may throw only `DocumentException` | `tests/fuzz-seeds/pptx-encrypted/*` (agile with one spin, and standard) |

The seeds are decks Egap makes; `seeds <folder>` writes them again:

```
dotnet build tests/Egap.Presentations.Fuzz -c Release
dotnet tests/Egap.Presentations.Fuzz/bin/Release/net10.0/Egap.Presentations.Fuzz.dll seeds tests/fuzz-seeds
```

Daytime runs are 10 minutes per target at most. With the managed mutator:

```
dotnet tests/Egap.Presentations.Fuzz/bin/Release/net10.0/Egap.Presentations.Fuzz.dll mutate pptx 600 tests/fuzz-seeds/pptx findings/pptx
```

With AFL++ in Docker:

```
python tools/fuzz-afl.py 0.17 pptx pptxedit pptxcrypt
```

`replay <target> <folder>` runs each finding in a folder and prints every distinct failure once, with the first file that
shows it.
