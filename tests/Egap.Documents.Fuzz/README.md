# Egap.Documents.Fuzz

Fuzzing harnesses for Egap.Documents' readers (Phase 6 design Q19), run with SharpFuzz and AFL++ in the same Docker image as
Egap.Pdf.Fuzz (`tests/Egap.Pdf.Fuzz/Dockerfile`). A reader must never throw; anything thrown is a crash, and AFL++ also
reports hangs.

| Target | Input | Seeds |
|---|---|---|
| `markdown` | Any bytes read as Markdown, with GitHub's extensions and CommonMark only; text, list labels and every run's and paragraph's formatting read back | `tests/fuzz-seeds/markdown/*` |

Daytime runs are 10 minutes per target: `python tools/fuzz-afl.py 0.17 markdown`.
