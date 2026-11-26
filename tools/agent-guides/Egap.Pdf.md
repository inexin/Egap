# Egap.Pdf: guide for coding agents

Egap.Pdf reads, edits, renders, signs, redacts and saves PDF files, damaged and encrypted ones included: pages, outlines,
metadata, annotations, forms, attachments, layers, text and image extraction, drawing through an Egami canvas, PDF/A,
optimize, stamps, imposition, compare, OCR layers and print preflight. It is fully managed; images, fonts and drawing come
from Egami.

Namespaces: `Egap.Pdf`, with `Egap.Pdf.Ocr` for searchable scans, and `Egami` and `Egami.Pixels` for rendered pages.

{{shared}}

## Entry points

- `PdfDocument.Open(path | stream | bytes, PdfReadOptions)`: a file to read and edit. `PdfReadOptions.Password` opens an
  encrypted one, `Strict` throws a `PdfException` instead of repairing, and the `Max*` limits bound hostile files.
- `PdfDocument.Create()` starts an empty document; `Pages.AddBlank`, `InsertBlank`, `Import`, `Move` and `RemoveAt` change
  pages. `PdfDocument.Merge(a, b)` and `ExtractPages(PdfPageRange.Parse("1-3,z"))` make new documents.
- `pdf.Save(path, PdfSaveOptions)`: incremental by default; `Mode = PdfSaveMode.Rewrite`, `Compression`, `Linearize`,
  `Deterministic` and `Encryption = PdfEncryption.Aes256(user, owner)` for a whole new file.
- `page.ExtractText()`, `page.GetImages()`, `pdf.FindText(...)`: text in reading order, placed images, matches with boxes.
- `page.Render(new PdfRenderOptions { Dpi = 150 })` gives a `Raster<Rgba8>`; `page.RenderTo(canvas)` draws on any Egami
  canvas; `pdf.RenderPagesToFiles("page-{page:00}.png", options)` writes them all.
- `new PdfCanvas(page)`: Egami.Drawing's canvas, written as PDF content over the page.
- `pdf.Form` (`TryGetField`, `AddTextField`, `AddCheckBox`, `Flatten`), `pdf.Metadata`, `pdf.Outline`, `pdf.Attachments`,
  `page.Annotations`.
- `pdf.RedactText`, `MarkRedactions`, `ApplyRedactions`, `ReplaceText` and `Sanitize`: real removal of content.
- `PdfAConverter.Convert(pdf, PdfAConformance.PdfA2u)`, `PdfInvoice.Embed` (Factur-X, ZUGFeRD), `PdfPreflight.Run`.
- `pdf.SignAsync(fieldName, PdfSignOptions, output)` and `signature.Verify(PdfVerifyOptions)`: PAdES B-B to B-LTA.
- `pdf.Optimize`, `pdf.Stamp(PdfStamp.Text(...))`, `PdfImpose.NUp`, `PdfCompare.Run`, `RemoveBlankPages`,
  `DeskewScannedPages`, and `PdfOcr.MakeSearchableAsync` with an Egami OCR engine.

## Samples

Open, edit and save:

{{snippet getting-started.md#Open, edit and save a PDF}}

Pages, outline, annotations and forms:

{{snippet guides/editing.md#Editing PDF files}}

Render a page to PNG:

{{snippet getting-started.md#Render a page}}

Redact a phrase for real:

{{snippet guides/redaction-and-pdfa.md#Redact a phrase}}

Convert to PDF/A:

{{snippet guides/redaction-and-pdfa.md#PDF/A}}

Page numbers on every page but the cover:

{{snippet guides/pdf-tools.md#Add page numbers}}

Sign with a PFX certificate:

{{snippet guides/signatures.md#Signatures}}

## Common mistakes

- Forgetting `using` on a `PdfDocument`. It holds its file (or seekable stream) open until disposed, and a stream it reads
  must not change meanwhile. Documents from `Merge`, `ExtractPages`, `Create` and `PdfImpose` need disposing too.
- Mixing page numbers and indexes: `pdf.Pages[0]` and `PdfTextMatch.PageIndex` count from 0, while `PdfPageRange.Parse`
  and the `{page}` placeholder count from 1. `PdfPageRange.Resolve(count)` gives 0-based indexes.
- Mixing coordinate systems: `PdfRectangle(left, bottom, right, top)` is PDF user space, in points from the bottom left of
  the page; `PdfCanvas` draws in points from the top left as a viewer shows the page.
- Expecting a `PdfRedactAnnotation` to remove anything. Only `ApplyRedactions` (or `RedactText`) removes the text, paths and
  pixels; then save, and the file is written whole so no older revision keeps them.
- Treating `PdfAConverter.Convert` as a validator. Check `report.Succeeded`; it changes nothing when a rule cannot be met.
  Egap checks only the rules it enforces, so use a validator such as veraPDF where conformance must be proven.
- Calling `Save` after `SignAsync`. The signed bytes are the output stream; the document becomes read only
  (`IsReadOnly`), so open the signed file to go on.
- Expecting a network call. Timestamps and revocation data come only through the `HttpClient` you give
  `PdfTimestampClient` and the `Revocation` options of signing and verifying.
- Looking here for DOCX, HTML or Markdown to PDF, or PDF to DOCX: that is Egap.Documents (`SaveAsPdf`, `PdfConvert`,
  `PdfToDocument`).
- Ignoring `pdf.Warnings`. A damaged file opens repaired; the warnings say what was changed.
- Renders that differ between machines: set `PdfRenderOptions.UseSystemFonts = false` so only embedded fonts and the bundled
  substitutes draw text.

## More

- Guides: https://inexin.github.io/Egap/v2/articles/guides/index.html (editing, rendering, signatures, redaction and PDF/A,
  PDF tools, print production, OCR)
- Every page in one list: https://inexin.github.io/Egap/v2/llms.txt
- API reference: https://inexin.github.io/Egap/v2/api/Egap.Pdf.html
