# Egap

Egap is an open source family of document libraries for .NET 10: PDF files, and flow documents in DOCX, HTML and Markdown.
It is a sister project to [Egami](https://inexin.github.io/Egami/), the 2D imaging library it draws with.

- **MIT-licensed**, with no strings for commercial use.
- **Fully managed C#**: no native binaries, no P/Invoke; images, fonts and drawing come from Egami, which is managed too.
- **Safe on untrusted files**: damaged files are repaired where possible, and every reader is fuzz-tested.
- **PDF**: read, edit, render, sign and verify, redact, PDF/A, tagged PDF, optimize, imposition, stamps and compare.
- **Flow documents**: one model for DOCX, HTML and Markdown, with page layout and export to PDF.

```csharp
using Egap.Pdf;

using PdfDocument pdf = PdfDocument.Open("in.pdf");
Console.WriteLine(pdf.Pages[0].ExtractText());                   // lines in reading order

pdf.Metadata.Title = "Quarterly report";
pdf.Outline.Add("Summary", PdfDestination.ToPage(pdf.Pages[0]));
pdf.Save("out.pdf");                                             // incremental: the original bytes plus the changes
```

Start with [Getting started](articles/getting-started.md), browse the [guides](articles/guides/editing.md), or look anything
up in the [API reference](api/Egap.Pdf.yml).
