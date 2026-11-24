# Egap.Preview: guide for coding agents

Egap.Preview is the `egap-preview` dotnet tool: a live preview of Egap documents on a local web page, drawn by Egap's own
renderer. A program sends its document's pages with `ShowPreview()`, and under `dotnet watch` the page follows every
edit; given a DOCX file, the tool draws it again whenever it is saved.

Install: `dotnet tool install -g Egap.Preview`. The code side, `ShowPreview` and `PreviewOptions`, is in Egap.Documents
(namespace `Egap.Documents`), so a program needs no reference to this package.

{{shared}}

## Entry points

- `egap-preview`: waits for programs to send pages. `egap-preview letter.docx` shows a DOCX file instead.
- Options: `--port 12501` (the default), `--dpi 110`, `--no-open` (no browser window), `-h` for help.
- `document.ShowPreview()` (Egap.Documents) sends the document's pages to the tool and returns `false` when the tool is
  not running. `new PreviewOptions { Port = ..., Dpi = ..., Name = ..., Layout = ... }` changes where and how.
- `egap preview letter.docx` starts the same tool through the `egap` command when both are installed.

## Samples

```sh
dotnet tool install -g Egap.Preview
egap-preview                  # waits for ShowPreview()
egap-preview letter.docx      # or shows a file, again on every save
dotnet watch run              # in the project: each run sends the new pages
```

A report previewed as it is written:

{{snippet guides/reports.md#Live preview}}

## Common mistakes

- Expecting the tool to open HTML or Markdown files. It reads only DOCX; preview HTML and Markdown from code with the
  add-on that reads them (`HtmlFormat.Register()` then `Document.Load("invoice.html").ShowPreview()`).
- An HTML template that does not redraw on save: `dotnet watch` only restarts for files it watches, so add
  `<Watch Include="**/*.html" />` to an item group of the project file.
- Ignoring the result of `ShowPreview()`: `false` means nothing listens on the port, so start `egap-preview` first or
  give the port it uses in `PreviewOptions.Port`.
- Trying to reach the preview from another machine or a web page. It listens on 127.0.0.1 only and answers only programs
  and its own page; that is deliberate.
- Leaving `ShowPreview()` in production code paths. It is for development: each call lays out and renders every page
  and posts them to the local port, waiting up to five seconds.
- A strict layout that fails shows its message on the page above the last pages that laid out, and is thrown as well:
  reports use strict layout and portable fonts by default (`Document.DefaultLayoutOptions`).

## More

- Live preview: https://inexin.github.io/Egap/v2/articles/guides/reports.html#live-preview
- Reports in code: https://inexin.github.io/Egap/v2/articles/guides/reports.html
- Every page in one list: https://inexin.github.io/Egap/v2/llms.txt
