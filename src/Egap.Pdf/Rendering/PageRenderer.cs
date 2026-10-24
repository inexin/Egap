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

    /// <summary>The clips mirrored on the canvas, outermost first, each with the canvas's save count before its save.</summary>
    private readonly List<PdfClipPath> applied = [];
    private readonly List<int> clipSaves = [];
    private int baseSaveCount;

    /// <summary>The clips a transparency group was opened inside: its content cannot restore below them.</summary>
    private int clipFloor;

    private readonly RenderCache cache;

    public PageRenderer(Canvas canvas, Matrix3x2 pageToDevice, RenderSettings settings, RenderCache cache)
    {
        this.canvas = canvas;
        this.pageToDevice = pageToDevice;
        this.settings = settings;
        this.cache = cache;
        colors = cache.Colors(settings.OutputIntents, settings.DefaultCmyk);
        plates = canvas is InkCanvas ink ? cache.Inks(ink.Raster.Inks) : null;
    }

    /// <summary>Draws content with its resources, in default user space mapped to the canvas by the page matrix.</summary>
    public void Render(PdfContent content, PdfDictionary? resources, PdfReadOptions? options)
    {
        Render(content, resources, PdfMatrix.Identity, options);
    }

    /// <summary>Draws content starting from <paramref name="matrix"/> in default user space.</summary>
    public void Render(PdfContent content, PdfDictionary? resources, PdfMatrix matrix, PdfReadOptions? options)
    {
        baseSaveCount = canvas.Save();
        readOptions = options;
        applied.Clear();
        clipSaves.Clear();
        groups.Clear();
        patternSpaces.Clear();
        patternSpaces.Push(matrix);
        hiddenMarks.Clear();
        hidden = 0;
        clipFloor = 0;
        pendingGlyphs.Clear();
        pendingState = null;
        textClip = null;
        try
        {
            try
            {
                Walk(content, resources, matrix, options);
                FlushText();
            }
            catch (OperationLimit)
            {
                // The limit's warning is collected below; the rest of the content is left out.
            }
            finally
            {
                Collected.AddRange(Warnings);
            }
        }
        finally
        {
            canvas.RestoreToCount(baseSaveCount);
            ForgetInkImages();
        }
    }

    protected override void OnPaintPath(PdfPath path, PdfPathPaint paint)
    {
        PdfPathPaint draw = paint & (PdfPathPaint.Fill | PdfPathPaint.EvenOddFill | PdfPathPaint.Stroke);
        if (draw == 0 || path.IsEmpty || !Finite(path))
        {
            return;
        }

        // A clip set by W or W* takes effect after the path is painted (8.5.4), so the new clip is left out here.
        bool clipped = (paint & (PdfPathPaint.Clip | PdfPathPaint.EvenOddClip)) != 0;
        PdfGraphicsState state = State;
        if (Begin(state, clipped ? 1 : 0, state.Matrix) is not int restore)
        {
            return;
        }

        if ((draw & (PdfPathPaint.Fill | PdfPathPaint.EvenOddFill)) != 0)
        {
            Path filled = ToPath(path, (draw & PdfPathPaint.EvenOddFill) != 0 ? FillRule.EvenOdd : FillRule.NonZero);
            if (PatternOnPlates(state.FillColor) && FillPatternWithInks(filled, state.FillColor, state.FillAlpha, state.Matrix))
            {
                // Drawn on the plates through a clip.
            }
            else if (ColorPaint(state.FillColor, state.FillAlpha) is Paint fill)
            {
                canvas.FillPath(filled, fill);
            }
        }

        if ((draw & PdfPathPaint.Stroke) != 0)
        {
            Path stroked = ToPath(path, FillRule.NonZero);
            Stroke pen = ToStroke(state);
            if (PatternOnPlates(state.StrokeColor) && StrokeOutline(stroked, pen) is Path outline
                && FillPatternWithInks(outline, state.StrokeColor, state.StrokeAlpha, state.Matrix, fill: false))
            {
                // Drawn on the plates through the stroke's outline as a clip.
            }
            else if (ColorPaint(state.StrokeColor, state.StrokeAlpha, fill: false) is Paint stroke)
            {
                canvas.StrokePath(stroked, pen, stroke);
            }
        }

        End(restore);
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

        // Inside a transparency group the clips it was opened in stay: its layer sits above their saves.
        common = Math.Max(common, Math.Min(clipFloor, applied.Count));
        if (common < applied.Count)
        {
            canvas.RestoreToCount(clipSaves[common]);
            applied.RemoveRange(common, applied.Count - common);
            clipSaves.RemoveRange(common, clipSaves.Count - common);
        }

        for (int i = common; i < wanted; i++)
        {
            PdfClipPath clip = clips[i];
            clipSaves.Add(canvas.Save());
            applied.Add(clip);
            if (Device(clip.Matrix) is Matrix3x2 clipMatrix)
            {
                canvas.SetMatrix(clipMatrix);
                if (Finite(clip.Path))
                {
                    canvas.ClipPath(ToPath(clip.Path, clip.EvenOdd ? FillRule.EvenOdd : FillRule.NonZero), ClipOp.Intersect, settings.AntialiasPaths);
                }
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

    /// <summary>
    /// The current transfer function (10.5), sampled once per document; null when there is none, it changes nothing, it is
    /// turned off, or it cannot be read (with a warning).
    /// </summary>
    private TransferTable? Transfer
    {
        get
        {
            if (!settings.TransferFunctions || State.Transfer is not PdfObject value)
            {
                return null;
            }

            TransferTable? table = cache.Transfer(value, out string? problem);
            if (problem is not null && warnedTransfers.Add(value))
            {
                Warn(PdfWarningCode.MalformedContent, $"A transfer function could not be read; colours were drawn as they are: {problem}");
            }

            return table;
        }
    }

    private readonly HashSet<PdfObject> warnedTransfers = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// The paint for a colour at an alpha; a pattern's is mapped for drawing under <paramref name="drawing"/> (the current
    /// matrix when null), the user space matrix the canvas will have when the paint is used.
    /// </summary>
    private Paint? ColorPaint(PdfColor color, double alpha, PdfMatrix? drawing = null, bool fill = true)
    {
        if (InkPaint(color, alpha, fill, out Paint? ink))
        {
            return ink;
        }

        if (uncolored is RgbaF32 tint)
        {
            // Inside an uncoloured pattern's cell everything takes the pattern's colour (8.7.3.3).
            return new SolidPaint(tint with { A = (float)alpha }) { Antialias = settings.AntialiasPaths, BlendMode = BlendModeOf(State.BlendMode), Overprint = OverprintOf(fill, null, null) };
        }

        if (color.Family == "Pattern")
        {
            return PatternPaint(color, alpha, drawing ?? State.Matrix);
        }

        if (colors.ToRgb(color, Resources, ColorContext.IntentOf(State.RenderingIntent)) is not RgbaF32 rgb)
        {
            Warn(PdfWarningCode.MalformedContent, $"The colour {color} could not be read; it was drawn black.");
            rgb = new RgbaF32(0, 0, 0, 1);
        }

        // On plates the transfer function acts on the inks, which the canvas makes from this sRGB colour after it is drawn.
        if (plates is null && rgb.A > 0 && Transfer is TransferTable transfer)
        {
            rgb = transfer.Apply(rgb);
        }

        // A colourant that marks nothing (None) paints nothing at all (8.6.6.4).
        return rgb.A == 0 ? null : new SolidPaint(new RgbaF32(rgb.R, rgb.G, rgb.B, (float)alpha)) { Antialias = settings.AntialiasPaths, BlendMode = BlendModeOf(State.BlendMode), Overprint = OverprintOf(fill, null, null) };
    }

    /// <summary>Whether every point of a path is a finite number in single precision, as Egami's paths need: a coordinate too large for a float is drawn as nothing.</summary>
    internal static bool Finite(PdfPath path)
    {
        foreach (PdfPathSegment segment in path.Segments)
        {
            if (!float.IsFinite((float)segment.Point.X) || !float.IsFinite((float)segment.Point.Y)
                || (segment.Verb == PdfPathVerb.CurveTo && (!float.IsFinite((float)segment.Control1.X) || !float.IsFinite((float)segment.Control1.Y)
                    || !float.IsFinite((float)segment.Control2.X) || !float.IsFinite((float)segment.Control2.Y))))
            {
                return false;
            }
        }

        return true;
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
