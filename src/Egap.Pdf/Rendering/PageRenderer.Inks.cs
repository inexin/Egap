using System.Numerics;

using Egami;
using Egami.Colors;
using Egami.Drawing;
using Egami.Operations;
using Egami.Pixels;

using Egap.Pdf.Images;

namespace Egap.Pdf.Rendering;

/// <summary>
/// Drawing onto plates (Phase 8 design Q4, Build Plan tasks 6 and 7): on an <see cref="InkCanvas"/>, colours that print as
/// inks are painted as Egami ink colours (CMYK as process inks, Separation and DeviceN on their plates), images in those
/// spaces become ink rasters, shadings get ink stops and vertex inks, transparency groups blend their process part in the
/// group's colour space, and overprint and overprint mode pick the plates each paint prints on. Everything else is drawn
/// as sRGB, which the canvas converts into the process inks through its profile. Pattern fills of paths are drawn by
/// clipping to the path and replaying the pattern there, so their inks reach the plates.
/// </summary>
internal sealed partial class PageRenderer
{
    /// <summary>The most tiles a pattern fill replays on plates, against tiny steps over large areas.</summary>
    private const int MaxInkTiles = 16384;

    /// <summary>The plates drawn on, for an ink canvas or a pattern cell recorded for one; null when drawing RGB.</summary>
    private InkMap? inks;

    /// <summary>For the cell of an uncoloured pattern on plates: the ink colour every fill and stroke takes.</summary>
    private InkColor? uncoloredInk;

    /// <summary>The plates a paint prints on: every plate without overprint, else its own, its zero CMYK ones left out in mode 1.</summary>
    private Overprint OverprintOf(bool fill, InkPlan? plan, ImageColorSpace? space)
    {
        if (inks is null || !(fill ? State.FillOverprint : State.StrokeOverprint))
        {
            return Overprint.Off;
        }

        // Overprint mode 1 applies to DeviceCMYK colour only (8.6.7).
        return State.OverprintMode == 1 && plan is { IsCmyk: true } && space is { IsDevice: true } ? Overprint.NonZero : Overprint.On;
    }

    /// <summary>
    /// The ink paint for a colour on plates: true with the paint (null when it marks nothing) when the colour prints as
    /// inks, false when it is drawn as sRGB.
    /// </summary>
    private bool InkPaint(PdfColor color, double alpha, bool fill, out Paint? paint)
    {
        paint = null;
        if (inks is null)
        {
            return false;
        }

        if (uncoloredInk is InkColor tint)
        {
            paint = new SolidPaint(tint) { Opacity = (float)alpha, Antialias = settings.AntialiasPaths, BlendMode = BlendModeOf(State.BlendMode), Overprint = OverprintOf(fill, null, null) };
            return true;
        }

        if (color.Family == "Pattern" || colors.Space(color.Space, Resources, ColorContext.IntentOf(State.RenderingIntent)) is not ImageColorSpace space
            || inks.Plan(space) is not InkPlan plan)
        {
            return false;
        }

        if (plan.PaintsNothing)
        {
            return true;
        }

        paint = new SolidPaint(plan.Color(Components(color, space)))
        {
            Opacity = (float)alpha,
            Antialias = settings.AntialiasPaths,
            BlendMode = BlendModeOf(State.BlendMode),
            Overprint = OverprintOf(fill, plan, space),
        };
        return true;
    }

    /// <summary>A colour's components, as many as its space has, missing or broken ones 0.</summary>
    private static double[] Components(PdfColor color, ImageColorSpace space)
    {
        double[] components = new double[Math.Max(space.Components, 1)];
        for (int i = 0; i < space.Components; i++)
        {
            components[i] = i < color.Components.Count && double.IsFinite(color.Components[i]) ? color.Components[i] : 0;
        }

        return components;
    }

    /// <summary>The ink colour of an uncoloured pattern's colour in its underlying space, or null to draw it as sRGB.</summary>
    private InkColor? UncoloredInk(PdfObject under, IReadOnlyList<double> components)
    {
        if (inks is null || colors.Space(under, Resources, ColorContext.IntentOf(State.RenderingIntent)) is not ImageColorSpace space || inks.Plan(space) is not InkPlan plan)
        {
            return null;
        }

        return plan.Color(Components(new PdfColor(under, components), space));
    }

    /// <summary>
    /// A group's blending colour space on plates (11.4.7): an RGB or gray group blends its process part in that space, a
    /// CMYK group (or one without /CS) in the inks themselves.
    /// </summary>
    private ColorProfile? GroupSpace(PdfDictionary group, PdfDictionary? resources)
    {
        if (inks is null || group.Get("CS")?.Resolve() is not PdfObject cs || colors.Space(cs, resources, RenderingIntent.RelativeColorimetric) is not ImageColorSpace space)
        {
            return null;
        }

        return space.Family switch
        {
            ColorFamily.Rgb or ColorFamily.Lab => space.Profile is { Model: ColorModel.Rgb } profile ? profile : ColorProfile.Srgb,
            ColorFamily.Gray => space.Profile is { Model: ColorModel.Gray } gray ? gray : ColorProfile.GraySrgb,
            _ => null,
        };
    }

    /// <summary>
    /// Draws an image onto plates; false when it does not print as inks or the canvas is not an ink canvas, and it is drawn
    /// as sRGB instead. An overprinting image in spot colourants paints each of its plates through itself as a mask, so the
    /// plates it does not name keep what is below (8.6.7).
    /// </summary>
    private bool DrawInkImage(PdfImage source, PdfImageOptions options, PdfGraphicsState state, PdfName? name)
    {
        if (inks is null || canvas is not InkCanvas ink)
        {
            return false;
        }

        ImageColorSpace? space = source.ColorSpace is PdfObject spaceObject ? colors.Space(spaceObject, Resources, ColorContext.IntentOf(state.RenderingIntent)) : null;
        InkPlan? plan = space is null ? null : inks.Plan(space);
        InkRaster raster;
        try
        {
            // Every other image converts from sRGB into the process inks here, onto plates of the whole ink set, so it
            // knocks out the spot plates below it as well.
            raster = plan is not null ? ImageDecoder.DecodeInks(source, options, plan, inks.Inks) : SrgbToPlates(source.ToRaster<Rgba8>(options));
        }
        catch (PdfException e) when (e is not PdfLimitException)
        {
            Warn(PdfWarningCode.MalformedContent, $"The image {(name is null ? "inline" : "/" + name.Value)} could not be drawn: {e.Message}");
            return true;
        }

        using (raster)
        {
            if (Begin(state, 0, ImageSpace * state.Matrix) is not int restore)
            {
                return true;
            }

            float wide = new Vector2(canvas.Matrix.M11, canvas.Matrix.M12).Length(), high = new Vector2(canvas.Matrix.M21, canvas.Matrix.M22).Length();
            bool interpolate = source.Interpolate || wide < raster.Width || high < raster.Height;
            SamplingOptions sampling = new() { Filter = !settings.AntialiasImages ? ResampleFilter.Nearest : interpolate ? ResampleFilter.Bilinear : ResampleFilter.Nearest };
            Overprint overprint = OverprintOf(fill: true, plan, space);
            RectF unit = new(0, 0, 1, 1);
            if (overprint != Overprint.Off && plan is { IsCmyk: false })
            {
                foreach (Colorant colorant in plan.Colorants)
                {
                    if (raster.Plate(colorant.Name) is not InkPlate plate)
                    {
                        continue;
                    }

                    using Raster<Rgba8> mask = Raster.Create<Rgba8>(raster.Width, raster.Height);
                    for (int y = 0; y < raster.Height; y++)
                    {
                        Span<Rgba8> row = mask.Row(y);
                        Span<byte> tints = plate.Row8(y);
                        for (int x = 0; x < raster.Width; x++)
                        {
                            row[x] = new Rgba8(0, 0, 0, tints[x]);
                        }
                    }

                    SolidPaint full = new(InkColor.Mix(new InkTint(colorant, 1))) { Opacity = (float)state.FillAlpha, BlendMode = BlendModeOf(state.BlendMode), Overprint = Overprint.On, Antialias = settings.AntialiasImages };
                    canvas.DrawImageMask(mask, unit, full, sampling);
                }
            }
            else
            {
                ink.DrawImage(raster, unit, sampling, new SolidPaint(new RgbaF32(1, 1, 1, 1))
                {
                    Opacity = (float)state.FillAlpha,
                    BlendMode = BlendModeOf(state.BlendMode),
                    Overprint = overprint,
                    Antialias = settings.AntialiasImages,
                });
            }

            End(restore);
        }

        return true;
    }

    /// <summary>An sRGB image as plates of the ink set: its colours in the process inks, premultiplied by its alpha.</summary>
    private InkRaster SrgbToPlates(Raster<Rgba8> image)
    {
        using (image)
        {
            InkRaster raster = new(inks!.Inks, image.Width, image.Height);
            ColorTransform transform = inks.FromSrgb;
            InkPlate?[] process = [raster.Plate("Cyan"), raster.Plate("Magenta"), raster.Plate("Yellow"), raster.Plate("Black")];
            Rgb8[] rgb = new Rgb8[image.Width];
            Cmyk8[] cmyk = new Cmyk8[image.Width];
            for (int y = 0; y < image.Height; y++)
            {
                Span<Rgba8> row = image.Row(y);
                Span<byte> alpha = raster.Alpha.Row8(y);
                for (int x = 0; x < row.Length; x++)
                {
                    rgb[x] = new Rgb8(row[x].R, row[x].G, row[x].B);
                    alpha[x] = row[x].A;
                }

                transform.Apply<Rgb8, Cmyk8>(rgb, cmyk);
                for (int x = 0; x < row.Length; x++)
                {
                    int a = row[x].A;
                    Cmyk8 c = cmyk[x];
                    if (process[0] is InkPlate cyan)
                    {
                        cyan.Row8(y)[x] = (byte)((c.C * a + 127) / 255);
                    }

                    if (process[1] is InkPlate magenta)
                    {
                        magenta.Row8(y)[x] = (byte)((c.M * a + 127) / 255);
                    }

                    if (process[2] is InkPlate yellow)
                    {
                        yellow.Row8(y)[x] = (byte)((c.Y * a + 127) / 255);
                    }

                    if (process[3] is InkPlate black)
                    {
                        black.Row8(y)[x] = (byte)((c.K * a + 127) / 255);
                    }
                }
            }

            return raster;
        }
    }

    /// <summary>
    /// Fills a path with a pattern on plates: the path becomes a clip and the pattern is drawn inside it, a shading as its
    /// gradient or mesh, a tiling pattern as its cell replayed once per tile; false when the pattern cannot be read.
    /// </summary>
    private bool FillPatternWithInks(Egami.Drawing.Path path, PdfColor color, double alpha)
    {
        if (color.Pattern is not PdfName patternName || Resources?.Get<PdfDictionary>("Pattern")?.Get(patternName)?.Resolve() is not PdfObject pattern)
        {
            return false;
        }

        PdfDictionary dictionary = pattern as PdfDictionary ?? (pattern as PdfStream)?.Dictionary ?? [];
        PdfMatrix space = (PdfMatrix.FromArray(dictionary.Get<PdfArray>("Matrix")) ?? PdfMatrix.Identity) * patternSpaces.Peek();
        if (!State.Matrix.TryInvert(out PdfMatrix fromUser))
        {
            return false;
        }

        PdfMatrix local = space * fromUser;
        Matrix3x2 toUser = new((float)local.A, (float)local.B, (float)local.C, (float)local.D, (float)local.E, (float)local.F);
        if (dictionary.GetInt("PatternType") == 2)
        {
            if (dictionary.Get("Shading")?.Resolve() is not PdfObject shading || Shading(shading) is not ShadingPaint ready)
            {
                return false;
            }

            int saved = canvas.Save();
            canvas.ClipPath(path, ClipOp.Intersect, settings.AntialiasPaths);
            canvas.SetMatrix(toUser * canvas.Matrix);
            if (ready.Background is RgbaF32 background && background.A > 0)
            {
                canvas.DrawPaint(new SolidPaint(background) { Opacity = (float)alpha, BlendMode = BlendModeOf(State.BlendMode), Overprint = OverprintOf(true, null, null) });
            }

            if (ready.BBox is PdfRectangle box)
            {
                canvas.ClipRect(new RectF((float)box.Left, (float)box.Bottom, (float)box.Width, (float)box.Height), ClipOp.Intersect, settings.AntialiasPaths);
            }

            if (ready.Paint is Paint gradient)
            {
                canvas.DrawPaint(Shaded(gradient, alpha, Matrix3x2.Identity) with { Overprint = OverprintOf(true, null, null) });
            }
            else
            {
                DrawMesh(ready, alpha);
            }

            canvas.RestoreToCount(saved);
            return true;
        }

        if (pattern is not PdfStream stream || stream.Dictionary.GetInt("PatternType") != 1)
        {
            return false;
        }

        PdfDictionary d = stream.Dictionary;
        double xStep = Math.Abs(d.GetNumber("XStep") ?? 0), yStep = Math.Abs(d.GetNumber("YStep") ?? 0);
        if (PdfRectangle.FromArray(d.Get<PdfArray>("BBox")) is not PdfRectangle cellBox || cellBox.IsEmpty || !(xStep > 0) || !(yStep > 0) || maskDepth >= MaxMaskDepth)
        {
            return false;
        }

        RgbaF32 tint = default;
        InkColor? inkTint = null;
        bool uncoloredPattern = d.GetInt("PaintType") == 2;
        if (uncoloredPattern)
        {
            PdfObject? under = color.Space is PdfArray { Count: > 1 } withBase ? withBase.Get(1).Resolve() : null;
            if (under is null)
            {
                return false;
            }

            inkTint = UncoloredInk(under, color.Components);
            tint = colors.ToRgb(new PdfColor(under, color.Components), Resources, ColorContext.IntentOf(State.RenderingIntent)) ?? default;
        }

        if (Cell(stream, cellBox, tint, uncoloredPattern, inkTint) is not Picture cell)
        {
            return false;
        }

        // The tiles that reach the path: its bounds taken into pattern space.
        if (!Matrix3x2.Invert(toUser, out Matrix3x2 toPattern))
        {
            return false;
        }

        RectF bounds = path.Bounds;
        Vector2[] corners = [new(bounds.Left, bounds.Top), new(bounds.Right, bounds.Top), new(bounds.Left, bounds.Bottom), new(bounds.Right, bounds.Bottom)];
        Vector2[] inPattern = [.. corners.Select(c => Vector2.Transform(c, toPattern))];
        double minX = inPattern.Min(p => p.X), maxX = inPattern.Max(p => p.X), minY = inPattern.Min(p => p.Y), maxY = inPattern.Max(p => p.Y);
        long i0 = (long)Math.Floor((minX - cellBox.Right) / xStep), i1 = (long)Math.Ceiling((maxX - cellBox.Left) / xStep);
        long j0 = (long)Math.Floor((minY - cellBox.Top) / yStep), j1 = (long)Math.Ceiling((maxY - cellBox.Bottom) / yStep);
        if ((i1 - i0 + 1) * (j1 - j0 + 1) > MaxInkTiles || !double.IsFinite(minX + maxX + minY + maxY))
        {
            return false;
        }

        int restore = canvas.Save();
        canvas.ClipPath(path, ClipOp.Intersect, settings.AntialiasPaths);
        bool layered = alpha < 1 || State.BlendMode.Value is not ("Normal" or "Compatible");
        if (layered)
        {
            canvas.SaveLayer(new LayerOptions { Opacity = (float)alpha, BlendMode = BlendModeOf(State.BlendMode) });
        }

        canvas.SetMatrix(toUser * canvas.Matrix);
        Matrix3x2 pattern0 = canvas.Matrix;
        for (long i = i0; i <= i1; i++)
        {
            for (long j = j0; j <= j1; j++)
            {
                canvas.DrawPicture(cell, Matrix3x2.CreateTranslation((float)(i * xStep), (float)(j * yStep)));
                canvas.SetMatrix(pattern0);
            }
        }

        canvas.RestoreToCount(restore);
        return true;
    }
}
