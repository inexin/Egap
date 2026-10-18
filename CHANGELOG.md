# Changelog

What each Egap package adds, newest first. Nothing before the first release below was published; those versions were
internal milestones whose public API was frozen and reviewed, and they are listed so the version numbers make sense.

## Egap.Pdf 1.4.0 (unreleased, first release on NuGet)

### PDF tools
- Optimize: presets Lossless (default), Print, Ebook and Screen; image downsampling and re-encoding (JPEG, Flate, CCITT G4),
  lossless JPEG optimization, duplicate and font merging, font subsetting, and a savings report.
- Imposition: N-up, booklet, poster tiling, grid and fit to page size.
- Stamping: text, image and page stamps; page numbers, headers and footers, Bates numbering and watermarks as removable
  pagination artifacts.
- Compare: a word-level text diff with page alignment, a visual diff of rendered pages, and a marked-up copy.
- Clean-up: grayscale conversion, blank page removal, deskew of scanned pages, text to outlines.

### Fixes
- Page tree repair skips objects that are plainly not pages, such as outline items or annotations listed among a damaged
  file's pages, instead of turning them into pages. A rewrite of such a file no longer breaks its outline.
- On macOS, verifying a signature whose certificate has an unreadable public key reports the signature invalid instead of
  throwing.
- `PdfPageSize.ToString()` no longer overflows the stack.

## Egap.Pdf 1.3.0 (2026-10-16, not published)

- Signatures: PAdES B-B to B-LTA, deferred and certification signatures, timestamps, and verification with a report.
- Redaction that removes text, paths and image pixels for real; sanitizing of hidden data.
- PDF/A creation, conversion and checks (1b to 4f), basic PDF/UA, tagged PDF, linearized saves, an invisible OCR text layer
  from hOCR or ALTO.

## Egap.Pdf 1.2.0 (2026-10-16, not published)

- Rendering to rasters, onto any Egami canvas or to a display list: transparency, all shading types, patterns, layers,
  annotations, and Egap.Fonts substitutes for fonts that are not embedded.

## Egap.Pdf 1.1.0 (2026-10-13, not published)

- Content streams parsed, walked and rewritten; drawing on pages through an Egami canvas with embedded font subsets; fonts
  read; text extracted in reading order; images listed, decoded and replaced.

## Egap.Pdf 1.0.0 (2026-10-13, not published)

- Document model: pages, labels, outlines, destinations, metadata (Info and XMP), attachments, layers, annotations, forms
  (XFA data included); merge and split.

## Egap.Pdf 0.1.0 (2026-10-12, not published)

- Reading, damaged and encrypted files included (RC4 and AES, revisions 2 to 6), into a lazily loaded object graph with
  change tracking; incremental and full saves, object streams, AES-256 encryption.

## Egap.Documents 1.1.0 (unreleased, first release on NuGet)

- Images in Markdown output through a resolver or a folder beside the file; EMF and WMF pictures as PNG or SVG.
- Page drawing and PDF export: a picture used on many pages, such as a logo in a header, is embedded once; SVG pictures
  are drawn as vectors.
- Builds on 1.0.0 (2026-10-17, not published): one document model for DOCX, DOTX, DOCM, HTML and Markdown, with styles,
  lists, tables, fields, comments and tracked changes; find, replace and a document builder; page layout with floats,
  columns, headers, footers and footnotes, drawn on any Egami canvas.

## Egap.Pdf.Conversion 1.1.0 (unreleased, first release on NuGet)

- Renamed from Egap.Pdf.Layout, with its namespaces (`Egap.Pdf.Layout` is now `Egap.Pdf.Conversion`). Breaking, before the
  first release.
- Tables from tagged PDFs such as Word's come back with merged and empty cells, tables split across pages joined, and
  tables nested in cells.
- Builds on 1.0.0 (2026-10-17, not published): documents saved as PDF with links, outline and tags; PDF pages analysed into
  blocks, reading order, headings, lists, tables and figures and converted back into documents; auto-tagging; DoclingDocument
  JSON and table CSV output.

## Egap.Fonts 1.0.0 (unreleased, first release on NuGet)

- Liberation Sans, Serif and Mono, Carlito and Caladea (SIL OFL 1.1) as metric-compatible substitutes for Helvetica,
  Arial, Times, Times New Roman, Courier, Courier New, Calibri and Cambria, found by name or alias.
