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
