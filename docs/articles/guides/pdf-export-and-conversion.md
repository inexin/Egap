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

With `FormFields`, HTML form controls and Word content controls become PDF form fields where they were laid out, each
named by its HTML name or Word tag and starting with its value.

```csharp
using Egap.Documents;
using Egap.Pdf.Conversion;

Document form = HtmlFormat.Load("application.html");
form.SaveAsPdf("application.pdf", new PdfExportOptions { FormFields = true });
```

## One-line conversions

`PdfConvert` turns HTML, Markdown, DOCX and images into PDF in one call. A page size and margins in the options replace the
input's own. Converting a file reads its images from the file's own folder; HTML or Markdown given as text reads them only
from `BaseDirectory`, and never from the network.

```csharp
using Egap.Pdf;
using Egap.Pdf.Conversion;

PdfConvert.FromFile("report.docx", "report.pdf");
PdfConvert.FromHtml("<h1>Invoice</h1><p>Total: 120 EUR</p>", "invoice.pdf", new PdfConvertOptions
{
    PageSize = PdfPageSize.A4,
    Margins = PageMargins.All(Length.Mm(20)),
    Conformance = PdfAConformance.PdfA3b,
});
using PdfDocument scans = PdfConvert.FromImages(["scan-1.jpg", "scan-2.png"], new PdfConvertOptions { PageSize = PdfPageSize.A4 });
scans.RenderPagesToFiles("thumbs/page-{page:00}.png", new PdfRenderOptions { Dpi = 72 });
```

`Header` and `Footer` put the same band on every page, in the top or bottom margin, in place of the input's own headers
and footers. A text band has up to three parts (left, middle and right); an HTML band is styled with the page's own style
sheets. Both take the placeholders `{page}`, `{total-pages}`, `{label}`, `{date}`, `{time}`, `{title}` and `{file}`, and
a number format after a colon, as in `{page:000}`. HTML pages can set the same thing themselves with `@page` margin boxes
and running elements (`position: running(header)` with `content: element(header)`).

```csharp
using Egap.Pdf.Conversion;

PdfConvert.FromHtml("<title>Invoice 2026-118</title><style>.hdr { text-align: right }</style><p>Total: 120 EUR</p>", "invoice.pdf", new PdfConvertOptions
{
    Header = PageBand.Html("<div class='hdr'><b>ACME</b> {date}</div>"),
    Footer = PageBand.Text(left: "{title}", center: "Page {page} of {total-pages}") with { FontSize = 8 },
});
```

`TableOfContents` builds a table of contents from the headings, each entry linked to its heading, with or without page
numbers. It goes where HTML has `<nav data-toc>` or Markdown a `[TOC]` paragraph, else at the start.

```csharp
using Egap.Pdf.Conversion;

PdfConvert.FromFile("handbook.md", "handbook.pdf", new PdfConvertOptions { TableOfContents = TableOfContentsMode.WithPageNumbers });
```

`PdfStamp.Html` lays out a piece of HTML as a stamp, such as a seal or a letterhead, and draws it on every page.

```csharp
using Egap.Pdf;
using Egap.Pdf.Conversion;

using PdfDocument invoice = PdfDocument.Open("invoice.pdf");
invoice.Stamp(PdfStamp.Html("<div style='border:3px solid green;color:green;font:bold 28pt sans-serif'>PAID</div>", Length.Mm(50))
    with { Anchor = StampAnchor.TopRight, Offset = new(-40, -40), Rotation = 15 });
invoice.Save("invoice-paid.pdf");
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
