using System.Numerics;
using System.Runtime.CompilerServices;

using Egami.Drawing;
using Egami.Pixels;

using Egap.Pdf.Images;

namespace Egap.Pdf.Rendering;

/// <summary>
/// Tiling patterns (8.7.3): the pattern cell is drawn once into an Egami picture by a renderer of its own, in pattern space
/// clipped to /BBox, and fills through a repeating picture paint whose tile is /XStep by /YStep. A coloured pattern keeps its
/// own colours; an uncoloured one paints everything in the colour given with the pattern, so its picture is made per colour.
/// Patterns inside pattern cells nest at most <see cref="MaxMaskDepth"/> deep.
/// </summary>
internal sealed partial class PageRenderer
{
    private static readonly ConditionalWeakTable<PdfStream, Dictionary<RgbaF32, Picture?>> Cells = [];

    /// <summary>For the cell of an uncoloured pattern: the colour every fill and stroke takes, whatever the content sets.</summary>
    private RgbaF32? uncolored;

    private PicturePaint? TilingPaint(PdfObject pattern, PdfColor color, double alpha, PdfMatrix drawing)
    {
        if (pattern is not PdfStream stream || stream.Dictionary.GetInt("PatternType") != 1)
        {
            Warn(PdfWarningCode.MalformedContent, $"The pattern /{color.Pattern?.Value} is neither a tiling nor a shading pattern.");
            return null;
        }

        PdfDictionary d = stream.Dictionary;
        double xStep = Math.Abs(d.GetNumber("XStep") ?? 0), yStep = Math.Abs(d.GetNumber("YStep") ?? 0);
        if (PdfRectangle.FromArray(d.Get<PdfArray>("BBox")) is not PdfRectangle box || box.IsEmpty || !(xStep > 0) || !(yStep > 0) || maskDepth >= MaxMaskDepth)
        {
            Warn(PdfWarningCode.MalformedContent, $"The tiling pattern /{color.Pattern?.Value} has no area or step, or nests too deep.");
            return null;
        }

        // An uncoloured pattern's colour comes with it, in the Pattern space's underlying space (8.7.3.3).
        RgbaF32 tint = default;
        if (d.GetInt("PaintType") == 2)
        {
            PdfObject? under = color.Space is PdfArray { Count: > 1 } space ? space.Get(1).Resolve() : null;
            if (under is null || colors.ToRgb(new PdfColor(under, color.Components), Resources, ColorContext.IntentOf(State.RenderingIntent)) is not RgbaF32 rgb)
            {
                Warn(PdfWarningCode.MalformedContent, $"The uncoloured pattern /{color.Pattern?.Value} has no colour to paint in.");
                return null;
            }

            tint = rgb;
        }

        if (Cell(stream, box, tint, d.GetInt("PaintType") == 2) is not Picture cell)
        {
            return null;
        }

        PdfMatrix patternSpace = (PdfMatrix.FromArray(d.Get<PdfArray>("Matrix")) ?? PdfMatrix.Identity) * patternSpaces.Peek();
        if (!drawing.TryInvert(out PdfMatrix fromUser))
        {
            return null;
        }

        PdfMatrix local = patternSpace * fromUser;
        RectF tile = new((float)box.Left, (float)box.Bottom, (float)xStep, (float)yStep);
        if (box.Width > xStep || box.Height > yStep)
        {
            cell = Wrap(cell, box, tile);
        }

        return new PicturePaint(cell, TileMode.Repeat, TileMode.Repeat, tile)
        {
            LocalMatrix = new Matrix3x2((float)local.A, (float)local.B, (float)local.C, (float)local.D, (float)local.E, (float)local.F),
            Opacity = (float)alpha,
            BlendMode = BlendModeOf(State.BlendMode),
            Antialias = settings.AntialiasPaths,
        };
    }

    /// <summary>The most copies of a cell <see cref="Wrap"/> draws into one tile, against tiny steps under a large box.</summary>
    private const int MaxWrappedCopies = 4096;

    /// <summary>
    /// A cell whose box is larger than its step, folded into one tile: cells overlap their neighbours (8.7.3.1), so each tile
    /// shows the parts of every cell that reach into it. Past <see cref="MaxWrappedCopies"/> the cell is left as it is.
    /// </summary>
    private static Picture Wrap(Picture cell, PdfRectangle box, RectF tile)
    {
        int left = (int)Math.Ceiling(-box.Width / tile.Width), up = (int)Math.Ceiling(-box.Height / tile.Height);
        if ((long)(2 - left) * (2 - up) > MaxWrappedCopies)
        {
            return cell;
        }

        PictureRecorder recorder = new(tile);
        recorder.ClipRect(tile, antialias: false);
        for (int i = left; i <= 1; i++)
        {
            for (int j = up; j <= 1; j++)
            {
                recorder.DrawPicture(cell, Matrix3x2.CreateTranslation(i * tile.Width, j * tile.Height));
            }
        }

        return recorder.Finish();
    }

    /// <summary>The pattern cell as a picture in pattern space, made once per pattern and colour; null when it cannot be drawn.</summary>
    private Picture? Cell(PdfStream pattern, PdfRectangle box, RgbaF32 tint, bool uncoloredPattern)
    {
        Dictionary<RgbaF32, Picture?> byColor = Cells.GetValue(pattern, _ => []);
        lock (byColor)
        {
            if (byColor.TryGetValue(tint, out Picture? known))
            {
                return known;
            }
        }

        Picture? picture = null;
        try
        {
            PictureRecorder recorder = new(new RectF((float)box.Left, (float)box.Bottom, (float)box.Width, (float)box.Height));
            PageRenderer inner = new(recorder, Matrix3x2.Identity, settings, cache) { maskDepth = maskDepth + 1, uncolored = uncoloredPattern ? tint : null };
            PdfContent content = PdfContent.Parse(System.Text.Encoding.ASCII.GetBytes(FormattableString.Invariant($"{box.Left} {box.Bottom} {box.Width} {box.Height} re W n")));
            content.AddRange(PdfContent.Read(pattern));
            try
            {
                inner.Render(content, pattern.Dictionary.Get<PdfDictionary>("Resources") ?? Resources, PdfMatrix.Identity, readOptions);
            }
            finally
            {
                Collected.AddRange(inner.Collected);
                operations += inner.operations;
            }

            picture = recorder.Finish();
        }
        catch (Exception e) when (e is PdfException and not PdfLimitException || e is ArgumentException)
        {
            Warn(PdfWarningCode.MalformedContent, $"A tiling pattern's cell could not be drawn: {e.Message}");
        }

        lock (byColor)
        {
            byColor[tint] = picture;
        }

        return picture;
    }
}
