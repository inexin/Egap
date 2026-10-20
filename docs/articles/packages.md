# Packages

| Package | What it does | Depends on |
| --- | --- | --- |
| Egap.Pdf | Reads, edits, renders, signs, redacts and saves PDF; PDF/A, tagged PDF, optimize, imposition, stamps, barcodes, compare | Egami.Drawing, Egami.Barcodes, Egami.Fonts.Substitutes |
| Egap.Documents | One document model with DOCX, plain text and Docling JSON, page layout onto any Egami canvas, PDF export, and PDF pages analysed and converted back into documents | Egap.Pdf, Egami.Drawing, Egami.Barcodes, Egami.Svg, Egami.Metafiles, Egami.Fonts.Substitutes |
| Egap.Documents.Html | HTML and XHTML read and written through the CSS cascade, with `@page` rules and margin boxes, HTML to PDF, HTML stamps and page bands | Egap.Documents, Egami.Css, AngleSharp |
| Egap.Documents.Markdown | CommonMark and GitHub Markdown read and written, with front matter and images, and Markdown to PDF | Egap.Documents, Markdig |

Take Egap.Pdf alone for PDF work, Egap.Documents for documents and conversions, and an add-on for each of HTML and Markdown
you read or write. The add-ons keep the class names you know (`HtmlFormat`, `MarkdownFormat`) in namespace
`Egap.Documents`. `Document.Load` and `Document.Save` reach an add-on's format once you call its `Register()` at startup:

```csharp
using Egap.Documents;

HtmlFormat.Register();
MarkdownFormat.Register();
Document notes = Document.Load("notes.md");
```

Without the call, loading such a file throws a `NotSupportedException` that names the package and the call.
`Document.Identify` tells which format a file is in without any add-on.

The add-ons start at 2.0.0 and move in step with Egap.Documents. Egap.Pdf.Conversion is retired: its code is in
Egap.Documents 2.0 (see [Moving to Egap.Documents 2.0](guides/migrating-to-2.0.md)).

Egami.Drawing brings in Egami and Egami.Fonts. Every package is fully managed, MIT-licensed, and targets .NET 10; the
add-ons' parsers, AngleSharp (MIT) and Markdig (BSD-2-Clause), are managed too. The substitute fonts for missing fonts come
from Egami.Fonts.Substitutes, under the SIL Open Font License 1.1. All four packages are trimmable and work with Native AOT.
