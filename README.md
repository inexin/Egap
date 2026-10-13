# Egap

Egap is an open source family of document libraries for .NET, starting with **Egap.Pdf** for reading and writing PDF files.
It is a sister project to [Egami](https://github.com/inexin/Egami), the 2D imaging library.

- **MIT-licensed**, with no strings for commercial use.
- **Fully managed C#**: no native binaries, no P/Invoke, no wrapped native libraries; images, fonts and drawing come from
  Egami, which is managed too.
- **Safe on untrusted files**: readers are built to be fuzz-tested from the start.

> **Status:** 1.0.0, public API frozen; nothing is published to NuGet yet. Egap.Pdf reads PDF files, damaged and encrypted
> ones included (RC4 and AES, revisions 2 to 6), into a lazily loaded object graph with change tracking, and saves them
> incrementally or by full rewrite, with or without object streams, keeping, dropping or applying AES-256 encryption. On top
> of that it edits pages, labels, outlines, destinations, metadata, attachments and layers; reads, adds and draws
> annotations; reads, fills and flattens forms, XFA data included; and merges and splits documents.

```csharp
using Egap.Pdf;

using PdfDocument pdf = PdfDocument.Open("in.pdf", new PdfReadOptions { Password = "secret" });
foreach (PdfWarning warning in pdf.Warnings) Console.WriteLine(warning);

pdf.Pages.Move(0, pdf.Pages.Count - 1);                  // pages: insert, remove, move, labels
pdf.Metadata.Title = "Quarterly report";                 // Info and XMP kept in step
pdf.Outline.Add("Summary", PdfDestination.ToPage(pdf.Pages[0]));
pdf.Pages[0].Annotations.Add(new PdfHighlightAnnotation(PdfTextMarkupAnnotation.Quad(new PdfRectangle(72, 700, 300, 714))));

if (pdf.Form.TryGetField("customer.name", out PdfField field) && field is PdfTextField name)
{
    name.Value = "Ada Lovelace";                         // the widget is redrawn
}

pdf.Form.Flatten();                                      // fields drawn into the pages and removed
pdf.Save("out.pdf");                                     // incremental: the original bytes plus the changes

using PdfDocument other = PdfDocument.Open("other.pdf");
using PdfDocument merged = PdfDocument.Merge(pdf, other);  // fields renamed on a clash, outlines appended
merged.Save("merged.pdf", new PdfSaveOptions { Mode = PdfSaveMode.Rewrite, Compression = PdfCompression.Compact });
```

## Projects

| Project | What it is |
| --- | --- |
| `src/Egap.Pdf` | The PDF library (package `Egap.Pdf`). |
| `tests/Egap.Pdf.Tests` | Unit tests (xUnit v3). |
| `tests/Egap.Pdf.Fuzz` | SharpFuzz harness for AFL++. |
| `benchmarks/Egap.Pdf.Benchmarks` | BenchmarkDotNet benchmarks. |

## Building

You need the .NET 10 SDK, and Egami packages in a folder feed beside this checkout: Egap.Pdf draws, decodes images and
reads fonts through [Egami](https://github.com/inexin/Egami), which is not on NuGet yet. With an Egami checkout next to this
one, `python tools/pack-egami.py` packs it into `../egami-feed` (see `nuget.config`) at the versions in
`Directory.Packages.props`; add `--update` to move to Egami's current commit.

```
python tools/pack-egami.py
dotnet build
dotnet test
```

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md).

## License

[MIT](LICENSE).
