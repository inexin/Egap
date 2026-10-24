# EPUB

EPUB books come from the add-on package Egap.Documents.Html, the same one that reads and writes HTML. It reads EPUB 2 and
EPUB 3 books into the document model and writes the model as an EPUB 3.3 book, so a DOCX, HTML or Markdown file can become
an e-book and a book can become a PDF or a Word file.

```sh
dotnet add package Egap.Documents.Html
```

The format's class is `EpubFormat`, in namespace `Egap.Documents`. `HtmlFormat.Register()` at startup lets
`Document.Load`, `Document.Save` and `PdfConvert.FromFile` handle `.epub` files too. Calling `EpubFormat` or the members it
adds to `Document` (`Document.LoadEpub`, `ToEpub`, `SaveAsEpub`) needs no registration.

```csharp
using Egap.Documents;
using Egap.Documents.Pdf;

Document book = Document.LoadEpub("novel.epub");
book.SaveAsPdf("novel.pdf");
Document.LoadDocx("handbook.docx").SaveAsEpub("handbook.epub");
```

## Reading

A book is read into one document. Each file of the book's reading order (its spine) becomes a section that starts on a new
page, read through the HTML reader with the book's own style sheets, for screen media unless `Media` says `"print"`. The
cover image gets a section of its own at the start unless the first chapter already shows it. Links from one chapter to
another become links inside the document, and an id that two chapters both use is renamed in the second.

The book's table of contents (the EPUB 3 navigation document, or the EPUB 2 NCX) becomes the outline: each entry's
target paragraph gets the entry's outline level unless it is a heading already, so a PDF made from the book gets the same
bookmarks and a table of contents field lists the same entries.

The package metadata becomes document properties:

| EPUB | Document |
| --- | --- |
| `dc:title` | `Properties.Title` |
| `dc:creator` (each) | `Properties.Author`, joined with "; " |
| `dc:subject` (each) | `Properties.Keywords`, joined with "; " |
| `dc:description` | `Properties.Comments` |
| `dc:date` | `Properties.Created` |
| `dcterms:modified` | `Properties.Saved` |
| `dc:language` | The default language of the text |
| `dc:publisher`, `dc:rights`, `dc:contributor`, the unique identifier | Custom properties `Publisher`, `Rights`, `Contributors`, `Identifier` |

Fonts that the book's style sheets declare with `@font-face` are loaded into `Document.EmbeddedFonts`, with the IDPF and
Adobe font obfuscation undone. Fixed-layout books (comics, picture books) are read with one section per page, the page
the size of the page's viewport and its positioned content placed as the HTML reader places it. Audio, video, media
overlays and scripts are left out with a `MediaDropped` warning; scripts never run.

```csharp
using Egap.Documents;

Document book = EpubFormat.Load("novel.epub", out IReadOnlyList<ConversionWarning> warnings, new EpubLoadOptions
{
    Media = "print",
    UserStyleSheets = ["body { font-size: 11pt; }"],
});
```

| Option | Default | Does |
| --- | --- | --- |
| `ResourceResolver` | None | Fetches what a chapter links to outside the book by absolute URL, or returns null to leave it out |
| `Media` | `"screen"` | The media the book's style sheets apply for: `"screen"` or `"print"` |
| `UserStyleSheets` | None | Style sheets applied as the user's, after the default one and before the book's own |
| `Limits` | `DocumentLimits` defaults | Entries, entry and total sizes, compression ratio and nesting (see [Limits](index.md#limits)) |

Egap does not remove DRM. A book whose files are encrypted with Adobe ADEPT, Readium LCP, Apple FairPlay or any scheme
other than font obfuscation throws a `DocumentException` that names the scheme. A book whose entries point outside the
container is refused the same way, and nothing outside the book is fetched unless you give a resolver.

## Writing

The document is written as an EPUB 3.3 book that EPUBCheck, the W3C validator, passes without errors:

- XHTML chapter files from the HTML writer, split where `SplitAt` says: at section breaks and at level 1 headings by
  default. A book read from EPUB keeps its own chapter files and their ids.
- A navigation document with the outline down to `NavigationDepth` levels, and for EPUB 2 reading systems a `toc.ncx`
  and a `guide`.
- The pictures, written once each; pictures in formats EPUB does not hold are written as PNG.
- The metadata from the document properties, with the book's identifier kept when it was read from EPUB, else a
  `urn:uuid` made from the title and author.
- Accessibility metadata from what the document has (`schema:accessMode`, `accessibilityFeature`, `accessibilityHazard`
  and a summary), as the EPUB Accessibility 1.1 specification asks, with the language and the pictures' alternative text.

```csharp
using Egap.Documents;

Document report = Document.LoadDocx("report.docx");
EpubFormat.Save(report, "report.epub", new EpubSaveOptions
{
    SplitAt = EpubSplit.HeadingLevel1,
    NavigationDepth = 2,
    CoverImage = new ImageData(File.ReadAllBytes("cover.jpg"), "image/jpeg"),
    EmbedFonts = true,
});
```

| Option | Default | Does |
| --- | --- | --- |
| `SplitAt` | `SectionBreak` and `HeadingLevel1` | Where a new chapter file starts; `None` writes one file |
| `NavigationDepth` | 3 | How many outline levels the table of contents shows (1 to 9) |
| `EmbedFonts` | Off | Whether `Document.EmbeddedFonts` go into the book, subset to the characters used and obfuscated as EPUB says |
| `CoverImage` | None | A picture for a cover page before the first chapter |
| `Identifier` | Kept, or made from the title and author | The book's unique identifier |
| `AccessibilitySummary` | A sentence about what the book has | The accessibility summary in the package |
| `Modified` | The document's saved time, else now | The book's last-modified time |

Embedding fonts is a licence question only you can answer, so it is off until you turn it on. A reading system that lacks
a font falls back to its own.
