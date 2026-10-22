# Changelog

What each Egap package adds, newest first. Nothing before the first release below was published; those versions were
internal milestones whose public API was frozen and reviewed, and they are listed so the version numbers make sense.

## Egap.Documents 2.1.0 (unreleased)

### Reports in code
- `Report.Create` builds a document with nested lambdas (`Egap.Documents.Reports`): pages with size, margins, header,
  footer and content; containers with box, alignment, text style and paging settings; and text with spans and page
  numbers, pictures, barcodes, columns, rows (flex), grids, tables with repeating header and footer rows, rules, page
  breaks, form fields and reusable `IComponent`s. `TextStyle` chains are immutable and shared; named styles are
  character styles in the document. The same report saves as PDF, DOCX and HTML.
- A report lays out strictly with portable fonts. `Document.DefaultLayoutOptions` holds that, and is used wherever a
  layout or export is asked for without options.
- `ShowPreview()` sends a document's pages, drawn by Egap's renderer, to the egap-preview tool on this machine
  (`PreviewOptions`: port, resolution, layout options, name); it returns false when the tool is not running.

### Flex and grid layout
- `FlexContainer` and `FlexItem`: items in a row or a column that grow and shrink to share the space, wrap onto more
  lines, and line up by `FlexLayout`'s justify and align settings and gaps. Layout breaks pages between lines.
- `GridContainer` and `GridItem`: column and row tracks in points, per cent, fractions or sized to their content
  (`GridTrack`, `GridTrackSize`), items placed by line, span or named area, the rest by automatic placement, dense or not.
  Layout breaks pages between rows and keeps rows an item spans together.
- `BoxStyle` draws a box around a container, an item or a group of blocks (`ContentControl.Box`): margins, padding,
  borders, background, rounded corners (`BoxCorners`), a shadow (`BoxShadow`), width and least height. A boxed group
  breaks across pages, drawn open at the break. `LayoutAlignment` names the alignments.
- Layout gives `ContainerBox` and `ItemBox` boxes for them; `DocumentVisitor` has start and end methods for each.
- DOCX saves a container as a borderless table with the items in its cells, spans and merged cells for a grid, and a
  boxed group as a one-cell table, with the items' backgrounds, borders and padding. Plain text lists the items' text,
  and tagged PDF tags a container and each item as a `Div`.

### Shapes and text boxes
- `Shape.CornerRadius`, `Shape.Shadow`, `Shape.TextInsets` and `Shape.FitToText`: rounded rectangles of any radius, a
  blurred shadow, a text box's own insets, and a text box that takes the size of its text. DOCX reads and writes them
  (the roundRect adjustment, an outer shadow, the body insets and shape auto-fit). A rounded rectangle without a radius
  is drawn with Word's default corners instead of square ones.

## Egap.Preview 1.0.0 (unreleased)

### A live preview tool
- `egap-preview`, a dotnet tool, shows documents on a local web page and reloads it when new pages arrive: the pages a
  program sends with `ShowPreview()`, so a report run under `dotnet watch` follows every edit, or a DOCX, HTML or
  Markdown file, drawn again whenever it is saved. A strict layout's failure shows on the page. It listens on 127.0.0.1
  only, port 12500 unless `--port` says otherwise, and needs nothing but Egap's packages.

## Egap.Documents.Html 2.1.0 (unreleased)

### CSS layout
- `display: flex` and `display: grid` read into flex and grid containers with their items, instead of stacked blocks
  with a warning: direction, wrapping, grow, shrink, basis, order, justify and align settings, gaps, track lists with
  `fr`, `minmax()`, `fit-content()` and `repeat()`, named areas, line and span placement, and automatic placement.
- A block with `border-radius` or `box-shadow` becomes a group with a box: background, borders, padding, rounded
  corners and the first outer shadow, drawn around its blocks.
- `position: absolute` and `fixed` boxes become text boxes floating in front of the text, placed by `top`, `right`,
  `bottom` and `left`, instead of staying in the flow with a warning; a fixed box shows on every page.
- HTML writing writes flex and grid containers, boxed groups and floating text boxes back as the same CSS.

## Egap.Documents.Markdown 2.1.0 (unreleased)

### Flex and grid containers
- Flex and grid containers are written as their items' content in order, with a warning.

## Egap.Documents 2.0.0 (2026-10-21)

### Docs
- A migration guide, "Moving to Egap.Documents 2.0", maps old packages and namespaces to new ones; the packages page,
  getting started, the flow documents and PDF conversion guides and the package READMEs describe the four packages.
- Each document format has its own page (DOCX, HTML, Markdown, plain text, Docling JSON) with its options, under a new
  Document formats section. The guides are grouped by package in alphabetical order, with an index page, and the
  cookbook's recipes moved to the end of their guides.

### Markdown and HTML move out
- Breaking: Markdown is now the Egap.Documents.Markdown package. Add it, and call `MarkdownFormat.Register()` where
  `Document.Load`, `Document.Save` or `PdfConvert.FromFile` should handle Markdown.
- Breaking: HTML is now the Egap.Documents.Html package, and Egap.Documents no longer depends on Egami.Css. Add it, and
  call `HtmlFormat.Register()` where `Document.Load`, `Document.Save`, `PdfConvert.FromFile` or a `PageBand.Html` band
  outside `PdfConvert.FromHtml` should handle HTML. Without it they throw a `NotSupportedException` that names the package.

### Formats
- Breaking: `Document.Load` and `Document.Save` read and write HTML and Markdown only after `HtmlFormat.Register()` and
  `MarkdownFormat.Register()`. Without the call they throw a `NotSupportedException` that names the package and the call.
- Breaking: a `.txt` file loads as plain text, one paragraph per line, instead of as Markdown. Text without an extension
  that does not start with a tag still loads as Markdown.
- `Document.Identify` tells PDF, DOCX, HTML, Markdown, plain text and Docling JSON apart without loading the file and
  returns a `DocumentFormatKind`.
- `TextFormat.Parse` and `TextFormat.Load` read plain text: a paragraph per line, tabs and form feeds kept as tabs and
  page breaks.
- `Document.Load` reads PDF (as `PdfToDocument` does) and Docling JSON, and `Document.Save` writes `.pdf` (as `SaveAsPdf`
  does).
- Each core format reads as members of `Document`: `Document.LoadDocx`, `SaveAsDocx`, `Document.LoadText`,
  `Document.ParseText`, `ToText`, `SaveAsText` and `Document.LoadPdf`, forwarding to `DocxFormat`, `TextFormat` and
  `PdfToDocument`. `TextFormat.ToText(document)` still works.

### Egap.Pdf.Conversion folded in
- Breaking: everything Egap.Pdf.Conversion held now ships in Egap.Documents, which references Egap.Pdf: PDF export
  (`SaveAsPdf`, `ToPdf`, `PdfExportOptions`), `PdfConvert`, PDF to document (`PdfToDocument`), layout analysis
  (`PdfLayoutAnalyzer`), tagging (`PdfAutoTagger`), `TableCsv`, `PageBand` and `HtmlStamp`. These types move from
  namespace `Egap.Pdf.Conversion` to `Egap.Documents.Pdf`; the Docling types (`DoclingJson`, `DoclingJsonOptions`,
  `DoclingImage`, `DoclingImageMode`) move to `Egap.Documents`. Replace the package reference and the using line.
  Egap.Pdf.Conversion gets no new version.

### Barcodes
- `DocumentBuilder.InsertBarcode` writes a barcode as an SVG picture, drawn as vectors by layout and PDF export, with a PNG
  stand-in for Word.
- `Picture.Fallback` holds a raster stand-in for an SVG picture; DOCX reads and writes Word's SVG pictures (the SVG in an
  extension beside its PNG) with it.

### Form controls
- HTML inputs, check boxes, radio buttons, selects and text areas are read as inline content controls holding their values,
  instead of being dropped.
- `ContentControlProperties.Items` holds a drop-down list's or combo box's items, read from and written to DOCX;
  `RadioGroup` ties check boxes into a set of options.

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
- `GroupPaging.ScaleToFit` draws a group smaller (a `ScaledBox`) when that makes it fit where it starts.
- `ParagraphFormat.MaxLines` clamps a paragraph with an ellipsis (HTML `-webkit-line-clamp`); `RowFormat.IsFooter` repeats a
  table's last rows at the bottom of each page it continues from; `PageSetup.BalanceColumns` evens out the columns of a
  section's last page.

### Margin boxes
- `Section.MarginBoxes` holds content for the sixteen page margin boxes of CSS (`MarginBoxPosition`: the corners and
  three boxes along each edge), for every page or only the first, with PAGE and NUMPAGES fields for page numbers.
  `DocumentBuilder.MoveToMarginBox` writes into one. Layout draws them in the margins beside the headers and footers.
- HTML reads and writes `@page` margin boxes (`@bottom-center { content: "Page " counter(page) " of " counter(pages) }`),
  `@page :first` included. DOCX writes the top and bottom boxes as a three-part table in the header or footer, and
  `DocxFormat.Save` now returns a warning for the corner and side boxes it leaves out.
- HTML running elements: an element with `position: running(name)` leaves the body and shows in the margin boxes whose
  content is `element(name)`.
- PAGE, NUMPAGES and SECTIONPAGES fields in headers, footers and margin boxes follow their `\#` number format and `\*`
  formats such as roman numerals.

### Tables of contents
- `DocumentBuilder.InsertTableOfContents` writes a TOC field and builds it; `Field.Update` recomputes one field.
- PAGEREF fields show the page their bookmark is on, filled by layout, so a table of contents gets its page numbers.
- HTML `<nav data-toc>` is read as a table of contents field.

### Mail merge
- `Document.FillMergeFields` fills MERGEFIELD fields from an `IMergeData` record with their `\#`, `\@`, `\*`, `` and
  `` switches, then works out IF fields; fields without a value are removed with a `MergeFieldsRemoved` warning.
  `MailMergeOptions` sets the culture and can remove paragraphs left empty.
- `MergeData` makes records from dictionaries, rows with column names and objects (trimming-safe), with dotted names for
  nested records.
- Repeated regions, between `TableStart:Name` and `TableEnd:Name` merge fields (rows of one table, or blocks of one story)
  or in a table row of `Name.Field` fields, nested and reading what a record lacks from the record around it; a region
  with no records is removed with a warning.
- `Document.MailMerge` makes one document per record, lazily; `MailMergeAll` makes one document of all records, separated
  by `MailMergeOptions.Separator`, with warnings through `OnWarning`.

### Fixes
- HTML: a space after an inline element at the start of a paragraph (`<b>ACME</b> Ltd`) was lost.
- HTML: the `<title>` and the author meta element are read into the document properties, as the writer writes them.
- DOCX: a name or text holding characters XML cannot (such as a control character in an HTML id) no longer fails the
  save; those characters are left out.
- Layout: a page larger than 14 400 points on a side (PDF's limit) is laid out at that size instead of running out of
  memory.

### Strict layout and portable fonts
- `LayoutOptions.Strict` throws a `LayoutException` naming the node, the page and the `LayoutProblem` (text or content
  running past its area, a missing glyph, a font with no stand-in) instead of degrading.
- `FontSettings.Portable` uses only embedded fonts and the bundled substitutes, so layout and, with a deterministic save,
  the PDF bytes are the same on every machine.

### Form fields
- `PdfExportOptions.FormFields` turns HTML form controls and DOCX content controls (plain text, date, drop-down list, combo
  box, check box) into PDF form fields at their laid-out place, named by the control's name or tag, with its value.

### One-line conversions
- `PdfConvert` converts HTML, Markdown, DOCX and image files to PDF in one call, with the page size, margins, PDF/A level and
  tagging as options. HTML and Markdown read their images from their own folder only.
- `PdfStamp.Html` makes a stamp of laid-out HTML, such as a seal or a letterhead.

### Headers and footers
- `PdfConvertOptions.Header` and `Footer` put a `PageBand` on every page in place of the input's own: text at the left,
  middle and right (`PageBand.Text`), or HTML styled with the page's own style sheets (`PageBand.Html`), with the
  placeholders `{page}`, `{total-pages}`, `{label}`, `{date}`, `{time}`, `{title}` and `{file}`.
- Margin boxes are tagged as artifacts in tagged PDF, as headers and footers are.
- `PdfConvertOptions.TableOfContents` builds a table of contents from the headings, with or without page numbers, at
  HTML's `<nav data-toc>`, a Markdown `[TOC]` paragraph or the start; its entries link to the headings.

## Egap.Documents.Html 2.0.0 (2026-10-21)

### Trimming and Native AOT
- The package is marked trimmable and AOT compatible: a Native AOT app that uses all of it, with AngleSharp, publishes
  without a trim or AOT warning and runs.

### A package of its own
- HTML reading and writing moves out of Egap.Documents into this package, with the CSS cascade (Egami.Css), `@page` rules
  and margin boxes. `HtmlFormat`, `HtmlLoadOptions` and `HtmlSaveOptions` keep their names and namespace
  (`Egap.Documents`), and `HtmlStamp` keeps `Egap.Documents.Pdf`, so code compiles once the package is added.
- `HtmlFormat.Register()` lets `Document.Load`, `Document.Save` and `PdfConvert.FromFile` handle `.html` files and
  `PageBand.Html` bands.
- HTML as members of `Document`: `Document.ParseHtml`, `Document.LoadHtml`, `ToHtml` and `SaveAsHtml`.
  `HtmlFormat.ToHtml(document)` still works.
- `PdfConvert.FromHtml` and `PdfStamp.Html` come from this package as extension members, so the calls compile unchanged.
  `PdfConvert.FromHtml` lays out HTML bands without `Register()`.

### AngleSharp
- HTML is parsed by [AngleSharp](https://github.com/AngleSharp/AngleSharp) (MIT) instead of Egap's own tokenizer and tree
  builder, which are gone, with the entity table. The package depends on AngleSharp 1.8.5 or later. AngleSharp fetches
  nothing: images and style sheets still come only through `HtmlLoadOptions.ResourceResolver`.
- `HtmlLoadOptions.Encoding` is now the fallback for bytes without a byte order mark or a meta charset, as its
  documentation said; before, it also overrode a meta charset.
- Pages nested more than 4,096 elements deep, which only hostile input has, are read with the deeper start tags left out
  and all text kept, so they take no longer to read than a flat page.
- Loading speed against Egap.Documents 1.x's own parser, same machine and session: within 2 % for text and 1 KB
  pages, 11 % faster for 900 KB pages read from bytes and up to 6 % slower for 45 KB ones, with 1 to 2 % less memory.

## Egap.Documents.Markdown 2.0.0 (2026-10-21)

### Trimming and Native AOT
- The package is marked trimmable and AOT compatible: a Native AOT app that uses all of it, with Markdig, publishes
  without a trim or AOT warning and runs.

### A package of its own
- Markdown reading and writing moves out of Egap.Documents into this package. `MarkdownFormat`, `MarkdownLoadOptions`,
  `MarkdownSaveOptions`, `MarkdownExtensions`, `MarkdownImage` and `MarkdownImageMode` keep their names and namespace
  (`Egap.Documents`), so code compiles once the package is added.
- `MarkdownFormat.Register()` lets `Document.Load`, `Document.Save` and `PdfConvert.FromFile` handle `.md` files.
- Markdown as members of `Document`: `Document.ParseMarkdown`, `Document.LoadMarkdown`, `ToMarkdown` and `SaveAsMarkdown`.
  `MarkdownFormat.ToMarkdown(document)` still works.
- `PdfConvert.FromMarkdown` comes from this package as an extension member, so the call compiles unchanged.

### Markdig
- Markdown is parsed by [Markdig](https://github.com/xoofx/markdig) (BSD-2-Clause) instead of Egap's own parser, which is
  gone. The package depends on Markdig 1.4.1 or later. Every CommonMark 0.31 and GFM table, strikethrough and autolink spec
  example still reads as before; GitHub's email autolinks and parentheses in links are added on top of Markdig's.
- Markdown nested hundreds of levels deep (quotes, lists or brackets), which Markdig refuses, is read as plain text with a
  `FormattingDropped` warning.
- The writer puts a list item's first block on the marker line, such as a code fence right after `1.`, and writes a
  no-break space at the start of a line as `&#160;`, so other readers read the output as meant.
- Loading speed against Egap.Documents 1.x's own parser, same machine and session: 1 KB files take 22 % longer (94 to
  115 us) with 13 % less memory; 45 KB files 2 % longer with 11 % less memory; 900 KB files 4 % longer with 2 % more
  memory. Design Q7 accepts up to 25 %.

## Egap.Pdf 1.5.0 (2026-10-19)

### Barcodes
- `PdfStamp.Barcode` stamps any symbol of Egami.Barcodes (QR, Data Matrix, PDF417, Aztec, Code 128, Code 39, ITF, EAN and
  UPC) as vector paths in the stamp's colour. Egap.Pdf now depends on Egami.Barcodes.

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
