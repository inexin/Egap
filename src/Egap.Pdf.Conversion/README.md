# Egap.Pdf.Conversion (retired)

Egap.Pdf.Conversion is retired: everything it held ships in [Egap.Documents](https://www.nuget.org/packages/Egap.Documents)
2.0 and later. Its last release on nuget.org stays as it is and gets no new version.

To move over, replace the package reference with Egap.Documents and change the using lines:

- PDF export, `PdfConvert`, `PdfToDocument`, `PdfLayoutAnalyzer`, `PdfAutoTagger`, `TableCsv`, `PageBand` and `HtmlStamp`
  are in namespace `Egap.Documents.Pdf`.
- `DoclingJson` and its options are in namespace `Egap.Documents`.
- `PdfConvert.FromHtml`, `PdfStamp.Html` and HTML page bands need Egap.Documents.Html, and `PdfConvert.FromMarkdown` needs
  Egap.Documents.Markdown.

```csharp
using Egap.Documents;
using Egap.Documents.Pdf;
using Egap.Pdf;

Document report = Document.Load("report.docx");
report.SaveAsPdf("report.pdf", new PdfExportOptions { Tagged = true });

using PdfDocument pdf = PdfDocument.Open("paper.pdf");
Document flow = PdfToDocument.Convert(pdf);
flow.Save("paper.docx");
```

Source and changelog: [github.com/inexin/Egap](https://github.com/inexin/Egap).
