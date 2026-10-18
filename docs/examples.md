# Examples

Longer examples for each package. Each package's own README on NuGet has a shorter one.

## Edit a PDF

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

## Text, images and drawing

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

## Render pages

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

## Sign, redact, archive and the PDF tools

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

// PDF tools: compress (lossless by default; Print, Ebook and Screen presets downsample and use JPEG).
using PdfDocument scan = PdfDocument.Open("scan.pdf");
IReadOnlyList<int> blank = scan.RemoveBlankPages();               // the page numbers removed
scan.DeskewScannedPages();
PdfOptimizeReport smaller = scan.Optimize(PdfOptimizeOptions.Ebook with { JpegQuality = 70 });
Console.WriteLine($"{smaller.BytesBefore} -> {smaller.BytesAfter} bytes, {smaller.Images.Count} images changed");
scan.Save("scan-small.pdf");                                      // every save after Optimize is a compact rewrite

using PdfDocument contract = PdfDocument.Open("contract.pdf");
contract.Stamp(PdfStamp.Text("Page {page} of {pages}") with { Anchor = StampAnchor.BottomCenter, Offset = new(0, 24) });
contract.Stamp(PdfStamp.Bates(new PdfBatesSequence("ACME-", digits: 6)) with { Anchor = StampAnchor.BottomRight });
using PdfDocument handout = PdfImpose.NUp(contract, new NUpOptions { Columns = 2, Rows = 2 });
handout.Save("handout.pdf");

using PdfDocument before = PdfDocument.Open("contract-v1.pdf"), after = PdfDocument.Open("contract-v2.pdf");
using PdfComparison changes = PdfCompare.Run(before, after);
foreach (PdfTextChange change in changes.TextChanges) Console.WriteLine($"{change.Kind}: {change.OldText} -> {change.NewText}");
changes.WriteMarkup("contract-changes.pdf");                      // the new document with each change marked
```

## Flow documents

```csharp
using Egap.Documents;

// One document model for DOCX, HTML and Markdown, with page layout.
Document doc = Document.Load("report.docx");                      // DOCX, DOTX, DOCM, HTML or Markdown, told apart by content
foreach (Paragraph p in doc.Descendants<Paragraph>())
{
    Console.WriteLine($"{p.ListLabel?.Text} {p.GetText()}");    // list labels computed as Word numbers them
}

DocumentBuilder builder = new(doc);                               // edit from code at a cursor
builder.Writeln("Added at the end.");
doc.Replace("colour", "color");                                   // also where the text crosses runs
IReadOnlyList<TextMatch> found = doc.Find("color");               // paragraph, offset and text of each match
doc.Save("report-edited.docx");                                   // what the model does not name is kept
IReadOnlyList<ConversionWarning> dropped = doc.Save("report.md"); // the format from the extension, with what it could not hold

// Each format's class takes its own options.
Document readme = MarkdownFormat.Load("README.md", new MarkdownLoadOptions { Extensions = MarkdownExtensions.GitHub });
DocxFormat.Save(readme, "readme.dotx", new DocxSaveOptions { Kind = DocxKind.Template });

// Page layout and, through Egap.Pdf.Layout, PDF with links, outline and tags.
DocumentLayout pages = doc.Layout();                              // pages, lines and boxes linked to the model
pages.Pages[0].Render(dpi: 150).Save("page1.png");                // any Egami canvas: raster, SVG, PDF
doc.SaveAsPdf("report.pdf", new PdfExportOptions { Tagged = true, Conformance = PdfAConformance.PdfA2a });

// HTML read as browsers parse it, styled through the CSS cascade; nothing is fetched without a resolver.
Document page = HtmlFormat.Load("invoice.html", new HtmlLoadOptions { BaseUri = new Uri("https://example.com/"), ResourceResolver = Fetch });
page.SaveAsPdf("invoice.pdf");
```

