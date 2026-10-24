# Rendering pages

A page renders to an Egami raster, or onto any Egami canvas: pixels, SVG, or another PDF.

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

`PdfRenderOptions` also picks the page box, a region, the background, which annotations and layers are drawn, and whether
system fonts may stand in for fonts the file does not embed. Turn system fonts off for output that is the same on every
machine. Fonts a file names but does not embed are drawn with bundled substitutes that keep the same widths.

Pages render as on screen, where overprint is ignored. `SimulateOverprint` shows them as they print instead, and
`RenderSeparations` gives the ink plates themselves; see [Print production](print-production.md).

## Colour

Device colours draw as the document's output intent says when it has one. Without a CMYK output intent, DeviceCMYK
colours, CMYK JPEGs and spot colours with a CMYK alternate draw through the Coated FOGRA39 press profile, as on a
European offset press. `DefaultCmykProfile` picks another press profile, such as one for U.S. web presses:

```csharp
using Egami.Colors;
using Egap.Pdf;

ColorProfile swop = ColorProfile.FromIcc(File.ReadAllBytes("USWebCoatedSWOP.icc"));
PdfRenderOptions us = new() { DefaultCmykProfile = swop };
```

Transfer functions in a graphics state (`/TR` and `/TR2`) change the colours drawn after it, as they do on a printer.
Set `ApplyTransferFunctions` to `false` to draw colours as they are.
