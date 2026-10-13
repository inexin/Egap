# Egap.Pdf.Fuzz

Fuzzing harnesses for Egap's readers, run with [SharpFuzz](https://github.com/Metalnem/sharpfuzz) and AFL++. A lenient read
must never throw anything but a `PdfLimitException`; a strict read may throw only `PdfException`. Any other exception is a
crash, and AFL++ also reports hangs.

| Target | Input | Seeds |
|---|---|---|
| `lexer` | Any bytes, tokenized leniently and strictly | `tests/fuzz-seeds/lexer/*` |
| `parser` | Any bytes: every direct object in turn and every `n g obj` found, leniently and strictly; each indirect object is written back and must parse strictly to the same bytes | `tests/fuzz-seeds/parser/*` |
| `filters` | A filter pick, predictor parameters and data, decoded leniently and strictly | `tests/fuzz-seeds/filters/*` |
| `streams` | PDF bytes: every stream object found is decoded leniently; for corpus runs, replay prints what was decoded and every warning | `tests/fuzz-seeds/parser/*` |
| `xref` | PDF bytes: the cross-reference is read and every listed object loaded, leniently and strictly; reading may fail with `PdfException` (this target has no repair by scanning; `document` does) | `tests/fuzz-seeds/xref/*` |
| `document` | PDF bytes opened as a `PdfDocument`, with repair by scanning: every object loaded, streams decoded, an edit made; leniently and strictly | `tests/fuzz-seeds/xref/*`, `tests/fuzz-seeds/document/*` |
| `rewrite` | PDF bytes opened leniently and saved by full rewrite three ways (input style, compact, compact with new AES-256 encryption); each output must open strictly with no warnings and hold the same objects | `tests/fuzz-seeds/xref/*`, `tests/fuzz-seeds/document/*` |
| `incremental` | PDF bytes saved incrementally: unchanged must be byte-identical; after an edit the update must keep the original bytes, reopen and hold the same objects | `tests/fuzz-seeds/xref/*`, `tests/fuzz-seeds/document/*` |
| `trees` | PDF bytes: every dictionary with `/Names`, `/Nums` or `/Kids` read as a name or number tree; a tree read without warnings must find each entry by lookup; after 400 sets and removes drawn from the input it must read back strictly, holding exactly what the edits say, no node over 128 | `tests/fuzz-seeds/trees/*` |
| `pages` | PDF bytes: the page tree read leniently and strictly; every page's attributes and label read, each page found again by `IndexOf`, boxes nested inside the media box, rotation one of 0, 90, 180, 270; reading must not count as an edit, so an unchanged incremental save stays byte-identical | `tests/fuzz-seeds/pages/*`, `tests/fuzz-seeds/xref/*` |
| `pageedits` | PDF bytes: 60 inserts, removes and moves and a few page label ranges drawn from the input; every page keeps its media box, crop box and rotation; the result written whole reopens strictly without a warning and with the same labels, and saved incrementally keeps the original bytes and reopens with the same pages | `tests/fuzz-seeds/pages/*`, `tests/fuzz-seeds/xref/*` |
| `navigation` | PDF bytes: every action typed and read, its `/Next` sequence ending with each action once; every destination array written back equal; named destinations looked up, then set and removed, and the rewritten file must hold exactly those set | `tests/fuzz-seeds/navigation/*`, `tests/fuzz-seeds/xref/*` |
| `outlines` | PDF bytes: the outline read leniently and strictly, each item found among its parent's children; then 30 adds, removes, moves and open toggles drawn from the input, and the rewritten outline must need no repair, with every `/Count` right and as many items as the edits leave | `tests/fuzz-seeds/outlines/*`, `tests/fuzz-seeds/xref/*` |
| `xmp` | Any bytes parsed as an XMP packet; one that parses has its mapped properties read, set and written back, and must parse again with the values set and every element of other namespaces kept | `tests/fuzz-seeds/xmp/*` |
| `metadata` | PDF bytes: every metadata property read, then title, author and keywords set and the file saved with a fixed clock; reopened it must give back what was set and the new modification date | `tests/fuzz-seeds/metadata/*`, `tests/fuzz-seeds/xref/*` |
| `attachments` | PDF bytes: every embedded file's properties, bytes and checksum read; then 20 adds and removes drawn from the input, and the rewritten file must hold exactly those names with each added file's bytes and checksum right | `tests/fuzz-seeds/attachments/*`, `tests/fuzz-seeds/xref/*` |
| `layers` | PDF bytes: every optional content group read; then 20 shows, hides, locks, renames and removals drawn from the input, each read back at once, and the rewritten file must hold the same layers in the same state | `tests/fuzz-seeds/layers/*`, `tests/fuzz-seeds/xref/*` |
| `annotations` | PDF bytes: every public property of every annotation on the first 50 pages read and up to 50 appearances drawn; then 20 adds, popups and removals drawn from the input, each read back at once, and the rewritten file must hold the same annotations on each page | `tests/fuzz-seeds/annotations/*`, `tests/fuzz-seeds/xref/*` |
| `forms` | PDF bytes: every public property of up to 2,000 fields with their kids and widgets, every widget led back to its field, a field looked up by its full name, then up to 20 fields filled and read back before and after a rewrite | `tests/fuzz-seeds/forms/*`, `tests/fuzz-seeds/xref/*` |
| `flatten` | PDF bytes: the form and the annotations flattened with a filter drawn from the input; the rewritten file must keep its pages, lose its form, and have content that decodes | `tests/fuzz-seeds/forms/*`, `tests/fuzz-seeds/annotations/*` |
| `merge` | PDF bytes: the first 20 pages extracted twice and merged, written whole with identical objects merged; every page must keep its content and media box, and splitting the second copy off must give them again | `tests/fuzz-seeds/merge/*`, `tests/fuzz-seeds/xref/*` |
| `content` | Any bytes read as a content stream leniently and strictly; the operations written back must read back strictly, without a warning, to the same operations and write the same bytes again | `tests/fuzz-seeds/content/*` |
| `walker` | PDF bytes: the first 20 pages walked by a `PdfContentWalker` that hooks every event, leniently and strictly; marked content must be balanced after each page | `tests/fuzz-seeds/walker/*`, `tests/fuzz-seeds/xref/*` |

Daytime runs are 10 minutes per target; long runs only when the maintainer asks.

## AFL++ in Docker (any OS)

The Dockerfile here has the .NET SDK, AFL++ and SharpFuzz. `tools/fuzz-afl.py` starts one container per target instance;
each copies the sources, publishes and instruments the harness and fuzzes for the given hours, with findings in `.cache/afl`.

```sh
docker build -t egap-fuzz -f tests/Egap.Pdf.Fuzz/Dockerfile tests/Egap.Pdf.Fuzz
python tools/fuzz-afl.py 0.17 lexer
```

## Without AFL++ (any OS)

A simple mutation fuzzer runs in process: random edits of the seeds for the given seconds, with crashes and inputs that take
over 10 seconds saved to the findings directory.

```sh
dotnet run --project tests/Egap.Pdf.Fuzz -c Release -- mutate lexer 600 tests/fuzz-seeds/lexer .cache/mutate-lexer
```

## Reproducing and triaging

```sh
dotnet run --project tests/Egap.Pdf.Fuzz -c Release -- lexer path/to/crash
dotnet run --project tests/Egap.Pdf.Fuzz -c Release -- replay lexer .cache/afl/lexer/default/crashes
EGAP_REPLAY_WATCH="Flate data is damaged" dotnet run --project tests/Egap.Pdf.Fuzz -c Release -- replay streams ../egap-compare/.corpus/pdfjs
```

`replay` walks the directory tree; `list-objects <file>` prints `n g` for each object that loads, for comparing with qpdf's JSON; `EGAP_REPLAY_WATCH` lists the files that add to any printed count containing its text.

Every crash found becomes a regression test with the input under `tests/fuzz-seeds/` or inline in the test.
