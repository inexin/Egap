# OpenDocument Text

Egap.Documents reads OpenDocument Text, the format LibreOffice and OpenOffice write and Word opens: ODT documents, OTT
templates and flat FODT files, from ODF 1.2 to 1.4. It needs no add-on package. Writing ODT is not there yet; save a
document read from ODT as DOCX, RTF, PDF or any other format Egap writes.

## Reading

`OdtFormat.Load` reads a file or a stream. `Document.Load` and `Document.Identify` know ODT and OTT by the `mimetype`
entry an OpenDocument package starts with, and FODT by its root element, so a file is found whatever its extension.

```csharp
using Egap.Documents;

Document minutes = OdtFormat.Load("minutes.odt", out IReadOnlyList<ConversionWarning> warnings);
Document same = Document.Load("minutes.odt");                     // found by its mimetype entry
Document flat = OdtFormat.Load("minutes.fodt");                   // one XML file, pictures inline
minutes.SaveAsDocx("minutes.docx");
```

The reader follows the OpenDocument 1.3 specification and what LibreOffice writes. It reads:

- The package: the `mimetype` entry, the manifest, the content, styles, meta and settings parts and the pictures, and
  the same parts in one flat XML file. Document information and custom properties come from the meta part.
- Common styles as the model's styles, with their base and next style. A style LibreOffice names after one of Word's
  built-in styles gets Word's style id, such as `Heading1` for "Heading 1" and `Normal` for "Default Paragraph Style".
  Automatic styles become direct formatting, and default styles the document defaults.
- Character and paragraph formatting: fonts, sizes, colours, highlighting, underlines and strikes, raised and lowered
  text, spacing, scaling, caps, hidden text, languages, alignment, indents, spacing, line height, borders, shading,
  tab stops, keeping together and with the next paragraph, widows and orphans, and page and column breaks.
- Lists from list styles, with each level's numbering, labels and indents; a new list restarts its numbers, and a list
  that continues another (`text:continue-numbering`, `text:continue-list`) goes on counting. Headings are numbered by
  the outline style.
- Tables with their columns, header rows, cells spanning columns and rows, nested tables, borders, shading, padding
  and alignment.
- Page styles as sections: each master page's size, orientation, margins, columns and page numbering, its headers and
  footers, and a different first page or left page. Sections with columns become continuous sections.
- Footnotes and endnotes, comments with their ranges, authors, dates and whether they are resolved, bookmarks, and links.
- Fields as Word's fields: page numbers and counts, dates and times, document information, file names, chapters,
  sequences, references to bookmarks, sequences and notes, custom properties and mail merge columns. A table of contents
  or other index is a `TOC` field holding its entries. Fields Word has nothing like, such as variables, keep their text.
- Tracked insertions and deletions with their authors and dates, a deletion that spans paragraphs too. Tracked
  formatting changes are read as accepted, with a warning.
- Frames: pictures (stored in the package or, in a flat file, inline), text boxes, rectangles, ellipses and lines,
  inline or floating, placed and wrapped as the frame says. A frame holding only a table, as LibreOffice writes Word's
  floating tables, is a floating table.

Charts, formulas and other embedded objects come in as islands that keep their markup and draw the picture the file
holds for them; other drawing shapes and groups are islands too. Each of those is reported as a `ConversionWarning`,
and so is a picture the package does not hold. Markup the reader does not know is kept in the document's extension data
under the format name `odt`.

A password-protected OpenDocument file throws a `DocumentException` that says so: Egap does not read ODF encryption yet.

## Limits

An ODT package is read under `DocumentLimits` like DOCX: the number of entries, the size of each part and of all of them,
the compression ratio and the nesting depth. Pass your own in `OdtLoadOptions.Limits`, or in `DocumentLoadOptions.Limits`
with `Document.Load`; a file past a limit throws a `DocumentLimitException`.

```csharp
using Egap.Documents;

Document upload = OdtFormat.Load("upload.odt", new OdtLoadOptions
{
    Limits = new DocumentLimits { MaxPartSize = 64 * 1024 * 1024 },
});
```
