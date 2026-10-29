# DOCX

Egap.Documents reads and writes Word's files: documents (`.docx`), templates (`.dotx`) and their macro-enabled forms
(`.docm`, `.dotm`), strict and transitional. It needs no add-on package.

## Reading

A Word file reads into the model with Word's style model, lists numbered as Word numbers them, tables, fields, comments
and tracked changes; the [flow documents guide](../guides/flow-documents.md) shows what you can do with them. What the model
does not name travels along in each node's extension data. Macros are kept and never run. An old binary Word file (`.doc`)
is recognized and refused with an error that says so. `DocxLoadOptions.Limits` keeps untrusted files in bounds (see
[Limits](index.md#limits)).

```csharp
using Egap.Documents;

Document report = Document.LoadDocx("report.docx", out IReadOnlyList<ConversionWarning> warnings);
DocxKind? kind = DocxFormat.KindOf(report);                       // Document, Template or a macro-enabled kind
report.SaveAsDocx("report-copy.docx");                            // what the model does not name is kept
DocxFormat.Save(report, "report.dotx", new DocxSaveOptions { Kind = DocxKind.Template });
```

## Word compatibility

Word lays a file out by the rules of the Word version that made it: `DocumentSettings.CompatibilityMode` says which (15
for Word 2013 and later, 14 for 2010, 12 for 2007, 11 and below for older files), and the settings part's compatibility
options switch single layout rules on or off. Egap reads both and lays the document out by the same rules.

`DocumentSettings.WordCompatibility` names each option Egap acts on. A flag is what the file or your code set, else the
default for the compatibility mode, so changing the mode changes every flag nobody set:

| Flag | When on |
| --- | --- |
| `AllowSpaceOfSameStyleInTable` | Contextual spacing is ignored in table cells. |
| `DoNotUseIndentAsNumberingTabStop` | The tab after a list label skips the hanging indent and goes to the next tab stop. |
| `SplitPageBreakAndParagraphMark` | After a page break that ends a paragraph, the paragraph mark takes a line on the next page. |
| `OverrideTableStyleFontSizeAndJustification` | A table style's font size and alignment win over an 11 or 12 point, left-aligned default paragraph style. |
| `EnableOpenTypeFeatures` | Runs' ligatures, contextual alternates, stylistic sets and number forms are used. |
| `DoNotBreakWrappedTables` | A floating table stays on one page. |
| `AllowTextAfterFloatingTableBreak` | Text may follow a floating table on the page where it breaks. |
| `TableIndentFromCellText` | A table's indent is measured to its first cell's text (Word before 2013; not saved to DOCX). |

A new document has Word 2013's options, and saving it writes them. A file's options go back as they were read; a flag
you set is written, and the options Egap does not act on stay in the file and are listed in `OtherSettings`.

```csharp
using Egap.Documents;

Document old = Document.LoadDocx("word-2007.docx");
WordCompatibility compat = old.Settings.WordCompatibility;
bool legacyTables = !compat.OverrideTableStyleFontSizeAndJustification;
string? ulTrailSpace = compat.OtherSettings.GetValueOrDefault("ulTrailSpace");   // "1" when the file has it

compat.EnableOpenTypeFeatures = true;          // shape with the runs' ligatures and number forms
old.Settings.CompatibilityMode = 15;           // the flags nobody set follow Word 2013's rules
compat.ResetToModeDefaults();                  // forget the file's values too
```

## Writing

DOCX to DOCX is lossless: parts and paragraphs you did not edit are written as they were read. Saving to a path takes the
kind from the extension, so `report.dotx` is a template; saving to a stream keeps the kind the document was read as (a new
document is a `.docx`). `DocxSaveOptions.Kind` overrides both. A kind that holds no macros drops the macros a `.docm` or
`.dotm` had.

A document read from another format saves as DOCX with what Word can hold. Some things the model has Word does not:

- Word has no word spacing or overline; it keeps the OpenType features it knows (ligatures, number forms and spacing,
  stylistic sets, contextual alternates). See [Text options](../guides/flow-documents.md#text-options).
- Paging settings on groups of blocks, clamped lines and balanced columns are not saved; the content is. See
  [Page layout](../guides/flow-documents.md#page-layout).
- Margin boxes at the top and bottom of the page are saved as a three-part table in the header or footer; those in the
  corners and at the sides are left out with a warning.
- Flex and grid containers are saved as borderless tables with their items in the cells, and a group with a box as a
  one-cell table; the cells keep the items' backgrounds, borders and padding, not rounded corners or shadows. See
  [Flex and grid layout](../guides/flow-documents.md#flex-and-grid-layout).
- An SVG picture is saved with the PNG stand-in in `Picture.Fallback`, as Word stores SVG pictures, so older readers show
  it too. [Barcodes](../guides/flow-documents.md#barcodes) get one at 300 pixels per inch.
