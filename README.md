# Egap

Egap is an open source family of document libraries for .NET, starting with **Egap.Pdf** for reading and writing PDF files.
It is a sister project to [Egami](https://github.com/inexin/Egami), the 2D imaging library.

- **MIT-licensed**, with no strings for commercial use.
- **Fully managed C#**: no native binaries, no P/Invoke, no wrapped native libraries; images, fonts and drawing come from
  Egami, which is managed too.
- **Safe on untrusted files**: readers are built to be fuzz-tested from the start.

> **Status:** 1.3.0, public API frozen; nothing is published to NuGet yet. Egap.Pdf reads PDF files, damaged and encrypted
> ones included (RC4 and AES, revisions 2 to 6), into a lazily loaded object graph with change tracking, and saves them
> incrementally or by full rewrite, with or without object streams, keeping, dropping or applying AES-256 encryption. On top
> of that it edits pages, labels, outlines, destinations, metadata, attachments and layers; reads, adds and draws
> annotations; reads, fills and flattens forms, XFA data included; and merges and splits documents. Since 1.1 it also
> parses, walks and rewrites content streams, draws on pages through an Egami canvas with embedded font subsets, reads
> fonts, extracts text in reading order, and lists, decodes and replaces images. Since 1.2 it renders pages to rasters, onto
> any Egami canvas (SVG and PDF included) or to a display list, with transparency, shadings, patterns, layers, annotations
> and open substitutes (Egap.Fonts) for fonts that are not embedded. Since 1.3 it signs (PAdES B-B to B-LTA, deferred and
> certification signatures), timestamps and verifies signatures; truly redacts and sanitises; creates, converts and checks
> PDF/A files with basic PDF/UA; keeps and builds structure trees with tagging while drawing; saves linearized files; and adds
> invisible OCR text from hOCR or ALTO.

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

```csharp
using Egami;
using Egami.Drawing;
using Egami.Pixels;
using Egap.Pdf;

using PdfDocument pdf = PdfDocument.Open("in.pdf");
PdfPage page = pdf.Pages[0];
Console.WriteLine(page.ExtractText());                   // lines in reading order

foreach (PdfPlacedImage placed in page.GetImages())      // where each image is drawn, and its pixels
{
    using Raster<Rgba8> pixels = placed.Image.ToRaster<Rgba8>();
}

using (PdfCanvas canvas = new(page))                     // Egami's canvas, drawn as PDF content over the page
{
    canvas.FillRect(new RectF(72, 72, 200, 20), new SolidPaint(new RgbaF32(1, 0.9f, 0, 0.5f)));
}

pdf.Save("out.pdf");
```

```csharp
using Egami;
using Egami.Pixels;
using Egap.Pdf;

using PdfDocument pdf = PdfDocument.Open("in.pdf");
using (Raster<Rgba8> image = pdf.Pages[0].Render(new PdfRenderOptions { Dpi = 150 }))
{
    image.Save("page.png");
}

using Raster<Rgba8> thumbnail = pdf.Pages[0].Render(PdfRenderOptions.FitTo(256, 256));   // decodes large images reduced
```

```csharp
using System.Security.Cryptography.X509Certificates;
using Egap.Pdf;

using PdfDocument pdf = PdfDocument.Open("in.pdf");
using X509Certificate2 certificate = X509CertificateLoader.LoadPkcs12FromFile("me.pfx", password);
await using (FileStream signed = File.Create("signed.pdf"))
{
    await pdf.SignAsync("Signature1", new PdfSignOptions             // PAdES, incremental, written to a new stream
    {
        Signer = PdfSigner.FromCertificate(certificate),
        Level = PadesLevel.BaselineT,
        Timestamp = new PdfTimestampClient(tsaUrl, http),             // the caller's HttpClient; Egap opens no connection itself
    }, signed);
}

using PdfDocument check = PdfDocument.Open("signed.pdf");
foreach (PdfSignature signature in check.Signatures)
{
    PdfSignatureReport report = signature.Verify(new PdfVerifyOptions { TrustAnchors = [root] });
    Console.WriteLine($"{signature.Name}: {report.Integrity} {report.Chain} {report.Timestamp} {report.ChangesSince}");
}

using PdfDocument archive = PdfDocument.Open("report.pdf");
archive.Pages[0].Annotations.Add(new PdfRedactAnnotation(new PdfRectangle(72, 700, 300, 714)));
archive.ApplyRedactions();                                        // covered text, paths and pixels removed for real
PdfAReport conversion = PdfAConverter.Convert(archive, PdfAConformance.PdfA2u);
if (conversion.Succeeded)
{
    archive.Save("archive.pdf", new PdfSaveOptions { Linearize = true });
}
```

```csharp
using Egap.Documents;

// Egap.Documents 0.1 preview (Phase 6): the flow document model and Markdown, read and written.
Document doc = MarkdownFormat.Load("README.md");                 // CommonMark plus GitHub's extensions
foreach (Paragraph p in doc.Descendants<Paragraph>())
{
    Console.WriteLine($"{p.ListLabel?.Text} {p.GetText()}");    // list labels computed as Word numbers them
}

DocumentBuilder builder = new(doc);                               // edit from code at a cursor
builder.Writeln("Added at the end.");
doc.Replace("colour", "color");                                   // also where the text crosses runs
IReadOnlyList<ConversionWarning> dropped = MarkdownFormat.Save(doc, "out.md");

// Egap.Documents 0.2 preview: DOCX, DOTX and DOCM (strict and transitional) read and written.
Document report = DocxFormat.Load("report.docx");                // what the model does not name is kept
report.Sections[0].Body.AddParagraph("Appended.");
DocxFormat.Save(report, "report-edited.docx");                    // unedited parts are written as they were read
DocxFormat.Save(MarkdownFormat.Load("README.md"), "readme.docx");  // Markdown to Word
```

## Projects

| Project | What it is |
| --- | --- |
| `src/Egap.Pdf` | The PDF library (package `Egap.Pdf`). |
| `src/Egap.Documents` | Flow documents, in progress (Phase 6; 0.2 preview has the model, Markdown and DOCX): one document model with Markdown, DOCX and HTML readers and writers and a page layout engine (package `Egap.Documents`). |
| `src/Egap.Pdf.Layout` | The bridge between Egap.Documents and Egap.Pdf, in progress: documents saved as PDF, PDF pages analysed back into documents (package `Egap.Pdf.Layout`). |
| `src/Egap.Fonts` | Open, metric-compatible substitutes for Helvetica, Arial, Times, Courier, Calibri and Cambria (package `Egap.Fonts`, SIL OFL fonts). |
| `tests/Egap.Pdf.Tests`, `tests/Egap.Documents.Tests`, `tests/Egap.Pdf.Layout.Tests` | Unit tests (xUnit v3). |
| `tests/Egap.Pdf.Fuzz`, `tests/Egap.Documents.Fuzz` | SharpFuzz harnesses for AFL++. |
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
