# PDF export and conversion

Egap.Pdf.Conversion connects Egap.Documents and Egap.Pdf in both directions.

## Documents to PDF

A document saves as PDF with its links, outline and, when asked, tags for accessibility. PDF/A works the same way.

```csharp
using Egap.Documents;
using Egap.Pdf;
using Egap.Pdf.Conversion;

Document doc = Document.Load("report.docx");
doc.SaveAsPdf("report.pdf", new PdfExportOptions { Tagged = true, Conformance = PdfAConformance.PdfA2a });

Document page = HtmlFormat.Load("invoice.html");
page.SaveAsPdf("invoice.pdf");
```

## PDF to documents

`PdfToDocument` analyses the pages into blocks, reading order, headings, lists, tables and figures, and builds a document you
can save as DOCX, HTML or Markdown. Tagged PDFs, such as Word's, keep their tables' merged and nested cells.

```csharp
using Egap.Documents;
using Egap.Pdf;
using Egap.Pdf.Conversion;

using PdfDocument pdf = PdfDocument.Open("paper.pdf");
Document flow = PdfToDocument.Convert(pdf);
flow.Save("paper.docx");
```

The same analysis can tag an untagged PDF, write its layout as DoclingDocument JSON, or export its tables as CSV.
