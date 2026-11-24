## Egap's vocabulary

Egap has its own names. Use them, not names from other PDF, Word or Excel libraries: there is no `PdfReader`, `PdfWriter`,
`XWPFDocument`, `Range.Text` or `IWorkbook` in Egap.

- `PdfDocument` (namespace `Egap.Pdf`) is a PDF file: `PdfDocument.Open`, `PdfDocument.Create`, `PdfDocument.Merge`, then
  `Pages`, `Metadata`, `Outline`, `Form`, `Attachments`, `Signatures` and `Save`. It is `IDisposable` and keeps its file open
  until disposed.
- `Document` (namespace `Egap.Documents`) is a flow document: one model for DOCX, DOC, RTF, ODT, plain text, PDF, HTML,
  MHTML, EPUB and Markdown. `Document.Load` and `Save` pick the format; `DocumentBuilder` edits at a cursor. It is not
  disposable.
- `Workbook` and `Worksheet` (namespace `Egap.Spreadsheets`) are an Excel workbook and its sheets; `sheet["B2"]` is a
  `Cell`, `sheet.Range("A1:C9")` a `CellRange`, and `CellValue` a cell's value.
- Each format has its own class (`DocxFormat`, `RtfFormat`, `OdtFormat`, `TextFormat`, `HtmlFormat`, `EpubFormat`,
  `MarkdownFormat`) and members on `Document` that forward to it: `Document.LoadDocx`, `SaveAsDocx`, `ToHtml`,
  `SaveAsMarkdown`. `SaveAsPdf` and `PdfConvert` (namespace `Egap.Documents.Pdf`) write PDF.
- Options are objects with `init` properties, set in an object initializer and passed to the call: `PdfReadOptions`,
  `PdfSaveOptions`, `PdfRenderOptions`, `PdfExportOptions`, `PdfConvertOptions`, `LayoutOptions`, `WorkbookLoadOptions`.
- Damaged input is repaired and reported, not thrown: `pdf.Warnings` (`PdfWarning`), a `Load` overload with
  `out IReadOnlyList<ConversionWarning>`, and the list `Document.Save` returns. A `Strict` option throws instead.
- Sizes are PDF points (1/72 inch). `Length.Mm(15)`, `Length.Cm`, `Length.In` and `Length.Pt` (namespace `Egap.Pdf`) turn
  into points by themselves. Page indexes in code count from 0; `PdfPageRange.Parse("1-3,z")` takes page numbers from 1.
- Images, fonts and drawing come from Egami: a page renders to an Egami `Raster<Rgba8>` (namespaces `Egami` and
  `Egami.Pixels`), and draws onto any Egami.Drawing `Canvas`.
- Egap never opens a network connection by itself: the resolver, base folder or `HttpClient` you pass decides what a
  document may fetch.
