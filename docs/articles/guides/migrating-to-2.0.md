# Moving to Egap.Documents 2.0

Egap.Documents 2.0 is one converter package: it takes in everything Egap.Pdf.Conversion held, and HTML and Markdown move to
two add-on packages. Most code compiles once the package references and the using lines change. Egap.Pdf does not change.

## Packages

| You used | Use now |
| --- | --- |
| Egap.Documents 1.x | Egap.Documents 2.0 |
| Egap.Pdf.Conversion | Egap.Documents 2.0 (Egap.Pdf.Conversion is retired and gets no new version) |
| `HtmlFormat`, `HtmlLoadOptions`, `HtmlSaveOptions`, `PdfConvert.FromHtml`, `PdfStamp.Html`, `PageBand.Html` | Egap.Documents.Html 2.0 |
| `MarkdownFormat` and its options, `PdfConvert.FromMarkdown` | Egap.Documents.Markdown 2.0 |

```sh
dotnet remove package Egap.Pdf.Conversion
dotnet add package Egap.Documents
dotnet add package Egap.Documents.Html       # if you read or write HTML
dotnet add package Egap.Documents.Markdown   # if you read or write Markdown
```

## Namespaces

| Type | 1.x namespace | 2.0 namespace |
| --- | --- | --- |
| `PdfExport` (`SaveAsPdf`, `ToPdf`), `PdfExportOptions`, `PdfConvert`, `PdfConvertOptions`, `PageBand`, `PageMargins`, `TableOfContentsMode` | `Egap.Pdf.Conversion` | `Egap.Documents.Pdf` |
| `PdfToDocument`, `PdfLayoutAnalyzer`, `PdfPageLayout` and the `Layout*` types, `PdfAutoTagger`, `TableCsv`, `HtmlStamp` | `Egap.Pdf.Conversion` | `Egap.Documents.Pdf` |
| `DoclingJson`, `DoclingJsonOptions`, `DoclingImage`, `DoclingImageMode` | `Egap.Pdf.Conversion` | `Egap.Documents` |
| `HtmlFormat`, `MarkdownFormat` and their options | `Egap.Documents` | `Egap.Documents` (unchanged, now in the add-ons) |

Replace `using Egap.Pdf.Conversion;` with `using Egap.Documents.Pdf;`, and add `using Egap.Documents;` where you use the
Docling types.

## Loading without naming the format

`Document.Load` and `Document.Save` know DOCX, PDF, plain text and Docling JSON. They reach HTML and Markdown only after
the add-on's `Register()`, called once at startup; `PdfConvert.FromFile` and HTML page bands use the same registration.
Without it they throw a `NotSupportedException` that names the package and the call.

```csharp
using Egap.Documents;

HtmlFormat.Register();
MarkdownFormat.Register();
Document notes = Document.Load("notes.md");
```

Calling `HtmlFormat` or `MarkdownFormat` directly, or `PdfConvert.FromHtml` and `PdfConvert.FromMarkdown`, needs no
registration.

## Other changes

- A `.txt` file loads as plain text, one paragraph per line, instead of as Markdown.
- `Document.Save("x.pdf")` writes PDF.
- `HtmlLoadOptions.Encoding` is the fallback for bytes that name no encoding; it no longer overrides a meta charset.
- Each format also reads as members of `Document`, such as `Document.LoadDocx`, `Document.LoadMarkdown`, `ToHtml` and
  `SaveAsDocx`; the format classes' methods still work.

The [changelog](https://github.com/inexin/Egap/blob/main/CHANGELOG.md) lists every change under each package.
