# Guides

Each guide covers one area of Egap: what it does, the options and their defaults, and the limits. Some guides end with
**Recipes**, short complete answers to common tasks such as merging files or adding page numbers, with code you can copy.

The guides are grouped by package, and within a group they are in alphabetical order. Each document format has its own
page in [Document formats](../formats/index.md). If you are new, read [Getting started](../getting-started.md) first, and
[Packages](../packages.md) to pick what to install.

## Egap.Pdf

Reading, editing, rendering and saving PDF files.

- [Editing PDF files](editing.md): pages, metadata, outlines, annotations and forms; damaged and encrypted files; saving.
- [Fonts](fonts.md): the open fonts that stand in for missing ones, in Egap.Pdf and Egap.Documents alike.
- [OCR and searchable scans](ocr.md): making scanned PDFs searchable through an OCR engine you plug in.
- [PDF tools](pdf-tools.md): optimize, stamps and imposition, page ranges, compare and clean-up.
- [Print production](print-production.md): preflight against a print profile, separations and ink coverage, colour conversion, PDF/X and overprint preview.
- [Redaction and PDF/A](redaction-and-pdfa.md): true redaction, replacing text, PDF/A and e-invoices.
- [Rendering pages](rendering.md): pages to images, or onto any Egami canvas.
- [Signatures](signatures.md): signing, verifying, and keys on smart cards and HSMs.
- [Text, images and drawing](text-images-drawing.md): text extraction, finding text, images, and drawing on a page.

## Egap.Documents

One document model for DOCX, HTML, Markdown and plain text, its page layout, and conversions to and from PDF.

- [Flow documents](flow-documents.md): the document model, editing, text options, barcodes, mail merge and page layout.
- [Moving to Egap.Documents 2.0](migrating-to-2.0.md): the new packages and namespaces, and what else changed.
- [PDF export and conversion](pdf-export-and-conversion.md): documents to PDF, one-line conversions, and PDF back to
  documents.
- [Reports in code](reports.md): the fluent report API, with rows, tables, grids, styles, components and paging, and the
  live preview tool.

## Document formats

The [overview](../formats/index.md) says what each format reads and writes, and how a file's format is found; then one page
per format, with its options:

- [Docling JSON](../formats/docling-json.md)
- [DOCX](../formats/docx.md)
- [EPUB](../formats/epub.md), in the Egap.Documents.Html package
- [HTML](../formats/html.md), in the Egap.Documents.Html package
- [Markdown](../formats/markdown.md), in the Egap.Documents.Markdown package
- [MHTML](../formats/mhtml.md), in the Egap.Documents.Html package
- [Plain text](../formats/text.md)
