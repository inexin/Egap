# Egap.Documents.Markdown

Markdown for [Egap.Documents](https://www.nuget.org/packages/Egap.Documents): CommonMark and GitHub Markdown read into the
document model and written from it, with front matter, images, footnotes and task lists, and Markdown to PDF. Fully managed
and MIT-licensed.

```csharp
using Egap.Documents;
using Egap.Documents.Pdf;

MarkdownFormat.Register();                                       // once at startup: Document.Load and Save know .md
Document notes = Document.LoadMarkdown("notes.md");
notes.SaveAsDocx("notes.docx");
string markdown = Document.LoadDocx("report.docx").ToMarkdown();
PdfConvert.FromMarkdown("# Hello", "hello.pdf");
```

`MarkdownFormat` keeps its name and namespace, so code written against Egap.Documents 1.x compiles once this package is
added. Source and changelog: [github.com/inexin/Egap](https://github.com/inexin/Egap).
