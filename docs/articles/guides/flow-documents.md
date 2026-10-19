# Flow documents

Egap.Documents holds one editable document model for DOCX, HTML and Markdown: sections, paragraphs, runs, tables, lists,
pictures, headers and footers, notes, fields, comments and tracked changes, with Word's style model.

```csharp
using Egap.Documents;

Document doc = Document.Load("report.docx");                      // DOCX, DOTX, DOCM, HTML or Markdown, told apart by content
foreach (Paragraph p in doc.Descendants<Paragraph>())
{
    Console.WriteLine($"{p.ListLabel?.Text} {p.GetText()}");    // list labels computed as Word numbers them
}

DocumentBuilder builder = new(doc);                               // edit from code at a cursor
builder.Writeln("Added at the end.");
doc.Replace("colour", "color");                                   // also where the text crosses runs
IReadOnlyList<TextMatch> found = doc.Find("color");               // paragraph, offset and text of each match
doc.Save("report-edited.docx");                                   // what the model does not name is kept
IReadOnlyList<ConversionWarning> dropped = doc.Save("report.md"); // the format from the extension, with what it could not hold
```

DOCX to DOCX is lossless: what the model does not name travels along in each node's extension data. Saving to a format that
cannot hold something, such as Markdown, returns a warning for each thing left out.

## Format options

Each format has its own class with its own options.

```csharp
using Egap.Documents;

Document readme = MarkdownFormat.Load("README.md", new MarkdownLoadOptions { Extensions = MarkdownExtensions.GitHub });
DocxFormat.Save(readme, "readme.dotx", new DocxSaveOptions { Kind = DocxKind.Template });
```

HTML is parsed as browsers parse it and styled through the CSS cascade. Nothing outside the file is fetched unless you pass
a resolver:

```csharp
using Egap.Documents;

Document page = HtmlFormat.Load("invoice.html", new HtmlLoadOptions
{
    BaseUri = new Uri("https://example.com/"),
    ResourceResolver = uri => File.Exists(uri.LocalPath) ? File.ReadAllBytes(uri.LocalPath) : null,
});
```

Form controls in HTML (text inputs, check boxes, radio buttons, selects and text areas) are read as content controls that
hold their values, as Word's are, so they keep their place in the text and a PDF export can turn them into form fields.

## Page layout

`Layout` breaks the document into pages, with floats, columns, headers and footers, and footnotes. Each page draws onto any
Egami canvas.

```csharp
using Egap.Documents;
using Egap.Documents.Layout;

Document doc = Document.Load("report.docx");
DocumentLayout pages = doc.Layout();                              // pages, lines and boxes linked to the model
pages.Pages[0].Render(dpi: 150).Save("page1.png");
```

For PDF output, see [PDF export and conversion](pdf-export-and-conversion.md).
