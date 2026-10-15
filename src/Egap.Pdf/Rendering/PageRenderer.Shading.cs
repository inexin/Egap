using System.Numerics;

using Egami.Drawing;
using Egami.Pixels;

using Egap.Pdf.Images;

namespace Egap.Pdf.Rendering;

/// <summary>
/// The <c>sh</c> operator and shading patterns (8.7.4): a shading paints the whole clip with <c>sh</c>, or what a fill,
/// stroke or glyph covers as a pattern, mapped from shading space to the page by the pattern matrix and the space of the
/// content the pattern is used in. A pattern's background fills under it; <c>sh</c> ignores the background, as the spec says.
/// </summary>
internal sealed partial class PageRenderer
{
    /// <summary>The matrix of each content stream being walked: the page's start, then each form's, which patterns live in.</summary>
    private readonly Stack<PdfMatrix> patternSpaces = new();

    protected override void OnShading(PdfObject? shading, PdfName name)
    {
        if (shading is null)
        {
            Warn(PdfWarningCode.MalformedContent, $"The shading /{name.Value} is not in the resources.");
            return;
        }

        PdfGraphicsState state = State;
        if (Shading(shading) is not ShadingPaint ready || Begin(state, 0, state.Matrix) is not int restore)
        {
            return;
        }

        if (ready.BBox is PdfRectangle box)
        {
            canvas.ClipRect(new RectF((float)box.Left, (float)box.Bottom, (float)box.Width, (float)box.Height), ClipOp.Intersect, settings.AntialiasPaths);
        }

        if (ready.Paint is Paint paint)
        {
            canvas.DrawPaint(Shaded(paint, state.FillAlpha, Matrix3x2.Identity));
        }
        else
        {
            DrawMesh(ready, state.FillAlpha);
        }

        End(restore);
    }

    /// <summary>The paint a pattern colour stands for: a shading pattern's gradient now, tiling patterns with task 14.</summary>
    private Paint? PatternPaint(PdfColor color, double alpha, PdfMatrix drawing)
    {
        if (color.Pattern is not PdfName name || Resources?.Get<PdfDictionary>("Pattern")?.Get(name)?.Resolve() is not PdfObject pattern)
        {
            Warn(PdfWarningCode.MalformedContent, $"The pattern /{color.Pattern?.Value} is not in the resources.");
            return null;
        }

        PdfDictionary dictionary = pattern as PdfDictionary ?? (pattern as PdfStream)?.Dictionary ?? [];
        if (dictionary.GetInt("PatternType") != 2 || dictionary.Get("Shading")?.Resolve() is not PdfObject shading || Shading(shading) is not ShadingPaint ready)
        {
            return TilingPaint(pattern, color, alpha, drawing);
        }

        // Shading space to the current user space: the pattern matrix into the content's space, then back from the CTM.
        PdfMatrix space = (PdfMatrix.FromArray(dictionary.Get<PdfArray>("Matrix")) ?? PdfMatrix.Identity) * patternSpaces.Peek();
        if (!drawing.TryInvert(out PdfMatrix fromUser))
        {
            return null;
        }

        PdfMatrix local = space * fromUser;
        Matrix3x2 m = new((float)local.A, (float)local.B, (float)local.C, (float)local.D, (float)local.E, (float)local.F);
        if ((ready.Paint is Paint gradient ? ready.BoxedPaint(gradient) : ready.MeshPaint()) is not Paint paint)
        {
            return null;
        }

        Paint shaded = Shaded(paint, alpha, m);
        if (ready.Background is RgbaF32 background && ready.Background.Value.A > 0)
        {
            // The background fills the area the shading leaves (8.7.4.3): the gradient over its colour.
            shaded = new BlendPaint(new SolidPaint(background with { A = (float)alpha }), shaded, Egami.Operations.BlendMode.SourceOver) { BlendMode = shaded.BlendMode, Antialias = shaded.Antialias };
        }

        return shaded;
    }

    private Paint Shaded(Paint paint, double alpha, Matrix3x2 local)
    {
        return paint with
        {
            Opacity = (float)alpha,
            LocalMatrix = local,
            BlendMode = BlendModeOf(State.BlendMode),
            Antialias = settings.AntialiasPaths,
        };
    }

    /// <summary>A shading read once per document; null with a warning when it cannot be read.</summary>
    private ShadingPaint? Shading(PdfObject shading)
    {
        try
        {
            return cache.Shading(shading, () => Shadings.Read(shading, colors, Resources, ColorContext.IntentOf(State.RenderingIntent)));
        }
        catch (Exception e) when (e is InvalidDataException or PdfException and not PdfLimitException)
        {
            Warn(PdfWarningCode.MalformedContent, $"A shading could not be read: {e.Message}");
            return null;
        }
    }

    /// <summary>A mesh shading drawn with <c>sh</c>: its triangles and patches in the current user space.</summary>
    private void DrawMesh(ShadingPaint shading, double alpha)
    {
        shading.Mesh?.Draw(canvas, new SolidPaint(new RgbaF32(1, 1, 1, 1)) { Opacity = (float)alpha, BlendMode = BlendModeOf(State.BlendMode), Antialias = settings.AntialiasPaths });
    }
}
