# OCR and searchable scans

Egap makes scanned PDFs searchable through an OCR engine you plug in. It sends each page's image to the engine and writes
the words that come back over the page as invisible text, so the page can be searched, selected and read aloud while it
looks exactly as the scan. Egap ships no engine: use Egami's `CommandOcrEngine` for any program that writes hOCR or ALTO,
such as Tesseract, or write a small class around an OS or cloud service (Egami's repository has samples for Tesseract,
Windows OCR and Azure).

## Make a PDF searchable

`PdfOcr.MakeSearchableAsync` changes the document in place; you save it. The report says, page by page, what was done,
how many words were added and how long the engine took.

```csharp
using Egami.Ocr;
using Egap.Pdf;
using Egap.Pdf.Ocr;

using PdfDocument pdf = PdfDocument.Open("scan.pdf");
CommandOcrEngine tesseract = new("tesseract {image} {output} -l {langs} --dpi {dpi} hocr", new CommandOcrOptions { Output = OcrOutputFormat.Hocr });
PdfOcrReport report = await PdfOcr.MakeSearchableAsync(pdf, tesseract, new PdfOcrOptions
{
    Languages = ["eng"],
    PdfA = PdfAConformance.PdfA2b,
});
Console.WriteLine($"{report.Words} words on {report.Pages.Count(p => p.Status == PdfOcrPageStatus.Recognized)} pages");
pdf.Save("searchable.pdf");
```

A page that is one scanned image covering the page, at 150 dpi or more, is read as its own pixels, with no render and no
resampling. Other pages are rendered at `Dpi` (300 by default). Each word is placed along its own baseline and scaled to its
width, so selection follows rotated and skewed words. In a tagged document each word's language goes on its text.

| Option | Default | Does |
| --- | --- | --- |
| `Mode` | `Skip` | `Skip` leaves pages that have text alone; `Redo` removes an earlier invisible text layer and reads the page again, keeping visible text; `Force` reads every page, keeping its text |
| `Pages` | Every page | The pages to look at, as indices from 0 |
| `Languages` | The engine's default | The languages to read, as the engine names them |
| `Dpi` | 300 | The resolution pages are rendered at when they are not one scanned image |
| `MinConfidence` | 0 | Words the engine is less sure of are left out |
| `Deskew` | Off | Straightens the image sent to the engine; the page itself is not changed |
| `RotatePages` | Off | Turns a page the engine finds sideways or upside down, with its `/Rotate` |
| `MaxDegreeOfParallelism` | The processor count | How many pages the engine reads at once; one at a time when the engine is not thread-safe |
| `Progress` | None | Told how many pages are done |
| `StopOnError` | Off | An engine error stops the run instead of marking the page as failed |
| `PdfA` | None | A PDF/A level the document is converted to in the same pass |
| `AllowInvalidatingSignatures` | Off | A signed document is refused unless this is set, since the text layer breaks its signatures |
| `MaxPages`, `MaxImagePixels` | 10,000 pages, 100 million pixels | Limits for untrusted files; a larger scan is rendered, a larger render is made at a lower resolution |

Words the page already has as text are not added twice in `Redo` and `Force`. An image larger than the engine's
`MaxImageDimension` is scaled down for it and the words scaled back.

## Scans in document conversion

`Document.LoadPdfAsync` and `PdfToDocument.ConvertAsync` run scanned pages through `PdfToDocumentOptions.OcrEngine`, so
their text becomes paragraphs rather than a picture. The PDF you pass is not changed. The sync `LoadPdf` keeps a scanned page
as a picture with a warning.

```csharp
using Egami.Ocr;
using Egap.Documents;
using Egap.Documents.Pdf;

CommandOcrEngine tesseract = new("tesseract {image} {output} --dpi {dpi} hocr", new CommandOcrOptions { Output = OcrOutputFormat.Hocr });
Document minutes = await Document.LoadPdfAsync("minutes-scan.pdf", new PdfToDocumentOptions { OcrEngine = tesseract });
minutes.Save("minutes.docx");
```

## An existing hOCR or ALTO file

When the OCR has already been run elsewhere, `PdfOcrLayer.AddHocr` and `AddAlto` add its words to a page, mapped onto the
page's crop box; `PdfOcrLayer.AddResult` adds an engine's result through an image-to-page matrix you give.
