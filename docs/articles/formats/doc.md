# DOC

Egap.Documents reads Word's binary documents and templates, the DOC and DOT files of Word 97 to 2003, and those of Word 6
and Word 95. It needs no add-on package. Egap does not write DOC: save a document read from one as DOCX, or in any other
format Egap writes.

## Reading

`Document.Load` reads a DOC file, and `Document.Identify` reports it as `DocumentFormatKind.Doc`. Both go by the file's
bytes: a compound file ([MS-CFB]) with a `WordDocument` stream is a binary Word file, whatever its extension.

```csharp
using Egap.Documents;

Document letter = Document.Load("letter.doc", out IReadOnlyList<ConversionWarning> warnings);
DocumentFormatKind kind = Document.Identify("letter.doc");        // Doc, or Encrypted for a password-protected one
letter.SaveAsDocx("letter.docx");
```

The reader follows [MS-DOC] and what Word writes. It reads:

- The text through the piece table, fast-saved files included, in Unicode or in the file's code page.
- Character and paragraph formatting, the style sheet, fonts, tab stops, borders and shading.
- Lists from the list and list override tables, and the paragraph numbering of Word 6 and Word 95.
- Tables, nested tables and merged cells.
- Sections with their page setup and columns, and their headers and footers.
- Footnotes and endnotes, comments with their ranges, authors and initials, fields, bookmarks, and tracked insertions and
  deletions with their authors and dates.
- The document settings Word keeps in the file (the DOP), such as the default tab stop and track changes.
- Pictures in the line (PNG, JPEG, EMF, WMF, Macintosh PICT, device-independent bitmaps and TIFF) with their size, scale
  and cropping.
- Floating pictures, text boxes, rectangles, ellipses and other simple shapes from the file's drawings ([MS-ODRAW]),
  placed as Word places them, and the members of shape groups.

What the reader cannot place is left out with a `ConversionWarning`: drawing shapes Egap has no kind for (WordArt, for
one), pictures in a format it does not read, and the headers, footers and drawings of Word 6 and Word 95 files.

## Passwords

A DOC file protected with a password to open it is `Encrypted` to `Document.Identify`, and loads with its password.
`DocumentLoadOptions` takes the password, or a callback that asks for one; a missing or wrong password throws a
`DocumentPasswordException`. The reader opens the three kinds of protection Word 97 and later write: RC4 CryptoAPI
encryption (the default from Word 2002 on), RC4 encryption ("Office 97/2000 compatible") and XOR obfuscation ("weak
encryption").

```csharp
using Egap.Documents;

Document report = Document.Load("report.doc", new DocumentLoadOptions { Password = "secret" });
Document asked = Document.Load("report.doc", new DocumentLoadOptions
{
    PasswordCallback = attempt => attempt <= 3 ? Console.ReadLine() : null,    // null gives up
});
```

Password-protected Word 6 and Word 95 files are refused with a `DocumentException`.

## From the command line

`egap convert` reads DOC files like any other input: to PDF, DOCX, ODT, RTF, HTML, Markdown or text. `--password` opens a
password-protected one.

```sh
egap convert letter.doc -o letter.pdf
egap convert old/*.doc -o converted/ --to docx
egap convert report.doc -o report.docx --password-env REPORT_PASSWORD
```

## Known limits

- Word 2 and earlier, and Word 4 and 5 for the Macintosh, are not read.
- Formatting revisions (a tracked change of formatting) are not read; insertions and deletions are.
- OLE objects come in as the picture the file keeps for them; their data is not read.
- Theme colours and fonts are what Word saved for them: a binary file keeps the colour, not the theme slot.
- Headers, footers and drawings of Word 6 and Word 95 files are left out with a warning, and so are their comment ranges.

## Limits

`DocumentLoadOptions.Limits` keeps an untrusted file in bounds: `MaxPartSize` caps each stream of the compound file and
the text, `MaxEntries` caps tables such as the paragraph count of a part and the list and style tables, and `MaxDepth`
caps how deep tables nest. A file past a limit throws a `DocumentLimitException`; a damaged file throws a
`DocumentException`.

```csharp
using Egap.Documents;

DocumentLoadOptions options = new() { Limits = new DocumentLimits { MaxPartSize = 16 * 1024 * 1024 } };
Document upload = Document.Load("upload.doc", options);
```
