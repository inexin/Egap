# MHTML

MHTML web archives (`.mht`, `.mhtml`) come from the add-on package Egap.Documents.Html. An archive holds an HTML page with
its pictures, style sheets and fonts in one file, as Chromium, Edge and Word save pages. The page is read through the HTML
reader, so everything [HTML](html.md) reads applies.

```sh
dotnet add package Egap.Documents.Html
```

The methods are on `HtmlFormat`, in namespace `Egap.Documents`: `LoadMhtml` and `SaveMhtml`, and `ToMhtml` and
`SaveAsMhtml` on `Document`. `HtmlFormat.Register()` at startup lets `Document.Load` and `Document.Save` handle `.mht` and
`.mhtml` files too.

```csharp
using Egap.Documents;

Document page = HtmlFormat.LoadMhtml("saved-page.mhtml");
page.SaveAsDocx("saved-page.docx");
Document.LoadDocx("report.docx").SaveAsMhtml("report.mhtml");
```

## Reading

Archives in Chromium's shape and in Word's are both read. The page is the part the archive's `start` parameter names, or
else its first HTML part, decoded in the charset the part names. Its links find the archive's other parts by their
`Content-Location` or by `cid:` URL, base64 and quoted-printable bodies alike. What the archive does not hold is fetched
only through `HtmlLoadOptions.ResourceResolver`, and never when you give none:

```csharp
using Egap.Documents;

Document page = HtmlFormat.LoadMhtml("saved-page.mhtml", out IReadOnlyList<ConversionWarning> warnings, new HtmlLoadOptions
{
    Media = "screen",
});
```

`HtmlLoadOptions` work as for HTML. `Limits` also bounds the archive: its parts (`MaxEntries`), the largest decoded part
(`MaxPartSize`), all parts together (`MaxTotalSize`) and how deep multipart parts nest (`MaxDepth`).

## Writing

The document is written in the shape Chromium saves pages in, which Chromium, Edge and Word open: a `multipart/related`
message whose first part is the page in quoted-printable UTF-8, followed by each picture once and each of
`Document.EmbeddedFonts` in base64, each with the `Content-Location` the page links to. `HtmlSaveOptions` work as for
HTML; a picture that `ImageWriter` gives an address for is linked there instead of stored.
