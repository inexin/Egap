using System.Globalization;
using System.Numerics;

using Egami;
using Egami.Drawing;
using Egami.Operations;
using Egami.Pixels;

using DrawingPath = Egami.Drawing.Path;

namespace Egap.Pdf;

/// <summary>
/// An Egami canvas that draws onto a PDF page: the same drawing code that fills a raster writes PDF operators here (Phase 3,
/// hub Q10). Paths, strokes, clips, opacity and blend modes become vector content; what PDF cannot express is drawn into an
/// image at <see cref="PdfCanvasOptions.FallbackDpi"/> and placed instead, with a warning.
/// </summary>
/// <remarks>
/// <para>
/// By default the canvas is in points, y down, from the top left of the page's crop box with the page's <c>/Rotate</c> applied,
/// so it draws what a viewer shows; <see cref="PdfCanvasCoordinates.Pdf"/> draws in PDF's own space instead. The drawing goes
/// over or under the page's existing content without touching it (Phase 3 design Q5), and is written when the canvas is
/// disposed.
/// </para>
/// <para>
/// PDF can only multiply the current transformation matrix, so a canvas transform is written lazily: drawing calls that share
/// a transform are written together inside <c>q</c>, the transform's <c>cm</c> and <c>Q</c>. Clips are written in device
/// space at the level of their <see cref="Canvas.Save"/>, so <see cref="Canvas.Restore"/> undoes them as PDF's <c>Q</c> does.
/// </para>
/// </remarks>
public sealed partial class PdfCanvas : Canvas
{
    private static readonly Dictionary<BlendMode, string> PdfBlendModes = new()
    {
        [BlendMode.SourceOver] = "Normal",
        [BlendMode.Multiply] = "Multiply",
        [BlendMode.Screen] = "Screen",
        [BlendMode.Overlay] = "Overlay",
        [BlendMode.Darken] = "Darken",
        [BlendMode.Lighten] = "Lighten",
        [BlendMode.ColorDodge] = "ColorDodge",
        [BlendMode.ColorBurn] = "ColorBurn",
        [BlendMode.HardLight] = "HardLight",
        [BlendMode.SoftLight] = "SoftLight",
        [BlendMode.Difference] = "Difference",
        [BlendMode.Exclusion] = "Exclusion",
        [BlendMode.Hue] = "Hue",
        [BlendMode.Saturation] = "Saturation",
        [BlendMode.Color] = "Color",
        [BlendMode.Luminosity] = "Luminosity",
    };

    private PdfContentWriter content = new();
    private readonly List<(PdfWarning Warning, int Count)> warnings = [];
    private readonly Dictionary<PdfWarning, int> warningIndex = [];
    private readonly PdfCanvasOptions options;
    private readonly PdfMatrix baseMatrix;
    private GraphicsGroup? group;
    private bool written;

    /// <summary>Starts drawing on a page.</summary>
    /// <param name="page">The page; its existing content is kept.</param>
    /// <param name="options">How to draw; <see cref="PdfCanvasOptions.Default"/> when <see langword="null"/>.</param>
    public PdfCanvas(PdfPage page, PdfCanvasOptions? options = null)
        : this(page, options ?? PdfCanvasOptions.Default, Layout(page ?? throw new ArgumentNullException(nameof(page)), options ?? PdfCanvasOptions.Default))
    {
    }

    private PdfCanvas(PdfPage page, PdfCanvasOptions options, (float Width, float Height, PdfMatrix Base) layout)
        : base(layout.Width, layout.Height)
    {
        Page = page;
        this.options = options;
        baseMatrix = layout.Base;
        content.Save();
        if (!baseMatrix.IsIdentity)
        {
            content.Transform(baseMatrix);
        }
    }

    /// <summary>The page the canvas draws on.</summary>
    public PdfPage Page { get; }

    /// <summary>
    /// The transformation from the canvas's device space to the page's default user space: the flip and turn that make the
    /// canvas match the displayed page, or the identity for <see cref="PdfCanvasCoordinates.Pdf"/>.
    /// </summary>
    public PdfMatrix PageMatrix => baseMatrix;

    /// <summary>
    /// What was drawn as an image because PDF cannot express it, and anything else worth knowing: each warning once, in the
    /// order first met, with how many times it happened when more than once.
    /// </summary>
    public IReadOnlyList<PdfWarning> Warnings => [.. warnings.Select(w => w.Count == 1 ? w.Warning : w.Warning with { Message = string.Create(CultureInfo.InvariantCulture, $"{w.Warning.Message} ({w.Count} times)") })];

    /// <inheritdoc/>
    protected override void OnSave()
    {
        if (Forward(c => c.Save()))
        {
            return;
        }

        CloseGroup();
        content.Save();
    }

    /// <inheritdoc/>
    protected override void OnRestore()
    {
        if (Forward(c => c.Restore()))
        {
            return;
        }

        CloseGroup();
        content.Restore();
    }

    /// <inheritdoc/>
    protected override void OnSetMatrix(Matrix3x2 matrix)
    {
        // Written lazily, with the next drawing call that needs it.
        Forward(c => c.SetMatrix(matrix));
    }

    /// <inheritdoc/>
    protected override void OnClipRect(RectF rect, ClipOp op, bool antialias)
    {
        if (Forward(c => c.ClipRect(rect, op, antialias)))
        {
            return;
        }

        if (op == ClipOp.Intersect && Matrix.M12 == 0 && Matrix.M21 == 0)
        {
            CloseGroup();
            Vector2 a = Vector2.Transform(new Vector2(rect.Left, rect.Top), Matrix);
            Vector2 b = Vector2.Transform(new Vector2(rect.Right, rect.Bottom), Matrix);
            content.Rectangle(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y)).Clip().EndPath();
            return;
        }

        OnClipPath(DrawingPath.Rect(rect), op, antialias);
    }

    /// <inheritdoc/>
    protected override void OnClipPath(DrawingPath path, ClipOp op, bool antialias)
    {
        if (Forward(c => c.ClipPath(path, op, antialias)))
        {
            return;
        }

        CloseGroup();
        DrawingPath device = path.Transform(Matrix);
        if (op == ClipOp.Difference)
        {
            // PDF clips only to the inside of a path: the outside is the bounds that could still be drawn, less the path.
            RectF bounds = DeviceClipBounds;
            device = DrawingPath.Rect(bounds).Op(device, PathOp.Difference);
        }

        if (device.IsEmpty)
        {
            content.Rectangle(0, 0, 0, 0).Clip().EndPath();
            return;
        }

        WritePath(device);
        content.Clip(device.FillRule == FillRule.EvenOdd).EndPath();
    }

    /// <inheritdoc/>
    protected override void OnDrawRect(RectF rect, Stroke? stroke, Paint paint)
    {
        if (Forward(c => Draw(c, rect, stroke, paint)))
        {
            return;
        }

        if (FillsStroke(paint, stroke))
        {
            OnDrawPath(DrawingPath.Rect(rect), stroke, paint);
            return;
        }

        if (!Begin(paint, stroke))
        {
            Unsupported(paint, (c, p) => Draw(c, rect, stroke, p));
            return;
        }

        content.Rectangle(rect.X, rect.Y, rect.Width, rect.Height);
        Finish(stroke, FillRule.NonZero);
    }

    /// <inheritdoc/>
    protected override void OnDrawPath(DrawingPath path, Stroke? stroke, Paint paint)
    {
        if (Forward(c => Draw(c, path, stroke, paint)))
        {
            return;
        }

        if (FillsStroke(paint, stroke))
        {
            // Filled as its outline, cut finely enough for a closer look than the canvas's own scale.
            float scale = MathF.Max(1, MathF.Sqrt(MathF.Abs(Matrix.GetDeterminant()))) * 4;
            path = stroke!.ToPath(path, scale);
            stroke = null;
            if (path.IsEmpty)
            {
                return;
            }
        }

        if (!Begin(paint, stroke))
        {
            Unsupported(paint, (c, p) => Draw(c, path, stroke, p));
            return;
        }

        WritePath(path);
        Finish(stroke, path.FillRule);
    }

    /// <inheritdoc/>
    protected override void OnDrawImage(Raster image, RectF source, RectF destination, SamplingOptions sampling, Paint? paint)
    {
        if (Forward(c => c.DrawImage(image, source, destination, sampling, paint)))
        {
            return;
        }

        DrawRaster(image, source, destination, sampling, paint);
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !written)
        {
            written = true;
            CloseGroup();
            content.Restore();
            Page.AddContent(content.ToArray(), options.Placement);
        }

        base.Dispose(disposing);
    }

    /// <summary>The canvas's size and the matrix from it to the page's user space (Phase 3 design Q13).</summary>
    private static (float Width, float Height, PdfMatrix Base) Layout(PdfPage page, PdfCanvasOptions options)
    {
        PdfRectangle crop = page.CropBox;
        if (options.Coordinates == PdfCanvasCoordinates.Pdf)
        {
            return ((float)Math.Max(crop.Right, 1), (float)Math.Max(crop.Top, 1), PdfMatrix.Identity);
        }

        float width = (float)crop.Width, height = (float)crop.Height;
        return page.Rotate switch
        {
            90 => (height, width, new PdfMatrix(0, 1, 1, 0, crop.Left, crop.Bottom)),
            180 => (width, height, new PdfMatrix(-1, 0, 0, 1, crop.Right, crop.Bottom)),
            270 => (height, width, new PdfMatrix(0, -1, -1, 0, crop.Right, crop.Top)),
            _ => (width, height, new PdfMatrix(1, 0, 0, -1, crop.Left, crop.Top)),
        };
    }

    /// <summary>
    /// Opens the group for the current transform, then sets the paint and pen; <see langword="false"/> when PDF cannot
    /// express the paint and the call must fall back.
    /// </summary>
    private bool Begin(Paint paint, Stroke? stroke)
    {
        if (paint.Blur is not null || !PdfBlendModes.ContainsKey(paint.BlendMode) && !(paint.BlendMode == BlendMode.Source && Opaque(paint)))
        {
            return false;
        }

        if (paint is not SolidPaint solid)
        {
            return BeginShaded(paint, stroke);
        }

        GraphicsGroup g = OpenGroup();
        bool isStroke = stroke is not null;
        RgbaF32 color = solid.Color;
        (double r, double gr, double b) = (Math.Clamp(color.R, 0, 1), Math.Clamp(color.G, 0, 1), Math.Clamp(color.B, 0, 1));
        if (isStroke ? g.Stroke != (r, gr, b) : g.Fill != (r, gr, b))
        {
            content.Rgb(r, gr, b, isStroke);
            if (isStroke)
            {
                g.Stroke = (r, gr, b);
            }
            else
            {
                g.Fill = (r, gr, b);
            }
        }

        SetAlpha(g, Math.Clamp(color.A, 0, 1) * paint.Opacity, paint.BlendMode);
        if (stroke is not null)
        {
            SetStroke(g, stroke);
        }

        return true;
    }

    private static bool Opaque(Paint paint)
    {
        return paint is SolidPaint solid && solid.Color.A >= 1 && paint.Opacity >= 1;
    }

    /// <summary>Sets the constant alpha and blend mode through a graphics state dictionary, shared by the whole document.</summary>
    private void SetAlpha(GraphicsGroup g, double alpha, BlendMode blend)
    {
        string mode = PdfBlendModes.TryGetValue(blend, out string? name) ? name : "Normal";
        double rounded = Math.Round(alpha, 4);
        if (g.Alpha == rounded && g.Blend == mode)
        {
            return;
        }

        PdfReference state = Page.Document.WrittenResources.ForValue(("ExtGState", rounded, mode), () => Page.Document.Objects.Add(new PdfDictionary
        {
            ["Type"] = PdfName.Get("ExtGState"),
            ["CA"] = new PdfReal(rounded),
            ["ca"] = new PdfReal(rounded),
            ["BM"] = PdfName.Get(mode),
        }));
        content.GraphicsState(AddResource("ExtGState", state));
        g.Alpha = rounded;
        g.Blend = mode;
    }

    private void SetStroke(GraphicsGroup g, Stroke stroke)
    {
        if (g.LineWidth != stroke.Width)
        {
            content.LineWidth(stroke.Width);
            g.LineWidth = stroke.Width;
        }

        if (g.Cap != (int)stroke.Cap)
        {
            content.LineCap((int)stroke.Cap);
            g.Cap = (int)stroke.Cap;
        }

        if (g.Join != (int)stroke.Join)
        {
            content.LineJoin((int)stroke.Join);
            g.Join = (int)stroke.Join;
        }

        if (stroke.Join == LineJoin.Miter && g.MiterLimit != stroke.MiterLimit)
        {
            content.MiterLimit(Math.Max(1, stroke.MiterLimit));
            g.MiterLimit = stroke.MiterLimit;
        }

        if (!Equals(g.Dash, stroke.Dash))
        {
            double[] intervals = stroke.Dash is null ? [] : [.. stroke.Dash.Intervals.Select(i => (double)i)];
            content.Dash(intervals, stroke.Dash?.Phase ?? 0);
            g.Dash = stroke.Dash;
        }
    }

    private void Finish(Stroke? stroke, FillRule rule)
    {
        if (pendingShadings is not null)
        {
            PaintShadings(rule);
        }
        else if (stroke is not null)
        {
            content.Stroke();
        }
        else
        {
            content.Fill(rule == FillRule.EvenOdd);
        }

        if (restoreAfterDraw)
        {
            content.Restore();
            restoreAfterDraw = false;
        }
    }

    private void WritePath(DrawingPath path)
    {
        WritePath(content, path);
    }

    /// <summary>Writes a path's contours, raising quadratic curves to cubics (exactly: a quadratic is a cubic).</summary>
    private static void WritePath(PdfContentWriter content, DrawingPath path)
    {
        ReadOnlySpan<Vector2> points = path.Points;
        int p = 0;
        Vector2 current = default;
        foreach (PathVerb verb in path.Verbs)
        {
            switch (verb)
            {
                case PathVerb.Move:
                    current = points[p++];
                    content.MoveTo(current.X, current.Y);
                    break;
                case PathVerb.Line:
                    current = points[p++];
                    content.LineTo(current.X, current.Y);
                    break;
                case PathVerb.Quad:
                    Vector2 control = points[p], end = points[p + 1];
                    p += 2;
                    Vector2 c1 = current + ((control - current) * (2f / 3)), c2 = end + ((control - end) * (2f / 3));
                    content.CurveTo(c1.X, c1.Y, c2.X, c2.Y, end.X, end.Y);
                    current = end;
                    break;
                case PathVerb.Cubic:
                    content.CurveTo(points[p].X, points[p].Y, points[p + 1].X, points[p + 1].Y, points[p + 2].X, points[p + 2].Y);
                    current = points[p + 2];
                    p += 3;
                    break;
                case PathVerb.Close:
                    content.ClosePath();
                    break;
            }
        }
    }

    /// <summary>The group drawing calls with the current transform go in: the open one when it has the same transform.</summary>
    private GraphicsGroup OpenGroup()
    {
        return OpenGroupWith(Matrix);
    }

    /// <summary>The group for drawing with a given transform: the open one when it has that transform.</summary>
    private GraphicsGroup OpenGroupWith(Matrix3x2 matrix)
    {
        if (group is not null && group.Matrix == matrix)
        {
            return group;
        }

        CloseGroup();
        content.Save();
        if (!matrix.IsIdentity)
        {
            content.Transform(matrix.M11, matrix.M12, matrix.M21, matrix.M22, matrix.M31, matrix.M32);
        }

        group = new GraphicsGroup(matrix);
        return group;
    }

    private void CloseGroup()
    {
        if (group is not null)
        {
            content.Restore();
            group = null;
        }
    }

    /// <summary>
    /// Draws a call that PDF cannot express into an image at the fallback resolution and places it (Phase 3 design Q14), or
    /// throws in strict mode. The call gets the paint to draw with: a mask blur's sigma is in device pixels, so it is scaled to
    /// the image's pixels, and the blur in the image is as wide on the page as on a raster at one pixel per unit.
    /// </summary>
    private void Unsupported(Paint paint, Action<Canvas, Paint> draw)
    {
        string what = paint.Blur is not null ? "a blurred paint" : PdfBlendModes.ContainsKey(paint.BlendMode) ? paint.GetType().Name : $"the blend mode {paint.BlendMode}";
        if (options.Strict)
        {
            throw new NotSupportedException($"PDF cannot express {what}.");
        }

        float scale = options.FallbackDpi / 72f;
        Paint scaled = paint.Blur is MaskBlur blur ? paint with { Blur = new MaskBlur(Math.Min(blur.Sigma * scale, MaskBlur.MaxSigma), blur.Style) } : paint;
        Fallback(c => draw(c, scaled), what, scale);
    }

    private static void Draw(Canvas canvas, RectF rect, Stroke? stroke, Paint paint)
    {
        if (stroke is null)
        {
            canvas.FillRect(rect, paint);
        }
        else
        {
            canvas.StrokeRect(rect, stroke, paint);
        }
    }

    private static void Draw(Canvas canvas, DrawingPath path, Stroke? stroke, Paint paint)
    {
        if (stroke is null)
        {
            canvas.FillPath(path, paint);
        }
        else
        {
            canvas.StrokePath(path, stroke, paint);
        }
    }

    /// <summary>Records a warning, counting repeats; in strict mode, anything drawn other than the call asked throws instead.</summary>
    private void Warn(PdfWarningCode code, string message)
    {
        if (code == PdfWarningCode.RasterFallback && options.Strict)
        {
            throw new NotSupportedException(message);
        }

        PdfWarning warning = new(code, message);
        if (warningIndex.TryGetValue(warning, out int index))
        {
            warnings[index] = (warning, warnings[index].Count + 1);
        }
        else
        {
            warningIndex[warning] = warnings.Count;
            warnings.Add((warning, 1));
        }
    }

    /// <summary>The graphics state written inside one group, so a state already set is not written again.</summary>
    private sealed class GraphicsGroup(Matrix3x2 matrix)
    {
        public Matrix3x2 Matrix { get; } = matrix;

        // Colours start unknown, so each group sets the first it uses: PDF's initial black is not always what a viewer has
        // (pdf.js starts a transparency group with the fill colour last set outside it, even one a Q undid).
        public (double R, double G, double B)? Fill { get; set; }

        public (double R, double G, double B)? Stroke { get; set; }

        public double Alpha { get; set; } = 1;

        public string Blend { get; set; } = "Normal";

        public float LineWidth { get; set; } = 1;

        public int Cap { get; set; }

        public int Join { get; set; }

        public float MiterLimit { get; set; } = 10;

        public DashPattern? Dash { get; set; }
    }
}
