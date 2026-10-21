# Markdown

Markdown comes from the add-on package Egap.Documents.Markdown. It reads CommonMark 0.31 with GitHub's extensions and
YAML front matter into the document model, and writes the model as GitHub Flavored Markdown. Parsing is
[Markdig](https://github.com/xoofx/markdig)'s.

```sh
dotnet add package Egap.Documents.Markdown
```

The format's class is `MarkdownFormat`, in namespace `Egap.Documents`. Call `MarkdownFormat.Register()` once at startup so
that `Document.Load`, `Document.Save` and `PdfConvert.FromFile` handle `.md` files too. Calling `MarkdownFormat` or the
members it adds to `Document` (`Document.ParseMarkdown`, `Document.LoadMarkdown`, `ToMarkdown`, `SaveAsMarkdown`) needs no
registration.

```csharp
using Egap.Documents;
using Egap.Documents.Pdf;

MarkdownFormat.Register();                                       // once at startup: Document.Load and Save know .md
Document notes = Document.LoadMarkdown("notes.md");
notes.SaveAsDocx("notes.docx");
string markdown = Document.LoadDocx("report.docx").ToMarkdown();
PdfConvert.FromMarkdown("# Hello", "hello.pdf");
```

## Reading

Headings take the Heading 1 to 6 styles, code the HTML Code and HTML Preformatted styles, and lists Word list definitions
built from their nesting. Files are read as UTF-8, or in the encoding a byte order mark names.

`MarkdownLoadOptions.Extensions` picks what is read beyond CommonMark; all of GitHub's are on by default.

| Extension | Reads |
| --- | --- |
| `Tables` | GitHub's pipe tables |
| `TaskLists` | Task list items, `- [ ]` and `- [x]` |
| `Strikethrough` | `~text~` and `~~text~~` |
| `Autolinks` | `www.`, `http://`, `https://` and e-mail addresses without angle brackets |
| `Footnotes` | `[^label]` with `[^label]:` definitions |
| `FrontMatter` | YAML front matter between `---` lines at the very start, kept and mapped to document properties |
| `GitHub` | All of the above (the default); `None` reads CommonMark only |

```csharp
using Egap.Documents;

Document readme = MarkdownFormat.Load("README.md", new MarkdownLoadOptions { Extensions = MarkdownExtensions.GitHub });
```

Images are not fetched unless you pass `MarkdownLoadOptions.ImageResolver`, which gives the bytes for a URL or null. An
image left unfetched becomes a picture that links to its URL. `PdfConvert.FromFile` reads a Markdown file's images from the
file's own folder, and never from the network.

## Writing

The model is written as GitHub Flavored Markdown in UTF-8 with LF line ends. Markdown holds less than the model, so each
thing left out comes back as a warning. Front matter that was read is always written back.

Pictures with an address of their own are written as links to it. The others, such as images embedded in a DOCX, follow
`Images`: by default each one goes to `ImageResolver`, or, when saving to a file, into a folder beside it.

| Option | Default | Does |
| --- | --- | --- |
| `Images` | `Files` | What happens to pictures without an address: `Files`, `DataUris` (which GitHub and many viewers do not show), `Placeholders` (an HTML comment marks each one) or `Omit` (left out, with a warning) |
| `ImageResolver` | None | Stores each image (browser-ready bytes, media type and a suggested name) and returns the address to write |
| `ImagesFolder` | `<name>.images` | The folder beside the Markdown file that images are written to |
| `ImageWriter` | None | Stores a picture and returns its address, or null to leave it out with a warning |
| `EmbedImages` | `false` | The same as `Images` set to `DataUris` |
| `FigureResolution` | 150 | Dots per inch for vector figures (Word's EMF and WMF pictures) written as PNG |
| `VectorFiguresAsSvg` | `false` | Writes vector figures as SVG instead of PNG |
| `MinImageSize` | 6 | Pictures narrower or shorter than this, in points, are left out as decoration (0 keeps them all) |
| `KeepBackgroundImages` | `false` | Keeps pictures behind the text, such as page backgrounds and watermarks |
| `CaptionsAsFigures` | `false` | Writes a picture with its caption as an HTML figure instead of an image and an italic paragraph |
| `WriteFrontMatter` | `false` | Writes YAML front matter with the title and author when the document has them |

```csharp
using Egap.Documents;

Document report = Document.Load("report.docx");
report.SaveAsMarkdown("report.md", new MarkdownSaveOptions { WriteFrontMatter = true, VectorFiguresAsSvg = true });
```

## Markdown to PDF

`PdfConvert.FromMarkdown` turns Markdown into a PDF in one call. With `PdfConvertOptions.TableOfContents` set, a `[TOC]`
paragraph marks where the table of contents goes.
[PDF export and conversion](../guides/pdf-export-and-conversion.md) shows the options.
