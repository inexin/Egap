# Egap.Documents

Flow documents for .NET 10: one editable document model with DOCX and plain-text readers and writers, a page layout engine
that draws onto any [Egami](https://www.nuget.org/packages/Egami) canvas, PDF export, and PDF pages analysed and converted
back into documents. Fully managed and MIT-licensed.

- DOCX, DOTX and DOCM read and written; what the model does not name is kept, so DOCX to DOCX is lossless. Files
  encrypted with a password open with it and save with one.
- Word's binary DOC files read (Word 97 to 2003, Word 6 and Word 95): formatting, lists, tables, sections, notes,
  comments, fields, tracked changes, pictures, shapes and text boxes; password-protected ones open with their password.
- RTF read and written as Word writes it: styles, lists, tables, sections, headers and footers, notes, fields, comments,
  tracked changes and pictures.
- OpenDocument Text (ODT, OTT, flat FODT and FOTT) read as LibreOffice writes it and written as ODF 1.3 (1.2 and 1.4 as
  options): styles, lists, tables, page styles, notes, fields, comments, tracked changes and pictures. Files encrypted with
  a password open with it and save with one; ODT to ODT keeps what the model does not name.
- Styles, lists numbered as Word numbers them, tables, fields, comments, tracked changes; find, replace and a builder.
- Page layout with floats, columns, headers and footers, footnotes, and flex and grid containers, rendered to images, SVG
  or PDF.
- Charts read, edited, made and drawn on any Egami canvas with `Chart.Draw`: one model for Word documents and workbooks.
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

Document doc = Document.Load("report.docx");                     // DOCX, DOC, RTF, ODT, PDF or plain text, told apart by content
DocumentBuilder builder = new(doc);
builder.Writeln("Added at the end.");
doc.Replace("colour", "color");
doc.Save("report-edited.docx");
doc.SaveAsPdf("report.pdf");

DocumentLayout pages = doc.Layout();
pages.Pages[0].Render(dpi: 150).Save("page1.png");
```

Source and changelog: [github.com/inexin/Egap](https://github.com/inexin/Egap).
