# Print production

Check a PDF before it goes to a printer: what colours, profiles, inks and image resolutions it uses, and whether that fits
the print job.

## Preflight

`PdfPreflight.Run` lists what a document uses, page by page: output intents with their condition and profile, every
colour space painted with (with its ICC profile description and spot ink names), images with their effective resolution
where they are placed largest, fonts and whether they are embedded, transparency, overprint, hairlines, and each page's
trim box and bleed. A device colour space that the page's resources replace with a Default space is reported as that space.

Given a `PdfPrintProfile`, it also lists the issues that break it. Issues are the same `PdfAIssue` records as PDF/A checks
use; preflight changes nothing, so none is fixable.

```csharp
using Egap.Pdf;

using PdfDocument pdf = PdfDocument.Open("brochure.pdf");
PdfPreflightReport report = PdfPreflight.Run(pdf, PdfPrintProfile.SheetfedCoated);
foreach (PdfPreflightItem item in report.OutputIntents)
{
    Console.WriteLine(item);                 // GTS_PDFX output intent FOGRA39 (Coated FOGRA39), CMYK profile "..."
}

foreach (PdfAIssue issue in report.Issues)
{
    Console.WriteLine($"{issue.Clause} page {issue.Page}: {issue.Description}");
}
```

## Print profiles

A `PdfPrintProfile` says what the job accepts. A value left null is not checked.

| Preset | Max ink | Min image dpi | Colour spaces | Bleed | Thinnest rule |
| --- | ---: | ---: | --- | ---: | ---: |
| `PdfX4` | | 150 | all but DeviceRGB | | |
| `SheetfedCoated` | 330 % | 250 | CMYK, gray, spot | 3 mm | 0.25 pt |
| `WebOffset` | 300 % | 200 | CMYK, gray, spot | 3 mm | 0.25 pt |
| `Newspaper` | 240 % | 150 | CMYK, gray | | 0.3 pt |

Every preset requires embedded fonts and allows transparency. Images of one bit per pixel (line art) need twice the
minimum resolution. Change any value with `with`:

```csharp
using Egap.Pdf;

PdfPrintProfile vinyl = PdfPrintProfile.WebOffset with { Name = "Vinyl sleeve", MaxInkCoverage = 260, AllowTransparency = false };
```

Without a profile, `Run` only lists what the document uses. Strokes thinner than 0.25 pt (`PdfPreflight.DefaultHairline`)
are then listed as hairlines.
