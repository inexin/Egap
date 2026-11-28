# Flow documents

Egap.Documents holds one editable document model for DOCX, plain text and, with the add-on packages Egap.Documents.Html
and Egap.Documents.Markdown, HTML and Markdown: sections, paragraphs, runs, tables, lists, pictures, headers and footers,
notes, fields, comments and tracked changes, with Word's style model.

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
IReadOnlyList<ConversionWarning> dropped = doc.Save("report.txt"); // the format from the extension, with what it left out
```

DOCX to DOCX is lossless: what the model does not name travels along in each node's extension data. Saving to a format that
cannot hold something, such as Markdown, returns a warning for each thing left out.

Each format also reads as members of `Document`, which forward to the format's class with the same options and warnings:

```csharp
using Egap.Documents;
using Egap.Documents.Pdf;

Document report = Document.LoadDocx("report.docx");
report.SaveAsDocx("report-copy.docx");
string text = report.ToText();                                    // as TextFormat.ToText
report.SaveAsPdf("report.pdf");
Document scanned = Document.LoadPdf("scanned.pdf");               // as PdfToDocument.Convert
```

`Document.Load` and `Save` know DOCX, PDF, plain text and Docling JSON by themselves; HTML and Markdown join them once
their add-on package is registered. Each format has its own page under [Document formats](../formats/index.md), with its
options and what it keeps.

## Restricting editing

A document can ask the program that opens it to keep it read only, or to allow only comments, tracked changes or form
filling, with editable ranges as exceptions and a password that lifts it. Word enforces this; Egap reads and writes it.

```csharp
using Egap.Documents;

Document contract = Document.Load("contract.docx");
contract.Protection.Protect(ProtectionKind.TrackedChanges, "legal");   // every change is tracked
contract.Save("contract-for-review.docx");

if (contract.Protection.Verify("legal"))
{
    contract.Protection.Unprotect();
}
```

The [DOCX page](../formats/docx.md#editing-restrictions) has the details and the editable ranges.

## Text options

Besides fonts, sizes and colours, runs take word spacing, an overline and any OpenType feature, such as ligatures,
tabular or old-style figures and stylistic sets. Paragraphs choose where lines may break inside words. HTML's
`word-spacing`, `text-decoration: overline`, `font-feature-settings`, `font-variant-ligatures`, `font-variant-numeric`,
`word-break` and `overflow-wrap` read into the same settings. DOCX keeps the features Word has (ligatures, number forms
and spacing, stylistic sets, contextual alternates); Word has no word spacing or overline.

Thai, Lao, Khmer and Myanmar, written without spaces between words, break into lines between words that Egami's word
dictionaries find, as Word and browsers break them; before, such a line broke only at a space or overflowed. Find and
Replace with `WholeWord`, PDF text search and document comparison use the same word boundaries.

Justified and distributed paragraphs break lines as Word 2013 and later do: a line takes one more word when its spaces
can shrink to fit it, by up to a quarter of their width, so justified text often takes fewer lines than in a browser, and
they hyphenate without the hyphenation zone, as Word does.
`DocumentSettings.WordCompatibility.ShrinkJustifiedSpaces` turns this off. It follows the compatibility mode: on in
mode 15, off for files from Word 2010 and older, and off for documents read from ODT, HTML, EPUB and Markdown, whose own
applications only stretch justified lines.

A justified paragraph can also stretch its Arabic lines with tatweels (kashida) between letters, as Word's low, medium
and high kashida alignments do: set `ParagraphFormat.Kashida` to `KashidaLevel.Low`, `Medium` or `High` with
`ParagraphAlignment.Justify`. What the tatweels do not take goes to word spaces, and lines in other scripts justify at
spaces as before. In PDF exports the inserted tatweels are left out of the text, so copy and search give the words as typed.

In a justified paragraph the line before a manual line break (`BreakKind.Line`) stretches to the margins, as in Word,
unless `DocumentSettings.WordCompatibility.DoNotExpandShiftReturn` is set; the format page for
[DOCX](../formats/docx.md#word-compatibility) says how page and column breaks and documents from other formats differ.

```csharp
using Egap.Documents;

Document doc = new();
DocumentBuilder builder = new(doc);
builder.Font.WordSpacing = 2;
builder.Font.Overline = true;
builder.Font.Features = FontFeatures.Parse("liga=0, tnum");
builder.ParagraphFormat.WordBreak = WordBreak.Anywhere;
builder.Writeln("Invoice 2026-118: https://example.com/a/very/long/address/that/must/break");

builder.ParagraphFormat.Bidi = true;
builder.ParagraphFormat.Alignment = ParagraphAlignment.Justify;
builder.ParagraphFormat.Kashida = KashidaLevel.Medium;
builder.Writeln("تعلم اللغة العربية يفتح أبواب الأدب والعلوم والتاريخ للطلاب في كل مكان");
```

### Ruby and emphasis marks

A `Ruby` inline sets a short annotation, such as furigana or pinyin, in small type over the inlines it holds, as Word's
phonetic guide does. Its `Text` is the annotation and `TextFormat` its formatting over the base's; `FontSize` is its size
(half the base's when null) and `Raise` how far its baseline is above the base's (Word's `w:hpsRaise`), and `Position`,
`Alignment` and `Language` say the rest. Layout keeps the annotation over its base, never hanging over the characters
beside it, and makes the line tall enough for it. The annotation is not part of the paragraph's text: `GetText`, find and
replace and text output see the base, a tagged PDF puts base and annotation in RB and RT elements, and DOCX, ODT, HTML and
EPUB keep it. RTF and Markdown write the base.

`RunFormat.Emphasis` puts emphasis marks (Word's `w:em`) beside each character: a dot, a comma (CSS's sesame), a circle,
or a dot under the text. `RunFormat.CombineUpright` is tate-chu-yoko, Word's horizontal in vertical: Egap lays text out
horizontally, where it changes nothing, and keeps it for files that are read and written.

```csharp
using Egap.Documents;

Document doc = new();
Paragraph paragraph = doc.AddSection().Body.AddParagraph("Tokyo is ");
Ruby ruby = new(doc, "とうきょう") { FontSize = 5, Alignment = RubyAlignment.DistributeSpace };
ruby.Inlines.Add(new Run(doc, "東京"));
paragraph.Inlines.Add(ruby);
Run marked = paragraph.AddRun("大切");
marked.Format.Emphasis = EmphasisMark.Comma;
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

## Comparing documents

`DocumentCompare.Run` compares two versions of a document and returns a new one with the differences as tracked changes,
as Word's *Compare* does, and the list of those changes. [Comparing documents](comparing-documents.md) has the details.

```csharp
using Egap.Documents;
using Egap.Documents.Comparing;

DocumentComparison comparison = DocumentCompare.Run(Document.Load("contract-v1.docx"), Document.Load("contract-v2.docx"));
comparison.Document.Save("contract-redline.docx");
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

Layout hyphenates words at line ends where the document asks for it, as Word does: `DocumentSettings.AutoHyphenation`
turns it on, `ConsecutiveHyphenLimit` limits the lines in a row that end in a hyphen, `HyphenationZone` (Word's 18
points by default) keeps short words whole when the line would leave less room than that, in justified paragraphs too,
and `HyphenateCapitals` says whether words in capitals break. A paragraph with `SuppressAutoHyphens` is not hyphenated.
Each run's language picks the dictionary from `LayoutOptions.Hyphenation`, Egami's built-in patterns for some 35
languages by default; text in a language with no dictionary is not hyphenated, and a `HyphenationDictionaryMissing`
warning names the language. In a PDF, an automatic hyphen is marked as a soft hyphen, so copied and searched text reads
the word whole. `LayoutOptions.LineFill = LineFill.Optimal` chooses the line breaks that suit each paragraph as a whole,
as TeX does, for more even spacing; the default fills each line as far as it goes, as Word does.

```csharp
using Egap.Documents;
using Egap.Documents.Layout;

Document doc = Document.Load("report.docx");
doc.Settings.AutoHyphenation = true;
doc.Settings.ConsecutiveHyphenLimit = 2;

// Egami.Fonts.Layout's types: the built-in dictionaries plus one for a language Egami does not ship.
Egami.Fonts.Layout.HyphenationDictionaries dictionaries = new(Egami.Fonts.Layout.HyphenationDictionaries.BuiltIn);
using (FileStream patterns = File.OpenRead("hyph-sv.tex"))
{
    dictionaries.Add("sv", Egami.Fonts.Layout.Hyphenator.Load(patterns));
}

DocumentLayout pages = doc.Layout(new LayoutOptions { Hyphenation = dictionaries, LineFill = Egami.Fonts.Layout.LineFill.Optimal });
foreach (LayoutWarning warning in pages.Warnings)
{
    Console.WriteLine(warning);                                     // e.g. no dictionary for a language
}
```

A table with a `TableFormat.Position` floats: it sits where its anchors and alignment or offsets put it (the text, the
margins or the page), text after it wraps on the side with more room, and when it does not fit it goes on at the top of
the next page's body with its header rows repeated. The text after a table that breaks follows it on the page where it
ends, unless `WordCompatibility.AllowTextAfterFloatingTableBreak` is on; `DoNotBreakWrappedTables` keeps it on one page,
and `AllowOverlap = false` moves it down until it clears the floating tables before it. A paragraph with a
`ParagraphFormat.Frame` is an old-style Word frame: it and the paragraphs after it with the same frame are placed the same
way and never split, and a frame with a `DropCap` is a large first letter that the next paragraph wraps beside. Floating
tables and frames in table cells, headers, footers, notes and text boxes are laid out in the text. DOCX reads and writes
both (`w:tblpPr`, `w:tblOverlap`, `w:framePr`), HTML reads `float: left` and `float: right` on a table as a floating
table and writes it back, and a report table floats with `Floating`.

```csharp
using Egap.Documents;

Document doc = new();
Body body = doc.AddSection().Body;
Table facts = body.AddTable(3, 2);
facts.Format.Position = new TablePosition
{
    HorizontalAnchor = HorizontalAnchor.Margin,
    HorizontalAlignment = FloatingAlignment.End,      // against the right margin
    VerticalAnchor = VerticalAnchor.Paragraph,         // where the text after it starts
    DistanceLeft = 9,                                   // the text keeps 9 points away
};
body.AddParagraph("This text wraps beside the table on the left.");

Paragraph cap = body.AddParagraph("O");
cap.Format.Frame = new FramePosition { DropCap = DropCap.Drop, DropCapLines = 3 };
body.AddParagraph("nce upon a time, the first letter of this paragraph sat three lines tall.");
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

A layout's `Warnings` list what it laid out differently from what the document asks for, each with the node and its
page. A floating table or a framed paragraph floats only in a section's body; one in a table cell, header, footer, note
or text box is laid out in the text there, with a `FloatLaidOutInline` warning. A SmartArt diagram draws from the drawing
Word stores beside it, shapes, text and all; a file that stores none (Word 2007 wrote none) draws the diagram's text as a
stack of blocks in the theme's accent colours, with a `SmartArtDrawnAsList` warning. Saving as PDF keeps no layout, so give
`LayoutOptions.OnWarning` to hear of them; `egap convert` prints them as warnings.

```csharp
using Egap.Documents;
using Egap.Documents.Layout;
using Egap.Documents.Pdf;

Document letter = Document.Load("letter.docx");
foreach (LayoutWarning warning in letter.Layout().Warnings)
{
    Console.WriteLine(warning);
}

letter.SaveAsPdf("letter.pdf", new PdfExportOptions
{
    Layout = new LayoutOptions { OnWarning = warning => Console.WriteLine($"{warning.Kind} on page {warning.PageIndex + 1}") },
});
```

## Flex and grid layout

A `FlexContainer` lays its items out in a row or a column, as CSS flexbox does: each item starts from its basis or its
content's width, takes a share of the space left by its grow factor or gives some up by its shrink factor, and lines up
along and across its line by the container's `JustifyContent` and `AlignItems`. With `Wrap`, items that do not fit go on
the next line. A `GridContainer` places its items in columns and rows of tracks, as CSS grid does: tracks in points, per
cent, fractions of what is left (`fr`) or sized to their content, items placed by line, span or named area, and the rest
filled in by automatic placement. Pages break between a flex container's lines and between a grid's rows, and inside a
line or row too tall for the room left; rows an item spans stay together.

```csharp
using Egap.Documents;

Document invoice = new();
Body body = invoice.AddSection().Body;

FlexContainer header = new(invoice) { Layout = new FlexLayout { JustifyContent = LayoutAlignment.SpaceBetween, ColumnGap = 12 } };
header.AddItem("Acme Ltd");
header.AddItem("Invoice 2026-118").Box = new BoxStyle { PaddingLeft = 6, Width = TableWidth.Points(160) };
body.Blocks.Add(header);

GridContainer lines = new(invoice)
{
    Layout = new GridLayout { Columns = [GridTrack.Fraction(1), GridTrack.Points(60), GridTrack.Points(80)], ColumnGap = 6, RowGap = 2 },
};
foreach (string cell in (string[])["Item", "Qty", "Price", "Paper", "2", "40 NOK"])
{
    lines.AddItem(cell);
}

GridItem total = lines.AddItem("Total: 80 NOK");
total.Column = 2;
total.ColumnSpan = 2;
body.Blocks.Add(lines);
```

A `BoxStyle` draws a box around a container, an item or a group of blocks: margins, padding, borders, a background,
rounded corners and a shadow, with a width and a least height. A group with a box breaks across pages like any other
blocks; its box is drawn open where it breaks. Rounded corners and shadows draw on every Egami canvas; in a PDF a blurred
shadow is drawn as an image.

```csharp
using Egap.Documents;

Document letter = new();
ContentControl card = new(letter)
{
    Box = BoxStyle.Padded(10).WithBorder(new Border(BorderStyle.Single, 1, DocumentColor.FromHex("336699"))) with
    {
        Background = DocumentColor.FromHex("F4F8FC"),
        CornerRadius = BoxCorners.All(6),
        Shadow = new BoxShadow(0, 2, 6) { Opacity = 0.3 },
        MarginTop = 12,
    },
};
card.AddParagraph("Paid in full on 21 October.");
letter.AddSection().Body.Blocks.Add(card);
```

HTML's `display: flex` and `display: grid` read into these containers, with the properties listed on the
[HTML](../formats/html.md#css-layout) page; a box with `border-radius` or `box-shadow` becomes a group with a box. DOCX has
no such containers, so a container is saved as a borderless table with its items in the cells, and a boxed group as a
one-cell table; Word keeps the content and the cells' backgrounds, borders and padding, not the corners or the shadow.

A text box can fit its text (`FitToText`), have its own insets (`TextInsets`), rounded corners (`CornerRadius`) and a
shadow (`Shadow`). HTML's absolutely positioned and fixed boxes read as such text boxes, floating in front of the text.

For PDF output, see [PDF export and conversion](pdf-export-and-conversion.md).
