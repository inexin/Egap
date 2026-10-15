using System.Numerics;

using Egami.Drawing;
using Egami.Fonts;

using Egap.Pdf.Fonts;

using Path = Egami.Drawing.Path;

namespace Egap.Pdf.Rendering;

/// <summary>
/// Text (design Q6): the glyphs one show operator places become one glyph run, positioned by the PDF's widths and the text
/// state as the walker computed them, never by the font's own advances; hinting off. Stroke modes stroke the glyph
/// outlines in user space; clip modes gather the outlines and clip at ET. Type 3 glyphs are drawn by the walker, which
/// walks their procedures.
/// </summary>
internal sealed partial class PageRenderer
{
    private readonly List<PdfGlyph> pendingGlyphs = [];
    private PdfGraphicsState? pendingState;
    private PdfPath? textClip;

    protected override void OnOperation(PdfOperation operation)
    {
        settings.Cancellation.ThrowIfCancellationRequested();
        CountOperation();
        FlushText();
        if (operation.Operator == "ET" && textClip is not null)
        {
            // The glyphs shown in a clip mode join the clip once the text object ends (9.3.6).
            State.AddClip(new PdfClipPath(textClip, PdfMatrix.Identity, EvenOdd: false));
            textClip = null;
        }
        else if (operation.Operator == "BT")
        {
            textClip = null;
        }
    }

    protected override void OnGlyph(PdfGlyph glyph)
    {
        PdfGraphicsState state = State;
        if (!ReferenceEquals(state, pendingState))
        {
            FlushText();
            pendingState = state;
        }

        pendingGlyphs.Add(glyph);
    }

    /// <summary>
    /// A Type 3 glyph's procedure is walked only when the render mode paints: invisible text (mode 3) and clip-only text (mode
    /// 7) draw nothing, as PDFium and pdf.js leave them (issue5421).
    /// </summary>
    protected override bool OnBeginType3Glyph(PdfGlyph glyph, PdfStream procedure)
    {
        return State.TextRenderMode is not (3 or 7) && hidden == 0;
    }

    /// <summary>Draws the glyphs gathered since the last flush, all shown by one operator in one state.</summary>
    private void FlushText()
    {
        if (pendingGlyphs.Count == 0 || pendingState is not PdfGraphicsState state)
        {
            return;
        }

        try
        {
            DrawText(state, pendingGlyphs);
        }
        finally
        {
            pendingGlyphs.Clear();
            pendingState = null;
        }
    }

    private void DrawText(PdfGraphicsState state, List<PdfGlyph> glyphs)
    {
        int mode = state.TextRenderMode;
        if (state.Font is not PdfDictionary dictionary || mode == 3)
        {
            return;
        }

        PdfFont font = PdfFont.FromDictionary(dictionary);
        if (font.Type == PdfFontType.Type3)
        {
            // The walker walks Type 3 procedures, which draw through the path and image callbacks.
            return;
        }

        Typeface? typeface = font.GetTypeface();
        FontSynthesis synthesis = FontSynthesis.None;
        ushort[] ids = new ushort[glyphs.Count];
        float[] scales = new float[glyphs.Count];
        Array.Fill(scales, 1f);
        if (typeface is not null)
        {
            for (int i = 0; i < ids.Length; i++)
            {
                ids[i] = font.GetGlyphId(glyphs[i].Code) ?? 0;
                scales[i] = (float)font.GetGlyphScale(ids[i]);
            }
        }
        else if (cache.Substitute(font, settings.SystemFonts) is SubstituteFont substitute)
        {
            // Not embedded, or the program could not be read (design Q5).
            typeface = substitute.Program.Typeface;
            synthesis = substitute.Synthesis;
            for (int i = 0; i < ids.Length; i++)
            {
                ids[i] = font.GetSubstituteGlyph(substitute.Program, glyphs[i].Code);
            }
        }
        else
        {
            return;
        }

        bool fill = mode is 0 or 2 or 4 or 6;
        bool stroke = mode is 1 or 2 or 5 or 6;
        bool clip = mode >= 4;
        if (fill)
        {
            FillGlyphs(state, typeface, synthesis, ids, scales, glyphs);
        }

        if (stroke || clip)
        {
            Path?[] outlines = new Path?[ids.Length];
            for (int i = 0; i < ids.Length; i++)
            {
                outlines[i] = Outline(typeface, ids[i], scales[i]);
            }

            if (stroke)
            {
                StrokeGlyphs(state, outlines, glyphs);
            }

            if (clip)
            {
                textClip ??= new PdfPath();
                for (int i = 0; i < outlines.Length; i++)
                {
                    if (outlines[i] is Path outline)
                    {
                        Append(textClip, outline, ToMatrix(glyphs[i].Matrix));
                    }
                }
            }
        }
    }

    /// <summary>One glyph run for the glyphs, in the em space of the first glyph, so upright text takes Egami's glyph cache.</summary>
    private void FillGlyphs(PdfGraphicsState state, Typeface typeface, FontSynthesis synthesis, ushort[] ids, float[] scales, List<PdfGlyph> glyphs)
    {
        PdfMatrix first = glyphs[0].Matrix;
        if (!first.TryInvert(out PdfMatrix inverse) || ColorPaint(state.FillColor, state.FillAlpha, PdfMatrix.Scale(1, -1) * first) is not Paint paint)
        {
            return;
        }

        // Glyph space is y up and Egami's glyphs are y down, so the run is flipped inside the first glyph's matrix.
        Vector2[] positions = new Vector2[ids.Length];
        for (int i = 0; i < ids.Length; i++)
        {
            PdfPoint origin = inverse.Transform(glyphs[i].Matrix.Transform(new PdfPoint(0, 0)));
            positions[i] = new Vector2((float)origin.X, (float)-origin.Y);
        }

        if (Begin(state, 0, PdfMatrix.Scale(1, -1) * first) is not int restore)
        {
            return;
        }

        // One run per stretch of glyphs at one scale: almost always the whole string (scales differ only in CID-keyed CFF fonts
        // whose Font DICTs scale their glyphs).
        Paint drawn = paint with { Antialias = settings.AntialiasText };
        for (int start = 0; start < ids.Length;)
        {
            int end = start + 1;
            while (end < ids.Length && scales[end] == scales[start])
            {
                end++;
            }

            Font font = (synthesis == FontSynthesis.None ? new Font(typeface, scales[start]) : new FontMatch(typeface, synthesis).ToFont(scales[start])) with { Hinting = FontHinting.None };
            GlyphRun run = GlyphRun.Create(font, ids.AsSpan(start, end - start), positions.AsSpan(start, end - start));
            canvas.DrawGlyphRun(run, Vector2.Zero, drawn);
            start = end;
        }

        End(restore);
    }

    /// <summary>Strokes the outlines in the user space the line width is measured in (9.3.6).</summary>
    private void StrokeGlyphs(PdfGraphicsState state, Path?[] outlines, List<PdfGlyph> glyphs)
    {
        if (!state.Matrix.TryInvert(out PdfMatrix toUser) || ColorPaint(state.StrokeColor, state.StrokeAlpha) is not Paint paint || Begin(state, 0, state.Matrix) is not int restore)
        {
            return;
        }

        PathBuilder builder = new();
        for (int i = 0; i < outlines.Length; i++)
        {
            if (outlines[i] is Path outline)
            {
                builder.AddPath(outline, ToMatrix(glyphs[i].Matrix * toUser));
            }
        }

        if (!builder.IsEmpty)
        {
            canvas.StrokePath(builder.ToPath(), ToStroke(state), paint with { Antialias = settings.AntialiasText });
        }

        End(restore);
    }

    /// <summary>A glyph's outline in em units, y up as glyph space is; null for a glyph with none.</summary>
    private static Path? Outline(Typeface typeface, ushort glyph, float scale)
    {
        OutlineSink sink = new(scale / typeface.UnitsPerEm);
        return typeface.GetGlyphOutline(glyph, sink) && !sink.Builder.IsEmpty ? sink.Builder.ToPath() : null;
    }

    private static void Append(PdfPath target, Path path, Matrix3x2 matrix)
    {
        Path placed = path.Transform(matrix);
        ReadOnlySpan<PathVerb> verbs = placed.Verbs;
        ReadOnlySpan<Vector2> points = placed.Points;
        int p = 0;
        Vector2 last = default;
        foreach (PathVerb verb in verbs)
        {
            switch (verb)
            {
                case PathVerb.Move:
                    last = points[p++];
                    target.MoveTo(last.X, last.Y);
                    break;
                case PathVerb.Line:
                    last = points[p++];
                    target.LineTo(last.X, last.Y);
                    break;
                case PathVerb.Quad:
                    Vector2 q = points[p], end = points[p + 1];
                    p += 2;
                    Vector2 c1 = last + ((q - last) * (2f / 3)), c2 = end + ((q - end) * (2f / 3));
                    target.CurveTo(c1.X, c1.Y, c2.X, c2.Y, end.X, end.Y);
                    last = end;
                    break;
                case PathVerb.Cubic:
                    Vector2 a = points[p], b = points[p + 1], e = points[p + 2];
                    p += 3;
                    target.CurveTo(a.X, a.Y, b.X, b.Y, e.X, e.Y);
                    last = e;
                    break;
                case PathVerb.Close:
                    target.Close();
                    break;
            }
        }
    }

    private static Matrix3x2 ToMatrix(PdfMatrix m)
    {
        return new Matrix3x2((float)m.A, (float)m.B, (float)m.C, (float)m.D, (float)m.E, (float)m.F);
    }

    /// <summary>Collects a glyph outline scaled from font units to em units.</summary>
    private sealed class OutlineSink(float scale) : IGlyphOutlineSink
    {
        public PathBuilder Builder { get; } = new();

        public void MoveTo(float x, float y) => Builder.MoveTo(x * scale, y * scale);

        public void LineTo(float x, float y) => Builder.LineTo(x * scale, y * scale);

        public void QuadTo(float cx, float cy, float x, float y) => Builder.QuadTo(cx * scale, cy * scale, x * scale, y * scale);

        public void CubicTo(float c1x, float c1y, float c2x, float c2y, float x, float y) => Builder.CubicTo(c1x * scale, c1y * scale, c2x * scale, c2y * scale, x * scale, y * scale);

        public void Close() => Builder.Close();
    }
}
