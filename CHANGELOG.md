# Changelog

What each Egap package adds, newest first. From 2.1.0 all Egap packages share one version and release together. Nothing before the first release below was published; those versions were
internal milestones whose public API was frozen and reviewed, and they are listed so the version numbers make sense.

## Egap.Cli 2.2.0 (unreleased)

A new package: the `egap` command, a .NET tool on Egap's public API, on the Egap release train. It shares egami's
conventions and command host (Egami.Cli.Host): globs, output templates, batches, exit codes 0 to 3 and an MCP server.

### Package icon

- The package has its own icon on NuGet: a prompt on a charcoal tile.

### Commands
- `info` (summary, `--all`, `--json`), `text` (reading order, or words with boxes as JSON) and `render` (PNG, JPEG, WebP,
  TIFF at a dpi or fitted).
- `convert` in every direction by the output's extension: HTML, Markdown, DOCX, text and images to PDF with
  `PdfConvertOptions` as flags; PDF to DOCX, HTML, Markdown or text; and the flow formats to each other. HTML resources
  stay in the input's folder unless `--allow-path` or `--allow-network`.
- `merge` (each file with its own pages after a `#`), `split` (per page, per range, every N pages), `pages` (select,
  reorder, delete, rotate, insert blank pages, labels), `impose`, `stamp`, `optimize`.
- `encrypt`, `decrypt`, `metadata show`, `set` and `strip` (with the outline as JSON), `redact`, `sanitize`, `form list`,
  `fill`, `flatten` and `reset`, `attach list`, `add`, `extract` and `remove`, `invoice embed` and `read`.
- `sign` (PFX, PEM or the certificate store; PAdES B-B to B-LTA; visible; certification), `verify`, `compare`,
  `pdfa check` and `convert` (PDF/UA check too).
- Print production: `preflight`, `pdfx check` and `convert`, `separations` (plates and a coverage heat map) and `color`.
- `ocr` runs an OCR program you name (`--engine-command`, such as Tesseract) on each page and adds its words as
  invisible text: `--lang`, `--mode skip|redo|force`, `--pdfa` (2b by default, `none` to leave the level), `--page-jobs`,
  `--dpi`, `--min-confidence`, `--deskew`, `--rotate-pages`, `--engine-timeout` and `--pages`.
- `convert --comments annotations` writes the document's comments as PDF annotations.
- `convert --embed-source` writes a hybrid PDF with the document's DOCX inside; converting such a PDF back uses that
  copy unless `--ignore-embedded-source`.
- `ocr-layer` (hOCR and ALTO), `clean` (grayscale, blank pages, deskew, text to outlines) and `merge-data` (mail merge from
  JSON or CSV).

### Pipelines, batches and assistants
- `run` chains steps with a bare `+`; every edit command is a step, `save` may come more than once, `--dry-run` prints
  the plan, and `--workflow` and `--save-workflow` read and write JSON workflows (`egap-workflow-1.json`).
- Passwords by `--password-env`, `--password-file` or a prompt; edits keep signatures where they can and stop before
  breaking one unless `--allow-invalidating-signatures`.
- `egap mcp`: every command as a tool for AI assistants, inside allowed folders; signing and encryption only with
  `--allow-sign`; `ocr`, which runs a program the call names, never.
- `egap <name>` runs `egap-<name>` from the PATH, so `egap preview` starts egap-preview.

## Egap.Pdf 2.2.0 (unreleased)

### OCR
- `PdfOcr.MakeSearchableAsync` makes scanned PDFs searchable through any `IOcrEngine` from Egami.Ocr (Egami 1.2.0), such as
  `CommandOcrEngine` running Tesseract: the engine's words go over each page as invisible text, along each word's own
  baseline and scaled to its width. A page that is one scanned image is read as its own pixels; other pages are rendered.
- `PdfOcrOptions`: `Skip`, `Redo` and `Force` modes, a page range, languages, render resolution, a confidence floor,
  deskew of the engine's copy, page rotation from the engine, parallel pages with progress, PDF/A in the same pass, and
  page and pixel limits. A signed document is refused unless `AllowInvalidatingSignatures`. `PdfOcrReport` says per page
  what was done, the words added, their mean confidence and the engine's time.
- `PdfOcrLayer.AddResult` adds an engine's result to a page through an image-to-page matrix.
- The glyphless font of OCR layers gives a descent of -1 instead of 0, so text extractors size the words' boxes to the
  words instead of guessing a descent, and its glyph has an outline (never drawn in render mode 3), so PDFium finds rotated
  words, which it dropped before.
- `PdfOcrLayer.AddLines`, `AddHocr` and `AddAlto` add the font to the page's own resources, so a page whose resources are
  inherited, shared with other pages or held by reference no longer fails.
- Egap now builds on Egami 1.2.0.

### Preflight
- `PdfPreflight.Run` lists what a document uses for print, page by page: output intents, colour spaces with their ICC
  profiles and spot inks, images with their effective resolution, fonts, transparency, overprint, hairlines and bleed.
- `PdfPrintProfile` says what a print job accepts (most ink, least image resolution, colour spaces, bleed, thinnest rule,
  embedded fonts, transparency), with presets for PDF/X-4, sheetfed coated, web offset and newspaper. Preflight lists the
  issues that break it as `PdfAIssue` records.

### Separations and ink coverage
- `PdfPage.RenderSeparations` renders a page as ink plates, one per process and spot ink, with a multi-ink renderer:
  overprint, overprint mode, knockout and blend modes act on the inks. `PdfSeparations` measures total ink coverage
  (`MaxCoverage`, `ShareOver`, `CoveragePerPlate`) and draws a heat map (`CoverageMap`). `PdfSeparationOptions` sets the
  press profile and which spot inks get plates.
- Preflight's ink check: `PdfPrintProfile.MaxInkCoverage` is measured on each page's plates and reported.

### Colour conversion
- `PdfColorConverter.Convert` converts a document's RGB, calibrated, Lab and ICC-based colours into a print profile
  (FOGRA39 or the output intent by default) through Egami's colour engine: colour operators, images, shadings, patterns,
  soft masks, Type 3 glyphs and annotations. CMYK and spot colours are kept; `Spots` converts spots and `RetargetCmyk`
  moves CMYK between press profiles. Black and gray text and thin rules go on black ink alone by default.
- `PdfDocument.ConvertToGray` now runs on the same conversion pass, and also converts the page's transparency group space.

### PDF/X
- `PdfXConverter.Check` and `Convert` for PDF/X-1a, X-3, X-4 and X-6 (X-1a and X-3 only without transparency), and checks
  for X-4p, X-5g, X-5pg, X-5n, X-6p and X-6n: output intent, trim and bleed boxes, identification and XMP, `/Trapped`, fonts,
  colour spaces against the output intent, transparency, encryption, external content, transfer functions, annotations
  in the print area, actions and optional content. Reports are `PdfAReport`s.
- `PdfOutputCondition` names registered print conditions (FOGRA39 with Egami's profile, FOGRA51, FOGRA52, GRACoL 2013,
  SWOP 2013, newspaper). A converted document saves whole, unencrypted, as its version's PDF version.

### Overprint preview
- `PdfRenderOptions.SimulateOverprint` renders a page as it prints: through its ink plates, proofed back to sRGB through
  the press profile, so overprint and spot colours show as on press.

### Changed
- DeviceCMYK colours in a document without a CMYK output intent now render through Egami's Coated FOGRA39 profile in
  place of a fitted approximation of it, the same press profile Egap's print tools use: CMYK pages look slightly different. `PdfRenderOptions.DefaultCmykProfile` picks another press profile. CMYK images
  convert a row at a time, and each CMYK colour once.
- Transfer functions in a graphics state (`/TR` and `/TR2`) now change the colours drawn after them: fills, strokes,
  text, images and shadings, and each plate's ink on separations and overprint previews. Files that use them look as on a
  printer; `PdfRenderOptions.ApplyTransferFunctions = false` draws colours as they are, as before.

### PDF/A
- Converting to PDF/A-1b now writes the `/CIDSet` that PDF/A-1 asks of subset CID fonts. Files made without it, such as
  ones made for another level, failed veraPDF after conversion.
- Conversion now fixes more rules: rendering intents PDF does not define become RelativeColorimetric (in graphics
  states, images and the `ri` operator); halftone names and halftone transfer functions on primary colourants are
  removed; form field widgets lose their actions; pages lose `/PresSteps`; ICC profiles that cannot be read are replaced
  by the standard profile with as many components; the catalog's metadata stream gets `/Type /Metadata /Subtype /XML`;
  and XMP on structure elements and other objects is checked and put right like the rest.
- Conversion now checks, and refuses, names longer than 127 bytes, Separation spaces of one name (All included) that
  disagree on their alternate or tint transform, DeviceCMYK reached through a DeviceN colourant's alternate without a
  CMYK output intent, and non-symbolic TrueType fonts that name neither MacRomanEncoding nor WinAnsiEncoding.
- A resource named `AA`, such as a font, is no longer removed as if it were additional actions. Conversion removed it
  and then refused the file, which it had already changed.

## Egap.Preview 2.2.0 (unreleased)

### Package icon

- The tool has its own icon on NuGet: an eye on a deep olive tile.

## Egap.Documents.Html 2.2.0 (unreleased)

### Kashida
- `HtmlLoadOptions.Kashida` turns on kashida for `text-align: justify` paragraphs, at a level you choose. CSS has no
  kashida value, so pages keep the browsers' justification by default.

### EPUB
- `EpubFormat.Load` reads EPUB 2.0.1 and EPUB 3.0 to 3.3 books into one document: a section per chapter starting on a new
  page, read through the HTML reader with the book's style sheets; the cover on a section of its own; links between
  chapters as links inside the document; the table of contents (EPUB 3 navigation document or EPUB 2 NCX) as outline
  levels, so PDF bookmarks follow it; and the metadata as document properties.
- Fonts the book declares with `@font-face` are loaded, with IDPF and Adobe font obfuscation undone. Fixed-layout books
  are read a page per section at the page's viewport size. Audio, video, media overlays and scripts are left out with a
  `MediaDropped` warning.
- Books under DRM (Adobe ADEPT, Readium LCP, Apple FairPlay, any encryption other than font obfuscation) are refused with
  a `DocumentException` that names the scheme. Entries that point outside the book are refused, and `DocumentLimits`
  caps entries, sizes and the compression ratio.
- `EpubFormat.Save` writes an EPUB 3.3 book that EPUBCheck passes: XHTML chapters split at section breaks and level 1
  headings (`EpubSaveOptions.SplitAt`), a navigation document to `NavigationDepth`, a toc.ncx and guide for EPUB 2
  readers, the pictures, accessibility metadata, and with `EmbedFonts` the embedded fonts subset and obfuscated. A book
  read from EPUB keeps its chapter files, ids and identifier.
- `Document.LoadEpub`, `ToEpub` and `SaveAsEpub`; `HtmlFormat.Register()` lets `Document.Load` and `Document.Save` handle
  `.epub`.

### MHTML
- `HtmlFormat.LoadMhtml` reads web archives as Chromium, Edge and Word save them: the page through the HTML reader, its
  pictures, style sheets and fonts from the archive's parts by `Content-Location` or `cid:` URL, in base64 or
  quoted-printable. Nothing outside the archive is fetched unless `ResourceResolver` gives it.
- `HtmlFormat.SaveMhtml`, `ToMhtml` and `SaveAsMhtml` write the archive in Chromium's shape, which Chromium, Edge and Word
  open. `HtmlFormat.Register()` lets `Document.Load` and `Document.Save` handle `.mht` and `.mhtml`.

### Fixes
- A paragraph whose first bookmark is not at its start no longer writes that bookmark twice, as the paragraph's id and as
  an anchor, which gave the page two elements with one id.

## Egap.Documents 2.2.0 (unreleased)

### Formats
- `DocumentFormatKind.Epub` and `DocumentFormatKind.Mhtml`: `Document.Identify` tells an EPUB by its first ZIP entry and
  an MHTML archive by its `multipart/related` headers or its `.mht` and `.mhtml` extension, without the add-on.
- `ConversionWarningKind.MediaDropped` for audio, video, media overlays and scripts a reader left out.

### Kashida justification
- `ParagraphFormat.Kashida` (`KashidaLevel.None`, `Low`, `Medium`, `High`): a justified paragraph stretches its Arabic lines
  with tatweels between letters, the rest of the free width going to word spaces. Built on Egami 1.2.0's kashida layout.
- DOCX reads and writes Word's `lowKashida`, `mediumKashida` and `highKashida` alignments; they were read as plain
  justification before.
- PDF exports leave the inserted tatweels out of the text, so copied and searched text is the words as typed.
- Egap now builds on Egami 1.2.0.

### Comments in PDF
- `PdfExportOptions.Comments = PdfCommentMode.Annotations` writes each comment as a markup annotation: a Highlight over the
  commented text's lines (a Text note where its range is empty) with author, date, text and a popup; replies as Text
  annotations with `/IRT`; resolved comments with a `Completed` review state, or left out with `IncludeResolved = false`.
  Every annotation prints and has an appearance (PDF/A-2b and later hold), and tagged output nests each, popups too, in an
  `Annot` element (PDF/UA-1 holds).
- PDF import reads a highlight's comment onto the words it covers, keeps reply chains and reads the `Completed` state as
  `Comment.Done`, so DOCX to PDF to DOCX keeps author, date, text, range, replies and resolved state.
### Hybrid PDF
- `PdfExportOptions.EmbedSource` and `PdfConvertOptions.EmbedSource` embed a DOCX of the document as the PDF's source (an
  associated file at the catalog's `/AF`, `AFRelationship /Source`). PDF/A-1 and PDF/A-2 throw an `ArgumentException`;
  PDF/A-3 and PDF/A-4f output stays valid.
- `PdfToDocument` and `Document.LoadPdf` open a hybrid PDF's own document instead of analysing its pages when the copy is
  marked as the source, is a DOCX, was embedded by Egap and the PDF has no later revision; otherwise they analyse the
  pages and warn with `PdfToDocumentWarningKind.EmbeddedSourceSkipped`. `PdfToDocumentOptions.EmbeddedSource` is `Prefer`
  (the default), `Ignore` or `Require` (throws a `DocumentException` without a trusted copy).

### Floating tables and frames
- `TableFormat.Position` (`TablePosition`: anchors, alignment or offsets, distances from the text, `AllowOverlap`) makes a
  floating table: placed like a floating picture, text wrapping on the side with more room, and continuing at the top of
  the next page's body when it does not fit, header rows repeated and rows splitting between their lines. The text after a
  table that breaks follows it on the page where it ends, unless `AllowTextAfterFloatingTableBreak`;
  `DoNotBreakWrappedTables` keeps it whole, and `AllowOverlap = false` moves it below the floating tables before it.
- `ParagraphFormat.Frame` (`FramePosition`) makes an old-style Word frame: the paragraph, with the ones after it that share
  the frame, is placed by the same rules and never split. A frame with `DropCap.Drop` or `DropCap.Margin` is a drop cap
  beside the next paragraph.
- DOCX reads and writes `w:tblpPr`, `w:tblOverlap` and `w:framePr` through the model; a missing horizontal anchor is the
  text's, as Word reads it. Floating tables and frames inside cells, headers, footers, notes and text boxes are laid out
  in the text.
- HTML reads `float: left` and `float: right` on a table as a floating table and writes it back; the report API's
  `TableBuilder.Floating` floats a report table.
- A table nested in a cell now breaks between its rows when the cell breaks across pages, instead of moving whole.
- Text that wraps around a float now honours a page or column break that ends its paragraph.

### Word compatibility options
- `DocumentSettings.WordCompatibility` names the Word compatibility options layout acts on, each a flag that follows
  `CompatibilityMode` unless the file or code sets it: `AllowSpaceOfSameStyleInTable`,
  `DoNotUseIndentAsNumberingTabStop`, `SplitPageBreakAndParagraphMark`, `OverrideTableStyleFontSizeAndJustification`,
  `EnableOpenTypeFeatures`, `DoNotBreakWrappedTables`, `AllowTextAfterFloatingTableBreak` and `TableIndentFromCellText`.
  `OtherSettings` lists the options read but not acted on, and `UseModeDefaults` forgets the set values.
- DOCX reads the flags from `w:compat` and writes the ones set in code, in schema order; options read from a file go back
  unchanged. A new document writes Word 2013's four compatibility settings.
- Layout follows the flags: files older than Word 2010 keep the table style's font size and alignment over an 11 or 12
  point default paragraph style and are shaped without the runs' OpenType features, as Word does. Contextual spacing now
  applies in table cells and in flex and grid items as in the body, unless `AllowSpaceOfSameStyleInTable` is on in a cell.
  Floating tables use `DoNotBreakWrappedTables` and `AllowTextAfterFloatingTableBreak` once they are laid out as floats.

### Scanned pages
- `Document.LoadPdfAsync` and `PdfToDocument.ConvertAsync` run scanned pages through `PdfToDocumentOptions.OcrEngine`, so
  their text becomes paragraphs instead of a picture; `PdfToDocumentOptions.Ocr` sets the languages, resolution and the
  rest. The sync `LoadPdf` and `Convert` keep a scanned page as a picture, as before.
- `PdfToDocumentWarningKind.OcrFailed` when the engine fails on a page, which stays a picture.

## Egap.Documents 2.1.0 (2026-10-22)

### Reports in code
- `Report.Create` builds a document in code (`Egap.Documents.Reports`): pages with size and margins whose `Header()`,
  `Content()` and `Footer()` return the containers to fill; containers with box, alignment, text style and paging settings; and text with spans and page
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
- A flex line or grid row too tall for the room left breaks inside its items, between lines of text and between table
  rows, each item's part open at the break; a line or row that does not start before the foot of the page moves on whole.
- Layout gives `ContainerBox` and `ItemBox` boxes for them; `DocumentVisitor` has start and end methods for each.
- DOCX saves a container as a borderless table with the items in its cells, spans and merged cells for a grid, and a
  boxed group as a one-cell table, with the items' backgrounds, borders and padding. Plain text lists the items' text,
  and tagged PDF tags a container and each item as a `Div`.

### Line spacing
- `LineSpacingRule.AtLeastCentered`: at least the given height, the extra space shared above and below the text as CSS's
  line-height shares it. DOCX saves it as at least.

### Shapes and text boxes
- `Shape.CornerRadius`, `Shape.Shadow`, `Shape.TextInsets` and `Shape.FitToText`: rounded rectangles of any radius, a
  blurred shadow, a text box's own insets, and a text box that takes the size of its text. DOCX reads and writes them
  (the roundRect adjustment, an outer shadow, the body insets and shape auto-fit). A rounded rectangle without a radius
  is drawn with Word's default corners instead of square ones.

### Speed
- A generated report lays out, exports and saves faster: table cells, paragraphs and the table style levels a cell takes
  from its style are resolved with fewer allocations, a paragraph keeps its laid-out text and its widths for the next pass,
  and tables, flex and grid containers keep their plans from one pass to the next.
- An SVG picture drawn on every page, such as a logo or a barcode in a header, is read and drawn once; a PDF writes it once
  as a form and shows it on every page.

### Fixes
- DOCX: Word 2010 run properties are written in the schema's order, text effects such as `w14:shadow` before font
  features such as `w14:numForm`, so Word's validator no longer reports an unexpected element in a saved file.

## Egap.Preview 2.1.0 (2026-10-22)

The first release, at the version every Egap package now shares.

### A live preview tool
- `egap-preview`, a dotnet tool, shows documents on a local web page and reloads it when new pages arrive: the pages a
  program sends with `ShowPreview()`, so a report run under `dotnet watch` follows every edit, or a DOCX file, drawn
  again whenever it is saved; HTML and Markdown are shown from code with their add-on and `ShowPreview()`. A strict layout's failure shows on the page. It listens on 127.0.0.1
  only, port 12501 unless `--port` says otherwise, refuses requests from other web pages, and needs nothing but Egap's packages.

## Egap.Documents.Html 2.1.0 (2026-10-22)

### CSS layout
- `display: flex` and `display: grid` read into flex and grid containers with their items, instead of stacked blocks
  with a warning: direction, wrapping, grow, shrink, basis, order, justify and align settings, gaps, track lists with
  `fr`, `minmax()`, `fit-content()` and `repeat()`, named areas, line and span placement, and automatic placement.
- A block with `border-radius` or `box-shadow` becomes a group with a box: background, borders, padding, rounded
  corners and the first outer shadow, drawn around its blocks.
- `position: absolute` and `fixed` boxes become text boxes floating in front of the text, placed by `top`, `right`,
  `bottom` and `left`, instead of staying in the flow with a warning; a fixed box shows on every page.
- HTML writing writes flex and grid containers, boxed groups and floating text boxes back as the same CSS.

### Closer to browsers
- `line-height` shares its extra space above and below the text, as browsers do, instead of putting it all above.
- A table keeps the space above it; a cell's top and bottom borders add to its height (half of each where borders
  collapse); an empty box with a border (a rule) is as thin as its border; and a page break before or after a table, a
  flex or grid container or a box breaks there.
- A flex or grid item keeps its bottom margin and its last child's, as browsers keep them inside the item.

### Fixes
- A bookmark or a footnote mark inside a hyperlink no longer writes a link inside the link, which HTML does not allow and
  which made browsers end the link early. The bookmark is written as `<span id>`, and the note mark keeps its id without a
  link of its own.

## Egap.Documents.Markdown 2.1.0 (2026-10-22)

### Flex and grid containers
- Flex and grid containers are written as their items' content in order, with a warning.

## Egap.Pdf 2.1.0 (2026-10-22)

From this release every Egap package shares one version, so Egap.Pdf moves from 1.5.0 to 2.1.0 with Egap.Documents. Nothing
was removed or changed in a way that breaks code: 2.1.0 is 1.5.0 plus what is below.

### Speed
- Content streams write coordinates faster, with the same text as before.

### Separations and ink coverage
- `PdfPage.RenderSeparations` renders a page as ink plates, one per process and spot ink, with a multi-ink renderer:
  overprint, overprint mode, knockout and blend modes act on the inks. `PdfSeparations` measures total ink coverage
  (`MaxCoverage`, `ShareOver`, `CoveragePerPlate`) and draws a heat map (`CoverageMap`). `PdfSeparationOptions` sets the
  press profile and which spot inks get plates.
- Preflight's ink check: `PdfPrintProfile.MaxInkCoverage` is measured on each page's plates and reported.

### Colour conversion
- `PdfColorConverter.Convert` converts a document's RGB, calibrated, Lab and ICC-based colours into a print profile
  (FOGRA39 or the output intent by default) through Egami's colour engine: colour operators, images, shadings, patterns,
  soft masks, Type 3 glyphs and annotations. CMYK and spot colours are kept; `Spots` converts spots and `RetargetCmyk`
  moves CMYK between press profiles. Black and gray text and thin rules go on black ink alone by default.
- `PdfDocument.ConvertToGray` now runs on the same conversion pass, and also converts the page's transparency group space.

### PDF/X
- `PdfXConverter.Check` and `Convert` for PDF/X-1a, X-3, X-4 and X-6 (X-1a and X-3 only without transparency), and checks
  for X-4p, X-5g, X-5pg, X-5n, X-6p and X-6n: output intent, trim and bleed boxes, identification and XMP, `/Trapped`, fonts,
  colour spaces against the output intent, transparency, encryption, external content, transfer functions, annotations
  in the print area, actions and optional content. Reports are `PdfAReport`s.
- `PdfOutputCondition` names registered print conditions (FOGRA39 with Egami's profile, FOGRA51, FOGRA52, GRACoL 2013,
  SWOP 2013, newspaper). A converted document saves whole, unencrypted, as its version's PDF version.

### Overprint preview
- `PdfRenderOptions.SimulateOverprint` renders a page as it prints: through its ink plates, proofed back to sRGB through
  the press profile, so overprint and spot colours show as on press.

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
