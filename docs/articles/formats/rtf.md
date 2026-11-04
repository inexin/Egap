# RTF

Egap.Documents reads and writes RTF, the Rich Text Format that Word, WordPad, TextEdit and many other programs write. It
needs no add-on package.

## Reading

`RtfFormat.Load` reads a file or a stream, and `RtfFormat.Parse` reads RTF held in a string. `Document.Load` and
`Document.Identify` recognize RTF by its first bytes (`{\rtf`, after a byte order mark or white space), so an RTF file is
found whatever its extension.

```csharp
using Egap.Documents;

Document letter = RtfFormat.Load("letter.rtf", out IReadOnlyList<ConversionWarning> warnings);
Document same = Document.Load("letter.rtf");                      // found by its first bytes
Document note = RtfFormat.Parse(@"{\rtf1\ansi Hello, {\b world}.\par}");
letter.SaveAsDocx("letter.docx");
```

The reader follows the RTF 1.9.1 specification and what Word 2007 and later write. It reads:

- Character and paragraph formatting, fonts, colours, borders, shading, tab stops and frames.
- The style sheet, with each style's base, next and linked style. A style named as a Word built-in style gets Word's
  style id, such as `Heading1` for "heading 1".
- Lists from the list and list override tables, and the older `\pn` paragraph numbering, which becomes a list of its own.
- Text in any code page: `\ansicpg`, each font's `\fcharset` and `\cpg`, Unicode characters (`\u` with `\uc`) and
  binary data (`\bin`).
- Tables, nested tables (`\itap`) and cells merged across (`\clmgf`, `\clmrg`) and down (`\clvmgf`, `\clvmrg`). The grid
  comes from the cells' edges.
- Sections with their page setup, columns and page numbering; headers and footers, first-page and even-page ones too.
- Footnotes and endnotes, with their own marks.
- Fields as Word writes them (`\fldinst` and `\fldrslt`), bookmarks and hyperlinks, which are `HYPERLINK` fields.
- Comments with their ranges, authors, initials, dates and replies, and tracked insertions, deletions and formatting
  changes with their authors and dates.
- Pictures in PNG, JPEG, EMF, WMF and device-independent bitmaps, with their size, scale and cropping.
- Text boxes, rectangles, ellipses and other simple shapes, and floating pictures (`\shp`), placed as Word places them.
- Document information (`\info`) and custom properties (`\userprops`).

What the model cannot hold comes in as an island: equations (`\mmath`) keep their RTF and their plain text, and other
drawing shapes and embedded objects (`\object`) keep their RTF and draw the picture the file holds for them. Each of
those is reported as a `ConversionWarning`, and so is a picture in a format Egap does not read (old Macintosh PICT and
Windows bitmap pictures). Groups marked `\*` that the reader does not know, such as Word's theme and data store, are kept
in the document's extension data under the format name `rtf`.

## Writing

`RtfFormat.Save` writes a document to a path or a stream, and `Document.Save` writes RTF for a path ending in `.rtf`. Both
return the `ConversionWarning`s for what RTF could not hold.

```csharp
using Egap.Documents;

Document report = Document.Load("report.docx");
IReadOnlyList<ConversionWarning> warnings = RtfFormat.Save(report, "report.rtf");
report.Save("copy.rtf");                                          // RTF by its extension

using MemoryStream stream = new();
RtfFormat.Save(report, stream, new RtfSaveOptions { KeepUnknownGroups = false });
```

The writer writes RTF 1.9.1 as Word 2007 and later write it, so Word, LibreOffice and WordPad open it as they open
Word's own files:

- The header: `\ansi\ansicpg1252`, the default fonts, the font table with each font's family and character set, the
  colour table, the document defaults, the style sheet with each style's base, next and linked style, the list and list
  override tables, the revision authors, document information and custom properties, and the page setup.
- Sections with their page setup and columns, and their headers and footers.
- Paragraphs and runs with all their formatting, tabs, borders, shading and frames. RTF text carries its whole
  formatting: each paragraph and run says what its styles give as well as its own formatting, as Word writes it. In a
  table with a table style, the text also says what the style's conditional formatting (a bold first row, banded
  shading) gives it, which RTF has no place for.
- Tables with merged cells, nested tables (`\itap`, `\nesttableprops`), borders, shading, widths and margins.
- Fields (`\fldinst` and `\fldrslt`), bookmarks, hyperlinks as `HYPERLINK` fields, footnotes and endnotes, comments
  with their ranges and replies, and tracked insertions, deletions and formatting changes with the revision table.
- Pictures: PNG, JPEG and EMF as they are, WMF without its placeable header, and other images (GIF, BMP, TIFF, WebP...)
  converted to PNG; an SVG picture is drawn as a PNG. Floating pictures, text boxes and simple shapes are `\shp` groups.

Text is 7-bit: a character outside printable ASCII is a `\uN` escape with a one-character fallback for readers without
Unicode (`\uc1`), and a character past U+FFFF is its two surrogates. A document read from RTF writes back what the
reader kept as it was: equations, shape groups and embedded objects (islands), and unknown `\*` groups where they sat.
`RtfSaveOptions.KeepUnknownGroups = false` leaves the unknown groups out.

RTF has no place for some of what the model holds; saving writes what comes closest, with a warning where it leaves
something out:

- Margin boxes are left out; content controls give their content, and flex and grid containers the borderless tables
  DOCX gives them.
- A theme colour or theme font is written as the colour or font it is in the document's theme.
- A shape in the text is anchored to its line, as RTF shapes float.
- Islands from other formats (HTML and DOCX ones) are written as their picture, or their text.

## Limits

`RtfLoadOptions.Limits` keeps an untrusted file in bounds: `MaxDepth` caps how deep groups nest, and `MaxPartSize` caps
each picture and each block of binary data. A file past a limit throws a `DocumentLimitException`; a file that does not
start with `{\rtf` throws a `DocumentException`.

```csharp
using Egap.Documents;

RtfLoadOptions options = new() { Limits = new DocumentLimits { MaxDepth = 64, MaxPartSize = 16 * 1024 * 1024 } };
Document upload = RtfFormat.Load("upload.rtf", options);
```
