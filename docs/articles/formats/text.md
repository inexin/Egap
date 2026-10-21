# Plain text

Egap.Documents reads and writes plain text without an add-on package. Reading turns each line into a paragraph; writing
gives the document's text in reading order, for search and indexing.

```csharp
using Egap.Documents;

Document notes = Document.LoadText("notes.txt");                  // one paragraph per line
Document report = Document.Load("report.docx");
string text = report.ToText(new TextSaveOptions { IncludeHeadersAndFooters = true });
report.SaveAsText("report.txt");
```

## Reading

Each line becomes a paragraph, a tab a tab and a form feed a page break. Lines may end in CR LF, LF or CR. A file is read
as UTF-8 unless a byte order mark names UTF-16 or UTF-32. `Document.Load` reads `.txt` and `.text` files as plain text.

## Writing

The text is written in reading order, one line per paragraph: list labels before their text with a tab, table rows as
tab-separated cells, text boxes after the paragraph they are anchored in, and equations as their text. Field codes, deleted
and hidden text, comments and pictures are left out. Files are written as UTF-8 without a byte order mark.

| Option | Default | Does |
| --- | --- | --- |
| `IncludeHeadersAndFooters` | `false` | Writes each section's headers before its body and its footers after it, each distinct text once |
| `IncludeNotes` | `true` | Writes footnotes and endnotes at the end, each after its mark in brackets, with the mark in the text |
| `NewLine` | `"\n"` | The line end written between paragraphs and table rows |
