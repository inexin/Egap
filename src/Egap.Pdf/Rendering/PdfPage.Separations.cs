using Egami;
using Egami.Colors;
using Egami.Drawing;

using Egap.Pdf.Images;
using Egap.Pdf.Rendering;

namespace Egap.Pdf;

public sealed partial class PdfPage
{
    /// <summary>
    /// Renders the page as ink plates (Phase 8 design Q4): one per process ink of the press profile and one per spot ink the
    /// page uses, drawn by a multi-ink renderer, so overprint, knockout and blend modes act on the inks as printed. CMYK and
    /// gray content goes straight onto the process plates, Separation and DeviceN colourants onto their own; RGB, Lab and
    /// calibrated colours convert through the press profile.
    /// </summary>
    /// <param name="options">The resolution, box, region, annotations and the rest; the background is paper, without ink.</param>
    /// <param name="separation">The press profile and the spot inks that get plates; <see cref="PdfSeparationOptions.Default"/> when null.</param>
    /// <param name="cancellationToken">Checked between content operators.</param>
    /// <exception cref="PdfLimitException">The plates would have more than <see cref="PdfRenderOptions.MaxOutputPixels"/> pixels.</exception>
    /// <exception cref="OperationCanceledException">The render was cancelled.</exception>
    public PdfSeparations RenderSeparations(PdfRenderOptions? options = null, PdfSeparationOptions? separation = null, CancellationToken cancellationToken = default)
    {
        options ??= PdfRenderOptions.Default;
        separation ??= PdfSeparationOptions.Default;
        RenderGeometry geometry = RenderGeometry.Of(this, options);
        if ((long)geometry.Width * geometry.Height > options.MaxOutputPixels)
        {
            throw new PdfLimitException(nameof(PdfRenderOptions.MaxOutputPixels), $"Plates of {geometry.Width} by {geometry.Height} pixels pass the limit of {options.MaxOutputPixels:N0}; render them in tiles through the region.");
        }

        InkRaster raster = new(InkSetFor(separation), geometry.Width, geometry.Height);
        try
        {
            using InkCanvas canvas = new(raster);
            canvas.Clear(InkColor.None);
            Draw(canvas, geometry.PageToDevice, options, background: false, cancellationToken);
        }
        catch
        {
            raster.Dispose();
            throw;
        }

        return new PdfSeparations(raster);
    }

    /// <summary>The inks of the page's plates: the press profile's process inks, then the spot inks it uses, some disabled.</summary>
    internal InkSet InkSetFor(PdfSeparationOptions separation)
    {
        ColorProfile press = separation.Profile ?? PressProfile(Document);
        InkSet inks = InkSet.FromProfile(press);
        if (separation.SpotsToProcess)
        {
            return inks;
        }

        foreach ((string name, PdfArray space) in Print.Inventory.Spots(this))
        {
            if (inks.Count >= InkSet.MaxInks || inks.Contains(name) || name.Length > Colorant.MaxNameLength)
            {
                continue;
            }

            inks = inks.WithSpot(name, SpotAppearance(space, name));
            if (separation.DisabledSpots.Contains(name))
            {
                inks = inks.WithDisabled(name);
            }
        }

        return inks;
    }

    /// <summary>The document's CMYK output intent profile, or Egami's bundled FOGRA39.</summary>
    internal static ColorProfile PressProfile(PdfDocument document)
    {
        return ColorContext.ForDocument(document, useOutputIntent: true).Cmyk ?? ColorProfile.Fogra39;
    }

    /// <summary>
    /// What a spot ink looks like at full tint, for proofing and for converting it where it has no plate: its alternate colour
    /// for 1 in this colourant and 0 in the others (a DeviceN space's own Separation for it first), CMYK where the alternate
    /// is CMYK, else sRGB.
    /// </summary>
    private static InkAppearance? SpotAppearance(PdfArray space, string name)
    {
        try
        {
            if (space.GetName(0)?.Value == "DeviceN" && space.Count > 4 && space.Get<PdfDictionary>(4)?.Get<PdfDictionary>("Colorants")?.Get(name)?.Resolve() is PdfArray own)
            {
                space = own;
            }

            ImageColorSpace read = ImageColorSpace.Read(space, null, new ColorContext());
            if (read.Colorants is not IReadOnlyList<string> names || read.Alternate is not ImageColorSpace alternate)
            {
                return null;
            }

            double[] tints = [.. names.Select(n => n == name ? 1.0 : 0.0)];
            if (alternate.Family == ColorFamily.Cmyk)
            {
                double[] cmyk = new double[4];
                read.ToAlternate(tints, cmyk);
                return InkAppearance.Cmyk((float)Math.Clamp(cmyk[0], 0, 1), (float)Math.Clamp(cmyk[1], 0, 1), (float)Math.Clamp(cmyk[2], 0, 1), (float)Math.Clamp(cmyk[3], 0, 1));
            }

            double[] rgb = new double[3];
            read.ToRgb(tints, rgb);
            return InkAppearance.Rgb((float)Math.Clamp(rgb[0], 0, 1), (float)Math.Clamp(rgb[1], 0, 1), (float)Math.Clamp(rgb[2], 0, 1));
        }
        catch (Exception e) when (e is InvalidDataException or PdfException or ArgumentException)
        {
            return null;
        }
    }
}
