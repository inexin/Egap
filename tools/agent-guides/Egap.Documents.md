# Egap.Documents: guide for coding agents

Egap.Documents is one editable document model for Word-style flow documents: DOCX, DOC (read), RTF, OpenDocument Text,
plain text, Docling JSON and PDF, with HTML, MHTML, EPUB and Markdown from the add-on packages. It edits documents, lays
them out into pages drawn on any Egami canvas, exports PDF (tagged, PDF/A), turns PDF pages back into documents, compares
versions, fills mail merges and builds reports from code.

Namespaces: `Egap.Documents` (the model and formats), `Egap.Documents.Pdf` (`SaveAsPdf`, `PdfConvert`, `PdfToDocument`),
`Egap.Documents.Layout` (`Layout`, `LayoutOptions`), `Egap.Documents.Comparing`, `Egap.Documents.Reports` and
`Egap.Documents.Charts`.

{{shared}}

## Entry points

- `Document.Load(path | stream)` reads any built-in format, told apart by content; `Document.Identify(path)` gives the
  `DocumentFormatKind` without loading, and `Document.IsEncrypted(path)` says whether it needs a password
  (`DocumentLoadOptions.Password` or `PasswordCallback`).
- `doc.Save(path)` writes by extension and returns the `ConversionWarning`s for what the format could not hold. Format
  classes give options: `DocxFormat`, `RtfFormat.Save`, `OdtFormat.Save`, `TextFormat`, and `doc.SaveAsDocx`, `ToText`.
- `new Document()`, `doc.AddSection().Body.AddParagraph(...)` and `AddTable(rows, columns)` build a tree;
  `new DocumentBuilder(doc)` writes at a cursor (`Write`, `Writeln`, `StartTable`, `InsertCell`, `EndRow`, `EndTable`,
  `InsertImage`, `InsertField`, `InsertTableOfContents`, `Font`, `ParagraphFormat`).
- `doc.Descendants<Paragraph>()`, `node.GetText()`, `doc.Find`, `doc.Replace`, `doc.Sections`, `doc.Styles`,
  `doc.Revisions` (`AcceptAll`, `RejectAll`), `doc.Protection`, `doc.Properties`.
- `doc.SaveAsPdf(path, new PdfExportOptions { Tagged = true, Conformance = ... })` and `doc.ToPdf()`: PDF export.
- `PdfConvert.FromFile`, `FromImages`: one-call conversions to PDF; `PdfToDocument.Convert(pdf)` and `Document.LoadPdf`:
  PDF to a document.
- `doc.Layout(LayoutOptions)` gives a `DocumentLayout`; each page `Render(dpi)`s to a raster or `Draw`s on a canvas.
- `doc.FillMergeFields`, `MailMerge`, `MailMergeAll` with `MergeData.From(...)`; `DocumentCompare.Run(original, revised)`;
  `Report.Create(r => ...)`.

## Samples

Load, edit and save:

{{snippet guides/flow-documents.md#Flow documents}}

DOCX to tagged PDF:

{{snippet getting-started.md#Turn a Word file into PDF}}

Pages as images:

{{snippet guides/flow-documents.md#Page layout}}

PDF back to DOCX:

{{snippet guides/pdf-export-and-conversion.md#PDF to documents}}

Mail merge:

{{snippet guides/flow-documents.md#Mail merge}}

Compare two versions:

{{snippet guides/comparing-documents.md#Comparing documents}}

Encrypted files:

{{snippet formats/docx.md#Passwords}}

## Common mistakes

- Loading `.html`, `.mht`, `.epub` or `.md` with `Document.Load` without the add-on: it throws a `NotSupportedException`
  that names the package. Add Egap.Documents.Html or Egap.Documents.Markdown and call `HtmlFormat.Register()` or
  `MarkdownFormat.Register()` once at startup.
- Disposing a `Document`: it is not disposable. The `PdfDocument`s that `ToPdf`, `PdfConvert.FromImages` and the
  in-memory `FromFile` overloads return are, so put `using` on those.
- Missing `using Egap.Documents.Pdf;`: `SaveAsPdf`, `ToPdf` and `Document.LoadPdf` are extension members there, so
  without it they do not compile.
- Writing `using Egap.Pdf.Conversion;`. That package is retired; its types are in `Egap.Documents.Pdf` (and the Docling
  types in `Egap.Documents`).
- Ignoring the warnings `Save` returns. Saving to a format that cannot hold something (Markdown, text) drops it and says so.
- Expecting `Save` to choose the format from anything but the extension: an unknown extension throws an
  `ArgumentException`; call the format's class to write another.
- Changing text run by run. `doc.Replace` and `doc.Find` work where text crosses runs; `DocumentBuilder` keeps formatting
  at the cursor.
- Expecting the same PDF on every machine by default. Layout may use system fonts; `LayoutOptions { Fonts =
  FontSettings.Portable }` uses only embedded fonts and the bundled substitutes, and `Strict = true` throws a
  `LayoutException` instead of degrading.
- Tracked changes printed by surprise: PDF export prints them accepted unless `PdfExportOptions.RevisionView` says
  `Markup` or `Original`.
- Untrusted files: DOCX, RTF, ODT, HTML and EPUB are read under `DocumentLimits`; a file past them throws a
  `DocumentLimitException`. Keep the defaults or lower them.
- Spreadsheets are a separate package: `Document.Identify` reports `Xlsx`, but loading a workbook as a document needs
  Egap.Spreadsheets and `SpreadsheetFormat.Register()`.

## More

- Flow documents: https://inexin.github.io/Egap/v2/articles/guides/flow-documents.html
- PDF export and conversion: https://inexin.github.io/Egap/v2/articles/guides/pdf-export-and-conversion.html
- Formats: https://inexin.github.io/Egap/v2/articles/formats/index.html
- Every page in one list: https://inexin.github.io/Egap/v2/llms.txt
- API reference: https://inexin.github.io/Egap/v2/api/Egap.Documents.html
