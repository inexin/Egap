# PDF export and conversion

Egap.Documents saves documents as PDF and turns PDF pages back into documents, in namespace `Egap.Documents.Pdf`. HTML and
Markdown come from their add-on packages, Egap.Documents.Html and Egap.Documents.Markdown, which add `PdfConvert.FromHtml`,
`PdfConvert.FromMarkdown`, `PdfStamp.Html` and HTML page bands.

## Documents to PDF

A document saves as PDF with its links, outline and, when asked, tags for accessibility. PDF/A works the same way.

```csharp
using Egap.Documents;
using Egap.Pdf;
using Egap.Documents.Pdf;

Document doc = Document.Load("report.docx");
doc.SaveAsPdf("report.pdf", new PdfExportOptions { Tagged = true, Conformance = PdfAConformance.PdfA2a });

Document page = HtmlFormat.Load("invoice.html");
page.SaveAsPdf("invoice.pdf");
```

With `FormFields`, HTML form controls and Word content controls become PDF form fields where they were laid out, each
named by its HTML name or Word tag and starting with its value.

```csharp
using Egap.Documents;
using Egap.Documents.Pdf;

Document form = HtmlFormat.Load("application.html");
form.SaveAsPdf("application.pdf", new PdfExportOptions { FormFields = true });
```

With `Comments = PdfCommentMode.Annotations`, the document's comments become PDF annotations any viewer shows and
edits, as Acrobat writes them: a highlight over the text each is about (a note icon where it is about no text) with its
author, date and text in a popup, replies threaded under it, and a resolved comment marked `Completed`.
`IncludeResolved = false` leaves resolved comments out. The annotations print and have appearances, so PDF/A-2 and later
hold, and tagged output puts each in an `Annot` element. Reading the PDF back gives the comments again, on the words they
were about.

```csharp
using Egap.Documents;
using Egap.Documents.Pdf;

Document draft = Document.Load("draft.docx");
draft.SaveAsPdf("draft-for-review.pdf", new PdfExportOptions { Comments = PdfCommentMode.Annotations, IncludeResolved = false });
draft.SaveAsPdf("draft-printed.pdf", new PdfExportOptions { Comments = PdfCommentMode.Margin });
```

`PdfCommentMode.Margin` prints the comments instead, as Word prints a document showing markup: each page keeps its paper
size, its content is drawn at 76 % on the left, and a pane on the right holds a balloon per comment, replies under it, beside
the line it is about, moved up or down only as far as the balloons around it need. The commented words are shaded in their
author's colour, the balloon shows the text with the author and date under it, and a dashed line runs from an arrow under
the comment's first character to its balloon. Balloons that do not fit on
their page go to a Comments list at the end of the document, each with its page number. Links and bookmarks follow the
smaller page; form fields cannot be written with this mode. In tagged output each balloon is a `Note` beside the
paragraph its comment is in. Tracked changes are not printed in the margin.

## One-line conversions

`PdfConvert` turns HTML, Markdown, DOCX and images into PDF in one call. A page size and margins in the options replace the
input's own. Converting a file reads its images from the file's own folder; HTML or Markdown given as text reads them only
from `BaseDirectory`, and never from the network. `FromFile` handles HTML and Markdown files once their add-on is
registered with `HtmlFormat.Register()` or `MarkdownFormat.Register()`.

```csharp
using Egap.Pdf;
using Egap.Documents.Pdf;

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
using Egap.Documents.Pdf;

PdfConvert.FromHtml("<title>Invoice 2026-118</title><style>.hdr { text-align: right }</style><p>Total: 120 EUR</p>", "invoice.pdf", new PdfConvertOptions
{
    Header = PageBand.Html("<div class='hdr'><b>ACME</b> {date}</div>"),
    Footer = PageBand.Text(left: "{title}", center: "Page {page} of {total-pages}") with { FontSize = 8 },
});
```

`TableOfContents` builds a table of contents from the headings, each entry linked to its heading, with or without page
numbers. It goes where HTML has `<nav data-toc>` or Markdown a `[TOC]` paragraph, else at the start.

```csharp
using Egap.Documents;
using Egap.Documents.Pdf;

MarkdownFormat.Register();
PdfConvert.FromFile("handbook.md", "handbook.pdf", new PdfConvertOptions { TableOfContents = TableOfContentsMode.WithPageNumbers });
```

`PdfStamp.Html` lays out a piece of HTML as a stamp, such as a seal or a letterhead, and draws it on every page.

```csharp
using Egap.Pdf;
using Egap.Documents.Pdf;

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
using Egap.Documents.Pdf;

using PdfDocument pdf = PdfDocument.Open("paper.pdf");
Document flow = PdfToDocument.Convert(pdf);
flow.Save("paper.docx");
```

A hybrid PDF carries the editable document inside it. Export with `PdfExportOptions.EmbedSource` (or
`PdfConvertOptions.EmbedSource`) and a DOCX of the document is attached as the PDF's source, an associated file marked
`/Source`. Loading the PDF then gives that document back, styles and all, instead of analysing the pages. The copy is used
only when it is marked as the source, is a DOCX, was embedded by Egap and the PDF has not been changed or signed since;
otherwise the pages are analysed and a `PdfToDocumentWarningKind.EmbeddedSourceSkipped` warning says why.
`PdfToDocumentOptions.EmbeddedSource` chooses `Prefer` (the default), `Ignore` or `Require`. PDF/A-1 and PDF/A-2 do not
allow embedded files, so the export throws with them; PDF/A-3, PDF/A-4f and plain PDF carry the copy.

```csharp
using Egap.Documents;
using Egap.Documents.Pdf;
using Egap.Pdf;

Document report = Document.LoadDocx("report.docx");
report.SaveAsPdf("report.pdf", new PdfExportOptions { EmbedSource = true, Conformance = PdfAConformance.PdfA3b });

Document again = Document.LoadPdf("report.pdf");                 // the DOCX inside, not an analysis of the pages
Document pages = Document.LoadPdf("report.pdf", new PdfToDocumentOptions { EmbeddedSource = EmbeddedSource.Ignore });
```

The same analysis can tag an untagged PDF, write its layout as DoclingDocument JSON, or export its tables as CSV. A scanned
page stays a picture unless you load with `Document.LoadPdfAsync` and an OCR engine (see
[OCR and searchable scans](ocr.md#scans-in-document-conversion)).

## Recipes

Short answers to common tasks in this area. Each one is complete: open, change, save.

### Turn a Word file into an e-book

```csharp
using Egap.Documents;

Document handbook = Document.LoadDocx("handbook.docx");
handbook.Properties.Title ??= "Staff handbook";
handbook.SaveAsEpub("handbook.epub", new EpubSaveOptions { NavigationDepth = 2 });
```

Each level 1 heading starts a chapter, and the headings make the book's table of contents. See [EPUB](../formats/epub.md).

### Print an e-book as PDF

```csharp
using Egap.Documents;
using Egap.Documents.Pdf;

Document book = Document.LoadEpub("novel.epub");
book.SaveAsPdf("novel.pdf", new PdfExportOptions { Outline = OutlineFrom.Headings });
```

Each chapter starts a new page, and the book's table of contents becomes the PDF's bookmarks.

### Save a page as one file

```csharp
using Egap.Documents;

Document page = HtmlFormat.Load("offer.html", new HtmlLoadOptions
{
    BaseUri = new Uri(new FileInfo("offer.html").FullName),
    ResourceResolver = uri => uri.IsFile && File.Exists(uri.LocalPath) ? File.ReadAllBytes(uri.LocalPath) : null,
});
page.SaveAsMhtml("offer.mhtml");
```

The archive holds the page with its pictures, and opens in Chromium, Edge and Word. See [MHTML](../formats/mhtml.md).
