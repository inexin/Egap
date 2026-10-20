# Flow documents

Egap.Documents holds one editable document model for DOCX, HTML and Markdown: sections, paragraphs, runs, tables, lists,
pictures, headers and footers, notes, fields, comments and tracked changes, with Word's style model.

```csharp
using Egap.Documents;

Document doc = Document.Load("report.docx");                      // any format Egap reads, told apart by content
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

`Document.Load` and `Save` know DOCX and plain text by themselves. HTML and Markdown join them once you register them at
startup; until then, loading such a file throws a `NotSupportedException` that names the call to make. `Document.Identify`
tells which format a file is in without loading it, so a program can say what a file needs.

```csharp
using Egap.Documents;

HtmlFormat.Register();                                            // once at startup: Load and Save now read and write HTML
MarkdownFormat.Register();
DocumentFormatKind kind = Document.Identify("upload.bin");        // Pdf, Docx, Html, Markdown, Text, DoclingJson or Unknown
Document notes = TextFormat.Load("notes.txt");                    // one paragraph per line
```

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

## Barcodes

`InsertBarcode` writes a barcode as a picture of the size you give. Layout and PDF export draw it as vectors; DOCX keeps it
as an SVG picture with a PNG stand-in at 300 pixels per inch, as Word stores SVG pictures, so older readers show it too. Any
SVG picture can carry such a stand-in in `Picture.Fallback`.

```csharp
using Egami.Barcodes;
using Egap.Documents;

Document label = new();
DocumentBuilder builder = new(label);
builder.Writeln("Parcel 2026-118");
builder.InsertBarcode(Barcode.Code128("PARCEL-2026-118"), 200, 50);
label.Save("label.docx");
```

## Mail merge

`FillMergeFields` fills a template's MERGEFIELD fields from one record. Each field becomes plain text in its own
formatting, written with its number (`\#`), date (`\@`) and text (`\*`) formats and the text it puts before and after a
value (``, ``); IF fields that test the values are worked out. `MergeData` makes records from dictionaries, rows
with column names and objects, without reflection that trimming would break. Implement `IMergeData` to read records
from anywhere else. Fields the record has no value for are removed, each with a warning.

```csharp
using Egap.Documents;

Document letter = Document.Load("letter.docx");
IReadOnlyList<ConversionWarning> missing = letter.FillMergeFields(MergeData.From(new Dictionary<string, object?>
{
    ["Name"] = "Kari Nordmann",
    ["Amount"] = 1234.5m,
    ["Due"] = new DateOnly(2026, 11, 1),
}));
letter.Save("letter-kari.docx");
```

A region repeats once per record of a sequence in the data: the rows or paragraphs from a `TableStart:Lines` merge field to
a `TableEnd:Lines` one, or a table row whose fields are all named `Lines.Something`. Regions nest, and a field a line does
not have is read from the record around it. `MailMerge` makes one document per record, as the sequence is read, and
`MailMergeAll` one document of them all, each record starting on a new page (or as `Separator` says).

```csharp
using Egap.Documents;

Document template = Document.Load("invoice-template.docx");
IEnumerable<IMergeData> customers = MergeData.From(["Name", "City"], [["Ola", "Oslo"], ["Kari", "Bergen"]]);
int number = 1;
foreach (Document invoice in template.MailMerge(customers))
{
    invoice.Save($"invoice-{number++}.docx");
}

Document statements = template.MailMergeAll(customers, new MailMergeOptions { OnWarning = w => Console.WriteLine(w) });
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

Margin boxes put text in the page margins, as CSS's `@page` margin boxes do: a box in each corner and three along each
edge, on every page of the section or only the first. They hold PAGE and NUMPAGES fields for page numbers and sit beside
the headers and footers without moving the body. HTML's `@top-left`, `@bottom-center` and the other margin box rules read
into them, `counter(page)` and `counter(pages)` included, and write back the same way. An element with
`position: running(name)` leaves the body and shows in the boxes whose content is `element(name)`. DOCX has no margin boxes, so the
top and bottom ones are saved as a three-part table in the header or footer; the corner and side boxes are left out with
a warning.

```csharp
using Egap.Documents;

Document report = Document.Load("report.docx");
DocumentBuilder builder = new(report);
builder.MoveToMarginBox(MarginBoxPosition.TopLeft);
builder.Write("Quarterly report");
builder.MoveToMarginBox(MarginBoxPosition.BottomRight);
builder.Write("Page ");
builder.InsertField("PAGE");
builder.Write(" of ");
builder.InsertField("NUMPAGES");
builder.MoveToMarginBox(MarginBoxPosition.TopLeft, firstPage: true);   // an empty box: no title on the cover
```

`InsertTableOfContents` writes a TOC field and builds its entries from the headings, each linked to its heading. Layout
fills in the page numbers, as it does for every PAGEREF field. HTML's `<nav data-toc>` is read as such a field.

```csharp
using Egap.Documents;

Document handbook = Document.Load("handbook.docx");
DocumentBuilder builder = new(handbook);
builder.MoveToDocumentStart();
builder.InsertTableOfContents();
builder.InsertParagraph();
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
