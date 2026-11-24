# Egap.Documents.Markdown: guide for coding agents

Egap.Documents.Markdown is the Markdown add-on for Egap.Documents: CommonMark 0.31 with GitHub's extensions and YAML front
matter read into the document model, and the model written as GitHub Flavored Markdown, with images, footnotes, task
lists and tables; and Markdown to PDF. Parsing is Markdig's.

Namespaces: `Egap.Documents` (`MarkdownFormat` and its options keep the names they had in Egap.Documents 1.x), and
`Egap.Documents.Pdf` for `PdfConvert.FromMarkdown` and `SaveAsPdf`.

{{shared}}

## Entry points

- `MarkdownFormat.Register()` once at startup: `Document.Load`, `Document.Save` and `PdfConvert.FromFile` then handle
  `.md` and `.markdown`. Calling `MarkdownFormat` or the `Document` members directly needs no registration.
- `Document.LoadMarkdown(path)`, `Document.ParseMarkdown(text)` and `MarkdownFormat.Load(path, MarkdownLoadOptions)`:
  Markdown to a document. `Extensions` (`MarkdownExtensions.GitHub` by default, `None` for plain CommonMark) and
  `ImageResolver` are the options.
- `doc.ToMarkdown()`, `doc.SaveAsMarkdown(path, MarkdownSaveOptions)` and `MarkdownFormat.Save`: a document as Markdown.
  `Images` (`MarkdownImageMode.Files`, `DataUris`, `Placeholders`, `Omit`), `ImagesFolder`, `ImageResolver`,
  `ImageWriter`, `WriteFrontMatter`, `FigureResolution`, `VectorFiguresAsSvg` and `RevisionView` are the options.
- `PdfConvert.FromMarkdown(markdown, outputPath, PdfConvertOptions)`: Markdown text to PDF in one call; with a
  `TableOfContents` it fills a `[TOC]` paragraph.

## Samples

Markdown in and out, and Markdown to PDF:

{{snippet formats/markdown.md#Markdown}}

Choose the extensions read:

{{snippet formats/markdown.md#Reading}}

Write a DOCX as Markdown with front matter and SVG figures:

{{snippet formats/markdown.md#Writing}}

## Common mistakes

- `Document.Load("notes.md")` before `MarkdownFormat.Register()`: it throws a `NotSupportedException` that names this
  package. A `.txt` file loads as plain text, not Markdown.
- Expecting images to be fetched. Nothing is read unless `MarkdownLoadOptions.ImageResolver` returns the bytes; an image
  left unfetched becomes a picture that links to its URL. `PdfConvert.FromFile` reads a file's images from its own
  folder, never from the network.
- Losing pictures on save. Pictures without an address go to a folder beside the file (`<name>.images`) by default; with a
  stream or `ToMarkdown` give an `ImageResolver`, or choose `DataUris` (which GitHub does not show) or `Omit`.
- Ignoring the warnings. Markdown holds less than the model, and each thing left out comes back from
  `SaveAsMarkdown` as a `ConversionWarning`.
- Expecting CRLF or another encoding: Markdown is written as UTF-8 with LF line ends.

## More

- Markdown: https://inexin.github.io/Egap/v2/articles/formats/markdown.html
- PDF export and conversion: https://inexin.github.io/Egap/v2/articles/guides/pdf-export-and-conversion.html
- Every page in one list: https://inexin.github.io/Egap/v2/llms.txt
- API reference: https://inexin.github.io/Egap/v2/api/Egap.Documents.MarkdownFormat.html
