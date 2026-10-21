# Document formats

Egap.Documents reads each format below into one document model and writes the model back out, so any format it reads can
be saved as any format it writes. DOCX, PDF, plain text and Docling JSON are built in. HTML and Markdown come from the
add-on packages Egap.Documents.Html and Egap.Documents.Markdown.

Each format has its own page with its options and what it keeps. This page covers what they share: loading and saving
without naming the format, how a file's format is found, warnings and limits.

## At a glance

| Format | Package | Reads | Writes |
| --- | --- | --- | --- |
| [DOCX](docx.md) | Egap.Documents | DOCX, DOTX, DOCM and DOTM, strict and transitional | The same four kinds; DOCX to DOCX keeps what the model does not name |
| [HTML](html.md) | Egap.Documents.Html | HTML and XHTML, parsed as browsers parse it and styled through the CSS cascade | HTML (UTF-8), with the page setup as `@page` rules |
| [Markdown](markdown.md) | Egap.Documents.Markdown | CommonMark with GitHub's extensions and YAML front matter | GitHub Flavored Markdown (UTF-8) |
| [Plain text](text.md) | Egap.Documents | One paragraph per line | The text in reading order, for search and indexing |
| [Docling JSON](docling-json.md) | Egap.Documents | DoclingDocument JSON, from Docling or from Egap | Page layouts of a PDF, through `DoclingJson.Write` |
| PDF | Egap.Documents | Pages analysed into blocks, headings, lists, tables and figures | Tagged PDF and PDF/A |

PDF in and out is covered in [PDF export and conversion](../guides/pdf-export-and-conversion.md).

## Loading and saving

`Document.Load` and `Document.Save` work out the format by themselves. `Load` looks at the file's first bytes and, for
text, at its extension; `Save` goes by the extension. DOCX, PDF, plain text and Docling JSON need nothing more. HTML and
Markdown join them once you register their add-on at startup; until then, loading such a file throws a
`NotSupportedException` that names the package and the call to make. `Document.Identify` tells which format a file is in
without loading it and without any add-on, so a program can say what a file needs.

```csharp
using Egap.Documents;

HtmlFormat.Register();                                            // once at startup: Load and Save now read and write HTML
MarkdownFormat.Register();
DocumentFormatKind kind = Document.Identify("upload.bin");        // Pdf, Docx, Html, Markdown, Text, DoclingJson or Unknown
Document notes = TextFormat.Load("notes.txt");                    // one paragraph per line
```

`Identify` decides in this order:

1. A ZIP package, or an old binary Word file, is `Docx`. Reading an old binary Word file fails with an error that says so.
2. `%PDF-` in the first 1024 bytes is `Pdf`.
3. Text whose extension names HTML (`.html`, `.htm`, `.xhtml`), Markdown (`.md`, `.markdown`, `.mdown`, `.mkd`) or plain
   text (`.txt`, `.text`) is that format.
4. A JSON object that names the `DoclingDocument` schema is `DoclingJson`.
5. Text that starts with a tag is `Html`, and other text is `Markdown`.
6. Binary data no format knows is `Unknown`.

`Save` writes `.docx`, `.dotx`, `.docm` and `.dotm` as Word's, `.pdf` as PDF, `.txt` as plain text, and, once their
add-on is registered, `.html` and `.htm` as HTML and `.md` and `.markdown` as Markdown. Any other extension throws an
`ArgumentException`.

Each format also has its own class (`DocxFormat`, `HtmlFormat`, `MarkdownFormat`, `TextFormat`, `DoclingJson`) with its
own options, and members on `Document` that forward to it, such as `Document.LoadDocx`, `ToHtml` and `SaveAsMarkdown`.
Calling a format directly needs no `Register()`.

## Warnings

Each `Load` has a form with an `out` list of `ConversionWarning`s for what could not be read as it was, and each `Save`
returns a list of what the format could not hold, so a program can log it or show it.

## Limits

DOCX and HTML files are read under `DocumentLimits`, so an untrusted file cannot exhaust memory. Pass your own in the
format's load options (`DocxLoadOptions.Limits`, `HtmlLoadOptions.Limits`); a file past a limit throws a
`DocumentLimitException`.

| Limit | Default | Does |
| --- | --- | --- |
| `MaxEntries` | 10,000 | The most entries a DOCX package may have |
| `MaxPartSize` | 512 MiB | The largest uncompressed part, or the largest HTML page |
| `MaxTotalSize` | 2 GiB | The most uncompressed bytes in all parts together |
| `MaxCompressionRatio` | 1,000 | The highest compression ratio a part over 1 MiB may have, against ZIP bombs |
| `MaxDepth` | 256 | How deep elements, tables and other structures may nest; deeper HTML is kept as plain text |
