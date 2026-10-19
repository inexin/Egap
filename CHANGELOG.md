# Changelog

What each Egap package adds, newest first. Nothing before the first release below was published; those versions were
internal milestones whose public API was frozen and reviewed, and they are listed so the version numbers make sense.

## Egap.Documents 1.3.0 (unreleased)

### Text options
- `RunFormat.WordSpacing`, `Overline` and `Features` (any OpenType feature on or off, `FontFeatures.Parse("liga=0, tnum")`),
  and `ParagraphFormat.WordBreak` (normal, break all, keep all, anywhere), laid out and drawn.
- HTML reads and writes them as `word-spacing`, `text-decoration: overline`, `font-feature-settings`,
  `font-variant-ligatures`, `font-variant-numeric`, `word-break` and `overflow-wrap`; DOCX keeps ligatures, number forms
  and spacing, stylistic sets and contextual alternates.

### Paging groups
- `ContentControl.Paging` (`GroupPaging`) keeps a group of blocks together, starts it on a new page when too little room is
  left (`EnsureSpace`), never splits it (`ShowEntire`) or cuts it where its page ends (`StopPaging`).
  `DocumentBuilder.StartGroup` and `EndGroup` make groups; HTML `break-inside: avoid` on a box of several blocks makes one.
- Content controls inside headers, footers, table cells and text boxes are now laid out; before, their blocks were left out.

### Strict layout and portable fonts
- `LayoutOptions.Strict` throws a `LayoutException` naming the node, the page and the `LayoutProblem` (text or content
  running past its area, a missing glyph, a font with no stand-in) instead of degrading.
- `FontSettings.Portable` uses only embedded fonts and the bundled substitutes, so layout and, with a deterministic save,
  the PDF bytes are the same on every machine.

## Egap.Pdf 1.5.0 (unreleased)

### Page ranges and units
- `PdfPageRange.Parse` reads page choices such as `"1,3,5-10,z"`, with `r` counting from the end, `:odd` and `:even`, and
  `x` to leave pages out. `ExtractPages` and `PdfStamp.PageRange` take one; `Resolve` turns one into page indices.
- `Length` holds a size in points made from millimetres, centimetres, inches or points (`Length.Mm(20)`, `Length.Parse("2cm")`).
  It converts to and from points without a cast, so it works wherever a size in points is asked for.

### Placeholders
- One set of placeholder names for page furniture: `{page}`, `{total-pages}`, `{label}`, `{date}`, `{time}`, `{title}` and
  `{file}`, each with an optional format. Stamps take them, `{pages}` still works, and `{{` writes a brace.
  `PdfPlaceholders.Fill` fills them in any text.

### Find text
- `FindText` on a document or a page finds a phrase or a regular expression, with case and whole-word options and a page
  range. Each match gives its page, text, glyphs and a quadrilateral per line; phrases are found across line breaks within
  a paragraph.

### Redact and replace text
- `RedactText` redacts every match of a phrase or regular expression for real in one call; `MarkRedactions` adds the marks
  without applying them. Redaction marks already in the document are left alone.
- `ReplaceText` replaces a phrase or a regular expression (with `$1` groups) in the match's own font, size, colour and
  rendering mode. A match whose font lacks a glyph for the new text is left as it was with a `TextNotReplaced` warning, or
  written in a fallback font when one is given.

### Form fields in code
- `PdfForm` adds text fields, check boxes, radio groups, combo boxes, list boxes, push buttons and signature fields, with
  options for value, length, flags, tooltip, colours and border, and appearances drawn for each. Dotted names make parent
  fields.

### E-invoices
- `PdfInvoice.Embed` makes a Factur-X (ZUGFeRD 2.1 and later) e-invoice: the XML attached as `factur-x.xml` or
  `xrechnung.xml` with the relationship its profile asks for, the document converted to PDF/A-3, and the Factur-X extension
  schema in the XMP. `PdfInvoice.Read` reads Factur-X and ZUGFeRD 1.0 to 2.3 invoices back.
- `Attachments.Add` takes a `PdfAttachmentRelationship` and lists the file among the document's associated files.

### Docs
- A cookbook of short recipes (merge, page numbers, signing, redacting a phrase, an HTML invoice as Factur-X PDF/A-3), and a
  PKCS#11 sample in the signatures guide for keys on smart cards and HSMs.

### Fixes
- Redaction, PDF/A conversion and text search on a document that was drawn on since it was last saved first write the font
  subsets the drawing used, so glyph positions and fonts are right.
- Redaction removes a covered image whole when it cannot be decoded or is too large to decode, instead of throwing or
  taking minutes.
- `PdfInvoice.Read` skips an attachment whose stream cannot be decoded.

### Images and page files
- `PdfDocument.FromImages` makes a page per image: JPEG files are embedded as they are stored, and each frame of a TIFF or
  GIF gets its own page. Pages take the image's size at its resolution, or fit a page size within a margin.
- `RenderPagesToFiles` writes pages as PNG, JPEG, WebP, TIFF or other image files, named by a pattern such as
  `"page-{page:00}.png"`.

## Egap.Documents 1.2.0 (unreleased)

### Form controls
- HTML inputs, check boxes, radio buttons, selects and text areas are read as inline content controls holding their values,
  instead of being dropped.
- `ContentControlProperties.Items` holds a drop-down list's or combo box's items, read from and written to DOCX;
  `RadioGroup` ties check boxes into a set of options.

## Egap.Pdf.Conversion 1.2.0 (unreleased)

### Form fields
- `PdfExportOptions.FormFields` turns HTML form controls and DOCX content controls (plain text, date, drop-down list, combo
  box, check box) into PDF form fields at their laid-out place, named by the control's name or tag, with its value.

### One-line conversions
- `PdfConvert` converts HTML, Markdown, DOCX and image files to PDF in one call, with the page size, margins, PDF/A level and
  tagging as options. HTML and Markdown read their images from their own folder only.
- `PdfStamp.Html` makes a stamp of laid-out HTML, such as a seal or a letterhead.

## Egap.Pdf 1.4.1 and Egap.Documents 1.1.1 (2026-10-18)

- The bundled substitute fonts (Liberation Sans, Serif and Mono, Carlito, Caladea) now come from Egami's
  [Egami.Fonts.Substitutes](https://www.nuget.org/packages/Egami.Fonts.Substitutes) 1.0.0 instead of Egap.Fonts. The fonts are
  the same files, so output does not change.
- Egap.Fonts is deprecated and gets no new versions. If you used `Egap.Fonts.StandardFonts`, reference Egami.Fonts.Substitutes
  and use `Egami.Fonts.Substitutes.SubstituteFonts`, which has the same members. Code that reached Egap.Fonts only through
  Egap.Pdf or Egap.Documents needs the same change, since they no longer bring it in.

### Fixes
- Markdown images that an image resolver supplies take their own size, at 96 pixels an inch, instead of none, so they show
  when the document is laid out.
- Page tree repair never takes the catalog for a page, and finds pages by scanning only where it would also accept them
  as kids of a tree. A file whose catalog named itself as the page tree no longer breaks a linearized save, and pages of a
  damaged file no longer go missing after an extract, merge and rewrite.
- Outline repair cuts a chain of items where it reaches the catalog, a page or a page tree node, instead of writing outline
  links into it.

## Egap.Pdf 1.4.0 (2026-10-18)

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

## Egap.Documents 1.1.0 (2026-10-18)

- Images in Markdown output through a resolver or a folder beside the file; EMF and WMF pictures as PNG or SVG.
- Page drawing and PDF export: a picture used on many pages, such as a logo in a header, is embedded once; SVG pictures
  are drawn as vectors.
- Builds on 1.0.0 (2026-10-17, not published): one document model for DOCX, DOTX, DOCM, HTML and Markdown, with styles,
  lists, tables, fields, comments and tracked changes; find, replace and a document builder; page layout with floats,
  columns, headers, footers and footnotes, drawn on any Egami canvas.

## Egap.Pdf.Conversion 1.1.0 (2026-10-18)

- Renamed from Egap.Pdf.Layout, with its namespaces (`Egap.Pdf.Layout` is now `Egap.Pdf.Conversion`). Breaking, before the
  first release.
- Tables from tagged PDFs such as Word's come back with merged and empty cells, tables split across pages joined, and
  tables nested in cells.
- Builds on 1.0.0 (2026-10-17, not published): documents saved as PDF with links, outline and tags; PDF pages analysed into
  blocks, reading order, headings, lists, tables and figures and converted back into documents; auto-tagging; DoclingDocument
  JSON and table CSV output.

## Egap.Fonts 1.0.0 (2026-10-18)

- Liberation Sans, Serif and Mono, Carlito and Caladea (SIL OFL 1.1) as metric-compatible substitutes for Helvetica,
  Arial, Times, Times New Roman, Courier, Courier New, Calibri and Cambria, found by name or alias.
