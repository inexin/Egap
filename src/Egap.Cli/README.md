# Egap.Cli

The `egap` command: turn HTML, Markdown, DOCX and images into PDF, and inspect, merge, split, stamp, redact, fill, sign and
check PDFs from a shell, a build script or an AI assistant, with [Egap](https://github.com/inexin/Egap), a fully managed PDF
and document library for .NET.

```sh
dotnet tool install --global Egap.Cli
egap info report.pdf
egap convert notes.md -o notes.pdf --page-size A4
egap merge cover.pdf body.pdf#2-z -o book.pdf
egap run scan.pdf clean --deskew + stamp --page-numbers + save out.pdf
```

- Converts HTML, Markdown, DOCX and images to PDF, and PDF back to DOCX, HTML, Markdown or text.
- Merges, splits, imposes, stamps, optimizes, encrypts, redacts and sanitizes; fills forms; embeds files and e-invoices.
- Signs (PAdES B-B to B-LTA) and verifies; checks and converts PDF/A and PDF/X; preflights for print, with ink separations.
- `run` chains steps over each file, and JSON workflow files keep a recipe; every command takes many files and globs.
- `egap mcp` serves the commands as tools to AI assistants over the Model Context Protocol, inside the folders you allow.
- Exit codes 0 (done), 1 (a file failed or a check found a problem), 2 (usage) and 3 (a safety limit); JSON with
  versioned schemas for every report.

The [guide](https://github.com/inexin/Egap/blob/main/docs/articles/cli/index.md) explains the conventions and every
command. MIT licensed.
