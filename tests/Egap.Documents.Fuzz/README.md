# Egap.Documents.Fuzz

Fuzzing harnesses for Egap.Documents' readers (Phase 6 design Q19), run with SharpFuzz and AFL++ in the same Docker image as
Egap.Pdf.Fuzz (`tests/Egap.Pdf.Fuzz/Dockerfile`). A reader must never throw; anything thrown is a crash, and AFL++ also
reports hangs.

| Target | Input | Seeds |
|---|---|---|
| `markdown` | Any bytes read as Markdown, with GitHub's extensions and CommonMark only; text, list labels and every run's and paragraph's formatting read back; written as Markdown and read again | `tests/fuzz-seeds/markdown/*` |
| `package` | Any bytes read as an OPC package (ZIP, content types, relationships); every part's relationships resolved and every XML part loaded; may throw only `DocumentException` | `tests/fuzz-seeds/package/*` |
| `csslayout` | Any bytes read as HTML with flex and grid containers, rounded and shadowed boxes and positioned boxes; laid out and drawn; written as HTML that must read and lay out again, and as DOCX that must read again | `tests/fuzz-seeds/csslayout/*` |
| `mime` | Any bytes read as a MIME message, every part's headers and text read; may throw only `DocumentException`. The quoted-printable codec decodes any bytes, and what it encodes decodes back | `tests/fuzz-seeds/mime/*` |
| `mhtml` | Any bytes read as an MHTML archive; written as MHTML that must read again; may throw only `DocumentException` | `tests/fuzz-seeds/mhtml/*` |
| `opf` | Any bytes read as an EPUB package document in a book that holds nothing else; may throw only `DocumentException` | `tests/fuzz-seeds/opf/*` |
| `nav` | Any bytes read as an EPUB 3 navigation document and as an EPUB 2 NCX; may throw only `DocumentException` | `tests/fuzz-seeds/nav/*` |
| `epub` | Any bytes read as an EPUB book; written as EPUB that must read again; may throw only `DocumentException` | `tests/fuzz-seeds/epub/*` |

Daytime runs are 10 minutes per target: `python tools/fuzz-afl.py 0.17 markdown`.
