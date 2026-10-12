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
