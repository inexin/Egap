# Egap.Pdf

A fully managed, MIT-licensed PDF library for .NET 10. It reads PDF files, damaged and encrypted ones included, edits them
and saves them incrementally or by full rewrite. Images, fonts and drawing come from [Egami](https://www.nuget.org/packages/Egami),
which is managed too: no native binaries, no P/Invoke.

- Pages, outlines, labels, metadata, attachments, layers, annotations and forms (XFA data included); merge and split.
- Content streams, text extraction in reading order, image extraction and replacement, drawing through an Egami canvas.
- Rendering to rasters or onto any Egami canvas.
- Signatures (PAdES B-B to B-LTA) and verification, true redaction, PDF/A, tagged PDF, linearization, an OCR text layer.
- Find, redact and replace text by phrase or regular expression; Factur-X and ZUGFeRD e-invoices.
- Tools: optimize, N-up and booklets, stamps and Bates numbers, compare, blank page removal and deskew.

PDF to DOCX, HTML or Markdown, and documents to tagged PDF, are in
[Egap.Documents](https://www.nuget.org/packages/Egap.Documents).

```csharp
using Egap.Pdf;

using PdfDocument pdf = PdfDocument.Open("in.pdf");
Console.WriteLine(pdf.Pages[0].ExtractText());                   // lines in reading order

pdf.Metadata.Title = "Quarterly report";
pdf.Outline.Add("Summary", PdfDestination.ToPage(pdf.Pages[0]));
pdf.Save("out.pdf");                                             // incremental: the original bytes plus the changes

using Egami.Raster<Egami.Pixels.Rgba8> image = pdf.Pages[0].Render(new PdfRenderOptions { Dpi = 150 });
image.Save("page.png");
```

Source, more examples and the changelog: [github.com/inexin/Egap](https://github.com/inexin/Egap).
