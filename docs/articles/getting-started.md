# Getting started

Egap needs .NET 10. Add the package for what you need:

```sh
dotnet add package Egap.Pdf            # PDF files
dotnet add package Egap.Documents      # DOCX and plain text, documents to PDF, and PDF back to documents
dotnet add package Egap.Documents.Html      # HTML
dotnet add package Egap.Documents.Markdown  # Markdown
```

Egami, the imaging library Egap draws with, comes along as a dependency. See [Packages](packages.md) for what each one holds.

## Open, edit and save a PDF

```csharp
using Egap.Pdf;

using PdfDocument pdf = PdfDocument.Open("in.pdf");
Console.WriteLine(pdf.Pages[0].ExtractText());                   // lines in reading order

pdf.Metadata.Title = "Quarterly report";
pdf.Outline.Add("Summary", PdfDestination.ToPage(pdf.Pages[0]));
pdf.Save("out.pdf");                                             // incremental: the original bytes plus the changes
```

Damaged files open too. Each repair Egap makes while reading is listed in `pdf.Warnings`; set `PdfReadOptions.Strict` to get
an exception instead.

## Render a page

```csharp
using Egami;
using Egami.Pixels;
using Egap.Pdf;

using PdfDocument pdf = PdfDocument.Open("in.pdf");
using Raster<Rgba8> image = pdf.Pages[0].Render(new PdfRenderOptions { Dpi = 150 });
image.Save("page.png");
```

## Turn a Word file into PDF

```csharp
using Egap.Documents;
using Egap.Documents.Pdf;

Document report = Document.Load("report.docx");
report.SaveAsPdf("report.pdf", new PdfExportOptions { Tagged = true });
```

## Next

The [guides](guides/index.md) cover each area in more depth, grouped by package, and each document format has a page in
[Document formats](formats/index.md). Every public type and member is in the [API reference](../api/Egap.Pdf.yml).
