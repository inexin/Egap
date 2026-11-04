# RTF

Egap.Documents reads RTF, the Rich Text Format that Word, WordPad, TextEdit and many other programs write. It needs no
add-on package. Writing RTF is not supported yet.

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

## Limits

`RtfLoadOptions.Limits` keeps an untrusted file in bounds: `MaxDepth` caps how deep groups nest, and `MaxPartSize` caps
each picture and each block of binary data. A file past a limit throws a `DocumentLimitException`; a file that does not
start with `{\rtf` throws a `DocumentException`.

```csharp
using Egap.Documents;

RtfLoadOptions options = new() { Limits = new DocumentLimits { MaxDepth = 64, MaxPartSize = 16 * 1024 * 1024 } };
Document upload = RtfFormat.Load("upload.rtf", options);
```
