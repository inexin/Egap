# Egap

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="branding/lockups/Egap-on-dark.svg">
  <img src="branding/lockups/Egap-on-light.svg" alt="Egap" height="64">
</picture>

Egap is an open source family of document libraries for .NET 10: PDF files, and flow documents in DOCX, HTML and Markdown.
It is a sister project to [Egami](https://github.com/inexin/Egami), the 2D imaging library it draws with.

- **MIT-licensed**, with no strings for commercial use.
- **Fully managed C#**: no native binaries, no P/Invoke; images, fonts and drawing come from Egami, which is managed too.
- **Safe on untrusted files**: damaged files are repaired where possible, and every reader is fuzz-tested.

```sh
dotnet add package Egap.Pdf
```

```csharp
using Egap.Pdf;

using PdfDocument pdf = PdfDocument.Open("in.pdf");
Console.WriteLine(pdf.Pages[0].ExtractText());                   // lines in reading order

pdf.Metadata.Title = "Quarterly report";
pdf.Outline.Add("Summary", PdfDestination.ToPage(pdf.Pages[0]));
pdf.Save("out.pdf");                                             // incremental: the original bytes plus the changes
```

## Packages

Add only the packages you use. Each one has its own guides.

| Package | Version | What it does | Docs |
| --- | --- | --- | --- |
| [Egap.Pdf](https://www.nuget.org/packages/Egap.Pdf) | 1.5.0 | Reads, edits, renders, signs, redacts and saves PDF; PDF/A, tagged PDF, optimize, imposition, stamps, compare | [Editing](docs/articles/guides/editing.md), [rendering](docs/articles/guides/rendering.md), [signatures](docs/articles/guides/signatures.md), [redaction and PDF/A](docs/articles/guides/redaction-and-pdfa.md), [PDF tools](docs/articles/guides/pdf-tools.md) |
| [Egap.Documents](https://www.nuget.org/packages/Egap.Documents) | 2.0.0 | One document model with DOCX, page layout onto any Egami canvas, PDF export, and PDF converted back into documents | [Flow documents](docs/articles/guides/flow-documents.md), [PDF export and conversion](docs/articles/guides/pdf-export-and-conversion.md), [formats](docs/articles/formats/index.md), [moving to 2.0](docs/articles/guides/migrating-to-2.0.md) |
| [Egap.Documents.Html](https://www.nuget.org/packages/Egap.Documents.Html) | 2.0.0 | HTML read and written through the CSS cascade, HTML to PDF | [HTML](docs/articles/formats/html.md) |
| [Egap.Documents.Markdown](https://www.nuget.org/packages/Egap.Documents.Markdown) | 2.0.0 | CommonMark and GitHub Markdown read and written, Markdown to PDF | [Markdown](docs/articles/formats/markdown.md) |

All four are on [NuGet](https://www.nuget.org/packages?q=Egap). [Packages](docs/articles/packages.md) lists what each one
needs. From 2.1.0 every Egap package shares one version and they release together. Egap.Pdf.Conversion is retired; its code is in Egap.Documents 2.0.

## Documentation

The documentation is at [inexin.github.io/Egap](https://inexin.github.io/Egap/), and its sources are in
[docs](docs/articles):

- **[Getting started](docs/articles/getting-started.md)** opens, edits and saves a first PDF and turns a Word file into PDF.
- **The [guides](docs/articles/guides/index.md)** explain one area each: what it does, the options, the defaults and the
  limits. They are grouped by package, and some end with recipes for common tasks.
- **[Document formats](docs/articles/formats/index.md)** has a page for each format Egap.Documents reads and writes, with
  its options.
- **The API reference** on the site lists every type and member.

Each package also has a short README with an example. What each version adds is in the [changelog](CHANGELOG.md).

## Building

You need the .NET 10 SDK. Egap draws, decodes images and reads fonts through [Egami](https://github.com/inexin/Egami),
whose packages come from nuget.org.

```
dotnet build
dotnet test
```

To try an unreleased Egami commit, `python tools/pack-egami.py --update` packs an Egami checkout next to this one into
`../egami-feed` and points `Directory.Packages.props` at it; then build with
`dotnet build -p:RestoreAdditionalProjectSources=../egami-feed`.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for building, testing and the clean-room rules. Security issues go through private
reporting; see [SECURITY.md](SECURITY.md).

## License

[MIT](LICENSE). The substitute fonts come from
[Egami.Fonts.Substitutes](https://www.nuget.org/packages/Egami.Fonts.Substitutes) under the SIL Open Font License 1.1; see
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
