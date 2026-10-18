# Egap.Documents

Flow documents for .NET 10: one editable document model with DOCX, HTML and Markdown readers and writers, and a page layout
engine that draws onto any [Egami](https://www.nuget.org/packages/Egami) canvas. Fully managed and MIT-licensed.

- DOCX, DOTX and DOCM read and written; what the model does not name is kept, so DOCX to DOCX is lossless.
- HTML parsed as browsers parse it and styled through the CSS cascade; CommonMark and GitHub Markdown.
- Styles, lists numbered as Word numbers them, tables, fields, comments, tracked changes; find, replace and a builder.
- Page layout with floats, columns, headers and footers and footnotes, rendered to images, SVG or PDF.

```csharp
using Egap.Documents;

Document doc = Document.Load("report.docx");                     // DOCX, HTML or Markdown, told apart by content
DocumentBuilder builder = new(doc);
builder.Writeln("Added at the end.");
doc.Replace("colour", "color");
doc.Save("report-edited.docx");
doc.Save("report.md");                                           // the format from the extension

DocumentLayout pages = doc.Layout();
pages.Pages[0].Render(dpi: 150).Save("page1.png");
```

For PDF output and PDF to document conversion, add [Egap.Pdf.Layout](https://www.nuget.org/packages/Egap.Pdf.Layout).
Source and changelog: [github.com/inexin/Egap](https://github.com/inexin/Egap).
