# Egap.Preview

`egap-preview` shows [Egap](https://github.com/inexin/Egap) documents on a local web page, drawn by Egap's own renderer,
and draws them again as they change.

```sh
dotnet tool install -g Egap.Preview
egap-preview                  # shows what programs send with document.ShowPreview()
egap-preview letter.docx      # shows a DOCX file, again whenever it is saved
```

In code, call `ShowPreview()` on a document (Egap.Documents) and run the program under `dotnet watch`: each run sends the
new pages and the page reloads. HTML and Markdown are previewed the same way: load them in your program with
Egap.Documents.Html or Egap.Documents.Markdown and call `ShowPreview()`; the tool itself reads only DOCX. The tool listens on 127.0.0.1 only, port 12500 unless `--port` says otherwise. Fully
managed and MIT-licensed.
