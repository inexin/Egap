# Egap.Pdf.Conversion

The bridge between [Egap.Documents](https://www.nuget.org/packages/Egap.Documents) and
[Egap.Pdf](https://www.nuget.org/packages/Egap.Pdf), in both directions. Fully managed and MIT-licensed.

- Documents saved as PDF with links, outline and tags, PDF/A included.
- One-line conversions of HTML, Markdown, DOCX and images to PDF, and stamps made from HTML.
- PDF pages analysed into blocks, reading order, headings, lists, tables and figures, and converted back into documents
  you can save as DOCX, HTML or Markdown; tagged PDFs such as Word's keep their tables' merged and nested cells.
- Untagged PDFs auto-tagged; layout written as DoclingDocument JSON or tables as CSV.

```csharp
using Egap.Documents;
using Egap.Pdf;
using Egap.Pdf.Conversion;

Document report = Document.Load("report.docx");
report.SaveAsPdf("report.pdf", new PdfExportOptions { Tagged = true });

using PdfDocument pdf = PdfDocument.Open("paper.pdf");
Document flow = PdfToDocument.Convert(pdf);
flow.Save("paper.docx");
```

Source and changelog: [github.com/inexin/Egap](https://github.com/inexin/Egap).
