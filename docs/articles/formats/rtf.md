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
- Automatic hyphenation (`\hyphauto`, `\hyphconsec`, `\hyphhotz`, `\hyphcaps`), which the writer saves too.

What the model cannot hold comes in as an island: equations (`\mmath`) keep their RTF and their plain text and lay out
and draw as Word sets them, and other drawing shapes and embedded objects (`\object`) keep their RTF and draw the
picture the file holds for them. Each of those is reported as a `ConversionWarning`, and so is a picture in a format
Egap does not read (old Macintosh PICT and Windows bitmap pictures). Groups marked `\*` that the reader does not know,
such as Word's theme and data store, are kept in the document's extension data under the format name `rtf`.

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
- Semicolons and tabs in font, style, list and author names are left out: a semicolon ends a table entry.

## Editing restrictions

An RTF file keeps Word's editing restriction too (`\readprot`, `\annotprot`, `\revprot` and `\formprot`, with the
password hash in `{\*\passwordhash}`), and its editable ranges (`{\*\protstart}` and `{\*\protend}`, with the people they
name in `{\*\protusertbl}`). Both read into `Document.Protection` and the editable range marks and are written back as
Word writes them, so a restriction and its password survive RTF to DOCX and back. The
[DOCX page](docx.md#editing-restrictions) shows the API.

## RTF inside DOCX

A DOCX file can hold another document as an embedded part, placed with a `w:altChunk` element; Word imports the part when
it opens the file. When the part is RTF (content type `application/rtf` or `text/rtf`), `DocxFormat.Load` does the same:
it reads the part with the RTF reader and puts its blocks where the chunk was, in the body, a table cell, a header or a
note. Styles the document already has by the same id are the document's, as when Word merges the chunk; the chunk's other
styles and its lists come along. The chunk's own sections, headers and footers are not taken, and notes and comments in a
chunk inside a header, note, comment or text box, which cannot hold them, are left out with a warning.

Saving writes the imported blocks as ordinary content, so the saved file has no altChunk. An RTF chunk that cannot be read
stays as it was, a `BlockIsland` that keeps the element and its part, and `DocxFormat.Load` reports it as a
`ResourceNotLoaded` warning. Chunks in other formats (HTML, MHT, plain text, DOCX) stay block islands too.

```csharp
using Egap.Documents;

Document merged = DocxFormat.Load("with-chunk.docx", out IReadOnlyList<ConversionWarning> warnings);
foreach (ConversionWarning warning in warnings)
{
    Console.WriteLine(warning);                                  // an RTF chunk that could not be read
}
```

## From the command line

`egap convert` reads and writes RTF in every direction it supports: RTF to PDF, DOCX, HTML, Markdown or text, and any of
those, and PDF, to RTF. `--set unknown-groups=false` leaves out the unknown `\*` groups an RTF input kept.

```sh
egap convert letter.rtf -o letter.pdf
egap convert report.docx -o report.rtf
egap convert old.rtf -o clean.rtf --set unknown-groups=false
```

## Known limits

Reading:

- Old Macintosh PICT pictures and Windows bitmap (`\wbitmap`) pictures are left out with a warning.
- Shape groups, other drawing shapes and embedded objects are islands: they draw their picture, and their content is
  not in the model. Equations are islands too; they draw as math, but cannot be edited through the model.
- Line numbering (`\linemod`) is not read: the model has none.
- A style keeps only what it changes from its base chain; a value it repeats from further up is dropped, which leaves
  the formatting in effect as it was. Picture sizes are whole twips.

Writing:

- Theme colours and fonts are written as what they are in the theme, and a hyperlink as a `HYPERLINK` field.
- Two top-level tables in a row become one table when read back, as RTF has nothing between rows to part them.
- Formatting a table style gives conditionally (a bold first row) is written as each cell's own formatting, and every
  cell's width is written.
- Shapes in the text become floating shapes anchored to their line; a picture's source address and an SVG picture's
  original are not written (the SVG is drawn as a PNG).
- Move revisions are written as insertions and deletions, and revisions of field characters are not written.
- Not written: a paragraph's "no list" override, a row's footer and hidden flags, a table style's band sizes, overline,
  word spacing, OpenType features and a comment's Done flag.

The Word checks wait for a Word install: the RTF round trips and reopen checks so far use LibreOffice's RTF, not Word's.

## Limits

`RtfLoadOptions.Limits` keeps an untrusted file in bounds: `MaxDepth` caps how deep groups nest, and `MaxPartSize` caps
each picture and each block of binary data. A file past a limit throws a `DocumentLimitException`; a file that does not
start with `{\rtf` throws a `DocumentException`.

```csharp
using Egap.Documents;

RtfLoadOptions options = new() { Limits = new DocumentLimits { MaxDepth = 64, MaxPartSize = 16 * 1024 * 1024 } };
Document upload = RtfFormat.Load("upload.rtf", options);
```
