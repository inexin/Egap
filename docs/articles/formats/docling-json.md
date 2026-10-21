# Docling JSON

Docling JSON is the DoclingDocument format that [Docling](https://github.com/docling-project/docling) writes: texts,
tables, pictures and list groups, linked into reading order, each with its role and its box on the page. Egap.Documents
writes a PDF's analysed page layouts as Docling JSON and reads Docling JSON back, from Docling or from Egap, without an
add-on package. The schema version written, and the newest read, is `DoclingJson.SchemaVersion`.

```csharp
using Egap.Documents;
using Egap.Documents.Pdf;
using Egap.Pdf;

using PdfDocument pdf = PdfDocument.Open("report.pdf");
IReadOnlyList<PdfPageLayout> pages = PdfLayoutAnalyzer.Analyze(pdf);
DoclingJson.Write(pages, "report.json", new DoclingJsonOptions { Name = "report", Indented = true });

IReadOnlyList<PdfPageLayout> back = DoclingJson.Read(File.ReadAllText("report.json"));   // page layouts again
Document flow = Document.Load("report.json");                                           // or a flow document
```

## Writing

`DoclingJson.Write` takes the page layouts that `PdfLayoutAnalyzer` finds (see
[PDF to documents](../guides/pdf-export-and-conversion.md#pdf-to-documents)). Roles become Docling's labels, boxes become
provenance with the page number and a box from the page's bottom left, as PDF measures, and page headers and footers go to
the furniture layer. Egap's own role and confidence go in each item's `meta`.

| Option | Default | Does |
| --- | --- | --- |
| `Name` | `"document"` | The document's name, Docling's `name` |
| `FileName`, `Source` | None | The source file's name and bytes; with `Source` set, the origin is written with its hash |
| `Indented` | `false` | Indents the JSON for reading |
| `Images` | `DataUris` | How pictures carry their image: `DataUris` (the file stands alone), `Files` or `Omit` |
| `ImagesFolder` | `<name>.images` | The folder beside the JSON that `Files` writes images to, each image once, named in reading order |
| `ImageResolver` | None | Stores each picture's image and returns the URI to write |
| `FigureResolution` | 200 | Pixels per inch at which figures drawn on the page become images, 10 to 1200 |
| `MinImageSize` | 6 | Figures smaller than this on both sides, in points, get no image (bullets, rules, specks) |

## Reading

`DoclingJson.Read` gives page layouts: texts become blocks with their roles, tables keep their cells, pictures become
figures, all in reading order. The JSON keeps no line breaks or fonts, so each text becomes one line with its words spread
over its box. `DoclingJson.ToDocument`, and `Document.Load` for a file that names the `DoclingDocument` schema, give a flow
document instead: texts as paragraphs in the styles of their roles, list items in lists, tables with their cells and spans,
pictures with the images the JSON carries, and page headers and footers in each section's header and footer.

Docling JSON holds page layouts, not flow documents, so `Document.Save` does not write it; use `DoclingJson.Write`.
