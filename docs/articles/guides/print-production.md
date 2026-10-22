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

## Separations and ink coverage

`RenderSeparations` renders a page as ink plates: one gray raster per process ink of the press profile and one per spot
ink the page uses. It draws with a true multi-ink renderer, so overprint, overprint mode, knockout and blend modes act on
the inks as a press prints them. CMYK and gray go straight onto the process plates, Separation and DeviceN colourants onto
their own plates, and RGB, Lab and calibrated colours convert through the press profile: the document's CMYK output intent,
or FOGRA39 when it has none.

```csharp
using Egami;
using Egami.Pixels;
using Egap.Pdf;

using PdfDocument pdf = PdfDocument.Open("brochure.pdf");
using PdfSeparations plates = pdf.Pages[0].RenderSeparations(new PdfRenderOptions { Dpi = 150 });
foreach (PdfInkPlate plate in plates.Plates)
{
    plate.Raster.Save($"{plate.Name}.png");                       // 255 is full ink
}

Console.WriteLine($"max TAC {plates.MaxCoverage}%, {plates.ShareOver(300):P1} of the page over 300%");
using Raster<Rgba8> heat = plates.CoverageMap(limit: 300);        // red where the ink is over the limit
heat.Save("coverage.png");
```

`PdfSeparationOptions` picks another press profile, leaves some spot inks without a plate (`DisabledSpots`) or converts
every spot to process (`SpotsToProcess`); a spot without a plate prints through its alternate colour. `plates.Inks` is the
Egami `InkRaster` behind the plates, which saves a separated TIFF and makes a soft proof.

When a print profile sets `MaxInkCoverage`, preflight separates every page at `InkCoverageDpi` (36 by default) and reports
its most total ink and the share over the limit. Coverage is only reported: preflight does not change any colour.

## Colour conversion

`PdfColorConverter.Convert` converts a document's colours into a print profile in place: RGB, calibrated, Lab and
ICC-based colours go to the target, while CMYK and spot colours keep their numbers and plates. Vectors stay vectors.
Colour operators, images, shadings, patterns, soft masks, Type 3 glyphs and annotation appearances are all converted; a
JPEG photo stays a JPEG at the quality it had, and an Indexed image keeps its samples under a converted palette.

```csharp
using Egami.Colors;
using Egap.Pdf;

using PdfDocument pdf = PdfDocument.Open("rgb.pdf");
PdfColorConversionResult result = PdfColorConverter.Convert(pdf, new PdfColorConversionOptions
{
    Target = ColorProfile.Fogra39,                // the default: the CMYK output intent, else FOGRA39
    Spots = PdfSpotConversion.Keep,               // Keep, Convert or ConvertAllButNamed
    BlackText = PdfBlackText.PureBlack,           // black and gray text and thin rules on black ink alone
});
Console.WriteLine(result);                        // what changed
pdf.Save("cmyk.pdf");
```

| Option | Default | What it does |
| --- | --- | --- |
| `Target` | output intent, else FOGRA39 | The gray, RGB or CMYK profile colours convert into |
| `Intent`, `BlackPointCompensation` | relative colorimetric, on | How every conversion maps colours |
| `Spots` | `Keep` | Spot colours keep their plates, with their alternate colours converted; `Convert` converts them too |
| `RetargetCmyk`, `SourceCmyk` | off | Also convert CMYK from one press profile to another, such as coated to newspaper |
| `BlackText`, `ThinRuleWidth` | `PureBlack`, 0.5 pt | Neutral text and rules up to the width go on black ink alone; images stay colorimetric |

Conversion does not limit ink: the target profile decides how much ink each colour takes, and preflight reports anything
over a print profile's `MaxInkCoverage`.

## PDF/X

`PdfXConverter.Check` checks a document against a PDF/X version and changes nothing; `PdfXConverter.Convert` fixes what it
can in place and reports each fix. When a rule cannot be met, conversion changes nothing and the report says why. The
reports are the same `PdfAReport` as for PDF/A.

```csharp
using Egap.Pdf;

using PdfDocument pdf = PdfDocument.Open("brochure.pdf");
PdfAReport report = PdfXConverter.Convert(pdf, PdfXConformance.PdfX4, new PdfXConvertOptions
{
    OutputCondition = PdfOutputCondition.Fogra39,     // the registered condition; its profile is embedded
    Bleed = 3,                                        // millimetres past the trim box, where conversion sets the boxes
});
if (report.Succeeded)
{
    pdf.Save("print.pdf");                            // written whole, unencrypted, as PDF 1.6
}
else
{
    Console.WriteLine(report);
}
```

| Version | Check | Convert |
| --- | --- | --- |
| PDF/X-4, X-6 | yes | yes |
| PDF/X-1a, X-3 | yes | when the file has no transparency (conversion does not flatten it) |
| PDF/X-5n, X-6n | yes | with an n-colorant output condition, for content already in its inks |
| PDF/X-4p, X-5g, X-5pg, X-6p | yes | no |

Conversion adds the output intent, sets a trim box (the crop box) and a bleed box where they are missing or wrong, writes the
identification, title, dates and `/Trapped`, embeds fonts (from the machine, or bundled substitutes with
`AllowFontSubstitution`), converts colours the output intent does not allow through `PdfColorConverter`, removes transfer
functions, JavaScript and external content references, and flattens annotations that print inside the bleed box.

`PdfOutputCondition` names registered print conditions: `Fogra39` carries Egami's bundled profile; `Fogra51`, `Fogra52`,
`Gracol2013`, `Swop2013` and `Newspaper` need the profile from you with `WithProfile`. No open-source PDF/X validator exists;
check important output in Acrobat Preflight or PackzView.
