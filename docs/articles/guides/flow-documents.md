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

## Text options

Besides fonts, sizes and colours, runs take word spacing, an overline and any OpenType feature, such as ligatures,
tabular or old-style figures and stylistic sets. Paragraphs choose where lines may break inside words. HTML's
`word-spacing`, `text-decoration: overline`, `font-feature-settings`, `font-variant-ligatures`, `font-variant-numeric`,
`word-break` and `overflow-wrap` read into the same settings. DOCX keeps the features Word has (ligatures, number forms
and spacing, stylistic sets, contextual alternates); Word has no word spacing or overline.

```csharp
using Egap.Documents;

Document doc = new();
DocumentBuilder builder = new(doc);
builder.Font.WordSpacing = 2;
builder.Font.Overline = true;
builder.Font.Features = FontFeatures.Parse("liga=0, tnum");
builder.ParagraphFormat.WordBreak = WordBreak.Anywhere;
builder.Writeln("Invoice 2026-118: https://example.com/a/very/long/address/that/must/break");
```

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

A group of blocks (a block content control) can say how it is placed across pages: kept together on one page, started
on a new page when less than some height is left, never split, cut where the page ends, or drawn smaller to fit the room
left. HTML's `break-inside: avoid` on a box of several blocks makes such a group. A paragraph can be clamped to a number
of lines, ending in an ellipsis (HTML's `-webkit-line-clamp`); table rows marked as footer rows repeat at the bottom of
each page the table continues from; and a section can balance its last page's columns. Word keeps the content, not
these settings.

```csharp
using Egap.Documents;

Document invoice = new();
DocumentBuilder builder = new(invoice);
builder.Writeln("Lines of the invoice...");
ContentControl totals = builder.StartGroup();
totals.Paging = new GroupPaging { KeepTogether = true, EnsureSpace = 120 };
builder.Writeln("Subtotal: 1 200 NOK");
builder.Write("Total: 1 500 NOK");
builder.EndGroup();
```

With `Strict`, layout throws a `LayoutException` naming the node and the page instead of degrading: a word too long for
its column, a table or picture past the page, a character no font has, or a font with no stand-in. `FontSettings.Portable`
uses only the document's embedded fonts and the bundled substitutes, never the system's, so a document prints the same
on every machine; with a deterministic save the PDF bytes are the same too. Both suit documents your own code builds;
for converting other people's files, leave them off and read the warnings.

```csharp
using Egap.Documents;
using Egap.Documents.Layout;

Document report = Document.Load("report.docx");
try
{
    DocumentLayout pages = report.Layout(new LayoutOptions { Strict = true, Fonts = FontSettings.Portable });
}
catch (LayoutException e)
{
    Console.WriteLine($"{e.Problem} on page {e.PageIndex + 1}: {e.Message}");
}
```

For PDF output, see [PDF export and conversion](pdf-export-and-conversion.md).
