# Egap.Documents.Html: guide for coding agents

Egap.Documents.Html is the HTML add-on for Egap.Documents: HTML and XHTML read into the document model, styled through
the CSS cascade (flexbox, grid, positioned boxes, `@page` rules and margin boxes), and written back; HTML to PDF, HTML
stamps and page bands; MHTML web archives; and EPUB 2 and 3 books read and EPUB 3.3 written. Parsing is AngleSharp's and
the cascade Egami.Css's. Scripts never run.

Namespaces: `Egap.Documents` (`HtmlFormat`, `EpubFormat` and their options keep the names they had in Egap.Documents 1.x),
and `Egap.Documents.Pdf` for `PdfConvert`, `PdfStamp.Html` and `PageBand.Html`.

{{shared}}

## Entry points

- `HtmlFormat.Register()` once at startup: `Document.Load`, `Document.Save` and `PdfConvert.FromFile` then handle `.html`,
  `.htm`, `.xhtml`, `.mht`, `.mhtml` and `.epub`. Calling the format classes directly needs no registration.
- `Document.LoadHtml(path)`, `Document.ParseHtml(html)` and `HtmlFormat.Load(path, HtmlLoadOptions)`: HTML to a document.
- `doc.ToHtml()`, `doc.SaveAsHtml(path)` and `HtmlFormat.Save(doc, path, HtmlSaveOptions)`: a document as HTML
  (`EmbedImages`, `ImageWriter`, `WritePageSetup`, `RevisionView`).
- `PdfConvert.FromHtml(html, outputPath, PdfConvertOptions)`: HTML text to PDF, with `PageSize`, `Margins`, `Header`,
  `Footer` (`PageBand.Html`, `PageBand.Text`), `TableOfContents` and `Conformance`.
- `HtmlFormat.LoadMhtml(path)` and `doc.SaveAsMhtml(path)`: web archives.
- `Document.LoadEpub(path)`, `EpubFormat.Load`, and `doc.SaveAsEpub(path, EpubSaveOptions)`: e-books.
- `PdfStamp.Html(html, width)`: a piece of HTML laid out as a stamp on PDF pages.
- `HtmlLoadOptions`: `BaseUri`, `ResourceResolver`, `Encoding`, `Media` (`"print"` or `"screen"`), `UserStyleSheets`,
  `Kashida` and `Limits`.

## Samples

HTML in and out, and HTML to PDF:

{{snippet formats/html.md#HTML}}

Let a page load its images and style sheets:

{{snippet formats/html.md#Reading}}

E-books:

{{snippet formats/epub.md#EPUB}}

Web archives:

{{snippet formats/mhtml.md#MHTML}}

An HTML invoice as a Factur-X PDF/A-3:

{{snippet guides/redaction-and-pdfa.md#Turn an HTML invoice into a Factur-X PDF/A-3}}

## Common mistakes

- `Document.Load("page.html")` before `HtmlFormat.Register()`: it throws a `NotSupportedException` naming this package.
  Register once at startup, or call `HtmlFormat` and the `Document.LoadHtml` members directly.
- Expecting images, fonts and style sheets from the web. Nothing outside the page is fetched unless `ResourceResolver`
  returns it; `data:` URLs always work. `PdfConvert.FromFile` reads from the file's own folder (or
  `PdfConvertOptions.BaseDirectory`), never from the network. Keep it that way for HTML from users.
- HTML given as text has no folder: relative images load only from `BaseDirectory` (or `BaseUri` with a resolver).
- Expecting screen styles. Style sheets apply for print media by default; set `Media = "screen"` for the screen rules.
- Expecting JavaScript output. Scripts never run, so content that scripts build is missing; render the page's final HTML
  first.
- Expecting every CSS layout feature: `position: sticky`, subgrid, masonry, named grid lines and inline flex and grid
  boxes are not laid out as such.
- Deep or huge pages: `HtmlLoadOptions.Limits` (`DocumentLimits`) bounds page size and nesting; deeper elements are kept as
  plain text and a page past a limit throws a `DocumentLimitException`.
- Books under DRM are refused when an EPUB is loaded.

## More

- HTML: https://inexin.github.io/Egap/v2/articles/formats/html.html
- EPUB: https://inexin.github.io/Egap/v2/articles/formats/epub.html
- MHTML: https://inexin.github.io/Egap/v2/articles/formats/mhtml.html
- PDF export and conversion: https://inexin.github.io/Egap/v2/articles/guides/pdf-export-and-conversion.html
- Every page in one list: https://inexin.github.io/Egap/v2/llms.txt
- API reference: https://inexin.github.io/Egap/v2/api/Egap.Documents.HtmlFormat.html
