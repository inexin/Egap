# Text, images and drawing

Each page gives its text in reading order and the images drawn on it. `PdfCanvas` is an Egami canvas whose drawing becomes
PDF content, so anything you can draw with Egami.Drawing you can draw on a page.

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

The canvas works in points from the top left of the page as a viewer shows it. Text drawn on it embeds a subset of the font.
What PDF cannot express exactly is drawn as an image, and each such fallback is counted in a warning.
