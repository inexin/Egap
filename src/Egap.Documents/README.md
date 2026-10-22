# Egap.Documents

Flow documents for .NET 10: one editable document model with DOCX and plain-text readers and writers, a page layout engine
that draws onto any [Egami](https://www.nuget.org/packages/Egami) canvas, PDF export, and PDF pages analysed and converted
back into documents. Fully managed and MIT-licensed.

- DOCX, DOTX and DOCM read and written; what the model does not name is kept, so DOCX to DOCX is lossless.
- Styles, lists numbered as Word numbers them, tables, fields, comments, tracked changes; find, replace and a builder.
- Page layout with floats, columns, headers and footers, footnotes, and flex and grid containers, rendered to images, SVG
  or PDF.
- Reports in code with `Report.Create`: nested lambdas for pages, rows, columns, tables, text, styles and components.
- PDF export with links, outline, tags and PDF/A; one-line conversions with `PdfConvert`.
- PDF to document: blocks, reading order, headings, lists, tables and figures; auto-tagging, Docling JSON and table CSV.
- HTML and Markdown come as add-on packages:
  [Egap.Documents.Html](https://www.nuget.org/packages/Egap.Documents.Html) and
  [Egap.Documents.Markdown](https://www.nuget.org/packages/Egap.Documents.Markdown).

```csharp
using Egap.Documents;
using Egap.Documents.Layout;
using Egap.Documents.Pdf;

Document doc = Document.Load("report.docx");                     // DOCX, PDF or plain text, told apart by content
DocumentBuilder builder = new(doc);
builder.Writeln("Added at the end.");
doc.Replace("colour", "color");
doc.Save("report-edited.docx");
doc.SaveAsPdf("report.pdf");

DocumentLayout pages = doc.Layout();
pages.Pages[0].Render(dpi: 150).Save("page1.png");
```

Source and changelog: [github.com/inexin/Egap](https://github.com/inexin/Egap).
