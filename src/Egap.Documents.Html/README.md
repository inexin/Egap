# Egap.Documents.Html

HTML for [Egap.Documents](https://www.nuget.org/packages/Egap.Documents): HTML and XHTML read into the document model,
styled through the CSS cascade, and written from it, with flexbox and grid layout, rounded and shadowed boxes, positioned
boxes, `@page` rules and margin boxes, HTML to PDF, and HTML stamps and page bands. Nothing outside the page is fetched unless you give a resolver. Fully managed and MIT-licensed.

```csharp
using Egap.Documents;
using Egap.Documents.Pdf;

HtmlFormat.Register();                                           // once at startup: Document.Load and Save know .html
Document page = Document.LoadHtml("invoice.html");
page.SaveAsDocx("invoice.docx");
string html = Document.LoadDocx("report.docx").ToHtml();
PdfConvert.FromHtml("<h1>Invoice</h1><p>Total: 120 EUR</p>", "invoice.pdf");
```

`HtmlFormat`, `HtmlLoadOptions` and `HtmlSaveOptions` keep their names and namespace, so code written against
Egap.Documents 1.x compiles once this package is added. Source and changelog:
[github.com/inexin/Egap](https://github.com/inexin/Egap).
