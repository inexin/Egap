# HTML

HTML and XHTML come from the add-on package Egap.Documents.Html. It reads a page into the document model, styled through
the CSS cascade, and writes the model back as HTML. It also brings HTML to PDF, HTML stamps and HTML page bands.

```sh
dotnet add package Egap.Documents.Html
```

The format's class is `HtmlFormat`, in namespace `Egap.Documents`. Call `HtmlFormat.Register()` once at startup so that
`Document.Load`, `Document.Save` and `PdfConvert.FromFile` handle `.html` files too. Calling `HtmlFormat` or the members it
adds to `Document` (`Document.ParseHtml`, `Document.LoadHtml`, `ToHtml`, `SaveAsHtml`) needs no registration.

```csharp
using Egap.Documents;
using Egap.Documents.Pdf;

HtmlFormat.Register();                                           // once at startup: Document.Load and Save know .html
Document page = Document.LoadHtml("invoice.html");
page.SaveAsDocx("invoice.docx");
string html = Document.LoadDocx("report.docx").ToHtml();
PdfConvert.FromHtml("<h1>Invoice</h1><p>Total: 120 EUR</p>", "invoice.pdf");
```

## Reading

HTML is parsed by AngleSharp as browsers parse it, and styled through Egami.Css's cascade with HTML's default style sheet,
for print media unless `Media` says `"screen"`. The computed styles become paragraphs, runs, lists, tables, and flex and
grid containers ([CSS layout](#css-layout)). Scripts never run, and nothing outside the page is fetched unless you pass a
resolver:

```csharp
using Egap.Documents;

Document page = HtmlFormat.Load("invoice.html", new HtmlLoadOptions
{
    BaseUri = new Uri("https://example.com/"),
    ResourceResolver = uri => File.Exists(uri.LocalPath) ? File.ReadAllBytes(uri.LocalPath) : null,
});
```

| Option | Default | Does |
| --- | --- | --- |
| `BaseUri` | None | The address relative URLs (images, style sheets) are resolved against |
| `ResourceResolver` | None | Fetches what the page links to by absolute URL, or returns null to leave it out; `data:` URLs need none |
| `Encoding` | UTF-8 when the bytes are valid UTF-8, else windows-1252 | The encoding of bytes when neither a byte order mark nor a meta charset names one |
| `Media` | `"print"` | The media the style sheets apply for: `"print"` or `"screen"` |
| `UserStyleSheets` | None | Style sheets applied as the user's, after the default one and before the page's own |
| `Limits` | `DocumentLimits` defaults | The largest page and how deep elements are read as structure (see [Limits](index.md#limits)) |

`PdfConvert.FromFile` reads an HTML file's images and style sheets from the file's own folder, or from
`PdfConvertOptions.BaseDirectory` when set, and never from the network.

## What CSS and HTML map to

Besides fonts, colours, spacing, borders, lists and tables, these read into the model:

- Form controls (text inputs, check boxes, radio buttons, selects and text areas) are read as content controls that hold
  their values, as Word's are, so they keep their place in the text and a PDF export can turn them into form fields.
- `word-spacing`, `text-decoration: overline`, `font-feature-settings`, `font-variant-ligatures`, `font-variant-numeric`,
  `word-break` and `overflow-wrap` set the [text options](../guides/flow-documents.md#text-options).
- `break-inside: avoid` on a box of several blocks makes a group that is kept on one page, and `-webkit-line-clamp`
  clamps a paragraph to a number of lines (see [Page layout](../guides/flow-documents.md#page-layout)).
- `@page` margin box rules such as `@top-left` and `@bottom-center`, with `counter(page)` and `counter(pages)`, become
  margin boxes. An element with `position: running(name)` leaves the body and shows in the boxes whose content is
  `element(name)`. Margin boxes are written back as the same rules.
- `<nav data-toc>` is read as a table of contents field.

## CSS layout

`display: flex` and `display: grid` read into [flex and grid containers](../guides/flow-documents.md#flex-and-grid-layout),
laid out as browsers lay them out and broken across pages between lines and rows. Each child element is an item, and text
directly in the container an item of its own.

| CSS | Read |
| --- | --- |
| Flex containers | `flex-direction`, `flex-wrap`, `flex-flow`, `justify-content`, `align-items`, `align-content`, `gap`, `row-gap`, `column-gap` |
| Flex items | `flex`, `flex-grow`, `flex-shrink`, `flex-basis`, `order`, `align-self` |
| Grid containers | `grid-template-columns` and `grid-template-rows` (lengths, `%`, `fr`, `auto`, `min-content`, `max-content`, `minmax()`, `fit-content()`, `repeat()` with a count, `auto-fill` or `auto-fit`), `grid-template-areas`, `grid-template`, `grid-auto-rows`, `grid-auto-columns`, `grid-auto-flow` with `dense`, the gaps, `justify-items`, `align-items`, `justify-content` and `place-items` |
| Grid items | `grid-area` (a name or lines), `grid-row`, `grid-column` and their `-start` and `-end` forms, `span`, negative lines, `justify-self`, `align-self`, `place-self` |
| Boxes | `margin`, `padding`, `border`, `background-color`, `border-radius`, `box-shadow` (the first outer shadow), `width`, `height`, `min-height` and `box-sizing` on containers and items |
| Positioned boxes | `position: absolute` and `fixed` with `top`, `right`, `bottom`, `left`, `width`, `height` and `z-index` |

A block with `border-radius` or `box-shadow` becomes a group with a box around its blocks; other blocks keep their
borders and background as paragraph borders and shading. An absolutely positioned box becomes a text box floating in front
of the text (behind it for a negative `z-index`), placed on the first page's body area, or from its positioned parent's
first line; a fixed box goes in the header, so it shows on every page. `position: sticky`, subgrid, masonry, named grid
lines and `inline-flex` and `inline-grid` boxes are not laid out as such; the last two read as inline content.

## Writing

The model is written as HTML in UTF-8, with what HTML cannot hold returned as warnings.

| Option | Default | Does |
| --- | --- | --- |
| `ImageWriter` | None | Stores a picture and returns the URL the page uses for it, such as a file beside the page |
| `EmbedImages` | `true` | Embeds pictures no writer stored as `data:` URLs |
| `WritePageSetup` | `true` | Writes the page size, margins and simple headers and footers as `@page` rules |

## HTML to PDF

`PdfConvert.FromHtml` turns HTML into a PDF in one call, with page size, margins, header and footer bands and a table of
contents. `PdfStamp.Html` lays out a piece of HTML as a stamp on every page of a PDF, and `PageBand.Html` makes a header
or footer band styled with the page's own style sheets. [PDF export and conversion](../guides/pdf-export-and-conversion.md)
shows each one.
