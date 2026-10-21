# Egap

Egap is an open source family of document libraries for .NET 10: PDF files, and flow documents in DOCX, HTML and Markdown.
It is a sister project to [Egami](https://inexin.github.io/Egami/), the 2D imaging library it draws with.

- **MIT-licensed**, with no strings for commercial use.
- **Fully managed C#**: no native binaries, no P/Invoke; images, fonts and drawing come from Egami, which is managed too.
- **Safe on untrusted files**: damaged files are repaired where possible, and every reader is fuzz-tested.
- **PDF**: read, edit, render, sign and verify, redact, PDF/A, tagged PDF, optimize, imposition, stamps and compare.
- **Flow documents**: one model for DOCX, HTML and Markdown (HTML and Markdown as add-on packages), with page layout,
  export to PDF and PDF converted back into documents.

```csharp
using Egap.Pdf;

using PdfDocument pdf = PdfDocument.Open("in.pdf");
Console.WriteLine(pdf.Pages[0].ExtractText());                   // lines in reading order

pdf.Metadata.Title = "Quarterly report";
pdf.Outline.Add("Summary", PdfDestination.ToPage(pdf.Pages[0]));
pdf.Save("out.pdf");                                             // incremental: the original bytes plus the changes
```

Start with [Getting started](articles/getting-started.md), browse the [guides](articles/guides/index.md) and the
[document formats](articles/formats/index.md), or look anything up in the [API reference](api/Egap.Pdf.yml).
