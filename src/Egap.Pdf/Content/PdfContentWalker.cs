using Egap.Pdf.Content;
using Egap.Pdf.IO;
using Egap.Pdf.Objects;

namespace Egap.Pdf;

/// <summary>
/// Walks content with its resources and tracks the graphics state (8.4) through it: the transformation matrix, <c>q</c> and
/// <c>Q</c>, the text state and matrices, colours and colour spaces, clips, graphics state dictionaries and marked content.
/// It descends into form XObjects and Type 3 glyph procedures. Derive from it and override the <c>On</c> methods to see paths,
/// glyphs, images, shadings and marked content with the state they are drawn in (Phase 3 design Q3).
/// </summary>
/// <remarks>
/// The walk is as lenient as reading: an unknown operator is passed to <see cref="OnOperation"/> and otherwise ignored, an
/// operator with missing or wrong operands is skipped, a <c>Q</c> without a <c>q</c> is ignored, and a resource that is not
/// there is reported in <see cref="Warnings"/>. Form XObjects nest at most <see cref="PdfReadOptions.MaxNestingDepth"/> deep,
/// and a form that draws itself is not entered again.
/// </remarks>
public class PdfContentWalker
{
    private static readonly PdfName DeviceGray = PdfName.Get("DeviceGray");
    private static readonly PdfName DeviceRgb = PdfName.Get("DeviceRGB");
    private static readonly PdfName DeviceCmyk = PdfName.Get("DeviceCMYK");
    private static readonly PdfName PatternName = PdfName.Get("Pattern");

    private readonly Stack<PdfGraphicsState> saved = new();
    private readonly List<PdfMarkedContent> marked = [];
    private readonly List<PdfDictionary?> resourceStack = [];
    private readonly HashSet<PdfStream> open = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<PdfStream, PdfContent> forms = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<PdfDictionary, FontWidths> fonts = new(ReferenceEqualityComparer.Instance);
    private WarningLog log = new(strict: false);
    private PdfReadOptions options = PdfReadOptions.Default;
    private PdfPath path = new();
    private int pendingClip;
    private int markedBase;

    /// <summary>The graphics state at the operation being walked.</summary>
    public PdfGraphicsState State { get; private set; } = new(PdfMatrix.Identity);

    /// <summary>The text matrix (<c>Tm</c>): where the next glyph goes, in text space.</summary>
    public PdfMatrix TextMatrix { get; private set; } = PdfMatrix.Identity;

    /// <summary>The text line matrix: the start of the current line.</summary>
    public PdfMatrix TextLineMatrix { get; private set; } = PdfMatrix.Identity;

    /// <summary>The marked content open at the operation being walked, outermost first, across form XObjects.</summary>
    public IReadOnlyList<PdfMarkedContent> MarkedContent => marked;

    /// <summary>The resources of the content being walked: the page's, a form's or a Type 3 font's.</summary>
    public PdfDictionary? Resources => resourceStack.Count > 0 ? resourceStack[^1] : null;

    /// <summary>How deep in form XObjects and Type 3 glyphs the walk is: 0 for the content it started with.</summary>
    public int Depth => Math.Max(0, resourceStack.Count - 1);

    /// <summary>What the walk repaired or skipped, including the warnings of every content stream read.</summary>
    public IReadOnlyList<PdfWarning> Warnings => log.ToArray();

    /// <summary>
    /// Walks a page's content in default user space (the identity matrix, y up, unrotated), with the page's resources.
    /// </summary>
    /// <exception cref="PdfException">The content is damaged and the document was opened in strict mode.</exception>
    /// <exception cref="PdfLimitException">Content decodes to more than the read options allow.</exception>
    public void Walk(PdfPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        Walk(page.GetContent(), page.Resources, PdfMatrix.Identity, page.Document.Options);
    }

    /// <summary>Walks content with the given resources, starting from <paramref name="matrix"/>.</summary>
    /// <param name="content">The operations to walk.</param>
    /// <param name="resources">The resources the content's names refer to; <see langword="null"/> for none.</param>
    /// <param name="matrix">The initial transformation matrix.</param>
    /// <param name="options">The limits and strictness for forms read on the way; <see cref="PdfReadOptions.Default"/> when <see langword="null"/>.</param>
    public void Walk(PdfContent content, PdfDictionary? resources, PdfMatrix matrix, PdfReadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        this.options = options ?? PdfReadOptions.Default;
        log = new WarningLog(this.options.Strict);
        saved.Clear();
        marked.Clear();
        resourceStack.Clear();
        open.Clear();
        markedBase = 0;
        State = new PdfGraphicsState(matrix);
        TextMatrix = TextLineMatrix = PdfMatrix.Identity;
        path = new PdfPath();
        pendingClip = 0;
        AddWarnings(content);
        resourceStack.Add(resources);
        try
        {
            Run(content);
        }
        finally
        {
            resourceStack.Clear();
        }
    }

    /// <summary>Called for every operation before the walker acts on it, unknown operators included.</summary>
    protected virtual void OnOperation(PdfOperation operation)
    {
    }

    /// <summary>
    /// Called when a path is painted or ended (<c>S</c>, <c>f</c>, <c>B</c>, <c>n</c> and the rest). A clip that
    /// <c>W</c> or <c>W*</c> set is already in <see cref="State"/> when this is called.
    /// </summary>
    /// <param name="path">The path, in the user space of <see cref="PdfGraphicsState.Matrix"/>.</param>
    /// <param name="paint">How it is painted.</param>
    protected virtual void OnPaintPath(PdfPath path, PdfPathPaint paint)
    {
    }

    /// <summary>Called for each glyph a text operator shows, before the text matrix moves past it.</summary>
    protected virtual void OnGlyph(PdfGlyph glyph)
    {
    }

    /// <summary>
    /// Called for an image XObject drawn with <c>Do</c> or an inline image. The image fills the unit square of user space,
    /// so <see cref="PdfGraphicsState.Matrix"/> places it.
    /// </summary>
    /// <param name="image">The image XObject's stream, or <see langword="null"/> for an inline image.</param>
    /// <param name="inlineImage">The inline image, or <see langword="null"/> for an XObject.</param>
    /// <param name="name">The XObject's name in the resources, or <see langword="null"/> for an inline image.</param>
    protected virtual void OnImage(PdfStream? image, PdfInlineImage? inlineImage, PdfName? name)
    {
    }

    /// <summary>Called for <c>sh</c>: a shading painted over the clip.</summary>
    /// <param name="shading">The shading dictionary or stream, or <see langword="null"/> when the name is not in the resources.</param>
    /// <param name="name">The shading's name in the resources.</param>
    protected virtual void OnShading(PdfObject? shading, PdfName name)
    {
    }

    /// <summary>
    /// Called before a form XObject is entered; return <see langword="false"/> to skip it. The form's matrix is already in
    /// <see cref="State"/> and its bounding box is a clip.
    /// </summary>
    protected virtual bool OnBeginForm(PdfStream form, PdfName name)
    {
        return true;
    }

    /// <summary>Called after a form XObject's content, before the state it was drawn in is restored.</summary>
    protected virtual void OnEndForm(PdfStream form, PdfName name)
    {
    }

    /// <summary>
    /// Called before a Type 3 glyph's procedure is walked; return <see langword="false"/> to skip it, which also skips glyph
    /// procedures for the rest of this glyph's string. By default glyph procedures are walked.
    /// </summary>
    protected virtual bool OnBeginType3Glyph(PdfGlyph glyph, PdfStream procedure)
    {
        return true;
    }

    /// <summary>Called when marked content begins (<c>BMC</c>, <c>BDC</c>); it is already last in <see cref="MarkedContent"/>.</summary>
    protected virtual void OnBeginMarkedContent(PdfMarkedContent markedContent)
    {
    }

    /// <summary>Called when marked content ends (<c>EMC</c>), while it is still last in <see cref="MarkedContent"/>.</summary>
    protected virtual void OnEndMarkedContent(PdfMarkedContent markedContent)
    {
    }

    /// <summary>Called for a marked-content point (<c>MP</c>, <c>DP</c>).</summary>
    protected virtual void OnMarkedPoint(PdfMarkedContent point)
    {
    }

    /// <summary>Records a warning, as the walk's own repairs are recorded; throws in strict mode.</summary>
    protected void Warn(PdfWarningCode code, string message)
    {
        log.Add(code, message);
    }

    private void Run(PdfContent content)
    {
        int saves = saved.Count;
        int markedAtStart = marked.Count;
        int outerBase = markedBase;
        markedBase = markedAtStart;
        try
        {
            foreach (PdfOperation operation in content)
            {
                OnOperation(operation);
                Execute(operation);
            }
        }
        finally
        {
            // A form or glyph cannot leave its saves or marked content open for what follows.
            while (saved.Count > saves)
            {
                State = saved.Pop();
            }

            while (marked.Count > markedAtStart)
            {
                PdfMarkedContent last = marked[^1];
                OnEndMarkedContent(last);
                marked.RemoveAt(marked.Count - 1);
            }

            markedBase = outerBase;
        }
    }

    private void Execute(PdfOperation op)
    {
        PdfObject[] a = op.OperandArray;
        switch (op.Operator)
        {
            case "q":
                saved.Push(State.Clone());
                break;
            case "Q":
                if (saved.Count > 0)
                {
                    State = saved.Pop();
                }

                break;
            case "cm" when op.GetMatrix() is PdfMatrix m:
                State.Matrix = m * State.Matrix;
                break;
            case "w" when Number(a, 0) is double width:
                State.LineWidth = width;
                break;
            case "J" when Number(a, 0) is double cap:
                State.LineCap = (int)Math.Clamp(cap, 0, 2);
                break;
            case "j" when Number(a, 0) is double join:
                State.LineJoin = (int)Math.Clamp(join, 0, 2);
                break;
            case "M" when Number(a, 0) is double limit:
                State.MiterLimit = limit;
                break;
            case "d" when a.Length == 2 && a[0] is PdfArray dashes:
                SetDash(dashes, Number(a, 1) ?? 0);
                break;
            case "ri" when a.Length == 1 && a[0] is PdfName intent:
                State.RenderingIntent = intent;
                break;
            case "i" when Number(a, 0) is double flatness:
                State.Flatness = flatness;
                break;
            case "gs" when a.Length == 1 && a[0] is PdfName name:
                ApplyExtGState(name);
                break;

            // Paths.
            case "m" when Point(a, 0) is PdfPoint p:
                path.MoveTo(p.X, p.Y);
                break;
            case "l" when Point(a, 0) is PdfPoint p:
                path.LineTo(p.X, p.Y);
                break;
            case "c" when Point(a, 0) is PdfPoint c1 && Point(a, 2) is PdfPoint c2 && Point(a, 4) is PdfPoint p:
                path.CurveTo(c1.X, c1.Y, c2.X, c2.Y, p.X, p.Y);
                break;
            case "v" when Point(a, 0) is PdfPoint c2 && Point(a, 2) is PdfPoint p:
                PdfPoint from = path.CurrentPoint;
                path.CurveTo(from.X, from.Y, c2.X, c2.Y, p.X, p.Y);
                break;
            case "y" when Point(a, 0) is PdfPoint c1 && Point(a, 2) is PdfPoint p:
                path.CurveTo(c1.X, c1.Y, p.X, p.Y, p.X, p.Y);
                break;
            case "h":
                path.Close();
                break;
            case "re" when Point(a, 0) is PdfPoint corner && Point(a, 2) is PdfPoint size:
                path.Rectangle(corner.X, corner.Y, size.X, size.Y);
                break;
            case "W":
                pendingClip = 1;
                break;
            case "W*":
                pendingClip = 2;
                break;
            case "S":
                Paint(PdfPathPaint.Stroke);
                break;
            case "s":
                path.Close();
                Paint(PdfPathPaint.Stroke);
                break;
            case "f" or "F":
                Paint(PdfPathPaint.Fill);
                break;
            case "f*":
                Paint(PdfPathPaint.EvenOddFill);
                break;
            case "B":
                Paint(PdfPathPaint.Fill | PdfPathPaint.Stroke);
                break;
            case "B*":
                Paint(PdfPathPaint.EvenOddFill | PdfPathPaint.Stroke);
                break;
            case "b":
                path.Close();
                Paint(PdfPathPaint.Fill | PdfPathPaint.Stroke);
                break;
            case "b*":
                path.Close();
                Paint(PdfPathPaint.EvenOddFill | PdfPathPaint.Stroke);
                break;
            case "n":
                Paint(PdfPathPaint.None);
                break;

            // Colour.
            case "CS" when a.Length == 1 && a[0] is PdfName space:
                State.StrokeColor = InitialColor(space);
                break;
            case "cs" when a.Length == 1 && a[0] is PdfName space:
                State.FillColor = InitialColor(space);
                break;
            case "SC" or "SCN":
                State.StrokeColor = SetComponents(State.StrokeColor, a);
                break;
            case "sc" or "scn":
                State.FillColor = SetComponents(State.FillColor, a);
                break;
            case "G" when Number(a, 0) is double gray:
                State.StrokeColor = new PdfColor(DeviceGray, [gray]);
                break;
            case "g" when Number(a, 0) is double gray:
                State.FillColor = new PdfColor(DeviceGray, [gray]);
                break;
            case "RG" when Numbers(a, 3) is double[] rgb:
                State.StrokeColor = new PdfColor(DeviceRgb, rgb);
                break;
            case "rg" when Numbers(a, 3) is double[] rgb:
                State.FillColor = new PdfColor(DeviceRgb, rgb);
                break;
            case "K" when Numbers(a, 4) is double[] cmyk:
                State.StrokeColor = new PdfColor(DeviceCmyk, cmyk);
                break;
            case "k" when Numbers(a, 4) is double[] cmyk:
                State.FillColor = new PdfColor(DeviceCmyk, cmyk);
                break;

            // Text.
            case "BT":
                TextMatrix = TextLineMatrix = PdfMatrix.Identity;
                break;
            case "ET":
                break;
            case "Tc" when Number(a, 0) is double spacing:
                State.CharacterSpacing = spacing;
                break;
            case "Tw" when Number(a, 0) is double spacing:
                State.WordSpacing = spacing;
                break;
            case "Tz" when Number(a, 0) is double scale:
                State.HorizontalScaling = scale;
                break;
            case "TL" when Number(a, 0) is double leading:
                State.Leading = leading;
                break;
            case "Tf" when a.Length == 2 && a[0] is PdfName fontName && Number(a, 1) is double size:
                SetFont(fontName, Resource("Font", fontName) as PdfDictionary, size);
                break;
            case "Tr" when Number(a, 0) is double mode:
                State.TextRenderMode = (int)Math.Clamp(mode, 0, 7);
                break;
            case "Ts" when Number(a, 0) is double rise:
                State.TextRise = rise;
                break;
            case "Td" when Point(a, 0) is PdfPoint offset:
                MoveLine(offset.X, offset.Y);
                break;
            case "TD" when Point(a, 0) is PdfPoint offset:
                State.Leading = -offset.Y;
                MoveLine(offset.X, offset.Y);
                break;
            case "Tm" when op.GetMatrix() is PdfMatrix tm:
                TextMatrix = TextLineMatrix = tm;
                break;
            case "T*":
                MoveLine(0, -State.Leading);
                break;
            case "Tj" when a.Length == 1 && a[0] is PdfString text:
                Show(text.Bytes);
                break;
            case "'" when a.Length == 1 && a[0] is PdfString text:
                MoveLine(0, -State.Leading);
                Show(text.Bytes);
                break;
            case "\"" when a.Length == 3 && Number(a, 0) is double word && Number(a, 1) is double character && a[2] is PdfString text:
                State.WordSpacing = word;
                State.CharacterSpacing = character;
                MoveLine(0, -State.Leading);
                Show(text.Bytes);
                break;
            case "TJ" when a.Length == 1 && a[0] is PdfArray items:
                ShowArray(items);
                break;

            // XObjects, images, shadings.
            case "Do" when a.Length == 1 && a[0] is PdfName name:
                DrawXObject(name);
                break;
            case "BI" when op is PdfInlineImage inline:
                OnImage(null, inline, null);
                break;
            case "sh" when a.Length == 1 && a[0] is PdfName name:
                OnShading(Resource("Shading", name), name);
                break;

            // Marked content.
            case "BMC" when a.Length == 1 && a[0] is PdfName tag:
                BeginMarked(new PdfMarkedContent(tag, null));
                break;
            case "BDC" when a.Length == 2 && a[0] is PdfName tag:
                BeginMarked(Marked(tag, a[1]));
                break;
            case "EMC":
                if (marked.Count > markedBase)
                {
                    OnEndMarkedContent(marked[^1]);
                    marked.RemoveAt(marked.Count - 1);
                }
                else
                {
                    Warn(PdfWarningCode.MalformedContent, "EMC without BMC or BDC was ignored.");
                }

                break;
            case "MP" when a.Length == 1 && a[0] is PdfName tag:
                OnMarkedPoint(new PdfMarkedContent(tag, null));
                break;
            case "DP" when a.Length == 2 && a[0] is PdfName tag:
                OnMarkedPoint(Marked(tag, a[1]));
                break;
        }
    }

    private void Paint(PdfPathPaint paint)
    {
        if (pendingClip != 0)
        {
            paint |= pendingClip == 2 ? PdfPathPaint.EvenOddClip : PdfPathPaint.Clip;
            State.AddClip(new PdfClipPath(path, State.Matrix, pendingClip == 2));
            pendingClip = 0;
        }

        OnPaintPath(path, paint);
        path = new PdfPath();
    }

    private void SetDash(PdfArray dashes, double phase)
    {
        double[] lengths = new double[Math.Min(dashes.Count, 64)];
        for (int i = 0; i < lengths.Length; i++)
        {
            lengths[i] = dashes.GetNumber(i) ?? 0;
        }

        State.DashArray = lengths;
        State.DashPhase = phase;
    }

    private void SetFont(PdfName name, PdfDictionary? font, double size)
    {
        if (font is null)
        {
            Warn(PdfWarningCode.MalformedContent, $"The font /{name.Value} is not in the resources.");
        }

        State.FontName = name;
        State.Font = font;
        State.FontSize = size;
    }

    private void MoveLine(double x, double y)
    {
        TextLineMatrix = PdfMatrix.Translation(x, y) * TextLineMatrix;
        TextMatrix = TextLineMatrix;
    }

    private void ShowArray(PdfArray items)
    {
        FontWidths widths = Widths(State.Font);
        foreach (PdfObject item in items)
        {
            switch (item)
            {
                case PdfString text:
                    Show(text.Bytes);
                    break;
                case PdfInteger or PdfReal:
                    double adjustment = ObjectValues.Number(item)!.Value / 1000 * State.FontSize;
                    TextMatrix = (widths.IsVertical
                        ? PdfMatrix.Translation(0, -adjustment)
                        : PdfMatrix.Translation(-adjustment * State.HorizontalScaling / 100, 0)) * TextMatrix;
                    break;
            }
        }
    }

    /// <summary>Shows a string: each code as a glyph, then the text matrix moves by its width and spacing (9.4.4).</summary>
    private void Show(ReadOnlySpan<byte> bytes)
    {
        PdfGraphicsState state = State;
        FontWidths widths = Widths(state.Font);
        double scale = state.HorizontalScaling / 100;
        PdfMatrix size = new(state.FontSize * scale, 0, 0, state.FontSize, 0, state.TextRise);
        bool type3 = state.Font?.GetName("Subtype")?.Value == "Type3";
        bool glyphProcedures = type3;
        for (int i = 0; i < bytes.Length;)
        {
            (int code, int length) = widths.NextCode(bytes, i);
            i += length;
            bool space = length == 1 && code == 32;
            double width = widths.Width(code);
            PdfGlyph glyph = new(code, length, width, size * TextMatrix * state.Matrix, space);
            OnGlyph(glyph);
            if (glyphProcedures)
            {
                glyphProcedures = RunType3Glyph(glyph);
            }

            double spacing = state.CharacterSpacing + (space ? state.WordSpacing : 0);
            TextMatrix = (widths.IsVertical
                ? PdfMatrix.Translation(0, -(width / 1000 * state.FontSize) - spacing)
                : PdfMatrix.Translation(((width / 1000 * state.FontSize) + spacing) * scale, 0)) * TextMatrix;
        }
    }

    /// <summary>Walks a Type 3 glyph's procedure with the glyph's matrix; returns whether to go on with the next glyphs.</summary>
    private bool RunType3Glyph(PdfGlyph glyph)
    {
        PdfDictionary font = State.Font!;
        if (glyph.Code is < 0 or > 255 || GlyphName(font, glyph.Code) is not PdfName glyphName
            || font.Get<PdfDictionary>("CharProcs")?.Get(glyphName)?.Resolve() is not PdfStream procedure)
        {
            return true;
        }

        if (!OnBeginType3Glyph(glyph, procedure))
        {
            return false;
        }

        PdfMatrix fontMatrix = font.Get<PdfArray>("FontMatrix") is PdfArray fm && fm.Count >= 6
            && Enumerable.Range(0, 6).All(k => fm.GetNumber(k) is not null)
            ? new PdfMatrix(fm.GetNumber(0)!.Value, fm.GetNumber(1)!.Value, fm.GetNumber(2)!.Value, fm.GetNumber(3)!.Value, fm.GetNumber(4)!.Value, fm.GetNumber(5)!.Value)
            : new PdfMatrix(0.001, 0, 0, 0.001, 0, 0);
        PdfDictionary? resources = font.Get<PdfDictionary>("Resources") ?? Resources;
        RunNested(procedure, resources, fontMatrix * glyph.Matrix, null, isForm: false, PdfName.Get("Type3"));
        return true;
    }

    private static PdfName? GlyphName(PdfDictionary font, int code)
    {
        if (font.Get("Encoding")?.Resolve() is not PdfDictionary encoding || encoding.Get<PdfArray>("Differences") is not PdfArray differences)
        {
            return null;
        }

        PdfName? found = null;
        long next = 0;
        foreach (PdfObject item in differences)
        {
            switch (item)
            {
                case PdfInteger start:
                    next = start.Value;
                    break;
                case PdfName name:
                    if (next == code)
                    {
                        found = name;
                    }

                    next++;
                    break;
            }
        }

        return found;
    }

    private void DrawXObject(PdfName name)
    {
        if (Resource("XObject", name) is not PdfStream xObject)
        {
            Warn(PdfWarningCode.MalformedContent, $"The XObject /{name.Value} is not in the resources.");
            return;
        }

        switch (xObject.Dictionary.GetName("Subtype")?.Value)
        {
            case "Image":
                OnImage(xObject, null, name);
                break;
            case "Form":
                PdfMatrix matrix = xObject.Dictionary.Get<PdfArray>("Matrix") is PdfArray m && m.Count >= 6 && Enumerable.Range(0, 6).All(k => m.GetNumber(k) is not null)
                    ? new PdfMatrix(m.GetNumber(0)!.Value, m.GetNumber(1)!.Value, m.GetNumber(2)!.Value, m.GetNumber(3)!.Value, m.GetNumber(4)!.Value, m.GetNumber(5)!.Value)
                    : PdfMatrix.Identity;
                PdfRectangle? box = PdfRectangle.FromArray(xObject.Dictionary.Get<PdfArray>("BBox"));
                RunNested(xObject, xObject.Dictionary.Get<PdfDictionary>("Resources") ?? Resources, matrix * State.Matrix, box, isForm: true, name);
                break;
        }
    }

    /// <summary>Walks a form's or a Type 3 glyph's content in a saved state, with loops and depth limited.</summary>
    private void RunNested(PdfStream stream, PdfDictionary? resources, PdfMatrix matrix, PdfRectangle? box, bool isForm, PdfName name)
    {
        if (resourceStack.Count > options.MaxNestingDepth)
        {
            Warn(PdfWarningCode.MalformedContent, $"Forms and glyphs nest more than {options.MaxNestingDepth} deep; /{name.Value} was not drawn.");
            return;
        }

        if (!open.Add(stream))
        {
            Warn(PdfWarningCode.MalformedContent, $"/{name.Value} draws itself; it was not entered again.");
            return;
        }

        PdfGraphicsState outer = State;
        PdfMatrix outerText = TextMatrix, outerLine = TextLineMatrix;
        PdfPath outerPath = path;
        int outerClip = pendingClip;
        State = outer.Clone();
        State.Matrix = matrix;
        path = new PdfPath();
        pendingClip = 0;
        try
        {
            if (box is PdfRectangle b)
            {
                State.AddClip(new PdfClipPath(new PdfPath().Rectangle(b.Left, b.Bottom, b.Width, b.Height), matrix, EvenOdd: false));
            }

            if (isForm && !OnBeginForm(stream, name))
            {
                return;
            }

            PdfContent content = Read(stream);
            resourceStack.Add(resources);
            try
            {
                Run(content);
            }
            finally
            {
                resourceStack.RemoveAt(resourceStack.Count - 1);
            }

            if (isForm)
            {
                OnEndForm(stream, name);
            }
        }
        finally
        {
            open.Remove(stream);
            State = outer;
            TextMatrix = outerText;
            TextLineMatrix = outerLine;
            path = outerPath;
            pendingClip = outerClip;
        }
    }

    private PdfContent Read(PdfStream stream)
    {
        if (!forms.TryGetValue(stream, out PdfContent? content))
        {
            try
            {
                content = PdfContent.Read(stream);
            }
            catch (PdfException e) when (e is not PdfLimitException && !options.Strict)
            {
                Warn(PdfWarningCode.MalformedContent, $"A form or glyph's content could not be read: {e.Message}");
                content = [];
            }

            AddWarnings(content);
            forms[stream] = content;
        }

        return content;
    }

    private void ApplyExtGState(PdfName name)
    {
        if (Resource("ExtGState", name) is not PdfDictionary gs)
        {
            Warn(PdfWarningCode.MalformedContent, $"The graphics state /{name.Value} is not in the resources.");
            return;
        }

        PdfGraphicsState state = State;
        foreach ((PdfName key, PdfObject raw) in gs.Entries)
        {
            PdfObject value = raw.Resolve();
            double? number = ObjectValues.Number(value);
            switch (key.Value)
            {
                case "LW" when number is double width:
                    state.LineWidth = width;
                    break;
                case "LC" when number is double cap:
                    state.LineCap = (int)Math.Clamp(cap, 0, 2);
                    break;
                case "LJ" when number is double join:
                    state.LineJoin = (int)Math.Clamp(join, 0, 2);
                    break;
                case "ML" when number is double limit:
                    state.MiterLimit = limit;
                    break;
                case "D" when value is PdfArray { Count: 2 } dash && dash.Get(0).Resolve() is PdfArray lengths:
                    SetDash(lengths, dash.GetNumber(1) ?? 0);
                    break;
                case "RI" when value is PdfName intent:
                    state.RenderingIntent = intent;
                    break;
                case "FL" when number is double flatness:
                    state.Flatness = flatness;
                    break;
                case "SA" when value is PdfBoolean adjust:
                    state.StrokeAdjustment = adjust.Value;
                    break;
                case "CA" when number is double alpha:
                    state.StrokeAlpha = Math.Clamp(alpha, 0, 1);
                    break;
                case "ca" when number is double alpha:
                    state.FillAlpha = Math.Clamp(alpha, 0, 1);
                    break;
                case "BM":
                    state.BlendMode = BlendMode(value) ?? state.BlendMode;
                    break;
                case "SMask":
                    state.SoftMask = value as PdfDictionary;
                    state.SoftMaskMatrix = state.Matrix;
                    break;
                case "AIS" when value is PdfBoolean ais:
                    state.AlphaIsShape = ais.Value;
                    break;
                case "TK" when value is PdfBoolean knockout:
                    state.TextKnockout = knockout.Value;
                    break;
                case "OP" when value is PdfBoolean overprint:
                    state.StrokeOverprint = overprint.Value;
                    if (gs.Get("op") is null)
                    {
                        state.FillOverprint = overprint.Value;
                    }

                    break;
                case "op" when value is PdfBoolean overprint:
                    state.FillOverprint = overprint.Value;
                    break;
                case "OPM" when number is double mode:
                    state.OverprintMode = mode == 1 ? 1 : 0;
                    break;
                case "Font" when value is PdfArray { Count: 2 } font && font.GetNumber(1) is double size:
                    PdfDictionary? dictionary = font.Get(0).Resolve() as PdfDictionary;
                    state.FontName = null;
                    state.Font = dictionary;
                    state.FontSize = size;
                    break;
            }
        }
    }

    private static readonly HashSet<string> BlendModes =
    [
        "Normal", "Compatible", "Multiply", "Screen", "Overlay", "Darken", "Lighten", "ColorDodge", "ColorBurn", "HardLight",
        "SoftLight", "Difference", "Exclusion", "Hue", "Saturation", "Color", "Luminosity",
    ];

    private static PdfName? BlendMode(PdfObject value)
    {
        return value switch
        {
            PdfName name when BlendModes.Contains(name.Value) => name.Value == "Compatible" ? PdfName.Get("Normal") : name,
            PdfArray array => array.Select(v => v.Resolve()).OfType<PdfName>().FirstOrDefault(n => BlendModes.Contains(n.Value)),
            _ => null,
        };
    }

    /// <summary>The colour a colour space starts with (8.6.8): its components at their initial values.</summary>
    private PdfColor InitialColor(PdfName name)
    {
        PdfObject space = name.Value switch
        {
            "DeviceGray" or "DeviceRGB" or "DeviceCMYK" or "Pattern" => name,
            _ => Resource("ColorSpace", name) ?? name,
        };
        if (space is PdfName && name.Value is not ("DeviceGray" or "DeviceRGB" or "DeviceCMYK" or "Pattern" or "G" or "RGB" or "CMYK" or "CalRGB" or "CalGray" or "Lab"))
        {
            Warn(PdfWarningCode.MalformedContent, $"The colour space /{name.Value} is not in the resources.");
        }

        string family = space is PdfName n ? n.Value : (space as PdfArray)?.GetName(0)?.Value ?? "";
        double[] components = family switch
        {
            "DeviceGray" or "G" or "CalGray" => [0],
            "DeviceRGB" or "RGB" or "CalRGB" => [0, 0, 0],
            "DeviceCMYK" or "CMYK" => [0, 0, 0, 1],
            "Lab" => [0, 0, 0],
            "Indexed" or "I" => [0],
            "Separation" => [1],
            "DeviceN" when space is PdfArray { Count: > 1 } deviceN && deviceN.Get(1).Resolve() is PdfArray names => [.. Enumerable.Repeat(1.0, Math.Min(names.Count, 32))],
            "ICCBased" when space is PdfArray { Count: > 1 } icc && icc.Get(1).Resolve() is PdfStream profile => [.. IccInitial(profile)],
            "Pattern" => [],
            _ => [0],
        };
        return new PdfColor(space, components);
    }

    private static double[] IccInitial(PdfStream profile)
    {
        long n = profile.Dictionary.GetInt("N") ?? 3;
        return n switch
        {
            1 => [0],
            4 => [0, 0, 0, 1],
            _ => [0, 0, 0],
        };
    }

    private static PdfColor SetComponents(PdfColor current, PdfObject[] operands)
    {
        PdfName? pattern = operands.Length > 0 ? operands[^1] as PdfName : null;
        int count = operands.Length - (pattern is null ? 0 : 1);
        double[] components = new double[Math.Max(0, count)];
        for (int i = 0; i < components.Length; i++)
        {
            components[i] = ObjectValues.Number(operands[i]) ?? 0;
        }

        return current with { Components = components, Pattern = pattern };
    }

    private void BeginMarked(PdfMarkedContent content)
    {
        marked.Add(content);
        OnBeginMarkedContent(content);
    }

    private PdfMarkedContent Marked(PdfName tag, PdfObject properties)
    {
        if (properties is PdfDictionary inline)
        {
            return new PdfMarkedContent(tag, inline);
        }

        if (properties is PdfName name)
        {
            PdfDictionary? found = Resource("Properties", name) as PdfDictionary;
            if (found is null)
            {
                Warn(PdfWarningCode.MalformedContent, $"The properties /{name.Value} are not in the resources.");
            }

            return new PdfMarkedContent(tag, found, name);
        }

        return new PdfMarkedContent(tag, null);
    }

    private PdfObject? Resource(string category, PdfName name)
    {
        return Resources?.Get<PdfDictionary>(category)?.Get(name)?.Resolve() is PdfObject value and not PdfNull ? value : null;
    }

    private FontWidths Widths(PdfDictionary? font)
    {
        if (font is null)
        {
            return FontWidths.For(null);
        }

        if (!fonts.TryGetValue(font, out FontWidths? widths))
        {
            widths = FontWidths.For(font);
            fonts[font] = widths;
        }

        return widths;
    }

    private void AddWarnings(PdfContent content)
    {
        foreach (PdfWarning warning in content.Warnings)
        {
            log.Add(warning.Code, warning.Message, warning.Offset, warning.ObjectNumber);
        }
    }

    private static double? Number(PdfObject[] operands, int index)
    {
        return index < operands.Length && ObjectValues.Number(operands[index]) is double value && double.IsFinite(value) ? value : null;
    }

    private static PdfPoint? Point(PdfObject[] operands, int index)
    {
        return Number(operands, index) is double x && Number(operands, index + 1) is double y ? new PdfPoint(x, y) : null;
    }

    private static double[]? Numbers(PdfObject[] operands, int count)
    {
        if (operands.Length < count)
        {
            return null;
        }

        double[] values = new double[count];
        for (int i = 0; i < count; i++)
        {
            if (Number(operands, i) is not double value)
            {
                return null;
            }

            values[i] = value;
        }

        return values;
    }
}

/// <summary>How a path is painted (8.5.3): any mix of stroking, filling and clipping, or none for <c>n</c> on its own.</summary>
[Flags]
public enum PdfPathPaint
{
    /// <summary>Not painted (<c>n</c>), as after a clip.</summary>
    None = 0,

    /// <summary>Stroked.</summary>
    Stroke = 1,

    /// <summary>Filled with the nonzero winding rule.</summary>
    Fill = 2,

    /// <summary>Filled with the even-odd rule.</summary>
    EvenOddFill = 4,

    /// <summary>Set as a clip with the nonzero winding rule (<c>W</c>).</summary>
    Clip = 8,

    /// <summary>Set as a clip with the even-odd rule (<c>W*</c>).</summary>
    EvenOddClip = 16,
}
