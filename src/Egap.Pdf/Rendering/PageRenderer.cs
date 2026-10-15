using System.Numerics;

using Egami.Drawing;
using Egami.Pixels;

using Egap.Pdf.Images;

using Path = Egami.Drawing.Path;

namespace Egap.Pdf.Rendering;

/// <summary>
/// Draws content onto an Egami canvas (Phase 4 design Q2): the graphics state walker turns operators into state, and each
/// operator that paints becomes canvas calls. The renderer keeps only what the canvas does not: the clips the content set,
/// which it mirrors as canvas saves (one per clip), and the colour spaces. It never reads the canvas back, so any canvas
/// works.
/// </summary>
internal sealed partial class PageRenderer : PdfContentWalker
{
    private readonly Canvas canvas;
    private readonly RenderSettings settings;
    private readonly Matrix3x2 pageToDevice;
    private readonly ColorCache colors;

    /// <summary>The clips mirrored on the canvas, outermost first; the canvas has one save for each above <see cref="baseSaveCount"/>.</summary>
    private readonly List<PdfClipPath> applied = [];
    private int baseSaveCount;

    public PageRenderer(Canvas canvas, Matrix3x2 pageToDevice, RenderSettings settings, ColorCache colors)
    {
        this.canvas = canvas;
        this.pageToDevice = pageToDevice;
        this.settings = settings;
        this.colors = colors;
    }

    /// <summary>Draws content with its resources, in default user space mapped to the canvas by the page matrix.</summary>
    public void Render(PdfContent content, PdfDictionary? resources, PdfReadOptions? options)
    {
        baseSaveCount = canvas.Save();
        applied.Clear();
        pendingGlyphs.Clear();
        pendingState = null;
        textClip = null;
        try
        {
            Walk(content, resources, PdfMatrix.Identity, options);
            FlushText();
        }
        finally
        {
            canvas.RestoreToCount(baseSaveCount);
        }
    }

    protected override void OnPaintPath(PdfPath path, PdfPathPaint paint)
    {
        PdfPathPaint draw = paint & (PdfPathPaint.Fill | PdfPathPaint.EvenOddFill | PdfPathPaint.Stroke);
        if (draw == 0 || path.IsEmpty)
        {
            return;
        }

        // A clip set by W or W* takes effect after the path is painted (8.5.4), so the new clip is left out here.
        bool clipped = (paint & (PdfPathPaint.Clip | PdfPathPaint.EvenOddClip)) != 0;
        PdfGraphicsState state = State;
        if (!SetUp(state.Clips, clipped ? 1 : 0, state.Matrix))
        {
            return;
        }

        if ((draw & (PdfPathPaint.Fill | PdfPathPaint.EvenOddFill)) != 0 && ColorPaint(state.FillColor, state.FillAlpha) is Paint fill)
        {
            canvas.FillPath(ToPath(path, (draw & PdfPathPaint.EvenOddFill) != 0 ? FillRule.EvenOdd : FillRule.NonZero), fill);
        }

        if ((draw & PdfPathPaint.Stroke) != 0 && ColorPaint(state.StrokeColor, state.StrokeAlpha) is Paint stroke)
        {
            canvas.StrokePath(ToPath(path, FillRule.NonZero), ToStroke(state), stroke);
        }
    }

    /// <summary>
    /// Mirrors the clips on the canvas, leaving out the last <paramref name="skip"/>, then sets the canvas matrix to
    /// <paramref name="matrix"/> on the page; false when the matrix is degenerate and nothing can be drawn.
    /// </summary>
    private bool SetUp(IReadOnlyList<PdfClipPath> clips, int skip, PdfMatrix matrix)
    {
        int wanted = clips.Count - skip;
        int common = 0;
        while (common < applied.Count && common < wanted && ReferenceEquals(applied[common], clips[common]))
        {
            common++;
        }

        if (common < applied.Count)
        {
            canvas.RestoreToCount(baseSaveCount + common);
            applied.RemoveRange(common, applied.Count - common);
        }

        for (int i = common; i < wanted; i++)
        {
            PdfClipPath clip = clips[i];
            canvas.Save();
            applied.Add(clip);
            if (Device(clip.Matrix) is Matrix3x2 clipMatrix)
            {
                canvas.SetMatrix(clipMatrix);
                canvas.ClipPath(ToPath(clip.Path, clip.EvenOdd ? FillRule.EvenOdd : FillRule.NonZero), ClipOp.Intersect, settings.AntialiasPaths);
            }
            else
            {
                // A clip under a degenerate matrix has no area, so nothing inside it shows.
                canvas.ClipRect(default);
            }
        }

        if (Device(matrix) is not Matrix3x2 device)
        {
            return false;
        }

        canvas.SetMatrix(device);
        return true;
    }

    /// <summary>A user space matrix as the canvas matrix that maps it to the device, or null when it is degenerate.</summary>
    private Matrix3x2? Device(PdfMatrix matrix)
    {
        Matrix3x2 m = new Matrix3x2((float)matrix.A, (float)matrix.B, (float)matrix.C, (float)matrix.D, (float)matrix.E, (float)matrix.F) * pageToDevice;
        float determinant = m.GetDeterminant();
        return float.IsFinite(determinant) && determinant != 0 && float.IsFinite(m.M31) && float.IsFinite(m.M32) ? m : null;
    }

    /// <summary>
    /// The stroke in user space. A line thinner than one device pixel is drawn one pixel wide (design Q11), as Egami's
    /// hairline, which also covers width 0, "the thinnest line the device can draw" (8.4.3.2).
    /// </summary>
    private Stroke ToStroke(PdfGraphicsState state)
    {
        float scale = MathF.Sqrt(MathF.Abs(canvas.Matrix.GetDeterminant()));
        float width = (float)state.LineWidth;
        bool hairline = !(width * scale >= 1) || !float.IsFinite(width);
        DashPattern? dash = null;
        if (state.DashArray.Count > 0)
        {
            float[] intervals = [.. state.DashArray.Select(d => (float)Math.Max(d, 0))];
            float total = intervals.Sum();

            // All zero lengths, or a pattern too fine to see, draws a solid line as other viewers do.
            if (total * scale >= 0.1f && float.IsFinite(total))
            {
                dash = new DashPattern(intervals.Length % 2 == 1 ? [.. intervals, .. intervals] : intervals, (float)state.DashPhase);
            }
        }

        return new Stroke(hairline ? 0 : width)
        {
            Cap = (LineCap)Math.Clamp(state.LineCap, 0, 2),
            Join = (LineJoin)Math.Clamp(state.LineJoin, 0, 2),
            MiterLimit = (float)Math.Max(state.MiterLimit, 1),
            Dash = dash,
        };
    }

    private SolidPaint? ColorPaint(PdfColor color, double alpha)
    {
        if (color.Family == "Pattern")
        {
            // Patterns arrive with shadings and tiling (tasks 12 and 14).
            return null;
        }

        if (colors.ToRgb(color, Resources) is not RgbaF32 rgb)
        {
            Warn(PdfWarningCode.MalformedContent, $"The colour {color} could not be read; it was drawn black.");
            rgb = new RgbaF32(0, 0, 0, 1);
        }

        return new SolidPaint(new RgbaF32(rgb.R, rgb.G, rgb.B, (float)alpha)) { Antialias = settings.AntialiasPaths };
    }

    /// <summary>A PDF path as an Egami path, in the same user space.</summary>
    internal static Path ToPath(PdfPath path, FillRule fillRule)
    {
        PathBuilder builder = new();
        foreach (PdfPathSegment segment in path.Segments)
        {
            switch (segment.Verb)
            {
                case PdfPathVerb.MoveTo:
                    builder.MoveTo((float)segment.Point.X, (float)segment.Point.Y);
                    break;
                case PdfPathVerb.LineTo:
                    builder.LineTo((float)segment.Point.X, (float)segment.Point.Y);
                    break;
                case PdfPathVerb.CurveTo:
                    builder.CubicTo((float)segment.Control1.X, (float)segment.Control1.Y, (float)segment.Control2.X, (float)segment.Control2.Y, (float)segment.Point.X, (float)segment.Point.Y);
                    break;
                case PdfPathVerb.Close:
                    builder.Close();
                    break;
            }
        }

        return builder.ToPath(fillRule);
    }
}
